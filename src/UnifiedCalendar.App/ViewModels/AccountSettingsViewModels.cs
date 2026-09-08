using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;

namespace UnifiedCalendar.App.ViewModels;

public sealed class AccountRegisteredEventArgs : EventArgs
{
    public AccountRegisteredEventArgs(AccountRegistrationResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public AccountRegistrationResult Result { get; }
}

public sealed partial class AccountSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly ICalendarSyncService _syncService;
    private readonly IAccountRegistrationService _registrationService;
    private readonly IAccountReauthenticationService _reauthenticationService;
    private readonly ITokenStore _tokenStore;
    private readonly ICacheStore _cacheStore;
    private readonly IAccountDeleteConfirmationService _deleteConfirmationService;
    private readonly IUiTextService _textService;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalTimeZoneProvider _localTimeZoneProvider;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly AsyncRelayCommand _addGoogleAccountCommand;
    private readonly AsyncRelayCommand _addMicrosoftAccountCommand;
    private readonly ConcurrentDictionary<Guid, byte> _busyAccountIds = new();
    private bool _disposed;

    public AccountSettingsViewModel(
        IApplicationSettingsService settingsService,
        ICalendarSyncService syncService,
        IAccountRegistrationService registrationService,
        IAccountReauthenticationService reauthenticationService,
        ITokenStore tokenStore,
        ICacheStore cacheStore,
        IUiTextService textService,
        IUiDispatcher dispatcher,
        TimeProvider timeProvider,
        ILocalTimeZoneProvider localTimeZoneProvider,
        IAccountDeleteConfirmationService? deleteConfirmationService = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _registrationService = registrationService
            ?? throw new ArgumentNullException(nameof(registrationService));
        _reauthenticationService = reauthenticationService
            ?? throw new ArgumentNullException(nameof(reauthenticationService));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        _deleteConfirmationService = deleteConfirmationService
            ?? DeclineAccountDeleteConfirmationService.Instance;
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _localTimeZoneProvider = localTimeZoneProvider
            ?? throw new ArgumentNullException(nameof(localTimeZoneProvider));

        HeadingText = _textService.Get(UiResourceKeys.SettingsAccountsHeading);
        EmptyText = _textService.Get(UiResourceKeys.SettingsAccountsEmpty);
        AddGoogleText = _textService.Get(UiResourceKeys.AddGoogleAccount);
        AddMicrosoftText = _textService.Get(UiResourceKeys.AddMicrosoftAccount);
        var unavailable = _textService.Get(UiResourceKeys.SettingsAccountsProviderUnavailable);
        GoogleUnavailableToolTip = registrationService.IsProviderAvailable(ProviderKind.Google)
            ? null
            : unavailable;
        MicrosoftUnavailableToolTip = registrationService.IsProviderAvailable(ProviderKind.Microsoft)
            ? null
            : unavailable;

        _addGoogleAccountCommand = new AsyncRelayCommand(
            cancellationToken => AddAccountAsync(ProviderKind.Google, cancellationToken),
            () => CanAdd(ProviderKind.Google));
        _addMicrosoftAccountCommand = new AsyncRelayCommand(
            cancellationToken => AddAccountAsync(ProviderKind.Microsoft, cancellationToken),
            () => CanAdd(ProviderKind.Microsoft));
        _settingsService.SettingsChanged += OnSettingsChanged;
        _syncService.AccountStateChanged += OnAccountStateChanged;
    }

    public ObservableCollection<AccountListItemViewModel> Accounts { get; } = [];

    public event EventHandler<AccountRegisteredEventArgs>? AccountRegistered;

    public string HeadingText { get; }

    public string EmptyText { get; }

    public string AddGoogleText { get; }

    public string AddMicrosoftText { get; }

    public string? GoogleUnavailableToolTip { get; }

    public string? MicrosoftUnavailableToolTip { get; }

    public IAsyncRelayCommand AddGoogleAccountCommand => _addGoogleAccountCommand;

    public IAsyncRelayCommand AddMicrosoftAccountCommand => _addMicrosoftAccountCommand;

    public bool HasAccounts => Accounts.Count > 0;

    public bool IsEmpty => !HasAccounts;

    public bool IsAuthenticationInProgress => IsAddingGoogle || IsAddingMicrosoft
        || Accounts.Any(account => account.IsBusy);

    internal int SettingsApplicationCount { get; private set; }

    [ObservableProperty]
    private bool _isAddingGoogle;

    [ObservableProperty]
    private bool _isAddingMicrosoft;

