using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;

namespace UnifiedCalendar.Tests;

internal sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public int InvocationCount { get; private set; }

    public Func<Task>? BeforeInvocation { get; set; }

    public Action? AfterInvocation { get; set; }

    public async Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        InvocationCount++;
        if (BeforeInvocation is not null)
        {
            await BeforeInvocation();
        }

        await callback();
        AfterInvocation?.Invoke();
    }
}

internal sealed class RecordingTimelineViewport : ITimelineViewport
{
    public TimelineViewportState State { get; set; } = new(null, 0d, null, null, false);

    public List<TimelineViewportRestoration> Restorations { get; } = [];

    public TimelineViewportState Capture(
        IReadOnlyList<EventKey> currentKeys,
        EventKey? detailKey) => State with { DetailKey = detailKey };

    public void Restore(TimelineViewportRestoration restoration) => Restorations.Add(restoration);
}

internal sealed class TestSettingsStore : ISettingsStore
{
    public AppSettings Settings { get; set; } = AppSettings.CreateDefault();

    public Exception? LoadException { get; set; }

    public bool CancelLoad { get; set; }

    public Exception? SaveException { get; set; }

    public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (CancelLoad)
        {
            return Task.FromCanceled<AppSettings>(cancellationToken);
        }

        if (LoadException is not null)
        {
            return Task.FromException<AppSettings>(LoadException);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Settings);
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SaveException is not null)
        {
            return Task.FromException(SaveException);
        }

        Settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class TestCalendarSyncService : ICalendarSyncService
{
    public event EventHandler<SyncSnapshotChangedEventArgs>? SnapshotChanged;

    public event EventHandler<AccountSyncStateChangedEventArgs>? AccountStateChanged;

    public SyncSnapshot CurrentSnapshot { get; set; } = Phase6Data.EmptySnapshot;

    public IReadOnlyList<AccountSyncState> AccountStates { get; set; } = [];

    public int RequestCount { get; private set; }

    public int RefreshCount { get; private set; }

    public List<IReadOnlyCollection<Guid>> TargetAccountIds { get; } = [];

    public SyncTriggerReason? LastReason { get; private set; }

    public Func<SyncTriggerReason, CancellationToken, Task<SyncRunResult>>? RequestHandler { get; set; }

    public Task<StartupCacheResult> InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new StartupCacheResult(CurrentSnapshot, AccountStates, CurrentSnapshot.Events.Count > 0));

    public Task RefreshFromSettingsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshCount++;
        return Task.CompletedTask;
    }

    public Task<SyncRunResult> RequestSyncAsync(
        SyncTriggerReason reason,
        CancellationToken cancellationToken = default)
    {
        RequestCount++;
        LastReason = reason;
        return RequestHandler?.Invoke(reason, cancellationToken)
            ?? Task.FromResult(new SyncRunResult(reason, []));
    }

    public Task<SyncRunResult> RequestSyncAsync(
        SyncTriggerReason reason,
        IReadOnlyCollection<Guid> internalAccountIds,
        CancellationToken cancellationToken = default)
    {
        TargetAccountIds.Add(internalAccountIds.ToArray());
        return RequestSyncAsync(reason, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void PublishSnapshot(SyncSnapshot snapshot)
    {
        CurrentSnapshot = snapshot;
        SnapshotChanged?.Invoke(this, new SyncSnapshotChangedEventArgs(snapshot));
    }

    public void PublishSnapshotNotification(SyncSnapshot snapshot) =>
        SnapshotChanged?.Invoke(this, new SyncSnapshotChangedEventArgs(snapshot));

    public void PublishState(AccountSyncState state)
    {
        AccountStates = AccountStates
            .Where(value => value.InternalAccountId != state.InternalAccountId)
            .Append(state)
            .ToArray();
        AccountStateChanged?.Invoke(this, new AccountSyncStateChangedEventArgs(state));
    }
}

internal sealed class RecordingAccountInteractionService :
    IAccountRegistrationService,
    IAccountReauthenticationService
{
    public bool IsAvailable { get; set; }

    public HashSet<ProviderKind>? AvailableProviders { get; set; }

    public List<ProviderKind> AddedProviders { get; } = [];

    public List<Guid> ReauthenticatedAccounts { get; } = [];

    public Func<ProviderKind, CancellationToken, Task<AccountRegistrationResult?>>? AddHandler { get; set; }

    public Func<Guid, CancellationToken, Task>? ReauthenticateHandler { get; set; }

    public bool IsProviderAvailable(ProviderKind provider) =>
        AvailableProviders?.Contains(provider) ?? IsAvailable;

    public Task<AccountRegistrationResult?> AddAccountAsync(
        ProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddedProviders.Add(provider);
        return AddHandler?.Invoke(provider, cancellationToken)
            ?? Task.FromResult<AccountRegistrationResult?>(null);
    }

    public Task ReauthenticateAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReauthenticatedAccounts.Add(internalAccountId);
        return ReauthenticateHandler?.Invoke(internalAccountId, cancellationToken)
            ?? Task.CompletedTask;
    }
}

