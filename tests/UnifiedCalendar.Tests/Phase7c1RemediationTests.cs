using System.Collections.Concurrent;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(CultureSensitiveCollection.Name)]
public sealed class Phase7c1RemediationTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 9, 6, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, true, "同期対象外")]
    [InlineData(true, false, "同期対象外")]
    [InlineData(true, true, "同期中")]
    public async Task InitialStateDistinguishesExcludedAndPendingSyncAccounts(
        bool enabled,
        bool calendarVisible,
        string expectedStatusText)
    {
        var account = Account(enabled, calendarVisible);
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [account]),
        };
        var cache = new MemoryCacheStore();
        await using var sync = new CalendarSyncService(
            [],
            store,
            cache,
            new MutableTimeProvider(NowUtc),
            localTimeZone: TimeZoneInfo.Utc);
        await sync.InitializeAsync(TestContext.Current.CancellationToken);
        var unavailable = new UnavailableAccountInteractionService();
        using var viewModel = CreateAccountViewModel(
            new ApplicationSettingsService(store),
            sync,
            unavailable,
            cache);

        viewModel.Initialize(store.Settings);

        var row = Assert.Single(viewModel.Accounts);
        Assert.Equal(SyncStatus.NotStarted, row.Status);
        Assert.Equal(expectedStatusText, row.SyncStatusText);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RestartRestoresLastFullySuccessfulTimeForExcludedAccount(
        bool enabled,
        bool calendarVisible)
    {
        var accountId = Guid.NewGuid();
        var active = Account(
            accountId,
            enabled: true,
            firstCalendarVisible: true,
            secondCalendarVisible: true);
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [active]),
        };
        var cache = new MemoryCacheStore();
        var olderSuccess = NowUtc.AddHours(-3);
        var newerSuccess = NowUtc.AddHours(-1);
        cache.Caches[accountId] = new AccountCache(
            accountId,
            ProviderKind.Google,
            NowUtc,
            [
                new CachedCalendar("first", "First", null, newerSuccess, []),
                new CachedCalendar("second", "Second", null, olderSuccess, []),
            ]);

        await using (var initial = new CalendarSyncService(
            [],
            store,
            cache,
            new MutableTimeProvider(NowUtc),
            localTimeZone: TimeZoneInfo.Utc))
        {
            var initialResult = await initial.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(
                olderSuccess,
                Assert.Single(initialResult.AccountStates).LastFullySuccessfulSyncUtc);
        }

        var excluded = Account(
            accountId,
            enabled,
            firstCalendarVisible: calendarVisible,
            secondCalendarVisible: calendarVisible);
        store.Settings = new AppSettings(accounts: [excluded]);
        await using var restarted = new CalendarSyncService(
            [],
            store,
            cache,
            new MutableTimeProvider(NowUtc),
            localTimeZone: TimeZoneInfo.Utc);

        var restartedResult = await restarted.InitializeAsync(TestContext.Current.CancellationToken);

        var state = Assert.Single(restartedResult.AccountStates);
        Assert.Equal(SyncStatus.NotStarted, state.Status);
        Assert.Equal(olderSuccess, state.LastFullySuccessfulSyncUtc);
    }

    [Theory]
    [InlineData(AccountInteractionFailure.DuplicateAccount)]
    [InlineData(AccountInteractionFailure.CalendarDiscoveryFailed)]
    [InlineData(AccountInteractionFailure.AuthenticationFailed)]
    public async Task MainAddFailureShowsTheSharedFailureMessage(AccountInteractionFailure failure)
    {
        var interactions = AvailableInteractions();
        interactions.AddHandler = (_, _) => Task.FromException<AccountRegistrationResult?>(
            new AccountInteractionException(failure));
        using var viewModel = Phase6Data.CreateViewModel(
            new TestCalendarSyncService(),
            new MutableTimeProvider(NowUtc),
            interactions: interactions);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.AddGoogleAccountCommand.ExecuteAsync(null);

        Assert.Equal(ExpectedFailureMessage(failure), viewModel.AccountOperationMessage);
        Assert.True(viewModel.HasAccountOperationMessage);
    }

    [Fact]
    public async Task MainAddCancellationOrSuccessClearsPreviousFailureMessage()
    {
        var interactions = AvailableInteractions();
        interactions.AddHandler = (_, _) => Task.FromException<AccountRegistrationResult?>(
            new AccountInteractionException(AccountInteractionFailure.AuthenticationFailed));
        using var viewModel = Phase6Data.CreateViewModel(
            new TestCalendarSyncService(),
            new MutableTimeProvider(NowUtc),
            interactions: interactions);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        await viewModel.AddGoogleAccountCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasAccountOperationMessage);

        interactions.AddHandler = (_, _) => Task.FromResult<AccountRegistrationResult?>(null);
        await viewModel.AddGoogleAccountCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.AccountOperationMessage);
        Assert.False(viewModel.HasAccountOperationMessage);
    }

    [Fact]
    public async Task WarningReauthenticationFailureIsVisibleAndCancellationClearsIt()
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [new CalendarSelection(account.InternalAccountId, "calendar", true)]),
            AccountStates =
            [
                Phase6Data.State(
                    account.InternalAccountId,
                    SyncStatus.AuthenticationRequired,
                    error: SyncErrorCategory.AuthenticationRequired),
            ],
        };
        var interactions = AvailableInteractions();
        interactions.ReauthenticateHandler = (_, _) => Task.FromException(
            new AccountInteractionException(AccountInteractionFailure.AuthenticationFailed));
        using var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(NowUtc),
            interactions: interactions);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var warning = Assert.Single(viewModel.AccountWarnings);

        await warning.ReauthenticateCommand.ExecuteAsync(null);

        Assert.Equal(
            ExpectedFailureMessage(AccountInteractionFailure.AuthenticationFailed),
            warning.OperationMessage);
        Assert.True(warning.HasOperationMessage);

        interactions.ReauthenticateHandler = (_, _) => Task.CompletedTask;
        await warning.ReauthenticateCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, warning.OperationMessage);
        Assert.False(warning.HasOperationMessage);
    }

    [Fact]
    public void AvailableAndUnavailableReauthenticationTooltipsUseNullAndReason()
    {
        var google = Account(enabled: true, calendarVisible: true);
        var microsoft = Account(
            Guid.NewGuid(),
            ProviderKind.Microsoft,
            enabled: true,
            firstCalendarVisible: true,
            secondCalendarVisible: false);
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [google, microsoft]),
        };
        var settings = new ApplicationSettingsService(store);
        var interactions = new RecordingAccountInteractionService
        {
            IsAvailable = true,
            AvailableProviders = [ProviderKind.Google],
        };
        using var viewModel = CreateAccountViewModel(
            settings,
            new TestCalendarSyncService(),
            interactions,
            new MemoryCacheStore());

        viewModel.Initialize(store.Settings);

        Assert.Null(viewModel.GoogleUnavailableToolTip);
        Assert.False(string.IsNullOrEmpty(viewModel.MicrosoftUnavailableToolTip));
        Assert.Null(viewModel.Accounts.Single(row =>
            row.Provider == ProviderKind.Google).ProviderUnavailableToolTip);
        Assert.False(string.IsNullOrEmpty(viewModel.Accounts.Single(row =>
            row.Provider == ProviderKind.Microsoft).ProviderUnavailableToolTip));
    }

    [Fact]
    public async Task OtherAccountUpdatePreservesPendingReauthenticationAndAppliesSettingsOnce()
    {
        var first = Account(enabled: true, calendarVisible: true);
        var second = Account(enabled: true, calendarVisible: true);
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [first, second]),
        };
        var settings = new ApplicationSettingsService(store);
        var interactions = AvailableInteractions();
        var reauthenticationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReauthentication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        interactions.ReauthenticateHandler = async (_, cancellationToken) =>
        {
            reauthenticationStarted.TrySetResult();
            await releaseReauthentication.Task.WaitAsync(cancellationToken);
        };
        using var viewModel = CreateAccountViewModel(
            settings,
            new TestCalendarSyncService(),
            interactions,
            new MemoryCacheStore());
        viewModel.Initialize(store.Settings);
        var firstRow = viewModel.Accounts.Single(row =>
            row.InternalAccountId == first.InternalAccountId);
        var secondRow = viewModel.Accounts.Single(row =>
            row.InternalAccountId == second.InternalAccountId);

        var reauthentication = firstRow.ReauthenticateCommand.ExecuteAsync(null);
        await reauthenticationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(firstRow.IsBusy);
        Assert.False(firstRow.ReauthenticateCommand.CanExecute(null));
        var applicationCount = viewModel.SettingsApplicationCount;

        await secondRow.ToggleEnabledCommand.ExecuteAsync(null);

        var currentFirstRow = viewModel.Accounts.Single(row =>
            row.InternalAccountId == first.InternalAccountId);
        Assert.Same(firstRow, currentFirstRow);
        Assert.True(currentFirstRow.IsBusy);
        Assert.False(currentFirstRow.ReauthenticateCommand.CanExecute(null));
        Assert.Single(interactions.ReauthenticatedAccounts);
        Assert.Equal(applicationCount + 1, viewModel.SettingsApplicationCount);

        releaseReauthentication.TrySetResult();
        await reauthentication;
        Assert.False(currentFirstRow.IsBusy);
        Assert.True(currentFirstRow.ReauthenticateCommand.CanExecute(null));
    }

    private static AccountSettingsViewModel CreateAccountViewModel(
        IApplicationSettingsService settings,
        ICalendarSyncService sync,
        IAccountRegistrationService interactions,
        ICacheStore cache) => new(
            settings,
            sync,
            interactions,
            (IAccountReauthenticationService)interactions,
            new MemoryTokenStore(),
            cache,
            new ResourceUiTextService(),
            new ImmediateUiDispatcher(),
            new MutableTimeProvider(NowUtc),
            new FixedLocalTimeZoneProvider());

    private static RecordingAccountInteractionService AvailableInteractions() => new()
    {
        IsAvailable = true,
        AvailableProviders = [ProviderKind.Google, ProviderKind.Microsoft],
    };

    private static string ExpectedFailureMessage(AccountInteractionFailure failure)
    {
        var text = new ResourceUiTextService();
        return AccountInteractionUiText.GetFailureMessage(text, failure);
    }

    private static AccountSettings Account(bool enabled, bool calendarVisible) =>
        Account(
            Guid.NewGuid(),
            ProviderKind.Google,
            enabled,
            calendarVisible,
            secondCalendarVisible: false);

    private static AccountSettings Account(
        Guid accountId,
        bool enabled,
        bool firstCalendarVisible,
        bool secondCalendarVisible) => Account(
            accountId,
            ProviderKind.Google,
            enabled,
            firstCalendarVisible,
            secondCalendarVisible);

    private static AccountSettings Account(
        Guid accountId,
        ProviderKind provider,
        bool enabled,
        bool firstCalendarVisible,
        bool secondCalendarVisible) => new(
            accountId,
            provider,
            $"subject-{accountId:N}",
            $"Account {accountId:N}",
            $"{accountId:N}@example.invalid",
            enabled,
            $"{provider.ToString().ToLowerInvariant()}/{accountId:D}",
            [
                new CalendarSetting("first", firstCalendarVisible),
                new CalendarSetting("second", secondCalendarVisible),
            ]);

    private sealed class MemoryCacheStore : ICacheStore
    {
        public ConcurrentDictionary<Guid, AccountCache> Caches { get; } = new();

        public Task<AccountCache?> LoadAccountAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Caches.GetValueOrDefault(internalAccountId));
        }

        public Task SaveAccountAsync(
            AccountCache accountCache,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Caches[accountCache.InternalAccountId] = accountCache;
            return Task.CompletedTask;
        }

        public Task RemoveAccountAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Caches.TryRemove(internalAccountId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedLocalTimeZoneProvider : ILocalTimeZoneProvider
    {
        public TimeZoneInfo GetCurrent() => TimeZoneInfo.Utc;
    }
}