    [ObservableProperty]
    private string _authenticationProgressText = string.Empty;

    [ObservableProperty]
    private string _operationMessage = string.Empty;

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplySettings(settings);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _syncService.AccountStateChanged -= OnAccountStateChanged;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    internal async Task ToggleEnabledAsync(
        AccountListItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        await RunRowOperationAsync(item, async linkedToken =>
        {
            var enable = !item.Enabled;
            await _settingsService.UpdateAsync(current =>
            {
                var accounts = current.Accounts.Select(account =>
                    account.InternalAccountId == item.InternalAccountId
                        ? CopyWithEnabled(account, enable)
                        : account);
                return CopyWithAccounts(current, accounts);
            }, linkedToken).ConfigureAwait(true);
            await _syncService.RefreshFromSettingsAsync(CancellationToken.None).ConfigureAwait(true);

            if (enable)
            {
                _ = ObserveSyncAsync(
                    _syncService.RequestSyncAsync(
                        SyncTriggerReason.Manual,
                        [item.InternalAccountId],
                        CancellationToken.None),
                    "EnableSync",
                    item.Provider,
                    item.InternalAccountId);
            }
        }, cancellationToken).ConfigureAwait(true);
    }

    internal Task ReauthenticateAsync(
        AccountListItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunRowOperationAsync(item, linkedToken =>
            _reauthenticationService.ReauthenticateAsync(item.InternalAccountId, linkedToken),
            cancellationToken);
    }

    internal async Task DeleteAsync(
        AccountListItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_deleteConfirmationService.ConfirmDelete(item.DisplayIdentity))
        {
            return;
        }

