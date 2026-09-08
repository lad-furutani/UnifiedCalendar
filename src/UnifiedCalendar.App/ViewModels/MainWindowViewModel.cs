using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;

namespace UnifiedCalendar.App.ViewModels;

public enum MainContentState
{
    Loading,
    Unregistered,
    Empty,
    Error,
    Timeline,
}

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ICalendarSyncService _syncService;
    private readonly InternalRefreshSignal _refreshSignal;
    private readonly CalendarPresentationService _presentationService;
    private readonly IApplicationSettingsService _settingsService;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTextService _textService;
    private readonly IAccountRegistrationService _registrationService;
    private readonly IAccountReauthenticationService _reauthenticationService;
    private readonly IExternalUriLauncher _uriLauncher;
    private readonly BrushCache _brushCache;
    private readonly SnapshotDiffer _snapshotDiffer;
    private readonly ITimelineViewport _viewport;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalTimeZoneProvider _localTimeZoneProvider;
    private readonly SemaphoreSlim _applySemaphore = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _initialSyncCompleted;
    private bool _disposed;
    private double _pixelsPerDip = 1d;

    public MainWindowViewModel(
        ICalendarSyncService syncService,
        InternalRefreshSignal refreshSignal,
        CalendarPresentationService presentationService,
        IApplicationSettingsService settingsService,
        IUiDispatcher dispatcher,
        IUiTextService textService,
        IAccountRegistrationService registrationService,
        IAccountReauthenticationService reauthenticationService,
        IExternalUriLauncher uriLauncher,
        BrushCache brushCache,
        SnapshotDiffer snapshotDiffer,
        ITimelineViewport viewport,
        TimeProvider timeProvider,
        TimeZoneInfo localTimeZone)
        : this(
            syncService,
            refreshSignal,
            presentationService,
            settingsService,
            dispatcher,
            textService,
            registrationService,
            reauthenticationService,
            uriLauncher,
            brushCache,
            snapshotDiffer,
            viewport,
            timeProvider,
            new FixedLocalTimeZoneProvider(localTimeZone))
    {
    }

    public MainWindowViewModel(
        ICalendarSyncService syncService,
        InternalRefreshSignal refreshSignal,
        CalendarPresentationService presentationService,
        IApplicationSettingsService settingsService,
        IUiDispatcher dispatcher,
        IUiTextService textService,
        IAccountRegistrationService registrationService,
        IAccountReauthenticationService reauthenticationService,
        IExternalUriLauncher uriLauncher,
        BrushCache brushCache,
        SnapshotDiffer snapshotDiffer,
        ITimelineViewport viewport,
        TimeProvider timeProvider,
        ILocalTimeZoneProvider localTimeZoneProvider)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _refreshSignal = refreshSignal ?? throw new ArgumentNullException(nameof(refreshSignal));
        _presentationService = presentationService ?? throw new ArgumentNullException(nameof(presentationService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _registrationService = registrationService ?? throw new ArgumentNullException(nameof(registrationService));
        _reauthenticationService = reauthenticationService
            ?? throw new ArgumentNullException(nameof(reauthenticationService));
        _uriLauncher = uriLauncher ?? throw new ArgumentNullException(nameof(uriLauncher));
        _brushCache = brushCache ?? throw new ArgumentNullException(nameof(brushCache));
        _snapshotDiffer = snapshotDiffer ?? throw new ArgumentNullException(nameof(snapshotDiffer));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _localTimeZoneProvider = localTimeZoneProvider
            ?? throw new ArgumentNullException(nameof(localTimeZoneProvider));

        ManualRefreshCommand = new AsyncRelayCommand(ManualRefreshAsync, () => !IsSyncing);
        AddGoogleAccountCommand = new AsyncRelayCommand(
            cancellationToken => AddAccountAsync(ProviderKind.Google, cancellationToken),
            () => _registrationService.IsProviderAvailable(ProviderKind.Google));
        AddMicrosoftAccountCommand = new AsyncRelayCommand(
            cancellationToken => AddAccountAsync(ProviderKind.Microsoft, cancellationToken),
            () => _registrationService.IsProviderAvailable(ProviderKind.Microsoft));
        var unavailable = _textService.Get(UiResourceKeys.SettingsAccountsProviderUnavailable);
        AddGoogleAccountToolTip = _registrationService.IsProviderAvailable(ProviderKind.Google)
            ? null
            : unavailable;
        AddMicrosoftAccountToolTip = _registrationService.IsProviderAvailable(ProviderKind.Microsoft)
            ? null
            : unavailable;
        CloseDetailsCommand = new RelayCommand(CloseDetails, () => IsDetailsOpen);

        _syncService.SnapshotChanged += OnSnapshotChanged;
        _syncService.AccountStateChanged += OnAccountStateChanged;
        _refreshSignal.RefreshRequested += OnInternalRefreshRequested;
        _settingsService.SettingsChanged += OnSettingsChanged;
        UpdateFixedText();
    }

    public ObservableCollection<TimelineItemViewModel> TimelineItems { get; } = [];

    public ObservableCollection<AccountWarningViewModel> AccountWarnings { get; } = [];

    public IAsyncRelayCommand ManualRefreshCommand { get; }

    public IAsyncRelayCommand AddGoogleAccountCommand { get; }

    public IAsyncRelayCommand AddMicrosoftAccountCommand { get; }

    public string? AddGoogleAccountToolTip { get; }

    public string? AddMicrosoftAccountToolTip { get; }

    public IRelayCommand CloseDetailsCommand { get; }

    [ObservableProperty]
    private string _currentTimeText = string.Empty;

    [ObservableProperty]
    private string _lastUpdateText = string.Empty;

    [ObservableProperty]
    private string _stateMessage = string.Empty;

    [ObservableProperty]
    private string _manualRefreshText = string.Empty;

    [ObservableProperty]
    private string _updatingText = string.Empty;

    [ObservableProperty]
    private string _addGoogleAccountText = string.Empty;

    [ObservableProperty]
    private string _addMicrosoftAccountText = string.Empty;

    [ObservableProperty]
    private string _stickyDateText = string.Empty;

    [ObservableProperty]
    private string _accountWarningsText = string.Empty;

    [ObservableProperty]
    private string _accountOperationMessage = string.Empty;

    [ObservableProperty]
    private double _timeColumnWidth = 128d;

    [ObservableProperty]
    private double _fontSizeDip = 14d;

    [ObservableProperty]
    private double _statusHeight = LayoutMetrics.StatusHeight;

    [ObservableProperty]
    private double _warningButtonHeight = LayoutMetrics.WarningButtonHeight;

    [ObservableProperty]
    private Thickness _eventRowMargin = new(10d, 5d, 10d, 5d);

    [ObservableProperty]
    private double _eventRowMinHeight = LayoutMetrics.CalculateEventRowMinHeight(14d, 5d, 1d);

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private MainContentState _contentState = MainContentState.Loading;

    [ObservableProperty]
    private EventDetailsViewModel? _eventDetails;

    public bool IsTimelineVisible => ContentState == MainContentState.Timeline;

    public bool IsLoading => ContentState == MainContentState.Loading;

    public bool IsUnregistered => ContentState == MainContentState.Unregistered;

    public bool IsEmpty => ContentState == MainContentState.Empty;

    public bool IsError => ContentState == MainContentState.Error;

    public bool IsDetailsOpen => EventDetails is not null;

    public bool HasLastUpdate => LastUpdateText.Length > 0;

    public bool HasAccountWarnings => AccountWarnings.Count > 0;

    public bool HasAccountOperationMessage => AccountOperationMessage.Length > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _settings = AppSettings.CreateDefault();
            Log.Warning(
                "UiSettingsLoadFailed {Stage} {ErrorCategory}",
                "Initialization",
                exception.GetType().Name);
        }

        await ProjectAndApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    public void SetTimeColumnWidth(double width)
    {
        if (double.IsFinite(width) && width > 0d)
        {
            TimeColumnWidth = width;
        }
    }

    public void SetPixelsPerDip(double pixelsPerDip)
    {
        if (!double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
        }

        _pixelsPerDip = pixelsPerDip;
        UpdateEventRowMinHeight();
    }

    public void UpdateStickyDate(DateOnly? date)
    {
        var header = TimelineItems
            .OfType<DayHeaderItemViewModel>()
            .FirstOrDefault(item => item.Date == date);
        StickyDateText = header?.Header ?? string.Empty;
    }

    public void OpenDetails(EventKey key)
    {
        var row = TimelineItems.OfType<EventRowViewModel>().FirstOrDefault(item => item.Key == key);
        if (row is null)
        {
            return;
        }

        if (EventDetails?.Key == key)
        {
            EventDetails.UpdateFrom(row.Presented);
        }
        else
        {
            EventDetails = new EventDetailsViewModel(row.Presented, _textService, _uriLauncher);
        }

        OnPropertyChanged(nameof(IsDetailsOpen));
        CloseDetailsCommand.NotifyCanExecuteChanged();
    }

    public void CloseDetails()
    {
        EventDetails = null;
        OnPropertyChanged(nameof(IsDetailsOpen));
        CloseDetailsCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _syncService.SnapshotChanged -= OnSnapshotChanged;
        _syncService.AccountStateChanged -= OnAccountStateChanged;
        _refreshSignal.RefreshRequested -= OnInternalRefreshRequested;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _lifetimeCancellation.Cancel();
    }

    partial void OnContentStateChanged(MainContentState value)
    {
        OnPropertyChanged(nameof(IsTimelineVisible));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsUnregistered));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsError));
    }

    partial void OnLastUpdateTextChanged(string value) => OnPropertyChanged(nameof(HasLastUpdate));

    partial void OnIsSyncingChanged(bool value) => ManualRefreshCommand.NotifyCanExecuteChanged();

    private async Task ManualRefreshAsync(CancellationToken cancellationToken)
    {
        if (IsSyncing)
        {
            return;
        }

        IsSyncing = true;
        try
        {
            await _syncService.RequestSyncAsync(
                SyncTriggerReason.Manual,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                UpdateStatus(_syncService.CurrentSnapshot);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    private async Task AddAccountAsync(
        ProviderKind provider,
        CancellationToken cancellationToken)
    {
        AccountOperationMessage = string.Empty;
        try
        {
            _ = await _registrationService
                .AddAccountAsync(provider, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AccountInteractionException exception)
        {
            Log.Warning(
                "AccountUiOperationFailed {Operation} {Provider} {ErrorCategory}",
                "AddFromMainWindow",
                provider,
                exception.Failure);
            AccountOperationMessage = AccountInteractionUiText.GetFailureMessage(
                _textService,
                exception.Failure);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountUiOperationFailed {Operation} {Provider} {ErrorCategory}",
                "AddFromMainWindow",
                provider,
                exception.GetType().Name);
            AccountOperationMessage = AccountInteractionUiText.GetFailureMessage(
                _textService,
                AccountInteractionFailure.AuthenticationFailed);
        }
    }

    partial void OnAccountOperationMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasAccountOperationMessage));

    private void OnSnapshotChanged(object? sender, SyncSnapshotChangedEventArgs eventArgs) =>
        Observe(ProjectAndApplyAsync(_lifetimeCancellation.Token), "SnapshotProjection");

    private void OnAccountStateChanged(object? sender, AccountSyncStateChangedEventArgs eventArgs) =>
        Observe(ProjectAndApplyAsync(_lifetimeCancellation.Token), "AccountStateProjection");

    private void OnInternalRefreshRequested(object? sender, InternalRefreshRequestedEventArgs eventArgs) =>
        Observe(ProjectAndApplyAsync(_lifetimeCancellation.Token), "InternalRefreshProjection");

    private void OnSettingsChanged(object? sender, ApplicationSettingsChangedEventArgs eventArgs)
    {
        _settings = eventArgs.Current;
        var projectionSections = SettingsSection.Display
            | SettingsSection.Accounts
            | SettingsSection.ColorRules;
        if ((eventArgs.ChangedSections & projectionSections) != 0)
        {
            Observe(ProjectAndApplyAsync(_lifetimeCancellation.Token), "SettingsProjection");
        }
    }

    private async Task ProjectAndApplyAsync(CancellationToken cancellationToken)
    {
        await _applySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = _syncService.CurrentSnapshot;
            var settings = _settings;
            var display = new DisplaySettings(
                settings.Display.Days,
                settings.Display.DefaultEventColor);
            var localTimeZone = _localTimeZoneProvider.GetCurrent();
            var presentation = await Task.Run(
                () => _presentationService.BuildSnapshot(
                    source,
                    display,
                    settings.ColorRules,
                    localTimeZone),
                cancellationToken).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                ApplyPresentation(source, presentation);
                return Task.CompletedTask;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applySemaphore.Release();
        }
    }

    private void ApplyPresentation(SyncSnapshot source, PresentationSnapshot presentation)
    {
        FontSizeDip = _settings.Display.FontSizeDip;
        StatusHeight = LayoutMetrics.CalculateStatusHeight(FontSizeDip);
        WarningButtonHeight = LayoutMetrics.CalculateWarningButtonHeight(FontSizeDip);
        var verticalPadding = _settings.Display.Density switch
        {
            DisplayDensity.Compact => 2d,
            DisplayDensity.Comfortable => 8d,
            _ => 5d,
        };
        EventRowMargin = new Thickness(10d, verticalPadding, 10d, verticalPadding);
        UpdateEventRowMinHeight();
        var before = _viewport.Capture(
            TimelineItems.OfType<EventRowViewModel>().Select(item => item.Key).ToArray(),
            EventDetails?.Key);
        var diff = _snapshotDiffer.Apply(
            TimelineItems,
            presentation,
            presented => new EventRowViewModel(
                presented,
                _textService,
                _brushCache,
                _uriLauncher,
                OpenDetails),
            day => new DayHeaderItemViewModel(day.Date, _textService.Format(day.Header)));

        if (EventDetails is not null)
        {
            var updated = TimelineItems
                .OfType<EventRowViewModel>()
                .FirstOrDefault(item => item.Key == EventDetails.Key);
            if (updated is null)
            {
                CloseDetails();
            }
            else
            {
                EventDetails.UpdateFrom(updated.Presented);
            }
        }

        var targetIds = GetDisplayTargetAccountIds(source);
        var accountStates = _syncService.AccountStates;
        var completedNow = targetIds.Count > 0 && targetIds.All(id =>
        {
            var state = accountStates.FirstOrDefault(value => value.InternalAccountId == id);
            return state is not null && state.Status is not SyncStatus.NotStarted and not SyncStatus.Syncing;
        });
        var firstCompletion = !_initialSyncCompleted && completedNow;
        _initialSyncCompleted |= completedNow;
        UpdateStatus(source);

        var upcomingKey = presentation.Events.FirstOrDefault()?.Key;
        var restoration = ViewportRestorationPlanner.Create(
            before,
            diff.PreviousEventKeys,
            diff.CurrentEventKeys,
            firstCompletion,
            upcomingKey);
        if (restoration.CloseDetails)
        {
            CloseDetails();
        }

        _viewport.Restore(restoration);
    }

    private void UpdateStatus(SyncSnapshot source)
    {
        UpdateFixedText();
        var targetIds = GetDisplayTargetAccountIds(source);
        var states = _syncService.AccountStates
            .Where(state => targetIds.Contains(state.InternalAccountId))
            .ToArray();
        IsSyncing = states.Any(state => state.Status == SyncStatus.Syncing);
        UpdateWarnings(source, states);
        LastUpdateText = CreateLastUpdateText(targetIds, states);

        ContentState = source.Accounts.Count == 0
            ? MainContentState.Unregistered
            : TimelineItems.Count > 0
                ? MainContentState.Timeline
                : IsSyncing || states.Length < targetIds.Count
                    || states.Any(state => state.Status == SyncStatus.NotStarted)
                    ? MainContentState.Loading
                    : AccountWarnings.Count > 0
                        ? MainContentState.Error
                        : MainContentState.Empty;
        StateMessage = ContentState switch
        {
            MainContentState.Unregistered => _textService.Get(UiResourceKeys.Unregistered),
            MainContentState.Loading => _textService.Get(UiResourceKeys.Loading),
            MainContentState.Error => _textService.Get(UiResourceKeys.Error),
            MainContentState.Empty => _textService.Get(UiResourceKeys.Empty, _settings.Display.Days),
            _ => string.Empty,
        };
    }

    private void UpdateWarnings(
        SyncSnapshot source,
        IReadOnlyCollection<AccountSyncState> targetStates)
    {
        AccountWarnings.Clear();
        foreach (var state in targetStates.Where(state => state.HasWarning))
        {
            var account = source.Accounts.FirstOrDefault(value => value.InternalAccountId == state.InternalAccountId);
            if (account is null)
            {
                continue;
            }

            var messageKey = state.Status switch
            {
                SyncStatus.AuthenticationRequired => UiResourceKeys.AccountAuthenticationError,
                SyncStatus.RateLimited => UiResourceKeys.AccountRateLimited,
                _ => UiResourceKeys.AccountSyncError,
            };
            AccountWarnings.Add(new AccountWarningViewModel(
                account.InternalAccountId,
                _textService.Get(messageKey, account.DisplayName),
                _textService.Get(UiResourceKeys.Reauthenticate),
                _reauthenticationService,
                _textService));
        }

        AccountWarningsText = string.Join(Environment.NewLine, AccountWarnings.Select(value => value.Message));
        OnPropertyChanged(nameof(HasAccountWarnings));
    }

    private string CreateLastUpdateText(
        IReadOnlySet<Guid> targetIds,
        IReadOnlyCollection<AccountSyncState> states)
    {
        if (targetIds.Count == 0)
        {
            return string.Empty;
        }

        if (states.Count != targetIds.Count
            || states.Any(state => state.Status != SyncStatus.Succeeded
                || !state.LastFullySuccessfulSyncUtc.HasValue))
        {
            return _textService.Get(UiResourceKeys.LastUpdateUnavailable);
        }

        var earliest = states.Min(state => state.LastFullySuccessfulSyncUtc!.Value);
        var localTimeZone = _localTimeZoneProvider.GetCurrent();
        var local = TimeZoneInfo.ConvertTime(earliest, localTimeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), localTimeZone);
        return _textService.Get(
            local.Date == nowLocal.Date
                ? UiResourceKeys.LastUpdateToday
                : UiResourceKeys.LastUpdateOtherDay,
            local);
    }

    private static IReadOnlySet<Guid> GetDisplayTargetAccountIds(SyncSnapshot source)
    {
        var visibleAccounts = source.CalendarSelections
            .Where(selection => selection.IsVisible)
            .Select(selection => selection.InternalAccountId)
            .ToHashSet();
        return source.Accounts
            .Where(account => account.Enabled && visibleAccounts.Contains(account.InternalAccountId))
            .Select(account => account.InternalAccountId)
            .ToHashSet();
    }

    private void UpdateFixedText()
    {
        var localNow = TimeZoneInfo.ConvertTime(
            _timeProvider.GetUtcNow(),
            _localTimeZoneProvider.GetCurrent());
        CurrentTimeText = _textService.Get(UiResourceKeys.CurrentTime, localNow);
        ManualRefreshText = _textService.Get(UiResourceKeys.ManualRefresh);
        UpdatingText = _textService.Get(UiResourceKeys.Updating);
        AddGoogleAccountText = _textService.Get(UiResourceKeys.AddGoogleAccount);
        AddMicrosoftAccountText = _textService.Get(UiResourceKeys.AddMicrosoftAccount);
    }

    private void UpdateEventRowMinHeight() => EventRowMinHeight =
        LayoutMetrics.CalculateEventRowMinHeight(
            FontSizeDip,
            EventRowMargin.Top,
            _pixelsPerDip);

    private static async void Observe(Task task, string stage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                "UiProjectionFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }

}

