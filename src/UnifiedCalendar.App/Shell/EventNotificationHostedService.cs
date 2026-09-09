using Microsoft.Extensions.Hosting;
using Serilog;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Notifications;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;

namespace UnifiedCalendar.App.Shell;

public sealed class EventNotificationHostedService : IHostedService
{
    private readonly ICalendarSyncService _syncService;
    private readonly IApplicationSettingsService _settingsService;
    private readonly InternalRefreshSignal _refreshSignal;
    private readonly CalendarPresentationService _presentationService;
    private readonly EventNotificationPlanner _planner;
    private readonly ITrayNotifier _notifier;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalTimeZoneProvider _localTimeZoneProvider;
    private readonly object _evaluationGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task _pendingEvaluation = Task.CompletedTask;
    private IReadOnlyCollection<EventNotificationKey> _notifiedKeys = [];
    private AppSettings? _settings;
    private SyncSnapshot? _snapshot;
    private bool _startupInitializationPending = true;
    private bool _started;
    private bool _stopping;

    public EventNotificationHostedService(
        ICalendarSyncService syncService,
        IApplicationSettingsService settingsService,
        InternalRefreshSignal refreshSignal,
        CalendarPresentationService presentationService,
        EventNotificationPlanner planner,
        ITrayNotifier notifier,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        ILocalTimeZoneProvider localTimeZoneProvider)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _refreshSignal = refreshSignal ?? throw new ArgumentNullException(nameof(refreshSignal));
        _presentationService = presentationService
            ?? throw new ArgumentNullException(nameof(presentationService));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _localTimeZoneProvider = localTimeZoneProvider
            ?? throw new ArgumentNullException(nameof(localTimeZoneProvider));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_evaluationGate)
        {
            if (_started)
            {
                return;
            }

            _settings = settings;
            _syncService.SnapshotChanged += OnSnapshotChanged;
            _settingsService.SettingsChanged += OnSettingsChanged;
            _refreshSignal.RefreshRequested += OnRefreshRequested;
            _snapshot = _syncService.CurrentSnapshot;
            _started = true;
        }

        QueueEvaluation(suppressEligibleNotifications: true);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (_evaluationGate)
        {
            if (!_started || _stopping)
            {
                return;
            }

            _stopping = true;
            pending = _pendingEvaluation;
        }

        _syncService.SnapshotChanged -= OnSnapshotChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _refreshSignal.RefreshRequested -= OnRefreshRequested;
        _lifetimeCancellation.Cancel();
        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal Task WaitForPendingEvaluationsAsync()
    {
        lock (_evaluationGate)
        {
            return _pendingEvaluation;
        }
    }

    private void OnSnapshotChanged(object? sender, SyncSnapshotChangedEventArgs eventArgs) =>
        QueueEvaluation(snapshot: eventArgs.Snapshot);

    private void OnSettingsChanged(object? sender, ApplicationSettingsChangedEventArgs eventArgs)
    {
        var suppress = (!eventArgs.Previous.Notifications.Enabled
                && eventArgs.Current.Notifications.Enabled)
            || eventArgs.Previous.Notifications.LeadMinutes
                != eventArgs.Current.Notifications.LeadMinutes;
        QueueEvaluation(eventArgs.Current, suppressEligibleNotifications: suppress);
    }

    private void OnRefreshRequested(object? sender, InternalRefreshRequestedEventArgs eventArgs)
    {
        if (eventArgs.Reason == InternalRefreshReason.MinuteBoundary)
        {
            QueueEvaluation();
        }
    }

    private void QueueEvaluation(
        AppSettings? settings = null,
        SyncSnapshot? snapshot = null,
        bool suppressEligibleNotifications = false)
    {
        lock (_evaluationGate)
        {
            if (!_started || _stopping)
            {
                return;
            }

            _settings = settings ?? _settings;
            _snapshot = snapshot ?? _snapshot;
            var request = new EvaluationRequest(
                _settings!,
                _snapshot!,
                suppressEligibleNotifications);
            _pendingEvaluation = _pendingEvaluation
                .ContinueWith(
                    _ => EvaluateAndObserveAsync(request, _lifetimeCancellation.Token),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task EvaluateAndObserveAsync(
        EvaluationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                "EventNotificationFailed {Stage} {ErrorCategory}",
                "Evaluation",
                exception.GetType().Name);
        }
    }

    private async Task EvaluateAsync(
        EvaluationRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Settings.Notifications.Enabled)
        {
            _notifiedKeys = [];
            return;
        }

        var localTimeZone = _localTimeZoneProvider.GetCurrent();
        var nowLocal = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), localTimeZone);
        var display = new DisplaySettings(
            request.Settings.Display.Days,
            request.Settings.Display.DefaultEventColor);
        var notifiedKeys = _notifiedKeys;
        var suppress = _startupInitializationPending || request.SuppressEligibleNotifications;
        var evaluation = await Task.Run(() =>
        {
            var presentation = _presentationService.BuildSnapshot(
                request.Snapshot,
                display,
                request.Settings.ColorRules,
                localTimeZone);
            var events = presentation.Events.Select(value => new EventNotificationSource(
                value.StableId,
                value.LocalStart,
                value.IsAllDay,
                value.IsMultiDay,
                value.Source.Title)).ToArray();
            var plan = _planner.Plan(
                events,
                nowLocal,
                request.Settings.Notifications.LeadMinutes,
                notifiedKeys,
                suppress);
            return new EvaluationResult(
                plan,
                events.Any(value => !value.IsAllDay && value.LocalStart.HasValue));
        }, cancellationToken).ConfigureAwait(false);

        _notifiedKeys = evaluation.Plan.NotifiedKeys;
        if (evaluation.HasTimedCandidate)
        {
            _startupInitializationPending = false;
        }

        if (evaluation.Plan.Notification is null)
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _notifier.ShowEventStarting(evaluation.Plan.Notification);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        Log.Information(
            "EventNotificationShown {Count}",
            evaluation.Plan.Notification.AdditionalCount + 1);
    }

    private sealed record EvaluationRequest(
        AppSettings Settings,
        SyncSnapshot Snapshot,
        bool SuppressEligibleNotifications);

    private sealed record EvaluationResult(
        EventNotificationPlan Plan,
        bool HasTimedCandidate);
}