        await RunRowOperationAsync(item, async linkedToken =>
        {
            await _settingsService.UpdateAsync(
                current => CopyWithAccounts(
                    current,
                    current.Accounts.Where(account =>
                        account.InternalAccountId != item.InternalAccountId)),
                linkedToken).ConfigureAwait(true);

            var cleanupFailed = false;
            try
            {
                await _syncService.RefreshFromSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                cleanupFailed = true;
                Log.Warning(
                    "AccountDeleteCleanupFailed {Stage} {ErrorCategory}",
                    "RefreshSettings",
                    exception.GetType().Name);
            }

            cleanupFailed |= !await TryCleanupAsync(
                "TokenRemove",
                item,
                () => _tokenStore.RemoveAsync(
                    item.Provider,
                    item.InternalAccountId,
                    CancellationToken.None)).ConfigureAwait(true);
            cleanupFailed |= !await TryCleanupAsync(
                "CacheRemove",
                item,
                () => _cacheStore.RemoveAccountAsync(
                    item.InternalAccountId,
                    CancellationToken.None)).ConfigureAwait(true);

            Log.Information(
                "AccountDeleted {Provider} {InternalAccountId}",
                item.Provider,
                item.InternalAccountId);
            if (!_disposed)
            {
                OperationMessage = cleanupFailed
                    ? _textService.Get(UiResourceKeys.SettingsAccountsCleanupWarning)
                    : string.Empty;
            }
        }, cancellationToken).ConfigureAwait(true);
    }

    private async Task AddAccountAsync(ProviderKind provider, CancellationToken cancellationToken)
    {
        SetAdding(provider, true);
        OperationMessage = string.Empty;
        AuthenticationProgressText = _textService.Get(
            UiResourceKeys.SettingsAccountsAuthenticationProgress,
            ProviderText(provider));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            var result = await _registrationService
                .AddAccountAsync(provider, linked.Token)
                .ConfigureAwait(true);
            if (!_disposed && result is not null)
            {
                AccountRegistered?.Invoke(this, new AccountRegisteredEventArgs(result));
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (AccountInteractionException exception)
        {
            Log.Warning(
                "AccountUiOperationFailed {Operation} {Provider} {ErrorCategory}",
                "Add",
                provider,
                exception.Failure);
            if (!_disposed)
            {
                OperationMessage = AccountInteractionUiText.GetFailureMessage(
                    _textService,
                    exception.Failure);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountUiOperationFailed {Operation} {Provider} {ErrorCategory}",
                "Add",
                provider,
                exception.GetType().Name);
            if (!_disposed)
            {
                OperationMessage = _textService.Get(UiResourceKeys.SettingsAccountsOperationFailed);
            }
        }
        finally
        {
            if (!_disposed)
            {
                SetAdding(provider, false);
                AuthenticationProgressText = string.Empty;
            }
        }
    }

    private async Task RunRowOperationAsync(
        AccountListItemViewModel item,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (!_busyAccountIds.TryAdd(item.InternalAccountId, 0))
        {
            return;
        }

        item.SetBusy(true);
        OperationMessage = string.Empty;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            await operation(linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (AccountInteractionException exception)
        {
            Log.Warning(
                "AccountUiOperationFailed {Operation} {Provider} {InternalAccountId} {ErrorCategory}",
                "Account",
                item.Provider,
                item.InternalAccountId,
                exception.Failure);
            if (!_disposed)
            {
                OperationMessage = AccountInteractionUiText.GetFailureMessage(
                    _textService,
                    exception.Failure);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountUiOperationFailed {Operation} {Provider} {InternalAccountId} {ErrorCategory}",
                "Account",
                item.Provider,
                item.InternalAccountId,
                exception.GetType().Name);
            if (!_disposed)
            {
                OperationMessage = _textService.Get(UiResourceKeys.SettingsAccountsOperationFailed);
            }
        }
        finally
        {
            _busyAccountIds.TryRemove(item.InternalAccountId, out _);
            if (!_disposed)
            {
                item.SetBusy(false);
                var current = Accounts.FirstOrDefault(account =>
                    account.InternalAccountId == item.InternalAccountId);
                if (current is not null && !ReferenceEquals(current, item))
                {
                    current.SetBusy(false);
                }

                OnPropertyChanged(nameof(IsAuthenticationInProgress));
            }
        }
    }

    private async Task<bool> TryCleanupAsync(
        string stage,
        AccountListItemViewModel item,
        Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(true);
            return true;
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountDeleteCleanupFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
            return false;
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        SettingsApplicationCount++;
        var states = _syncService.AccountStates.ToDictionary(
            state => state.InternalAccountId);
        var existing = Accounts.ToDictionary(account => account.InternalAccountId);
        var ordered = settings.Accounts
            .OrderBy(account => ProviderOrder(account.Provider))
            .ThenBy(account => account.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(account => account.Email, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(account => account.InternalAccountId)
            .Select(account =>
            {
                AccountListItemViewModel item;
                if (existing.TryGetValue(account.InternalAccountId, out var current)
                    && current.Provider == account.Provider)
                {
                    item = current;
                    item.UpdateAccount(
                        account,
                        states.GetValueOrDefault(account.InternalAccountId));
                }
                else
                {
                    item = new AccountListItemViewModel(
                        this,
                        account,
                        states.GetValueOrDefault(account.InternalAccountId),
                        _registrationService.IsProviderAvailable(account.Provider),
                        _textService,
                        _timeProvider,
                        _localTimeZoneProvider);
                }

                item.SetBusy(_busyAccountIds.ContainsKey(account.InternalAccountId));
                return item;
            })
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            if (index < Accounts.Count && ReferenceEquals(Accounts[index], ordered[index]))
            {
                continue;
            }

            var existingIndex = Accounts.IndexOf(ordered[index]);
            if (existingIndex >= 0)
            {
                Accounts.Move(existingIndex, index);
            }
            else
            {
                Accounts.Insert(index, ordered[index]);
            }
        }

        while (Accounts.Count > ordered.Length)
        {
            Accounts.RemoveAt(Accounts.Count - 1);
        }

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsAuthenticationInProgress));
    }

    private void OnSettingsChanged(object? sender, ApplicationSettingsChangedEventArgs eventArgs)
    {
        if (_disposed || !eventArgs.ChangedSections.HasFlag(SettingsSection.Accounts))
        {
            return;
        }

        Observe(_dispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                ApplySettings(eventArgs.Current);
            }

            return Task.CompletedTask;
        }), "SettingsChanged");
    }

    private void OnAccountStateChanged(object? sender, AccountSyncStateChangedEventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        Observe(_dispatcher.InvokeAsync(() =>
        {
            Accounts.FirstOrDefault(account =>
                account.InternalAccountId == eventArgs.State.InternalAccountId)
                ?.UpdateState(eventArgs.State);
            return Task.CompletedTask;
        }), "AccountStateChanged");
    }

    private bool CanAdd(ProviderKind provider) => !_disposed
        && _registrationService.IsProviderAvailable(provider)
        && (provider switch
        {
            ProviderKind.Google => !IsAddingGoogle,
            ProviderKind.Microsoft => !IsAddingMicrosoft,
            _ => false,
        });

    private void SetAdding(ProviderKind provider, bool value)
    {
        if (provider == ProviderKind.Google)
        {
            IsAddingGoogle = value;
        }
        else if (provider == ProviderKind.Microsoft)
        {
            IsAddingMicrosoft = value;
        }

        _addGoogleAccountCommand.NotifyCanExecuteChanged();
        _addMicrosoftAccountCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAuthenticationInProgress));
    }

    private string ProviderText(ProviderKind provider) => _textService.Get(provider switch
    {
        ProviderKind.Google => UiResourceKeys.ProviderGoogle,
        ProviderKind.Microsoft => UiResourceKeys.ProviderMicrosoft,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "The provider is not defined."),
    });

    private static int ProviderOrder(ProviderKind provider) => provider switch
    {
        ProviderKind.Google => 0,
        ProviderKind.Microsoft => 1,
        _ => int.MaxValue,
    };

    private static AccountSettings CopyWithEnabled(AccountSettings account, bool enabled) => new(
        account.InternalAccountId,
        account.Provider,
        account.ProviderSubjectId,
        account.DisplayName,
        account.Email,
        enabled,
        account.TokenRef,
        account.Calendars);

    private static AppSettings CopyWithAccounts(
        AppSettings current,
        IEnumerable<AccountSettings> accounts) => new(
            current.Display,
            current.Sync,
            current.General,
            current.Windows,
            accounts,
            current.ColorRules);

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
                "AccountUiProjectionFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }

    private static async Task ObserveSyncAsync(
        Task task,
        string stage,
        ProviderKind provider,
        Guid internalAccountId)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountSyncRequestFailed {Stage} {Provider} {InternalAccountId} {ErrorCategory}",
                stage,
                provider,
                internalAccountId,
                exception.GetType().Name);
        }
    }

    private sealed class DeclineAccountDeleteConfirmationService : IAccountDeleteConfirmationService
    {
        public static DeclineAccountDeleteConfirmationService Instance { get; } = new();

        public bool ConfirmDelete(string accountDisplayName) => false;
    }
}

