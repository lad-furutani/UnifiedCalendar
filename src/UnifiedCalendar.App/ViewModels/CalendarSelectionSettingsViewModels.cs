using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Sync;

namespace UnifiedCalendar.App.ViewModels;

public sealed partial class CalendarSelectionSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly ICalendarSyncService _syncService;
    private readonly ICalendarCatalogService _catalogService;
    private readonly IUiTextService _textService;
    private readonly IUiDispatcher _dispatcher;
    private readonly BrushCache _brushCache;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<CalendarDescriptor>>
        _lastSuccessfulCatalogs = new();
    private readonly ConcurrentDictionary<CalendarRowKey, byte> _busyRows = new();
    private Guid? _postRegistrationAccountId;
    private bool _skipNextActivationRefresh;
    private bool _disposed;

    public CalendarSelectionSettingsViewModel(
        IApplicationSettingsService settingsService,
        ICalendarSyncService syncService,
        ICalendarCatalogService catalogService,
        IUiTextService textService,
        IUiDispatcher dispatcher,
        BrushCache brushCache)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _brushCache = brushCache ?? throw new ArgumentNullException(nameof(brushCache));

        HeadingText = textService.Get(UiResourceKeys.SettingsCalendarsHeading);
        RefreshText = textService.Get(UiResourceKeys.SettingsCalendarsRefresh);
        LoadingText = textService.Get(UiResourceKeys.SettingsCalendarsLoading);
        EmptyText = textService.Get(UiResourceKeys.SettingsCalendarsEmpty);
        BackText = textService.Get(UiResourceKeys.SettingsCalendarsBackToAccounts);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRefresh);
        BackCommand = new RelayCommand(ReturnToAccounts, () => IsPostRegistrationFlow);
        _settingsService.SettingsChanged += OnSettingsChanged;
        _syncService.AccountStateChanged += OnAccountStateChanged;
    }

    public event EventHandler? BackToAccountsRequested;

    public ObservableCollection<CalendarAccountGroupViewModel> AccountGroups { get; } = [];

    public string HeadingText { get; }

    public string RefreshText { get; }

    public string LoadingText { get; }

    public string EmptyText { get; }

    public string BackText { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand BackCommand { get; }

    public bool IsEmpty => AccountGroups.Count == 0 && !IsLoading;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isPostRegistrationFlow;

    [ObservableProperty]
    private string _operationMessage = string.Empty;

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ApplySettingsToExistingRows(settings);
    }

    public Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (_skipNextActivationRefresh)
        {
            _skipNextActivationRefresh = false;
            return Task.CompletedTask;
        }

        return RefreshAsync(cancellationToken);
    }

    public void ShowRegisteredAccount(AccountRegistrationResult registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ThrowIfDisposed();
        var descriptors = DistinctCalendars(registration.Calendars);
        _lastSuccessfulCatalogs[registration.Account.InternalAccountId] = descriptors;
        _postRegistrationAccountId = registration.Account.InternalAccountId;
        _skipNextActivationRefresh = true;
        IsPostRegistrationFlow = true;
        BackCommand.NotifyCanExecuteChanged();
        ApplyCatalogs(
            [new CalendarAccountCatalog(registration.Account, ToEntries(descriptors), string.Empty)]);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        if (!await _refreshGate.WaitAsync(0, linked.Token).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IsLoading = true;
                OperationMessage = string.Empty;
                return Task.CompletedTask;
            }, linked.Token).ConfigureAwait(false);

            var settings = await _settingsService.LoadAsync(linked.Token).ConfigureAwait(false);
            var accounts = OrderedAccounts(settings.Accounts)
                .Where(account => !_postRegistrationAccountId.HasValue
                    || account.InternalAccountId == _postRegistrationAccountId.Value)
                .ToArray();
            var catalogs = new List<CalendarAccountCatalog>(accounts.Length);
            foreach (var account in accounts)
            {
                catalogs.Add(await LoadCatalogAsync(account, linked.Token).ConfigureAwait(false));
            }

            await _dispatcher.InvokeAsync(() =>
            {
                ApplyCatalogs(catalogs);
                return Task.CompletedTask;
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                "CalendarSelectionLoadFailed {Stage} {ErrorCategory}",
                "Settings",
                exception.GetType().Name);
            if (!_disposed)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    OperationMessage = _textService.Get(UiResourceKeys.SettingsCalendarsDiscoveryFailed);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshGate.Release();
            if (!_disposed)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    IsLoading = false;
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
        }
    }

    internal async Task ToggleVisibilityAsync(
        CalendarListItemViewModel item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var key = new CalendarRowKey(item.InternalAccountId, item.CalendarId);
        if (!_busyRows.TryAdd(key, 0))
        {
            return;
        }

        item.SetBusy(true);
        var makeVisible = !item.IsVisible;
        if (makeVisible && !item.CanReadEvents)
        {
            _busyRows.TryRemove(key, out _);
            item.SetBusy(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        try
        {
            await _settingsService.UpdateAsync(
                current => CopyWithCalendarVisibility(
                    current,
                    item.InternalAccountId,
                    item.CalendarId,
                    makeVisible),
                linked.Token).ConfigureAwait(true);
            await _syncService.RefreshFromSettingsAsync(CancellationToken.None).ConfigureAwait(true);
            if (makeVisible)
            {
                _ = ObserveSyncAsync(
                    _syncService.RequestSyncAsync(
                        SyncTriggerReason.Manual,
                        [item.InternalAccountId],
                        CancellationToken.None),
                    item.Provider,
                    item.InternalAccountId);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                "CalendarVisibilityUpdateFailed {Provider} {InternalAccountId} {ErrorCategory}",
                item.Provider,
                item.InternalAccountId,
                exception.GetType().Name);
            if (!_disposed)
            {
                OperationMessage = _textService.Get(UiResourceKeys.SettingsCalendarsUpdateFailed);
                item.NotifyVisibilityUnchanged();
                var current = FindRow(key);
                if (current is not null && !ReferenceEquals(current, item))
                {
                    current.NotifyVisibilityUnchanged();
                }
            }
        }
        finally
        {
            _busyRows.TryRemove(key, out _);
            if (!_disposed)
            {
                item.SetBusy(false);
                var current = FindRow(key);
                if (current is not null && !ReferenceEquals(current, item))
                {
                    current.SetBusy(false);
                }
            }
        }
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

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        RefreshCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsPostRegistrationFlowChanged(bool value) =>
        BackCommand.NotifyCanExecuteChanged();

    private async Task<CalendarAccountCatalog> LoadCatalogAsync(
        AccountSettings account,
        CancellationToken cancellationToken)
    {
        try
        {
            var descriptors = DistinctCalendars(await _catalogService
                .ListCalendarsAsync(account, cancellationToken)
                .ConfigureAwait(false));
            _lastSuccessfulCatalogs[account.InternalAccountId] = descriptors;
            return new CalendarAccountCatalog(account, ToEntries(descriptors), string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(
                "CalendarCatalogFetchFailed {Provider} {InternalAccountId} {ErrorCategory}",
                account.Provider,
                account.InternalAccountId,
                exception.GetType().Name);
            if (_lastSuccessfulCatalogs.TryGetValue(account.InternalAccountId, out var previous))
            {
                return new CalendarAccountCatalog(
                    account,
                    ToEntries(previous),
                    _textService.Get(UiResourceKeys.SettingsCalendarsDiscoveryFailed));
            }

            return new CalendarAccountCatalog(
                account,
                await CreateFallbackEntriesAsync(account, cancellationToken).ConfigureAwait(false),
                _textService.Get(UiResourceKeys.SettingsCalendarsDiscoveryFailed));
        }
    }

    private async Task<IReadOnlyList<CalendarCatalogEntry>> CreateFallbackEntriesAsync(
        AccountSettings account,
        CancellationToken cancellationToken)
    {
        AccountCache? cache = null;
        try
        {
            cache = await _catalogService
                .LoadCacheAsync(account.InternalAccountId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(
                "CalendarCatalogCacheLoadFailed {Provider} {InternalAccountId} {ErrorCategory}",
                account.Provider,
                account.InternalAccountId,
                exception.GetType().Name);
        }

        var cached = cache?.Calendars.ToDictionary(
            calendar => calendar.CalendarId,
            StringComparer.Ordinal)
            ?? new Dictionary<string, CachedCalendar>(StringComparer.Ordinal);
        var unavailableName = _textService.Get(UiResourceKeys.SettingsCalendarsNameUnavailable);
        return account.Calendars
            .Select(setting =>
            {
                var cachedCalendar = cached.GetValueOrDefault(setting.CalendarId);
                return new CalendarCatalogEntry(
                    setting.CalendarId,
                    cachedCalendar?.Name ?? unavailableName,
                    true,
                    cachedCalendar?.SourceColor);
            })
            .ToArray();
    }

    private void ApplyCatalogs(IReadOnlyList<CalendarAccountCatalog> catalogs)
    {
        var existingGroups = AccountGroups.ToDictionary(group => group.InternalAccountId);
        var states = _syncService.AccountStates.ToDictionary(state => state.InternalAccountId);
        var ordered = catalogs
            .Select(catalog =>
            {
                if (!existingGroups.TryGetValue(catalog.Account.InternalAccountId, out var group)
                    || group.Provider != catalog.Account.Provider)
                {
                    group = new CalendarAccountGroupViewModel(
                        catalog.Account,
                        _textService);
                }

                group.UpdateAccount(catalog.Account, catalog.DiscoveryErrorText);
                ApplyRows(
                    group,
                    catalog.Account,
                    catalog.Entries,
                    states.GetValueOrDefault(catalog.Account.InternalAccountId));
                return group;
            })
            .ToArray();

        ApplyOrderedCollection(AccountGroups, ordered);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyRows(
        CalendarAccountGroupViewModel group,
        AccountSettings account,
        IReadOnlyList<CalendarCatalogEntry> entries,
        AccountSyncState? state)
    {
        var settings = account.Calendars.ToDictionary(
            calendar => calendar.CalendarId,
            StringComparer.Ordinal);
        var existing = group.Calendars.ToDictionary(row => row.CalendarId, StringComparer.Ordinal);
        var rows = entries.Select(entry =>
        {
            if (!existing.TryGetValue(entry.CalendarId, out var row))
            {
                row = new CalendarListItemViewModel(
                    this,
                    account,
                    entry,
                    _textService,
                    _brushCache);
            }

            var isVisible = settings.GetValueOrDefault(entry.CalendarId)?.IsVisible ?? false;
            var calendarState = state?.CalendarStates.FirstOrDefault(value =>
                value.CalendarId.Equals(entry.CalendarId, StringComparison.Ordinal));
            row.Update(account, entry, isVisible, calendarState);
            row.SetBusy(_busyRows.ContainsKey(new CalendarRowKey(
                account.InternalAccountId,
                entry.CalendarId)));
            return row;
        }).ToArray();
        ApplyOrderedCollection(group.Calendars, rows);
        group.UpdateAllOffState(account.Calendars.All(calendar => !calendar.IsVisible));
    }

    private void ApplySettingsToExistingRows(AppSettings settings)
    {
        var accounts = settings.Accounts.ToDictionary(account => account.InternalAccountId);
        foreach (var group in AccountGroups.ToArray())
        {
            if (!accounts.TryGetValue(group.InternalAccountId, out var account))
            {
                AccountGroups.Remove(group);
                continue;
            }

            group.UpdateAccount(account, group.DiscoveryErrorText);
            var selections = account.Calendars.ToDictionary(
                calendar => calendar.CalendarId,
                StringComparer.Ordinal);
            var state = _syncService.AccountStates.FirstOrDefault(value =>
                value.InternalAccountId == account.InternalAccountId);
            foreach (var row in group.Calendars)
            {
                var visible = selections.GetValueOrDefault(row.CalendarId)?.IsVisible ?? false;
                var calendarState = state?.CalendarStates.FirstOrDefault(value =>
                    value.CalendarId.Equals(row.CalendarId, StringComparison.Ordinal));
                row.UpdateVisibilityAndState(visible, calendarState);
            }

            group.UpdateAllOffState(account.Calendars.All(calendar => !calendar.IsVisible));
        }

        OnPropertyChanged(nameof(IsEmpty));
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
                ApplySettingsToExistingRows(eventArgs.Current);
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
            var group = AccountGroups.FirstOrDefault(value =>
                value.InternalAccountId == eventArgs.State.InternalAccountId);
            if (group is not null)
            {
                foreach (var row in group.Calendars)
                {
                    row.UpdateSyncState(eventArgs.State.CalendarStates.FirstOrDefault(value =>
                        value.CalendarId.Equals(row.CalendarId, StringComparison.Ordinal)));
                }
            }

            return Task.CompletedTask;
        }), "AccountStateChanged");
    }

    internal void LeavePostRegistrationFlow()
    {
        if (!IsPostRegistrationFlow)
        {
            return;
        }

        IsPostRegistrationFlow = false;
        _postRegistrationAccountId = null;
    }

    private void ReturnToAccounts()
    {
        if (!IsPostRegistrationFlow)
        {
            return;
        }

        LeavePostRegistrationFlow();
        BackToAccountsRequested?.Invoke(this, EventArgs.Empty);
    }

    private CalendarListItemViewModel? FindRow(CalendarRowKey key) => AccountGroups
        .FirstOrDefault(group => group.InternalAccountId == key.InternalAccountId)
        ?.Calendars.FirstOrDefault(row => row.CalendarId.Equals(key.CalendarId, StringComparison.Ordinal));

    private bool CanRefresh() => !_disposed && !IsLoading;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private string SyncErrorText(SyncErrorCategory category) => _textService.Get(category switch
    {
        SyncErrorCategory.Network => UiResourceKeys.SettingsCalendarsSyncNetwork,
        SyncErrorCategory.Timeout => UiResourceKeys.SettingsCalendarsSyncTimeout,
        SyncErrorCategory.RateLimited => UiResourceKeys.SettingsCalendarsSyncRateLimited,
        SyncErrorCategory.AuthenticationRequired => UiResourceKeys.SettingsCalendarsSyncAuthentication,
        SyncErrorCategory.PermissionDenied => UiResourceKeys.SettingsCalendarsSyncPermission,
        SyncErrorCategory.NotFound => UiResourceKeys.SettingsCalendarsSyncNotFound,
        SyncErrorCategory.InvalidData => UiResourceKeys.SettingsCalendarsSyncInvalidData,
        SyncErrorCategory.Storage => UiResourceKeys.SettingsCalendarsSyncStorage,
        _ => UiResourceKeys.SettingsCalendarsSyncUnexpected,
    });

    internal string GetSyncErrorText(SyncErrorCategory category) => SyncErrorText(category);

    private static IReadOnlyList<CalendarDescriptor> DistinctCalendars(
        IEnumerable<CalendarDescriptor> calendars) => calendars
        .GroupBy(calendar => calendar.CalendarId, StringComparer.Ordinal)
        .Select(group => group.First())
        .ToArray();

    private static IReadOnlyList<CalendarCatalogEntry> ToEntries(
        IEnumerable<CalendarDescriptor> calendars) => calendars
        .Select(calendar => new CalendarCatalogEntry(
            calendar.CalendarId,
            calendar.Name,
            calendar.CanReadEvents,
            calendar.SourceColor))
        .ToArray();

    private static IEnumerable<AccountSettings> OrderedAccounts(
        IEnumerable<AccountSettings> accounts) => accounts
        .OrderBy(account => account.Provider switch
        {
            ProviderKind.Google => 0,
            ProviderKind.Microsoft => 1,
            _ => int.MaxValue,
        })
        .ThenBy(account => account.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(account => account.Email, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(account => account.InternalAccountId);

    private static AppSettings CopyWithCalendarVisibility(
        AppSettings current,
        Guid internalAccountId,
        string calendarId,
        bool isVisible)
    {
        var accounts = current.Accounts.Select(account =>
        {
            if (account.InternalAccountId != internalAccountId)
            {
                return account;
            }

            var calendars = account.Calendars
                .Select(calendar => calendar.CalendarId.Equals(calendarId, StringComparison.Ordinal)
                    ? new CalendarSetting(calendar.CalendarId, isVisible)
                    : calendar)
                .ToList();
            if (!calendars.Any(calendar =>
                calendar.CalendarId.Equals(calendarId, StringComparison.Ordinal)))
            {
                calendars.Add(new CalendarSetting(calendarId, isVisible));
            }

            return new AccountSettings(
                account.InternalAccountId,
                account.Provider,
                account.ProviderSubjectId,
                account.DisplayName,
                account.Email,
                account.Enabled,
                account.TokenRef,
                calendars);
        });
        return new AppSettings(
            current.Display,
            current.Sync,
            current.General,
            current.Windows,
            accounts,
            current.ColorRules,
            current.Notifications);
    }

    private static void ApplyOrderedCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> ordered)
        where T : class
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            if (index < target.Count && ReferenceEquals(target[index], ordered[index]))
            {
                continue;
            }

            var existingIndex = target.IndexOf(ordered[index]);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
            }
            else
            {
                target.Insert(index, ordered[index]);
            }
        }

        while (target.Count > ordered.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

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
                "CalendarSelectionProjectionFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }

    private static async Task ObserveSyncAsync(
        Task task,
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
                "CalendarSelectionSyncRequestFailed {Provider} {InternalAccountId} {ErrorCategory}",
                provider,
                internalAccountId,
                exception.GetType().Name);
        }
    }

    private sealed record CalendarAccountCatalog(
        AccountSettings Account,
        IReadOnlyList<CalendarCatalogEntry> Entries,
        string DiscoveryErrorText);

    private readonly record struct CalendarRowKey(Guid InternalAccountId, string CalendarId);
}