internal sealed class RecordingUriLauncher : IExternalUriLauncher
{
    public List<Uri> OpenedUris { get; } = [];

    public Task<bool> OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenedUris.Add(uri);
        return Task.FromResult(true);
    }
}

internal static class Phase6Data
{
    public static readonly DateTimeOffset Now = new(2026, 8, 27, 10, 30, 0, TimeSpan.Zero);

    public static readonly Guid GoogleAccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static readonly Guid MicrosoftAccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static SyncSnapshot EmptySnapshot { get; } = new([], [], [], Now);

    public static CalendarAccount Account(
        Guid accountId,
        ProviderKind provider,
        bool enabled = true,
        string? displayName = null) => new(
            accountId,
            provider,
            $"subject-{provider}",
            displayName ?? $"{provider} Account",
            $"{provider.ToString().ToLowerInvariant()}@example.invalid",
            enabled,
            $"{provider.ToString().ToLowerInvariant()}/{accountId}");

    public static CalendarEvent Event(
        string sourceEventId,
        Guid? accountId = null,
        ProviderKind provider = ProviderKind.Google,
        string calendarId = "calendar",
        string? title = null,
        DateTimeOffset? startUtc = null,
        DateTimeOffset? endUtc = null,
        Uri? meetingUri = null,
        Uri? sourceDetailUri = null,
        string? description = null,
        string? location = null,
        AttendeeResponse responseStatus = AttendeeResponse.Accepted) => new(
            new EventKey(
                provider,
                accountId ?? GoogleAccountId,
                calendarId,
                sourceEventId),
            title ?? sourceEventId,
            new TimedEventTiming(
                startUtc ?? Now.AddMinutes(15),
                endUtc ?? Now.AddHours(1)),
            $"{calendarId} name",
            description,
            location,
            responseStatus: responseStatus,
            sourceCalendarColor: RgbColor.Parse("#2F6FED"),
            meetingUri: meetingUri,
            sourceDetailUri: sourceDetailUri);

    public static SyncSnapshot Snapshot(
        IReadOnlyList<CalendarAccount> accounts,
        IReadOnlyList<CalendarSelection> selections,
        IReadOnlyList<CalendarEvent>? events = null) => new(
            accounts,
            selections,
            events ?? [],
            Now);

    public static AccountSyncState State(
        Guid accountId,
        SyncStatus status,
        DateTimeOffset? lastFullySuccessfulSyncUtc = null,
        string calendarId = "calendar",
        SyncErrorCategory error = SyncErrorCategory.None) => new(
            accountId,
            status,
            Now,
            lastFullySuccessfulSyncUtc,
            [new CalendarSyncState(calendarId, status, Now, lastFullySuccessfulSyncUtc, null, error)]);

    public static PresentationSnapshot Present(
        SyncSnapshot source,
        TimeProvider? timeProvider = null) => new CalendarPresentationService(
            timeProvider ?? new MutableTimeProvider(Now),
            ColorMetrics.ProgressLightnessDelta).BuildSnapshot(
                source,
                new DisplaySettings(),
                [],
                TimeZoneInfo.Utc);

    public static MainWindowViewModel CreateViewModel(
        TestCalendarSyncService sync,
        MutableTimeProvider timeProvider,
        TestSettingsStore? settings = null,
        IUiDispatcher? dispatcher = null,
        RecordingTimelineViewport? viewport = null,
        RecordingAccountInteractionService? interactions = null,
        RecordingUriLauncher? uriLauncher = null,
        InternalRefreshSignal? refreshSignal = null,
        IApplicationSettingsService? settingsService = null)
    {
        settings ??= new TestSettingsStore();
        dispatcher ??= new ImmediateUiDispatcher();
        viewport ??= new RecordingTimelineViewport();
        interactions ??= new RecordingAccountInteractionService();
        uriLauncher ??= new RecordingUriLauncher();
        return new MainWindowViewModel(
            sync,
            refreshSignal ?? new InternalRefreshSignal(),
            new CalendarPresentationService(timeProvider, ColorMetrics.ProgressLightnessDelta),
            settingsService ?? new ApplicationSettingsService(settings),
            dispatcher,
            new ResourceUiTextService(),
            interactions,
            interactions,
            uriLauncher,
            new BrushCache(),
            new SnapshotDiffer(),
            viewport,
            timeProvider,
            TimeZoneInfo.Utc);
    }

    public static async Task WaitUntilAsync(
        Func<bool> predicate,
        [CallerArgumentExpression(nameof(predicate))] string? predicateText = null)
    {
        var timeout = TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException(
                    $"The asynchronous UI projection did not complete within " +
                    $"{timeout.TotalSeconds:0} seconds: {predicateText}");
            }

            await Task.Delay(10);
        }
    }
}