public sealed partial class AccountListItemViewModel : ObservableObject
{
    private readonly AccountSettingsViewModel _owner;
    private readonly IUiTextService _textService;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalTimeZoneProvider _localTimeZoneProvider;
    private readonly AsyncRelayCommand _reauthenticateCommand;
    private readonly AsyncRelayCommand _toggleEnabledCommand;
    private readonly AsyncRelayCommand _deleteCommand;

    public AccountListItemViewModel(
        AccountSettingsViewModel owner,
        AccountSettings account,
        AccountSyncState? state,
        bool providerAvailable,
        IUiTextService textService,
        TimeProvider timeProvider,
        ILocalTimeZoneProvider localTimeZoneProvider)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(account);
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _localTimeZoneProvider = localTimeZoneProvider
            ?? throw new ArgumentNullException(nameof(localTimeZoneProvider));
        InternalAccountId = account.InternalAccountId;
        Provider = account.Provider;
        ProviderText = textService.Get(account.Provider switch
        {
            ProviderKind.Google => UiResourceKeys.ProviderGoogle,
            ProviderKind.Microsoft => UiResourceKeys.ProviderMicrosoft,
            _ => throw new ArgumentOutOfRangeException(nameof(account), "The provider is not defined."),
        });
        ProviderAvailable = providerAvailable;
        ProviderUnavailableToolTip = providerAvailable
            ? null
            : textService.Get(UiResourceKeys.SettingsAccountsProviderUnavailable);
        ReauthenticateText = textService.Get(UiResourceKeys.Reauthenticate);
        DeleteText = textService.Get(UiResourceKeys.SettingsAccountsDelete);
        BusyText = textService.Get(UiResourceKeys.SettingsAccountsOperationProgress);
        _reauthenticateCommand = new AsyncRelayCommand(
            cancellationToken => owner.ReauthenticateAsync(this, cancellationToken),
            CanReauthenticate);
        _toggleEnabledCommand = new AsyncRelayCommand(
            cancellationToken => owner.ToggleEnabledAsync(this, cancellationToken),
            () => !IsBusy);
        _deleteCommand = new AsyncRelayCommand(
            cancellationToken => owner.DeleteAsync(this, cancellationToken),
            () => !IsBusy);
        UpdateAccount(account, state);
    }

    public Guid InternalAccountId { get; }

    public ProviderKind Provider { get; }

    public string ProviderText { get; }

    public bool ProviderAvailable { get; }

    public string? ProviderUnavailableToolTip { get; }

    public string ReauthenticateText { get; }

    public string DeleteText { get; }

    public string BusyText { get; }

    public IAsyncRelayCommand ReauthenticateCommand => _reauthenticateCommand;

    public IAsyncRelayCommand ToggleEnabledCommand => _toggleEnabledCommand;

    public IAsyncRelayCommand DeleteCommand => _deleteCommand;

    public bool HasAuthenticationError => Status == SyncStatus.AuthenticationRequired;

    [ObservableProperty]
    private string _displayIdentity = string.Empty;

    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _enabledText = string.Empty;

    [ObservableProperty]
    private string _enabledStateText = string.Empty;

    [ObservableProperty]
    private SyncStatus _status;

    [ObservableProperty]
    private string _syncStatusText = string.Empty;

    [ObservableProperty]
    private string _lastSuccessfulSyncText = string.Empty;

    [ObservableProperty]
    private string _authenticationStateText = string.Empty;

    [ObservableProperty]
    private string _syncTargetReasonText = string.Empty;

    private bool _isSyncTarget;

    internal void UpdateAccount(AccountSettings account, AccountSyncState? state)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.InternalAccountId != InternalAccountId || account.Provider != Provider)
        {
            throw new ArgumentException("The account identity cannot change for an existing row.", nameof(account));
        }

        DisplayIdentity = string.IsNullOrWhiteSpace(account.DisplayName)
            ? account.Email
            : account.DisplayName;
        Email = account.Email;
        Enabled = account.Enabled;
        EnabledStateText = _textService.Get(
            account.Enabled
                ? UiResourceKeys.SettingsAccountsStateEnabled
                : UiResourceKeys.SettingsAccountsStateDisabled);
        _isSyncTarget = AccountSyncTargetPolicy.IsSyncTarget(account);
        SyncTargetReasonText = account.Enabled && account.Calendars.All(calendar => !calendar.IsVisible)
            ? _textService.Get(UiResourceKeys.SettingsAccountsAllCalendarsOff)
            : string.Empty;
        UpdateEnabledText();
        UpdateState(state);
    }

    internal void UpdateState(AccountSyncState? state)
    {
        Status = state?.Status ?? SyncStatus.NotStarted;
        SyncStatusText = _textService.Get(
            !_isSyncTarget
                ? UiResourceKeys.SettingsAccountsNotSyncTarget
                : Status switch
                {
                    SyncStatus.NotStarted or SyncStatus.Syncing => UiResourceKeys.SettingsAccountsSyncing,
                    SyncStatus.Succeeded => UiResourceKeys.SettingsAccountsSyncSucceeded,
                    SyncStatus.AuthenticationRequired => UiResourceKeys.SettingsAccountsAuthenticationError,
                    SyncStatus.RateLimited => UiResourceKeys.SettingsAccountsRateLimited,
                    SyncStatus.Cancelled => UiResourceKeys.SettingsAccountsNotUpdated,
                    _ => UiResourceKeys.SettingsAccountsSyncError,
                });
        AuthenticationStateText = HasAuthenticationError
            ? _textService.Get(UiResourceKeys.SettingsAccountsAuthenticationError)
            : string.Empty;
        LastSuccessfulSyncText = FormatLastSuccess(state?.LastFullySuccessfulSyncUtc);
        OnPropertyChanged(nameof(HasAuthenticationError));
    }

    internal void SetBusy(bool value)
    {
        IsBusy = value;
        _reauthenticateCommand.NotifyCanExecuteChanged();
        _toggleEnabledCommand.NotifyCanExecuteChanged();
        _deleteCommand.NotifyCanExecuteChanged();
    }

    private bool CanReauthenticate() => ProviderAvailable && !IsBusy;

    private void UpdateEnabledText() => EnabledText = _textService.Get(
        Enabled
            ? UiResourceKeys.SettingsAccountsDisable
            : UiResourceKeys.SettingsAccountsEnable);

    private string FormatLastSuccess(DateTimeOffset? lastSuccessUtc)
    {
        if (!lastSuccessUtc.HasValue)
        {
            return _textService.Get(UiResourceKeys.SettingsAccountsLastSuccessUnavailable);
        }

        var timeZone = _localTimeZoneProvider.GetCurrent();
        var local = TimeZoneInfo.ConvertTime(lastSuccessUtc.Value, timeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZone);
        return _textService.Get(
            local.Date == nowLocal.Date
                ? UiResourceKeys.SettingsAccountsLastSuccessToday
                : UiResourceKeys.SettingsAccountsLastSuccessOtherDay,
            local);
    }
}