public sealed partial class CalendarAccountGroupViewModel : ObservableObject
{
    private readonly IUiTextService _textService;

    internal CalendarAccountGroupViewModel(
        AccountSettings account,
        IUiTextService textService)
    {
        ArgumentNullException.ThrowIfNull(account);
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        InternalAccountId = account.InternalAccountId;
        Provider = account.Provider;
        ProviderText = textService.Get(account.Provider switch
        {
            ProviderKind.Google => UiResourceKeys.ProviderGoogle,
            ProviderKind.Microsoft => UiResourceKeys.ProviderMicrosoft,
            _ => throw new ArgumentOutOfRangeException(nameof(account), "The provider is not defined."),
        });
        SharedCalendarGuidanceText = textService.Get(UiResourceKeys.SettingsCalendarsMicrosoftSharedGuidance);
        AllOffText = textService.Get(UiResourceKeys.SettingsCalendarsAllOff);
        UpdateAccount(account, string.Empty);
    }

    public Guid InternalAccountId { get; }

    public ProviderKind Provider { get; }

    public string ProviderText { get; }

    public bool ShowSharedCalendarGuidance => Provider == ProviderKind.Microsoft;

    public string SharedCalendarGuidanceText { get; }

    public string AllOffText { get; }

    public ObservableCollection<CalendarListItemViewModel> Calendars { get; } = [];