public sealed partial class AccountWarningViewModel : ObservableObject
{
    public AccountWarningViewModel(
        Guid internalAccountId,
        string message,
        string reauthenticateText,
        IAccountReauthenticationService service,
        IUiTextService textService)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The account ID cannot be empty.", nameof(internalAccountId));
        }

        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(reauthenticateText);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(textService);
        InternalAccountId = internalAccountId;
        Message = message;
        ReauthenticateText = reauthenticateText;
        ReauthenticateCommand = new AsyncRelayCommand(
            cancellationToken => ReauthenticateAsync(service, textService, cancellationToken),
            () => service.IsAvailable);
    }

    public Guid InternalAccountId { get; }

    public string Message { get; }

    public string ReauthenticateText { get; }

    public IAsyncRelayCommand ReauthenticateCommand { get; }

    public bool HasOperationMessage => OperationMessage.Length > 0;

    [ObservableProperty]
    private string _operationMessage = string.Empty;

    private async Task ReauthenticateAsync(
        IAccountReauthenticationService service,
        IUiTextService textService,
        CancellationToken cancellationToken)
    {
        OperationMessage = string.Empty;
        try
        {
            await service.ReauthenticateAsync(
                InternalAccountId,
                cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AccountInteractionException exception)
        {
            Log.Warning(
                "AccountUiOperationFailed {Operation} {InternalAccountId} {ErrorCategory}",
                "ReauthenticateFromMainWindow",
                InternalAccountId,
                exception.Failure);
            OperationMessage = AccountInteractionUiText.GetFailureMessage(
                textService,
                exception.Failure);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountUiOperationFailed {Operation} {InternalAccountId} {ErrorCategory}",
                "ReauthenticateFromMainWindow",
                InternalAccountId,
                exception.GetType().Name);
            OperationMessage = AccountInteractionUiText.GetFailureMessage(
                textService,
                AccountInteractionFailure.AuthenticationFailed);
        }
    }

    partial void OnOperationMessageChanged(string value) =>
        OnPropertyChanged(nameof(HasOperationMessage));
}
