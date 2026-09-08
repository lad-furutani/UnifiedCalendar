using System.Collections.Concurrent;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Core.Time;

namespace UnifiedCalendar.Core.Sync;

public sealed class CalendarSyncService : ICalendarSyncService, IDisposable, IAsyncDisposable
{
    private readonly IReadOnlyDictionary<ProviderKind, ICalendarProvider> _providers;
    private readonly ISettingsStore _settingsStore;
    private readonly ICacheStore _cacheStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalTimeZoneProvider _localTimeZoneProvider;
    private readonly ISyncEventLogger _logger;
    private readonly SemaphoreSlim _accountSemaphore = new(SyncPolicy.MaxConcurrentAccounts);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<Guid, AccountExecution> _accountExecutions = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _rateLimitedUntil = new();
    private readonly object _stateLock = new();
    private readonly Dictionary<Guid, AccountCache> _caches = [];
    private readonly HashSet<Guid> _loadedCacheAccounts = [];
    private readonly Dictionary<Guid, AccountSyncState> _accountStates = [];
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly object _snapshotOrderLock = new();
    private readonly Queue<SyncSnapshot> _pendingSnapshotNotifications = [];
    private AppSettings _settings = AppSettings.CreateDefault();
    private SyncSnapshot _currentSnapshot;
    private bool _isPublishingSnapshots;
    private bool _stopped;
    private bool _disposed;

    public CalendarSyncService(
        IEnumerable<ICalendarProvider> providers,
        ISettingsStore settingsStore,
        ICacheStore cacheStore,
        TimeProvider timeProvider,
        ISyncEventLogger? logger = null,
        TimeZoneInfo? localTimeZone = null)
        : this(
            providers,
            settingsStore,
            cacheStore,
            timeProvider,
            localTimeZone is null
                ? new SystemLocalTimeZoneProvider()
                : new FixedLocalTimeZoneProvider(localTimeZone),
            logger)
    {
    }

    public CalendarSyncService(
        IEnumerable<ICalendarProvider> providers,
        ISettingsStore settingsStore,
        ICacheStore cacheStore,
        TimeProvider timeProvider,
        ILocalTimeZoneProvider localTimeZoneProvider,
        ISyncEventLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _localTimeZoneProvider = localTimeZoneProvider
            ?? throw new ArgumentNullException(nameof(localTimeZoneProvider));
        _logger = logger ?? new NullSyncEventLogger();

        var providerArray = providers.ToArray();
        if (providerArray.Any(provider => provider is null))
        {
            throw new ArgumentException("Providers cannot contain null elements.", nameof(providers));
        }

        if (providerArray.Select(provider => provider.Provider).Distinct().Count() != providerArray.Length)
        {
            throw new ArgumentException("Only one provider can be registered for each provider kind.", nameof(providers));
        }

        _providers = providerArray.ToDictionary(provider => provider.Provider);
        _currentSnapshot = new SyncSnapshot([], [], [], _timeProvider.GetUtcNow());
    }

    public event EventHandler<SyncSnapshotChangedEventArgs>? SnapshotChanged;

    public event EventHandler<AccountSyncStateChangedEventArgs>? AccountStateChanged;

    public SyncSnapshot CurrentSnapshot
    {
        get
        {
            lock (_stateLock)
            {
                return _currentSnapshot;
            }
        }
    }

    public IReadOnlyList<AccountSyncState> AccountStates
    {
        get
        {
            lock (_stateLock)
            {
                return Array.AsReadOnly(_accountStates.Values.ToArray());
            }
        }
    }

    public async Task<StartupCacheResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        AppSettings settings;
        try
        {
            settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.StorageFailed("SettingsLoad", null);
            var empty = UpdateSnapshotAndPublish(() =>
                _settings = AppSettings.CreateDefault());
            return new StartupCacheResult(empty, [], false, SyncErrorCategory.Storage);
        }

        var startupAccounts = settings.Accounts.ToArray();
        var loads = startupAccounts.Select(account => LoadStartupCacheAsync(account, cancellationToken));
        var loaded = await Task.WhenAll(loads).ConfigureAwait(false);

        var snapshot = UpdateSnapshotAndPublish(() =>
        {
            _settings = settings;
            foreach (var result in loaded)
            {
                if (result.LoadCompleted)
                {
                    _loadedCacheAccounts.Add(result.AccountId);
                }

                if (result.Cache is not null)
                {
                    _caches[result.AccountId] = result.Cache;
                }

                if (result.State is not null)
                {
                    _accountStates[result.AccountId] = result.State;
                }
            }

            foreach (var account in settings.Accounts)
            {
                _accountStates.TryAdd(
                    account.InternalAccountId,
                    CreateInitialState(account, _caches.GetValueOrDefault(account.InternalAccountId)));
            }
        });