    public bool HasDiscoveryError => DiscoveryErrorText.Length > 0;

    [ObservableProperty]
    private string _accountDisplayIdentity = string.Empty;

    [ObservableProperty]
    private string _discoveryErrorText = string.Empty;

    [ObservableProperty]
    private bool _isAllOff;

    internal void UpdateAccount(AccountSettings account, string discoveryErrorText)
    {
        if (account.InternalAccountId != InternalAccountId || account.Provider != Provider)
        {
            throw new ArgumentException("The account identity cannot change.", nameof(account));
        }

        AccountDisplayIdentity = string.IsNullOrWhiteSpace(account.DisplayName)
            ? account.Email
            : account.DisplayName;
        DiscoveryErrorText = discoveryErrorText;
    }

    internal void UpdateAllOffState(bool value) => IsAllOff = value;

    partial void OnDiscoveryErrorTextChanged(string value) =>
        OnPropertyChanged(nameof(HasDiscoveryError));
}

public sealed partial class CalendarListItemViewModel : ObservableObject
{
    private readonly CalendarSelectionSettingsViewModel _owner;
    private readonly IUiTextService _textService;
    private readonly BrushCache _brushCache;
    private readonly AsyncRelayCommand _toggleVisibilityCommand;

    internal CalendarListItemViewModel(
        CalendarSelectionSettingsViewModel owner,
        AccountSettings account,
        CalendarCatalogEntry entry,
        IUiTextService textService,
        BrushCache brushCache)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(entry);
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _brushCache = brushCache ?? throw new ArgumentNullException(nameof(brushCache));
        InternalAccountId = account.InternalAccountId;
        Provider = account.Provider;
        CalendarId = entry.CalendarId;
        ProviderText = textService.Get(account.Provider switch
        {
            ProviderKind.Google => UiResourceKeys.ProviderGoogle,
            ProviderKind.Microsoft => UiResourceKeys.ProviderMicrosoft,
            _ => throw new ArgumentOutOfRangeException(nameof(account), "The provider is not defined."),
        });
        ToggleText = textService.Get(UiResourceKeys.SettingsCalendarsShow);
        ReadUnavailableText = textService.Get(UiResourceKeys.SettingsCalendarsReadUnavailable);
        SyncWarningText = textService.Get(UiResourceKeys.SettingsCalendarsSyncWarning);
        BusyText = textService.Get(UiResourceKeys.SettingsAccountsOperationProgress);
        _toggleVisibilityCommand = new AsyncRelayCommand(
            cancellationToken => _owner.ToggleVisibilityAsync(this, cancellationToken),
            CanToggleVisibility);
    }

    public Guid InternalAccountId { get; }

    public ProviderKind Provider { get; }

    internal string CalendarId { get; }

    public string ProviderText { get; }

    public string ToggleText { get; }

    public string ReadUnavailableText { get; }

    public string SyncWarningText { get; }

    public string BusyText { get; }

    public string? ToggleToolTip => CanReadEvents ? null : ReadUnavailableText;

    public IAsyncRelayCommand ToggleVisibilityCommand => _toggleVisibilityCommand;

    public bool ShowReadUnavailableReason => !CanReadEvents;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _accountDisplayIdentity = string.Empty;

    [ObservableProperty]
    private Brush? _sourceColorBrush;

    [ObservableProperty]
    private bool _canReadEvents;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasSyncWarning;

    [ObservableProperty]
    private string? _syncWarningToolTip;

    internal void Update(
        AccountSettings account,
        CalendarCatalogEntry entry,
        bool isVisible,
        CalendarSyncState? state)
    {
        if (account.InternalAccountId != InternalAccountId
            || account.Provider != Provider
            || !entry.CalendarId.Equals(CalendarId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The calendar row identity cannot change.", nameof(account));
        }

        Name = entry.Name;
        AccountDisplayIdentity = string.IsNullOrWhiteSpace(account.DisplayName)
            ? account.Email
            : account.DisplayName;
        SourceColorBrush = entry.SourceColor.HasValue
            ? _brushCache.GetSolid(entry.SourceColor.Value)
            : null;
        CanReadEvents = entry.CanReadEvents;
        UpdateVisibilityAndState(isVisible, state);
        OnPropertyChanged(nameof(ShowReadUnavailableReason));
        OnPropertyChanged(nameof(ToggleToolTip));
        _toggleVisibilityCommand.NotifyCanExecuteChanged();
    }

    internal void UpdateVisibilityAndState(bool isVisible, CalendarSyncState? state)
    {
        IsVisible = isVisible;
        UpdateSyncState(state);
        _toggleVisibilityCommand.NotifyCanExecuteChanged();
    }

    internal void UpdateSyncState(CalendarSyncState? state)
    {
        HasSyncWarning = IsVisible && state?.Status is
            SyncStatus.Failed or SyncStatus.AuthenticationRequired or SyncStatus.RateLimited;
        SyncWarningToolTip = HasSyncWarning
            ? _owner.GetSyncErrorText(state!.ErrorCategory)
            : null;
    }

    internal void SetBusy(bool value)
    {
        IsBusy = value;
        _toggleVisibilityCommand.NotifyCanExecuteChanged();
    }

    internal void NotifyVisibilityUnchanged() => OnPropertyChanged(nameof(IsVisible));

    private bool CanToggleVisibility() => !IsBusy && (CanReadEvents || IsVisible);
}

internal sealed record CalendarCatalogEntry(
    string CalendarId,
    string Name,
    bool CanReadEvents,
    RgbColor? SourceColor);