        return new StartupCacheResult(
            snapshot,
            AccountStates,
            loaded.Any(result => result.Cache is not null));
    }

    public Task<SyncRunResult> RequestSyncAsync(
        SyncTriggerReason reason,
        CancellationToken cancellationToken = default) =>
        RequestSyncCoreAsync(reason, null, cancellationToken);

    public Task<SyncRunResult> RequestSyncAsync(
        SyncTriggerReason reason,
        IReadOnlyCollection<Guid> internalAccountIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(internalAccountIds);
        return RequestSyncCoreAsync(reason, internalAccountIds, cancellationToken);
    }

    public async Task RefreshFromSettingsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var accountIds = settings.Accounts
            .Select(account => account.InternalAccountId)
            .ToHashSet();

        UpdateSnapshotAndPublish(() =>
        {
            _settings = settings;
            foreach (var removedAccountId in _caches.Keys.Where(id => !accountIds.Contains(id)).ToArray())
            {
                _caches.Remove(removedAccountId);
                _loadedCacheAccounts.Remove(removedAccountId);
            }

            foreach (var removedAccountId in _accountStates.Keys.Where(id => !accountIds.Contains(id)).ToArray())
            {
                _accountStates.Remove(removedAccountId);
                _loadedCacheAccounts.Remove(removedAccountId);
                _rateLimitedUntil.TryRemove(removedAccountId, out _);
                if (_accountExecutions.TryGetValue(removedAccountId, out var execution))
                {
                    lock (execution.SyncRoot)
                    {
                        execution.PendingRequest = null;
                    }
                }
            }

            foreach (var account in settings.Accounts)
            {
                _accountStates.TryAdd(
                    account.InternalAccountId,
                    CreateInitialState(account, _caches.GetValueOrDefault(account.InternalAccountId)));
            }
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task[] background;
        lock (_stateLock)
        {
            if (!_stopped)
            {
                _stopped = true;
                _lifetimeCancellation.Cancel();
            }

            background = _backgroundTasks.ToArray();
        }

        if (background.Length == 0)
        {
            return;
        }

        await Task.WhenAll(background).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        DisposeResources();
    }

    public void Dispose()
    {
        using var timeout = new CancellationTokenSource(SyncPolicy.DisposeTimeout, _timeProvider);
        try
        {
            StopAsync(timeout.Token).GetAwaiter().GetResult();
            DisposeResources();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.UnexpectedFailed("DisposeTimeout", null, null);
            DisposeResources();
        }
    }

    private async Task<SyncRunResult> RequestSyncCoreAsync(
        SyncTriggerReason reason,
        IReadOnlyCollection<Guid>? internalAccountIds,
        CancellationToken cancellationToken)
    {
        ThrowIfStopped();
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        AppSettings settings;
        try
        {
            settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.StorageFailed("SettingsLoad", null);
            return new SyncRunResult(reason, [], SyncErrorCategory.Storage);
        }

        var selectedIds = internalAccountIds is null
            ? null
            : internalAccountIds.ToHashSet();
        var targets = settings.Accounts
            .Where(AccountSyncTargetPolicy.IsSyncTarget)
            .Where(account => selectedIds is null || selectedIds.Contains(account.InternalAccountId))
            .ToArray();

        UpdateSnapshotAndPublish(() =>
        {
            _settings = settings;
            foreach (var account in settings.Accounts)
            {
                _accountStates.TryAdd(
                    account.InternalAccountId,
                    CreateInitialState(account, _caches.GetValueOrDefault(account.InternalAccountId)));
            }
        });
        if (targets.Length == 0)
        {
            return new SyncRunResult(reason, []);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var tasks = targets
            .Select(account => new
            {
                Account = account,
                CalendarIds = GetTargetCalendarIds(account, reason),
            })
            .Where(target => target.CalendarIds is null || target.CalendarIds.Count > 0)
            .Select(target => QueueAccountSync(
                target.Account,
                settings.Display.Days,
                reason,
                target.CalendarIds,
                linked.Token))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new SyncRunResult(reason, results);
    }

    private Task<SyncAccountResult> QueueAccountSync(
        AccountSettings account,
        int displayDays,
        SyncTriggerReason reason,
        IReadOnlySet<string>? targetCalendarIds,
        CancellationToken cancellationToken)
    {
        var execution = _accountExecutions.GetOrAdd(account.InternalAccountId, _ => new AccountExecution());
        var request = new AccountSyncRequest(
            account,
            displayDays,
            reason,
            targetCalendarIds,
            cancellationToken);
        lock (execution.SyncRoot)
        {
            if (execution.ActiveTask is not null)
            {
                execution.PendingRequest = execution.PendingRequest is null
                    ? request
                    : MergeRequests(execution.PendingRequest, request);
                return execution.ActiveTask;
            }

            execution.ActiveTask = RunCoalescedAccountAsync(execution, request);
            return execution.ActiveTask;
        }
    }

    private IReadOnlySet<string>? GetTargetCalendarIds(
        AccountSettings account,
        SyncTriggerReason reason)
    {
        if (reason is not SyncTriggerReason.StartupRetry
            and not SyncTriggerReason.RateLimitRetry)
        {
            return null;
        }

        lock (_stateLock)
        {
            var state = _accountStates.GetValueOrDefault(account.InternalAccountId);
            if (state is null)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return state.CalendarStates
                .Where(calendar => reason == SyncTriggerReason.StartupRetry
                    ? calendar.ErrorCategory is SyncErrorCategory.Network or SyncErrorCategory.Timeout
                    : calendar.Status == SyncStatus.RateLimited)
                .Select(calendar => calendar.CalendarId)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    private static AccountSyncRequest MergeRequests(
        AccountSyncRequest pending,
        AccountSyncRequest incoming)
    {
        return incoming with
        {
            TargetCalendarIds = MergeTargetCalendarIds(
                pending.TargetCalendarIds,
                incoming.TargetCalendarIds),
        };
    }

    private static IReadOnlySet<string>? MergeTargetCalendarIds(
        IReadOnlySet<string>? pending,
        IReadOnlySet<string>? incoming)
    {
        if (pending is null || incoming is null)
        {
            return null;
        }

        var union = pending.ToHashSet(StringComparer.Ordinal);
        union.UnionWith(incoming);
        return union;
    }

    private async Task<SyncAccountResult> RunCoalescedAccountAsync(
        AccountExecution execution,
        AccountSyncRequest firstRequest)
    {
        await Task.Yield();
        var request = firstRequest;
        try
        {
            while (true)
            {
                var result = await ExecuteAccountAsync(request).ConfigureAwait(false);
                lock (execution.SyncRoot)
                {
                    if (execution.PendingRequest is null)
                    {
                        execution.ActiveTask = null;
                        return result;
                    }

                    request = execution.PendingRequest;
                    execution.PendingRequest = null;
                }
            }
        }
        catch
        {
            lock (execution.SyncRoot)
            {
                execution.PendingRequest = null;
                execution.ActiveTask = null;
            }

            throw;
        }
    }

    private async Task<SyncAccountResult> ExecuteAccountAsync(AccountSyncRequest request)
    {
        var accountSettings = request.Account;
        var account = accountSettings.ToCalendarAccount();
        var cancellationToken = request.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Reason != SyncTriggerReason.RateLimitRetry
            && _rateLimitedUntil.TryGetValue(account.InternalAccountId, out var retryAt)
            && retryAt > _timeProvider.GetUtcNow())
        {
            var deferredState = CreateDeferredState(
                accountSettings,
                request.TargetCalendarIds,
                retryAt);
            PublishAccountState(deferredState);
            return new SyncAccountResult(
                account.InternalAccountId,
                SyncAccountResultKind.Deferred,
                deferredState,
                retryAt);
        }

        if (request.Reason == SyncTriggerReason.RateLimitRetry)
        {
            _rateLimitedUntil.TryRemove(account.InternalAccountId, out _);
        }

        AccountCache? previousCache;
        try
        {
            previousCache = await GetOrLoadCacheAsync(accountSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.StorageFailed("CacheLoad", account.InternalAccountId);
            var state = CreateAccountFailureState(
                accountSettings,
                request.TargetCalendarIds,
                SyncErrorCategory.Storage);
            PublishAccountState(state);
            return new SyncAccountResult(
                account.InternalAccountId,
                SyncAccountResultKind.Failed,
                state);
        }

        var attemptUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        PublishAccountState(CreateSyncingState(
            accountSettings,
            request.TargetCalendarIds,
            attemptUtc));
        var accountStartedTimestamp = _timeProvider.GetTimestamp();
        var semaphoreEntered = false;
        try
        {
            await _accountSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            semaphoreEntered = true;
            accountStartedTimestamp = _timeProvider.GetTimestamp();
            _logger.AccountStarted(account.Provider, account.InternalAccountId, request.Reason);

            if (!_providers.TryGetValue(account.Provider, out var provider))
            {
                var missingProviderState = CreateAccountFailureState(
                    accountSettings,
                    request.TargetCalendarIds,
                    SyncErrorCategory.Unexpected,
                    attemptUtc);
                PublishAccountState(missingProviderState);
                _logger.UnexpectedFailed("ProviderResolution", account.Provider, account.InternalAccountId);
                return CompleteAccountLog(
                    account.Provider,
                    account.InternalAccountId,
                    SyncAccountResultKind.Failed,
                    0,
                    accountStartedTimestamp,
                    missingProviderState);
            }

            var calendarsResult = await ListCalendarsWithRetryAsync(
                provider,
                account,
                cancellationToken).ConfigureAwait(false);
            if (!calendarsResult.IsSuccess)
            {
                var error = calendarsResult.Error!;
                var failureState = CreateAccountFailureState(
                    accountSettings,
                    request.TargetCalendarIds,
                    ToSyncErrorCategory(error),
                    attemptUtc,
                    GetRetryAt(error));
                PublishAccountState(failureState);
                ScheduleRateLimitRetryIfNeeded(account, error);
                return CompleteAccountLog(
                    account.Provider,
                    account.InternalAccountId,
                    ToResultKind(error),
                    0,
                    accountStartedTimestamp,
                    failureState,
                    GetRetryAt(error));
            }

            var descriptors = calendarsResult.Value!
                .GroupBy(calendar => calendar.CalendarId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var range = CreateTimeRange(request.DisplayDays, previousCache);
            var successes = new Dictionary<string, CachedCalendar>(StringComparer.Ordinal);
            var previousCalendars = previousCache?.Calendars.ToDictionary(
                calendar => calendar.CalendarId,
                StringComparer.Ordinal)
                ?? new Dictionary<string, CachedCalendar>(StringComparer.Ordinal);
            var states = new List<CalendarSyncState>();
            DateTimeOffset? accountRetryAt = null;

            foreach (var selected in GetTargetCalendars(accountSettings, request.TargetCalendarIds))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!descriptors.TryGetValue(selected.CalendarId, out var descriptor))
                {
                    states.Add(CreateCalendarFailureState(
                        selected.CalendarId,
                        attemptUtc,
                        SyncErrorCategory.NotFound,
                        previousCalendars.GetValueOrDefault(selected.CalendarId)?.LastSuccessfulSyncUtc));
                    continue;
                }

                if (!descriptor.CanReadEvents)
                {
                    states.Add(CreateCalendarFailureState(
                        selected.CalendarId,
                        attemptUtc,
                        SyncErrorCategory.PermissionDenied,
                        previousCalendars.GetValueOrDefault(selected.CalendarId)?.LastSuccessfulSyncUtc));
                    continue;
                }

                var calendarStartedTimestamp = _timeProvider.GetTimestamp();
                var result = await GetEventsWithRetryAsync(
                    provider,
                    account,
                    descriptor,
                    range,
                    cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    var completedUtc = _timeProvider.GetUtcNow().ToUniversalTime();
                    try
                    {
                        successes.Add(
                            selected.CalendarId,
                            new CachedCalendar(
                                descriptor.CalendarId,
                                descriptor.Name,
                                descriptor.SourceColor,
                                completedUtc,
                                result.Events));
                        states.Add(new CalendarSyncState(
                            selected.CalendarId,
                            SyncStatus.Succeeded,
                            attemptUtc,
                            completedUtc,
                            null,
                            SyncErrorCategory.None));
                        _logger.CalendarCompleted(
                            account.Provider,
                            account.InternalAccountId,
                            selected.CalendarId,
                            result.Events.Count,
                            SyncStatus.Succeeded,
                            SyncErrorCategory.None,
                            GetElapsedMilliseconds(calendarStartedTimestamp));
                    }
                    catch (ArgumentException)
                    {
                        states.Add(CreateCalendarFailureState(
                            selected.CalendarId,
                            attemptUtc,
                            SyncErrorCategory.InvalidData,
                            previousCalendars.GetValueOrDefault(selected.CalendarId)?.LastSuccessfulSyncUtc));
                        _logger.CalendarCompleted(
                            account.Provider,
                            account.InternalAccountId,
                            selected.CalendarId,
                            0,
                            SyncStatus.Failed,
                            SyncErrorCategory.InvalidData,
                            GetElapsedMilliseconds(calendarStartedTimestamp));
                    }

                    continue;
                }

                var providerError = result.Error!;
                if (providerError.Category == ProviderErrorCategory.Cancelled
                    && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                var errorCategory = ToSyncErrorCategory(providerError);
                var calendarRetryAt = GetRetryAt(providerError);
                accountRetryAt = Latest(accountRetryAt, calendarRetryAt);
                states.Add(CreateCalendarFailureState(
                    selected.CalendarId,
                    attemptUtc,
                    errorCategory,
                    previousCalendars.GetValueOrDefault(selected.CalendarId)?.LastSuccessfulSyncUtc,
                    calendarRetryAt));
                _logger.CalendarCompleted(
                    account.Provider,
                    account.InternalAccountId,
                    selected.CalendarId,
                    0,
                    ToSyncStatus(providerError),
                    errorCategory,
                    GetElapsedMilliseconds(calendarStartedTimestamp));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var hasSuccessfulTargetCalendar = successes.Count > 0;
            if (!hasSuccessfulTargetCalendar)
            {
                var failedState = CreateCompletedAccountState(
                    accountSettings,
                    states,
                    attemptUtc);
                PublishAccountState(failedState);
                ScheduleRateLimitRetryIfNeeded(account, accountRetryAt);
                return CompleteAccountLog(
                    account.Provider,
                    account.InternalAccountId,
                    AccountResultKindFromState(failedState),
                    0,
                    accountStartedTimestamp,
                    failedState,
                    accountRetryAt);
            }

            var mergedCalendars = MergeCalendars(accountSettings, previousCache, successes);
            var generatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime();
            var stagedCache = new AccountCache(
                account.InternalAccountId,
                account.Provider,
                generatedAtUtc,
                mergedCalendars);
            try
            {
                await _cacheStore.SaveAccountAsync(stagedCache, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                _logger.StorageFailed("CacheSave", account.InternalAccountId);
                var storageState = CreateAccountFailureState(
                    accountSettings,
                    request.TargetCalendarIds,
                    SyncErrorCategory.Storage,
                    attemptUtc);
                PublishAccountState(storageState);
                return CompleteAccountLog(
                    account.Provider,
                    account.InternalAccountId,
                    SyncAccountResultKind.Failed,
                    0,
                    accountStartedTimestamp,
                    storageState);
            }

            cancellationToken.ThrowIfCancellationRequested();
            UpdateSnapshotAndPublish(() =>
            {
                if (ContainsAccountLocked(account.InternalAccountId))
                {
                    _caches[account.InternalAccountId] = stagedCache;
                    _loadedCacheAccounts.Add(account.InternalAccountId);
                }
            });

            var accountState = CreateCompletedAccountState(
                accountSettings,
                states,
                attemptUtc);
            PublishAccountState(accountState);
            ScheduleRateLimitRetryIfNeeded(account, accountRetryAt);
            var resultKind = AccountResultKindFromState(accountState);
            return CompleteAccountLog(
                account.Provider,
                account.InternalAccountId,
                resultKind,
                successes.Values.Sum(calendar => calendar.Events.Count),
                accountStartedTimestamp,
                accountState,
                accountRetryAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelledState = CreateAccountFailureState(
                accountSettings,
                request.TargetCalendarIds,
                SyncErrorCategory.Cancelled,
                attemptUtc);
            PublishAccountState(cancelledState);
            _logger.AccountCompleted(
                account.Provider,
                account.InternalAccountId,
                SyncAccountResultKind.Cancelled,
                0,
                GetElapsedMilliseconds(accountStartedTimestamp));
            throw;
        }
        catch (Exception)
        {
            _logger.UnexpectedFailed("AccountSync", account.Provider, account.InternalAccountId);
            var unexpectedState = CreateAccountFailureState(
                accountSettings,
                request.TargetCalendarIds,
                SyncErrorCategory.Unexpected,
                attemptUtc);
            PublishAccountState(unexpectedState);
            return CompleteAccountLog(
                account.Provider,
                account.InternalAccountId,
                SyncAccountResultKind.Failed,
                0,
                accountStartedTimestamp,
                unexpectedState);
        }
        finally
        {
            if (semaphoreEntered)
            {
                _accountSemaphore.Release();
            }
        }
    }

    private async Task<ProviderOperationResult<IReadOnlyList<CalendarDescriptor>>> ListCalendarsWithRetryAsync(
        ICalendarProvider provider,
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var calendars = await provider.ListCalendarsAsync(account, cancellationToken).ConfigureAwait(false);
                return ProviderOperationResult<IReadOnlyList<CalendarDescriptor>>.Success(calendars);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderException exception)
            {
                if (!ShouldRetry(exception.Error, attempt))
                {
                    return ProviderOperationResult<IReadOnlyList<CalendarDescriptor>>.Failure(exception.Error);
                }

                await DelayForRetryAsync(
                    account.Provider,
                    account.InternalAccountId,
                    null,
                    attempt,
                    exception.Error,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return ProviderOperationResult<IReadOnlyList<CalendarDescriptor>>.Failure(
                    new ProviderError(ProviderErrorCategory.Unexpected));
            }
        }
    }

    private async Task<ProviderCalendarResult> GetEventsWithRetryAsync(
        ICalendarProvider provider,
        CalendarAccount account,
        CalendarDescriptor calendar,
        TimeRangeUtc range,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderCalendarResult result;
            try
            {
                result = await provider.GetEventsAsync(
                    account,
                    calendar,
                    range,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ProviderException exception)
            {
                result = ProviderCalendarResult.Failure(exception.Error);
            }
            catch (Exception)
            {
                result = ProviderCalendarResult.Failure(
                    new ProviderError(ProviderErrorCategory.Unexpected));
            }

            if (result.IsSuccess || !ShouldRetry(result.Error!, attempt))
            {
                return result;
            }

            await DelayForRetryAsync(
                account.Provider,
                account.InternalAccountId,
                calendar.CalendarId,
                attempt,
                result.Error!,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DelayForRetryAsync(
        ProviderKind provider,
        Guid accountId,
        string? calendarId,
        int attempt,
        ProviderError error,
        CancellationToken cancellationToken)
    {
        var delay = SyncPolicy.TransientRetryDelays[attempt];
        _logger.RetryScheduled(
            provider,
            accountId,
            calendarId,
            attempt + 1,
            delay,
            ToSyncErrorCategory(error));
        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AccountCache?> GetOrLoadCacheAsync(
        AccountSettings account,
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_loadedCacheAccounts.Contains(account.InternalAccountId))
            {
                return _caches.GetValueOrDefault(account.InternalAccountId);
            }
        }

        var cache = await _cacheStore.LoadAccountAsync(account.InternalAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (cache is not null && cache.Provider != account.Provider)
        {
            throw new InvalidDataException("The account cache provider does not match the settings provider.");
        }

        lock (_stateLock)
        {
            if (ContainsAccountLocked(account.InternalAccountId))
            {
                _loadedCacheAccounts.Add(account.InternalAccountId);
                if (cache is not null)
                {
                    _caches[account.InternalAccountId] = cache;
                }
            }
        }

        return cache;
    }

    private async Task<StartupCacheLoad> LoadStartupCacheAsync(
        AccountSettings account,
        CancellationToken cancellationToken)
    {
        try
        {
            var cache = await _cacheStore.LoadAccountAsync(account.InternalAccountId, cancellationToken)
                .ConfigureAwait(false);
            if (cache is not null && cache.Provider != account.Provider)
            {
                throw new InvalidDataException("The account cache provider does not match the settings provider.");
            }

            var state = CreateInitialState(account, cache);
            return new StartupCacheLoad(account.InternalAccountId, cache, state, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.StorageFailed("CacheLoad", account.InternalAccountId);
            return new StartupCacheLoad(
                account.InternalAccountId,
                null,
                CreateAccountFailureState(account, null, SyncErrorCategory.Storage),
                false);
        }
    }

    private TimeRangeUtc CreateTimeRange(int displayDays, AccountCache? previousCache)
    {
        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var localTimeZone = _localTimeZoneProvider.GetCurrent();
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, localTimeZone);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var startUtc = LocalMidnightToUtc(today, localTimeZone);
        var endUtc = LocalMidnightToUtc(today.AddDays(displayDays), localTimeZone);

        var ongoingStart = previousCache?.Calendars
            .SelectMany(calendar => calendar.Events)
            .Select(calendarEvent => calendarEvent.Timing)
            .OfType<TimedEventTiming>()
            .Where(timing => timing.StartUtc < startUtc && timing.EndUtc > nowUtc)
            .Select(timing => (DateTimeOffset?)timing.StartUtc)
            .Min();
        if (ongoingStart.HasValue && ongoingStart.Value < startUtc)
        {
            startUtc = ongoingStart.Value;
        }

        return new TimeRangeUtc(startUtc, endUtc);
    }

    private static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }

    private SyncSnapshot UpdateSnapshotAndPublish(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        SyncSnapshot snapshot;
        var publish = false;
        lock (_snapshotOrderLock)
        {
            lock (_stateLock)
            {
                mutation();
                snapshot = BuildSnapshotLocked();
                _currentSnapshot = snapshot;
            }

            _pendingSnapshotNotifications.Enqueue(snapshot);
            if (!_isPublishingSnapshots)
            {
                _isPublishingSnapshots = true;
                publish = true;
            }
        }

        if (publish)
        {
            DrainSnapshotNotifications();
        }

        return snapshot;
    }

    private void DrainSnapshotNotifications()
    {
        while (true)
        {
            SyncSnapshot snapshot;
            lock (_snapshotOrderLock)
            {
                if (_pendingSnapshotNotifications.Count == 0)
                {
                    _isPublishingSnapshots = false;
                    return;
                }

                snapshot = _pendingSnapshotNotifications.Dequeue();
            }

            var handlers = SnapshotChanged;
            if (handlers is null)
            {
                continue;
            }

            var eventArgs = new SyncSnapshotChangedEventArgs(snapshot);
            foreach (EventHandler<SyncSnapshotChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, eventArgs);
                }
                catch (Exception)
                {
                    _logger.UnexpectedFailed("SnapshotSubscriber", null, null);
                }
            }
        }
    }

    private SyncSnapshot BuildSnapshotLocked()
    {
        var accounts = _settings.Accounts
            .Select(account => account.ToCalendarAccount())
            .ToArray();
        var selections = _settings.Accounts
            .SelectMany(account => account.Calendars.Select(calendar =>
                new CalendarSelection(
                    account.InternalAccountId,
                    calendar.CalendarId,
                    calendar.IsVisible)))
            .ToArray();
        var visible = _settings.Accounts
            .Where(account => account.Enabled)
            .ToDictionary(
                account => account.InternalAccountId,
                account => account.Calendars
                    .Where(calendar => calendar.IsVisible)
                    .Select(calendar => calendar.CalendarId)
                    .ToHashSet(StringComparer.Ordinal));
        var events = _caches.Values
            .Where(cache => visible.ContainsKey(cache.InternalAccountId))
            .SelectMany(cache => cache.Calendars
                .Where(calendar => visible[cache.InternalAccountId].Contains(calendar.CalendarId))
                .SelectMany(calendar => calendar.Events))
            .ToArray();
        return new SyncSnapshot(accounts, selections, events, _timeProvider.GetUtcNow());
    }

    private static IReadOnlyList<CachedCalendar> MergeCalendars(
        AccountSettings account,
        AccountCache? previousCache,
        IReadOnlyDictionary<string, CachedCalendar> successes)
    {
        var previous = previousCache?.Calendars.ToDictionary(
            calendar => calendar.CalendarId,
            StringComparer.Ordinal)
            ?? new Dictionary<string, CachedCalendar>(StringComparer.Ordinal);
        return account.Calendars
            .Where(calendar => calendar.IsVisible)
            .Select(calendar => successes.GetValueOrDefault(calendar.CalendarId)
                ?? previous.GetValueOrDefault(calendar.CalendarId))
            .Where(calendar => calendar is not null)
            .Cast<CachedCalendar>()
            .ToArray();
    }

    private static IEnumerable<CalendarSetting> GetTargetCalendars(
        AccountSettings account,
        IReadOnlySet<string>? targetCalendarIds) =>
        account.Calendars.Where(calendar =>
            calendar.IsVisible
            && (targetCalendarIds is null || targetCalendarIds.Contains(calendar.CalendarId)));

    private AccountSyncState CreateCompletedAccountState(
        AccountSettings account,
        IReadOnlyList<CalendarSyncState> calendarStates,
        DateTimeOffset attemptUtc) =>
        MergeAccountState(account, calendarStates, attemptUtc);

    private AccountSyncState CreateAccountFailureState(
        AccountSettings account,
        IReadOnlySet<string>? targetCalendarIds,
        SyncErrorCategory error,
        DateTimeOffset? attemptUtc = null,
        DateTimeOffset? retryAtUtc = null)
    {
        IReadOnlyDictionary<string, CalendarSyncState> previousCalendars;
        lock (_stateLock)
        {
            var previous = _accountStates.GetValueOrDefault(account.InternalAccountId);
            previousCalendars = previous?.CalendarStates.ToDictionary(
                calendar => calendar.CalendarId,
                StringComparer.Ordinal)
                ?? new Dictionary<string, CalendarSyncState>(StringComparer.Ordinal);
        }

        var status = ToSyncStatus(error);
        var states = GetTargetCalendars(account, targetCalendarIds)
            .Select(calendar => new CalendarSyncState(
                calendar.CalendarId,
                status,
                attemptUtc,
                previousCalendars.GetValueOrDefault(calendar.CalendarId)?.LastSuccessUtc,
                retryAtUtc,
                error))
            .ToArray();
        return MergeAccountState(account, states, attemptUtc);
    }

    private AccountSyncState CreateSyncingState(
        AccountSettings account,
        IReadOnlySet<string>? targetCalendarIds,
        DateTimeOffset attemptUtc)
    {
        IReadOnlyDictionary<string, CalendarSyncState> previousCalendars;
        lock (_stateLock)
        {
            var previous = _accountStates.GetValueOrDefault(account.InternalAccountId);
            previousCalendars = previous?.CalendarStates.ToDictionary(
                calendar => calendar.CalendarId,
                StringComparer.Ordinal)
                ?? new Dictionary<string, CalendarSyncState>(StringComparer.Ordinal);
        }

        var states = GetTargetCalendars(account, targetCalendarIds)
            .Select(calendar => new CalendarSyncState(
                calendar.CalendarId,
                SyncStatus.Syncing,
                attemptUtc,
                previousCalendars.GetValueOrDefault(calendar.CalendarId)?.LastSuccessUtc,
                null,
                SyncErrorCategory.None));
        return MergeAccountState(account, states, attemptUtc);
    }

    private static AccountSyncState CreateInitialState(AccountSettings account, AccountCache? cache)
    {
        var cached = cache?.Calendars.ToDictionary(
            calendar => calendar.CalendarId,
            StringComparer.Ordinal)
            ?? new Dictionary<string, CachedCalendar>(StringComparer.Ordinal);
        var calendarStates = account.Calendars
            .Where(calendar => calendar.IsVisible)
            .Select(calendar => new CalendarSyncState(
                calendar.CalendarId,
                SyncStatus.NotStarted,
                null,
                cached.GetValueOrDefault(calendar.CalendarId)?.LastSuccessfulSyncUtc,
                null,
                SyncErrorCategory.None))
            .ToArray();
        var lastFullySuccessfulSyncUtc = GetLastFullySuccessfulSyncUtc(
            calendarStates.Select(calendar => calendar.LastSuccessUtc));
        if (calendarStates.Length == 0)
        {
            var configuredCalendarIds = account.Calendars
                .Select(calendar => calendar.CalendarId)
                .ToHashSet(StringComparer.Ordinal);
            lastFullySuccessfulSyncUtc = GetLastFullySuccessfulSyncUtc(cache?.Calendars
                .Where(calendar => configuredCalendarIds.Contains(calendar.CalendarId))
                .Select(calendar => (DateTimeOffset?)calendar.LastSuccessfulSyncUtc)
                ?? []);
        }

        return new AccountSyncState(
            account.InternalAccountId,
            SyncStatus.NotStarted,
            null,
            lastFullySuccessfulSyncUtc,
            calendarStates);
    }

    private AccountSyncState CreateDeferredState(
        AccountSettings account,
        IReadOnlySet<string>? targetCalendarIds,
        DateTimeOffset retryAtUtc)
    {
        IReadOnlyDictionary<string, CalendarSyncState> previousCalendars;
        lock (_stateLock)
        {
            var previous = _accountStates.GetValueOrDefault(account.InternalAccountId);
            previousCalendars = previous?.CalendarStates.ToDictionary(
                calendar => calendar.CalendarId,
                StringComparer.Ordinal)
                ?? new Dictionary<string, CalendarSyncState>(StringComparer.Ordinal);
        }

        var states = GetTargetCalendars(account, targetCalendarIds)
            .Select(calendar => new CalendarSyncState(
                calendar.CalendarId,
                SyncStatus.RateLimited,
                null,
                previousCalendars.GetValueOrDefault(calendar.CalendarId)?.LastSuccessUtc,
                retryAtUtc,
                SyncErrorCategory.RateLimited));
        return MergeAccountState(account, states, attemptUtc: null);
    }

    private AccountSyncState MergeAccountState(
        AccountSettings account,
        IEnumerable<CalendarSyncState> updates,
        DateTimeOffset? attemptUtc)
    {
        // ExecuteAccountAsync calls are serialized per account by AccountExecution. Keeping the
        // read/merge under _stateLock and publishing immediately afterward relies on that invariant;
        // different accounts never address the same dictionary entry.
        lock (_stateLock)
        {
            var previous = _accountStates.GetValueOrDefault(account.InternalAccountId);
            var merged = previous?.CalendarStates.ToDictionary(
                calendar => calendar.CalendarId,
                StringComparer.Ordinal)
                ?? new Dictionary<string, CalendarSyncState>(StringComparer.Ordinal);
            foreach (var update in updates)
            {
                merged[update.CalendarId] = update;
            }

            var states = account.Calendars
                .Where(calendar => calendar.IsVisible)
                .Select(calendar => merged.GetValueOrDefault(calendar.CalendarId)
                    ?? new CalendarSyncState(
                        calendar.CalendarId,
                        SyncStatus.NotStarted,
                        null,
                        null,
                        null,
                        SyncErrorCategory.None))
                .ToArray();
            var status = DetermineAccountStatus(states);
            var lastFullySuccessfulSyncUtc = GetLastFullySuccessfulSyncUtc(
                states.Select(state => state.Status == SyncStatus.Succeeded
                    ? state.LastSuccessUtc
                    : null));
            if (!lastFullySuccessfulSyncUtc.HasValue)
            {
                lastFullySuccessfulSyncUtc = previous?.LastFullySuccessfulSyncUtc;
            }

            return new AccountSyncState(
                account.InternalAccountId,
                status,
                attemptUtc ?? previous?.LastAttemptUtc,
                lastFullySuccessfulSyncUtc,
                states);
        }
    }

    private static SyncStatus DetermineAccountStatus(IReadOnlyList<CalendarSyncState> states)
    {
        if (states.Count == 0 || states.All(state => state.Status == SyncStatus.NotStarted))
        {
            return SyncStatus.NotStarted;
        }

        if (states.All(state => state.Status == SyncStatus.Succeeded))
        {
            return SyncStatus.Succeeded;
        }

        if (states.Any(state => state.Status == SyncStatus.Syncing))
        {
            return SyncStatus.Syncing;
        }

        if (states.Any(state => state.Status == SyncStatus.Succeeded))
        {
            return SyncStatus.PartiallySucceeded;
        }

        if (states.Any(state => state.Status == SyncStatus.RateLimited))
        {
            return SyncStatus.RateLimited;
        }

        if (states.All(state => state.Status == SyncStatus.Cancelled))
        {
            return SyncStatus.Cancelled;
        }

        return states.Any(state => state.Status == SyncStatus.AuthenticationRequired)
            ? SyncStatus.AuthenticationRequired
            : SyncStatus.Failed;
    }

    private static CalendarSyncState CreateCalendarFailureState(
        string calendarId,
        DateTimeOffset attemptUtc,
        SyncErrorCategory error,
        DateTimeOffset? lastSuccessUtc = null,
        DateTimeOffset? retryAtUtc = null) =>
        new(
            calendarId,
            ToSyncStatus(error),
            attemptUtc,
            lastSuccessUtc,
            retryAtUtc,
            error);

    private void PublishAccountState(AccountSyncState state)
    {
        var publish = false;
        lock (_stateLock)
        {
            if (ContainsAccountLocked(state.InternalAccountId))
            {
                _accountStates[state.InternalAccountId] = state;
                publish = true;
            }
        }

        if (publish)
        {
            AccountStateChanged?.Invoke(this, new AccountSyncStateChangedEventArgs(state));
        }
    }

    private bool ContainsAccountLocked(Guid internalAccountId) =>
        _settings.Accounts.Any(account => account.InternalAccountId == internalAccountId);

    private void ScheduleRateLimitRetryIfNeeded(CalendarAccount account, ProviderError error)
    {
        if (error.Category == ProviderErrorCategory.RateLimited)
        {
            ScheduleRateLimitRetryIfNeeded(account, GetRetryAt(error));
        }
    }

    private void ScheduleRateLimitRetryIfNeeded(
        CalendarAccount account,
        DateTimeOffset? retryAtUtc)
    {
        if (!retryAtUtc.HasValue)
        {
            return;
        }

        var retryAt = retryAtUtc.Value.ToUniversalTime();
        _rateLimitedUntil.AddOrUpdate(
            account.InternalAccountId,
            retryAt,
            (_, _) => retryAt);
        var effectiveRetryAt = _rateLimitedUntil[account.InternalAccountId];
        _logger.RateLimitDeferred(account.Provider, account.InternalAccountId, effectiveRetryAt);
        TrackBackgroundTask(RunRateLimitRetryAsync(account.InternalAccountId, effectiveRetryAt));
    }

    private async Task RunRateLimitRetryAsync(Guid accountId, DateTimeOffset scheduledAtUtc)
    {
        try
        {
            var delay = scheduledAtUtc - _timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, _lifetimeCancellation.Token).ConfigureAwait(false);
            }

            if (!_rateLimitedUntil.TryGetValue(accountId, out var current)
                || current != scheduledAtUtc
                || !_rateLimitedUntil.TryRemove(
                    new KeyValuePair<Guid, DateTimeOffset>(accountId, scheduledAtUtc)))
            {
                return;
            }

            await RequestSyncAsync(
                SyncTriggerReason.RateLimitRetry,
                [accountId],
                _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _logger.UnexpectedFailed("RateLimitRetry", null, accountId);
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (_stateLock)
        {
            _backgroundTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_stateLock)
                {
                    _backgroundTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private SyncAccountResult CompleteAccountLog(
        ProviderKind provider,
        Guid accountId,
        SyncAccountResultKind result,
        int count,
        long startedTimestamp,
        AccountSyncState state,
        DateTimeOffset? retryAtUtc = null)
    {
        _logger.AccountCompleted(
            provider,
            accountId,
            result,
            count,
            GetElapsedMilliseconds(startedTimestamp));
        return new SyncAccountResult(
            accountId,
            result,
            state,
            retryAtUtc);
    }

    private long GetElapsedMilliseconds(long startedTimestamp) =>
        Math.Max(0L, (long)_timeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds);

    private DateTimeOffset? GetRetryAt(ProviderError error)
    {
        if (error.Category != ProviderErrorCategory.RateLimited
            || !error.RetryAfter.HasValue)
        {
            return null;
        }

        return _timeProvider.GetUtcNow().Add(error.RetryAfter.Value);
    }

    private static bool ShouldRetry(ProviderError error, int completedRetries) =>
        completedRetries < SyncPolicy.TransientRetryDelays.Count
        && error.Category is ProviderErrorCategory.Network
            or ProviderErrorCategory.Timeout
            or ProviderErrorCategory.ServerError;

    private static DateTimeOffset? GetLastFullySuccessfulSyncUtc(
        IEnumerable<DateTimeOffset?> successfulTimes)
    {
        var values = successfulTimes.ToArray();
        return values.Length > 0 && values.All(value => value.HasValue)
            ? values.Min(value => value!.Value)
            : null;
    }

    private static SyncStatus ToSyncStatus(ProviderError error) => error.Category switch
    {
        ProviderErrorCategory.AuthenticationRequired => SyncStatus.AuthenticationRequired,
        ProviderErrorCategory.RateLimited => SyncStatus.RateLimited,
        ProviderErrorCategory.Cancelled => SyncStatus.Cancelled,
        _ => SyncStatus.Failed,
    };

    private static SyncStatus ToSyncStatus(SyncErrorCategory error) => error switch
    {
        SyncErrorCategory.AuthenticationRequired => SyncStatus.AuthenticationRequired,
        SyncErrorCategory.RateLimited => SyncStatus.RateLimited,
        SyncErrorCategory.Cancelled => SyncStatus.Cancelled,
        SyncErrorCategory.None => SyncStatus.Succeeded,
        _ => SyncStatus.Failed,
    };

    private static SyncErrorCategory ToSyncErrorCategory(ProviderError error) => error.Category switch
    {
        ProviderErrorCategory.AuthenticationRequired => SyncErrorCategory.AuthenticationRequired,
        ProviderErrorCategory.PermissionDenied => SyncErrorCategory.PermissionDenied,
        ProviderErrorCategory.RateLimited => SyncErrorCategory.RateLimited,
        ProviderErrorCategory.Network => SyncErrorCategory.Network,
        ProviderErrorCategory.Timeout => SyncErrorCategory.Timeout,
        ProviderErrorCategory.ServerError => SyncErrorCategory.Network,
        ProviderErrorCategory.MalformedResponse => SyncErrorCategory.InvalidData,
        ProviderErrorCategory.Cancelled => SyncErrorCategory.Cancelled,
        ProviderErrorCategory.NotFound => SyncErrorCategory.NotFound,
        _ => SyncErrorCategory.Unexpected,
    };

    private static SyncAccountResultKind ToResultKind(ProviderError error) => error.Category switch
    {
        ProviderErrorCategory.Cancelled => SyncAccountResultKind.Cancelled,
        ProviderErrorCategory.RateLimited => SyncAccountResultKind.Deferred,
        _ => SyncAccountResultKind.Failed,
    };

    private static SyncAccountResultKind AccountResultKindFromState(AccountSyncState state) =>
        state.Status switch
        {
            SyncStatus.Succeeded => SyncAccountResultKind.Succeeded,
            SyncStatus.PartiallySucceeded => SyncAccountResultKind.PartiallySucceeded,
            SyncStatus.Cancelled => SyncAccountResultKind.Cancelled,
            SyncStatus.RateLimited => SyncAccountResultKind.Deferred,
            _ => SyncAccountResultKind.Failed,
        };

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (!first.HasValue)
        {
            return second;
        }

        if (!second.HasValue)
        {
            return first;
        }

        return first.Value >= second.Value ? first : second;
    }

    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
    }

    private void DisposeResources()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _accountSemaphore.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private sealed class AccountExecution
    {
        public object SyncRoot { get; } = new();

        public Task<SyncAccountResult>? ActiveTask { get; set; }

        public AccountSyncRequest? PendingRequest { get; set; }
    }

    private sealed record AccountSyncRequest(
        AccountSettings Account,
        int DisplayDays,
        SyncTriggerReason Reason,
        IReadOnlySet<string>? TargetCalendarIds,
        CancellationToken CancellationToken);

    private sealed record StartupCacheLoad(
        Guid AccountId,
        AccountCache? Cache,
        AccountSyncState? State,
        bool LoadCompleted);

    private sealed record ProviderOperationResult<T>(bool IsSuccess, T? Value, ProviderError? Error)
    {
        public static ProviderOperationResult<T> Success(T value) => new(true, value, null);

        public static ProviderOperationResult<T> Failure(ProviderError error) => new(false, default, error);
    }
}
