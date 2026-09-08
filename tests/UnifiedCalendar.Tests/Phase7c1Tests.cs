using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(CultureSensitiveCollection.Name)]
public sealed class Phase7c1Tests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RegistrationPersistsOnlyReadablePrimaryCalendarsAndStartsTargetedSync()
    {
        var accountId = Guid.NewGuid();
        var provider = SuccessfulProvider(
            ProviderKind.Google,
            accountId,
            "subject-google",
            "Account A",
            "same@example.invalid",
            [
                Descriptor("primary-readable", isPrimary: true, canReadEvents: true),
                Descriptor("primary-unreadable", isPrimary: true, canReadEvents: false),
                Descriptor("secondary-readable", isPrimary: false, canReadEvents: true),
            ]);
        var settingsStore = new TestSettingsStore();
        var settings = new ApplicationSettingsService(settingsStore);
        var sync = new TestCalendarSyncService();
        var tokenStore = new RecordingTokenStore();
        var service = CreateInteractionService([provider], settings, sync, tokenStore);

        await service.AddAccountAsync(ProviderKind.Google, TestContext.Current.CancellationToken);

        var saved = Assert.Single(settingsStore.Settings.Accounts);
        Assert.Equal(accountId, saved.InternalAccountId);
        Assert.True(saved.Enabled);
        Assert.Collection(
            saved.Calendars.OrderBy(calendar => calendar.CalendarId),
            value =>
            {
                Assert.Equal("primary-readable", value.CalendarId);
                Assert.True(value.IsVisible);
            },
            value =>
            {
                Assert.Equal("primary-unreadable", value.CalendarId);
                Assert.False(value.IsVisible);
            },
            value =>
            {
                Assert.Equal("secondary-readable", value.CalendarId);
                Assert.False(value.IsVisible);
            });
        Assert.Equal(1, sync.RefreshCount);
        Assert.Equal(1, sync.RequestCount);
        Assert.Equal([accountId], Assert.Single(sync.TargetAccountIds));
        Assert.Empty(tokenStore.Removed);
    }

    [Fact]
    public async Task RegistrationWithoutPrimaryCalendarPersistsAllCalendarsOff()
    {
        var accountId = Guid.NewGuid();
        var provider = SuccessfulProvider(
            ProviderKind.Google,
            accountId,
            "subject",
            "Account",
            "account@example.invalid",
            [Descriptor("secondary", isPrimary: false, canReadEvents: true)]);
        var settingsStore = new TestSettingsStore();
        var service = CreateInteractionService(
            [provider],
            new ApplicationSettingsService(settingsStore),
            new TestCalendarSyncService(),
            new RecordingTokenStore());

        await service.AddAccountAsync(ProviderKind.Google, TestContext.Current.CancellationToken);

        Assert.All(Assert.Single(settingsStore.Settings.Accounts).Calendars, value =>
            Assert.False(value.IsVisible));
    }

    [Fact]
    public async Task DuplicateProviderSubjectIsRejectedWithoutChangingExistingAccount()
    {
        var existing = Account(ProviderKind.Google, "duplicate", "Existing", "old@example.invalid");
        var original = new AppSettings(accounts: [existing]);
        var store = new TestSettingsStore { Settings = original };
        var tokenStore = new RecordingTokenStore();
        var newAccountId = Guid.NewGuid();
        var provider = SuccessfulProvider(
            ProviderKind.Google,
            newAccountId,
            existing.ProviderSubjectId,
            "Replacement",
            "new@example.invalid",
            [Descriptor("primary", true, true)]);
        var service = CreateInteractionService(
            [provider],
            new ApplicationSettingsService(store),
            new TestCalendarSyncService(),
            tokenStore);

        var exception = await Assert.ThrowsAsync<AccountInteractionException>(() =>
            service.AddAccountAsync(ProviderKind.Google, TestContext.Current.CancellationToken));

        Assert.Equal(AccountInteractionFailure.DuplicateAccount, exception.Failure);
        Assert.Same(original, store.Settings);
        Assert.Equal(
            (ProviderKind.Google, newAccountId),
            Assert.Single(tokenStore.Removed));
    }

    [Fact]
    public async Task SameEmailCanBeRegisteredForDifferentProviders()
    {
        var google = SuccessfulProvider(
            ProviderKind.Google,
            Guid.NewGuid(),
            "same-subject",
            "Google Account",
            "same@example.invalid",
            [Descriptor("google-primary", true, true)]);
        var microsoft = SuccessfulProvider(
            ProviderKind.Microsoft,
            Guid.NewGuid(),
            "same-subject",
            "Microsoft Account",
            "same@example.invalid",
            [Descriptor("microsoft-primary", true, true)]);
        var store = new TestSettingsStore();
        var service = CreateInteractionService(
            [google, microsoft],
            new ApplicationSettingsService(store),
            new TestCalendarSyncService(),
            new RecordingTokenStore());

        await service.AddAccountAsync(ProviderKind.Google, TestContext.Current.CancellationToken);
        await service.AddAccountAsync(ProviderKind.Microsoft, TestContext.Current.CancellationToken);

        Assert.Equal(2, store.Settings.Accounts.Count);
        Assert.Equal(
            [ProviderKind.Google, ProviderKind.Microsoft],
            store.Settings.Accounts.Select(account => account.Provider));
    }

    [Fact]
    public async Task CalendarDiscoveryFailureRollsBackTokenAndDoesNotRegisterAccount()
    {
        var accountId = Guid.NewGuid();
        var provider = SuccessfulProvider(
            ProviderKind.Google,
            accountId,
            "subject",
            "Private display name",
            "private@example.invalid",
            []);
        provider.ListCalendars = (_, _) => throw new ProviderException(
            new ProviderError(ProviderErrorCategory.Network));
        var store = new TestSettingsStore();
        var tokenStore = new RecordingTokenStore();
        var service = CreateInteractionService(
            [provider],
            new ApplicationSettingsService(store),
            new TestCalendarSyncService(),
            tokenStore);

        var exception = await Assert.ThrowsAsync<AccountInteractionException>(() =>
            service.AddAccountAsync(ProviderKind.Google, TestContext.Current.CancellationToken));

        Assert.Equal(AccountInteractionFailure.CalendarDiscoveryFailed, exception.Failure);
        Assert.Empty(store.Settings.Accounts);
        Assert.Equal((ProviderKind.Google, accountId), Assert.Single(tokenStore.Removed));
    }

    [Fact]
    public async Task ProviderCancellationLeavesSettingsAndUiErrorUnchanged()
    {
        var provider = new ScriptedCalendarProvider(ProviderKind.Google)
        {
            Authenticate = _ => Task.FromResult(AuthAccountResult.Failure(
                new ProviderError(ProviderErrorCategory.Cancelled))),
        };
        var store = new TestSettingsStore();
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var tokenStore = new RecordingTokenStore();
        var service = CreateInteractionService([provider], settings, sync, tokenStore);
        using var viewModel = CreateAccountViewModel(
            settings,
            sync,
            service,
            service,
            tokenStore,
            new RecordingCacheStore());
        viewModel.Initialize(store.Settings);

        await viewModel.AddGoogleAccountCommand.ExecuteAsync(null);

        Assert.Empty(store.Settings.Accounts);
        Assert.Empty(viewModel.Accounts);
        Assert.Equal(string.Empty, viewModel.OperationMessage);
        Assert.Empty(tokenStore.Removed);
    }

    [Fact]
    public async Task ClosingSettingsAfterAuthenticationSuccessStillCompletesRegistration()
    {
        var accountId = Guid.NewGuid();
        var listStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseList = new TaskCompletionSource<IReadOnlyList<CalendarDescriptor>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = SuccessfulProvider(
            ProviderKind.Google,
            accountId,
            "subject",
            "Account",
            "account@example.invalid",
            []);
        provider.ListCalendars = (_, cancellationToken) =>
        {
            Assert.False(cancellationToken.CanBeCanceled);
            listStarted.TrySetResult();
            return releaseList.Task;
        };
        var store = new TestSettingsStore();
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var tokenStore = new RecordingTokenStore();
        var service = CreateInteractionService([provider], settings, sync, tokenStore);
        var viewModel = CreateAccountViewModel(
            settings,
            sync,
            service,
            service,
            tokenStore,
            new RecordingCacheStore());
        viewModel.Initialize(store.Settings);

        var execution = viewModel.AddGoogleAccountCommand.ExecuteAsync(null);
        await listStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsAddingGoogle);
        Assert.False(viewModel.AddGoogleAccountCommand.CanExecute(null));
        Assert.NotEmpty(viewModel.AuthenticationProgressText);
        viewModel.Dispose();
        releaseList.SetResult([Descriptor("primary", true, true)]);
        await execution;

        Assert.Equal(accountId, Assert.Single(store.Settings.Accounts).InternalAccountId);
        Assert.Equal([accountId], Assert.Single(sync.TargetAccountIds));
    }

    [Fact]
    public async Task ReauthenticationStartsOnlyTargetAccountAfterSuccessAndNoneAfterFailure()
    {
        var account = Account(ProviderKind.Google, "subject", "Account", "account@example.invalid");
        var store = new TestSettingsStore { Settings = new AppSettings(accounts: [account]) };
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var provider = new ScriptedCalendarProvider(ProviderKind.Google)
        {
            Reauthenticate = (value, _) => Task.FromResult(AuthAccountResult.Success(
                value.InternalAccountId,
                value.ProviderSubjectId,
                value.DisplayName,
                value.Email)),
        };
        var service = CreateInteractionService(
            [provider], settings, sync, new RecordingTokenStore());

        await service.ReauthenticateAsync(account.InternalAccountId, TestContext.Current.CancellationToken);

        Assert.Equal([account.InternalAccountId], Assert.Single(sync.TargetAccountIds));

        provider.Reauthenticate = (_, _) => Task.FromResult(AuthAccountResult.Failure(
            new ProviderError(ProviderErrorCategory.AuthenticationRequired)));
        var exception = await Assert.ThrowsAsync<AccountInteractionException>(() =>
            service.ReauthenticateAsync(account.InternalAccountId, TestContext.Current.CancellationToken));
        Assert.Equal(AccountInteractionFailure.AuthenticationFailed, exception.Failure);
        Assert.Single(sync.TargetAccountIds);
    }

    [Fact]
    public void AccountListSortsByProviderAndDisplayNameAndFormatsLocalState()
    {
        var microsoft = Account(
            ProviderKind.Microsoft, "microsoft", "A Microsoft", "microsoft@example.invalid");
        var googleZ = Account(ProviderKind.Google, "google-z", "Z Google", "z@example.invalid");
        var googleA = Account(ProviderKind.Google, "google-a", "A Google", "a@example.invalid");
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [microsoft, googleZ, googleA]),
        };
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService
        {
            AccountStates =
            [
                State(googleA.InternalAccountId, SyncStatus.Succeeded, NowUtc.AddMinutes(-90)),
                State(googleZ.InternalAccountId, SyncStatus.AuthenticationRequired, null),
            ],
        };
        var interactions = new RecordingAccountInteractionService
        {
            IsAvailable = true,
            AvailableProviders = [ProviderKind.Google, ProviderKind.Microsoft],
        };
        using var viewModel = CreateAccountViewModel(
            settings,
            sync,
            interactions,
            interactions,
            new RecordingTokenStore(),
            new RecordingCacheStore());

        viewModel.Initialize(store.Settings);

        Assert.Equal(
            [googleA.InternalAccountId, googleZ.InternalAccountId, microsoft.InternalAccountId],
            viewModel.Accounts.Select(value => value.InternalAccountId));
        Assert.Contains("19:30", viewModel.Accounts[0].LastSuccessfulSyncText, StringComparison.Ordinal);
        Assert.Equal("有効", viewModel.Accounts[0].EnabledStateText);
        Assert.True(viewModel.Accounts[1].HasAuthenticationError);
        Assert.Contains("未取得", viewModel.Accounts[2].LastSuccessfulSyncText, StringComparison.Ordinal);

        sync.PublishState(State(googleZ.InternalAccountId, SyncStatus.Syncing, null));

        Assert.Equal(SyncStatus.Syncing, viewModel.Accounts[1].Status);
        Assert.Contains("同期中", viewModel.Accounts[1].SyncStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.Accounts[1].HasAuthenticationError);
    }

    [Fact]
    public void AccountListUsesStableIdOrderingWhenNamesAndEmailsAreEqual()
    {
        var lowerId = Guid.Parse("10000000-0000-0000-0000-000000000000");
        var higherId = Guid.Parse("20000000-0000-0000-0000-000000000000");
        var first = new AccountSettings(
            higherId,
            ProviderKind.Google,
            "subject-higher",
            "Same Name",
            "same@example.invalid",
            true,
            $"google/{higherId:D}",
            []);
        var second = new AccountSettings(
            lowerId,
            ProviderKind.Google,
            "subject-lower",
            "Same Name",
            "same@example.invalid",
            true,
            $"google/{lowerId:D}",
            []);
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [first, second]),
        };
        var interactions = AvailableInteractions();
        using var viewModel = CreateAccountViewModel(
            new ApplicationSettingsService(store),
            new TestCalendarSyncService(),
            interactions,
            interactions,
            new RecordingTokenStore(),
            new RecordingCacheStore());

        viewModel.Initialize(store.Settings);

        Assert.Equal([lowerId, higherId], viewModel.Accounts.Select(value => value.InternalAccountId));
    }

    [Fact]
    public async Task AuthenticationFailureLogContainsNoAccountPii()
    {
        const string displayName = "Sensitive Display";
        const string email = "sensitive-auth@example.invalid";
        const string subject = "sensitive-auth-subject";
        var provider = new ScriptedCalendarProvider(ProviderKind.Google)
        {
            Authenticate = _ => throw new InvalidOperationException(
                $"Provider failed for {displayName} {email} {subject}"),
        };
        var sink = new CollectingLogSink();
        var original = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        try
        {
            var service = CreateInteractionService(
                [provider],
                new ApplicationSettingsService(new TestSettingsStore()),
                new TestCalendarSyncService(),
                new RecordingTokenStore());

            await Assert.ThrowsAsync<AccountInteractionException>(() =>
                service.AddAccountAsync(ProviderKind.Google, TestContext.Current.CancellationToken));

            var rendered = string.Join(Environment.NewLine, sink.Events.Select(value => value.RenderMessage()));
            Assert.Contains("AccountAuthenticationFailed", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(displayName, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(email, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(subject, rendered, StringComparison.Ordinal);
        }
        finally
        {
            Log.Logger = original;
        }
    }

    [Fact]
    public void ProviderSpecificButtonsRemainVisibleThroughCommandsAndExplainUnavailableProvider()
    {
        var settings = new ApplicationSettingsService(new TestSettingsStore());
        var interactions = new RecordingAccountInteractionService
        {
            IsAvailable = true,
            AvailableProviders = [ProviderKind.Google],
        };
        using var viewModel = CreateAccountViewModel(
            settings,
            new TestCalendarSyncService(),
            interactions,
            interactions,
            new RecordingTokenStore(),
            new RecordingCacheStore());

        Assert.True(viewModel.AddGoogleAccountCommand.CanExecute(null));
        Assert.False(viewModel.AddMicrosoftAccountCommand.CanExecute(null));
        Assert.Null(viewModel.GoogleUnavailableToolTip);
        Assert.False(string.IsNullOrEmpty(viewModel.MicrosoftUnavailableToolTip));
    }

    [Fact]
    public async Task ToggleDisablesImmediatelyWithoutCleanupAndEnableStartsTargetedManualSync()
    {
        var account = Account(ProviderKind.Google, "subject", "Account", "account@example.invalid");
        var store = new TestSettingsStore { Settings = new AppSettings(accounts: [account]) };
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var interactions = new RecordingAccountInteractionService
        {
            IsAvailable = true,
            AvailableProviders = [ProviderKind.Google],
        };
        var tokens = new RecordingTokenStore();
        var cache = new RecordingCacheStore();
        using var viewModel = CreateAccountViewModel(
            settings, sync, interactions, interactions, tokens, cache);
        viewModel.Initialize(store.Settings);

        await viewModel.Accounts[0].ToggleEnabledCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(store.Settings.Accounts).Enabled);
        Assert.Equal(1, sync.RefreshCount);
        Assert.Equal(0, sync.RequestCount);
        Assert.Empty(tokens.Removed);
        Assert.Empty(cache.Removed);

        await viewModel.Accounts[0].ToggleEnabledCommand.ExecuteAsync(null);

        Assert.True(Assert.Single(store.Settings.Accounts).Enabled);
        Assert.Equal(2, sync.RefreshCount);
        Assert.Equal([account.InternalAccountId], Assert.Single(sync.TargetAccountIds));
        Assert.Equal(SyncTriggerReason.Manual, sync.LastReason);
    }

    [Fact]
    public async Task DeleteDeclineChangesNothing()
    {
        var account = Account(ProviderKind.Google, "subject", "Visible Name", "account@example.invalid");
        var store = new TestSettingsStore { Settings = new AppSettings(accounts: [account]) };
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var tokens = new RecordingTokenStore();
        var cache = new RecordingCacheStore();
        var confirmation = new RecordingDeleteConfirmation(false);
        var interactions = AvailableInteractions();
        using var viewModel = CreateAccountViewModel(
            settings, sync, interactions, interactions, tokens, cache, confirmation);
        viewModel.Initialize(store.Settings);

        await viewModel.Accounts[0].DeleteCommand.ExecuteAsync(null);

        Assert.Equal("Visible Name", Assert.Single(confirmation.DisplayNames));
        Assert.Single(store.Settings.Accounts);
        Assert.Equal(0, sync.RefreshCount);
        Assert.Empty(tokens.Removed);
        Assert.Empty(cache.Removed);
    }

    [Fact]
    public async Task ConfirmedDeleteRemovesSettingsFirstAndCleansOnlyTargetAccount()
    {
        var target = Account(ProviderKind.Google, "target", "Target", "target@example.invalid");
        var other = Account(ProviderKind.Microsoft, "other", "Other", "other@example.invalid");
        var store = new TestSettingsStore { Settings = new AppSettings(accounts: [target, other]) };
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var tokens = new RecordingTokenStore
        {
            BeforeRemove = (_, id) => Assert.DoesNotContain(
                store.Settings.Accounts,
                account => account.InternalAccountId == id),
        };
        var cache = new RecordingCacheStore();
        var interactions = AvailableInteractions();
        using var viewModel = CreateAccountViewModel(
            settings,
            sync,
            interactions,
            interactions,
            tokens,
            cache,
            new RecordingDeleteConfirmation(true));
        viewModel.Initialize(store.Settings);

        await viewModel.Accounts.Single(value =>
            value.InternalAccountId == target.InternalAccountId).DeleteCommand.ExecuteAsync(null);

        Assert.Equal(other.InternalAccountId, Assert.Single(store.Settings.Accounts).InternalAccountId);
        Assert.Equal((target.Provider, target.InternalAccountId), Assert.Single(tokens.Removed));
        Assert.Equal(target.InternalAccountId, Assert.Single(cache.Removed));
        Assert.Equal(1, sync.RefreshCount);
        Assert.Equal(other.InternalAccountId, Assert.Single(viewModel.Accounts).InternalAccountId);
    }

    [Fact]
    public async Task CleanupFailureKeepsDeletionAndShowsSafeWarningWithoutPiiLogs()
    {
        const string displayName = "Sensitive Person";
        const string email = "secret@example.invalid";
        const string subject = "secret-provider-subject";
        var account = Account(ProviderKind.Google, subject, displayName, email);
        var store = new TestSettingsStore { Settings = new AppSettings(accounts: [account]) };
        var settings = new ApplicationSettingsService(store);
        var tokens = new RecordingTokenStore { RemoveException = new IOException("token failure") };
        var cache = new RecordingCacheStore { RemoveException = new IOException("cache failure") };
        var sink = new CollectingLogSink();
        var original = Log.Logger;
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        try
        {
            var interactions = AvailableInteractions();
            using var viewModel = CreateAccountViewModel(
                settings,
                new TestCalendarSyncService(),
                interactions,
                interactions,
                tokens,
                cache,
                new RecordingDeleteConfirmation(true));
            viewModel.Initialize(store.Settings);

            await viewModel.Accounts[0].DeleteCommand.ExecuteAsync(null);

            Assert.Empty(store.Settings.Accounts);
            Assert.Contains("一部を削除できませんでした", viewModel.OperationMessage, StringComparison.Ordinal);
            var rendered = string.Join(Environment.NewLine, sink.Events.Select(value => value.RenderMessage()));
            Assert.Contains("AccountDeleteCleanupFailed", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(displayName, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(email, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(subject, rendered, StringComparison.Ordinal);
        }
        finally
        {
            Log.Logger = original;
        }
    }

    [Fact]
    public async Task SettingsSaveFailureStopsDeletionBeforeTokenOrCacheCleanup()
    {
        var account = Account(ProviderKind.Google, "subject", "Account", "account@example.invalid");
        var store = new FailingSettingsStore(new AppSettings(accounts: [account]));
        var settings = new ApplicationSettingsService(store);
        var tokens = new RecordingTokenStore();
        var cache = new RecordingCacheStore();
        var interactions = AvailableInteractions();
        using var viewModel = CreateAccountViewModel(
            settings,
            new TestCalendarSyncService(),
            interactions,
            interactions,
            tokens,
            cache,
            new RecordingDeleteConfirmation(true));
        viewModel.Initialize(store.Settings);

        await viewModel.Accounts[0].DeleteCommand.ExecuteAsync(null);

        Assert.Single(store.Settings.Accounts);
        Assert.Empty(tokens.Removed);
        Assert.Empty(cache.Removed);
        Assert.NotEmpty(viewModel.OperationMessage);
    }

    [Fact]
    public async Task RefreshFromSettingsPrunesRemovedStateAndCacheWithoutNetworkAccess()
    {
        var removed = Account(ProviderKind.Google, "removed", "Removed", "removed@example.invalid");
        var retained = Account(ProviderKind.Google, "retained", "Retained", "retained@example.invalid");
        var added = Account(ProviderKind.Google, "added", "Added", "added@example.invalid");
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [removed, retained]),
        };
        var cache = new RecordingCacheStore();
        cache.Caches[removed.InternalAccountId] = CacheWithEvent(removed, "removed-event");
        cache.Caches[retained.InternalAccountId] = CacheWithEvent(retained, "retained-event");
        var provider = new ScriptedCalendarProvider(ProviderKind.Google);
        await using var service = new CalendarSyncService(
            [provider],
            store,
            cache,
            new MutableTimeProvider(NowUtc),
            localTimeZone: TimeZoneInfo.Utc);
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, service.CurrentSnapshot.Events.Count);
        var eventCouldReadSnapshot = false;
        service.SnapshotChanged += (_, _) =>
        {
            var read = Task.Run(() => service.CurrentSnapshot);
            eventCouldReadSnapshot = read.Wait(TimeSpan.FromSeconds(2));
        };
        store.Settings = new AppSettings(accounts:
        [
            CopyWithEnabled(retained, enabled: false),
            added,
        ]);

        await service.RefreshFromSettingsAsync(TestContext.Current.CancellationToken);

        Assert.True(eventCouldReadSnapshot);
        Assert.Empty(service.CurrentSnapshot.Events);
        Assert.Equal(
            new[] { added.InternalAccountId, retained.InternalAccountId }.Order(),
            service.AccountStates.Select(value => value.InternalAccountId).Order());
        Assert.Equal(SyncStatus.NotStarted, service.AccountStates.Single(value =>
            value.InternalAccountId == added.InternalAccountId).Status);
        Assert.Equal(0, provider.ListCalls);
        Assert.Equal(0, provider.GetEventsCalls);

        store.Settings = new AppSettings(accounts:
        [
            CopyWithEnabled(retained, enabled: false),
            removed,
            added,
        ]);
        await service.RefreshFromSettingsAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            service.CurrentSnapshot.Events,
            value => value.Key.InternalAccountId == removed.InternalAccountId);
    }

    private static AccountInteractionService CreateInteractionService(
        IEnumerable<ICalendarProvider> providers,
        IApplicationSettingsService settings,
        ICalendarSyncService sync,
        ITokenStore tokenStore) => new(
            providers,
            settings,
            sync,
            tokenStore,
            new AppPaths(Path.Combine(Path.GetTempPath(), $"UnifiedCalendar-{Guid.NewGuid():N}")));

    private static AccountSettingsViewModel CreateAccountViewModel(
        IApplicationSettingsService settings,
        ICalendarSyncService sync,
        IAccountRegistrationService registration,
        IAccountReauthenticationService reauthentication,
        ITokenStore tokens,
        ICacheStore cache,
        IAccountDeleteConfirmationService? confirmation = null) => new(
            settings,
            sync,
            registration,
            reauthentication,
            tokens,
            cache,
            new ResourceUiTextService(),
            new ImmediateUiDispatcher(),
            new MutableTimeProvider(NowUtc),
            new FixedLocalTimeZoneProvider(TimeZoneInfo.CreateCustomTimeZone(
                "Phase7cJst",
                TimeSpan.FromHours(9),
                "Phase7cJst",
                "Phase7cJst")),
            confirmation);

    private static RecordingAccountInteractionService AvailableInteractions() => new()
    {
        IsAvailable = true,
        AvailableProviders = [ProviderKind.Google, ProviderKind.Microsoft],
    };

    private static ScriptedCalendarProvider SuccessfulProvider(
        ProviderKind provider,
        Guid accountId,
        string providerSubjectId,
        string displayName,
        string email,
        IReadOnlyList<CalendarDescriptor> calendars) => new(provider)
        {
            Authenticate = _ => Task.FromResult(AuthAccountResult.Success(
                accountId,
                providerSubjectId,
                displayName,
                email)),
            ListCalendars = (_, _) => Task.FromResult(calendars),
        };

    private static CalendarDescriptor Descriptor(
        string id,
        bool isPrimary,
        bool canReadEvents) => new(id, id, $"Calendar {id}", isPrimary, canReadEvents, null);

    private static AccountSettings Account(
        ProviderKind provider,
        string subject,
        string displayName,
        string email)
    {
        var id = Guid.NewGuid();
        return new AccountSettings(
            id,
            provider,
            subject,
            displayName,
            email,
            true,
            $"{provider.ToString().ToLowerInvariant()}/{id:D}",
            [new CalendarSetting("primary", true)]);
    }

    private static AccountSettings CopyWithEnabled(AccountSettings account, bool enabled) => new(
        account.InternalAccountId,
        account.Provider,
        account.ProviderSubjectId,
        account.DisplayName,
        account.Email,
        enabled,
        account.TokenRef,
        account.Calendars);

    private static AccountSyncState State(
        Guid accountId,
        SyncStatus status,
        DateTimeOffset? lastSuccessUtc) => new(
            accountId,
            status,
            NowUtc,
            lastSuccessUtc,
            [new CalendarSyncState(
                "primary",
                status,
                NowUtc,
                lastSuccessUtc,
                null,
                status == SyncStatus.AuthenticationRequired
                    ? SyncErrorCategory.AuthenticationRequired
                    : SyncErrorCategory.None)]);

    private static AccountCache CacheWithEvent(AccountSettings account, string eventId)
    {
        var calendarId = account.Calendars[0].CalendarId;
        var calendarEvent = new CalendarEvent(
            new EventKey(account.Provider, account.InternalAccountId, calendarId, eventId),
            $"Event {eventId}",
            new TimedEventTiming(NowUtc.AddHours(1), NowUtc.AddHours(2)),
            "Calendar");
        return new AccountCache(
            account.InternalAccountId,
            account.Provider,
            NowUtc,
            [new CachedCalendar(calendarId, "Calendar", null, NowUtc, [calendarEvent])]);
    }

    private sealed class ScriptedCalendarProvider(ProviderKind provider) : ICalendarProvider
    {
        public ProviderKind Provider { get; } = provider;

        public int ListCalls { get; private set; }

        public int GetEventsCalls { get; private set; }

        public Func<CancellationToken, Task<AuthAccountResult>> Authenticate { get; set; } =
            _ => Task.FromResult(AuthAccountResult.Failure(
                new ProviderError(ProviderErrorCategory.Cancelled)));

        public Func<CalendarAccount, CancellationToken, Task<AuthAccountResult>> Reauthenticate
            { get; set; } = (_, _) => Task.FromResult(AuthAccountResult.Failure(
                new ProviderError(ProviderErrorCategory.Cancelled)));

        public Func<CalendarAccount, CancellationToken, Task<IReadOnlyList<CalendarDescriptor>>>
            ListCalendars { get; set; } = (_, _) =>
                Task.FromResult<IReadOnlyList<CalendarDescriptor>>([]);

        public Task<AuthAccountResult> AuthenticateAsync(CancellationToken cancellationToken) =>
            Authenticate(cancellationToken);

        public Task<AuthAccountResult> ReauthenticateAsync(
            CalendarAccount account,
            CancellationToken cancellationToken) => Reauthenticate(account, cancellationToken);

        public Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
            CalendarAccount account,
            CancellationToken cancellationToken)
        {
            ListCalls++;
            return ListCalendars(account, cancellationToken);
        }

        public Task<ProviderCalendarResult> GetEventsAsync(
            CalendarAccount account,
            CalendarDescriptor calendar,
            TimeRangeUtc range,
            CancellationToken cancellationToken)
        {
            GetEventsCalls++;
            return Task.FromResult(ProviderCalendarResult.Success([]));
        }
    }

    private sealed class RecordingTokenStore : ITokenStore
    {
        public List<(ProviderKind Provider, Guid AccountId)> Removed { get; } = [];

        public Action<ProviderKind, Guid>? BeforeRemove { get; init; }

        public Exception? RemoveException { get; init; }

        public Task<byte[]?> ReadAsync(
            ProviderKind provider,
            Guid internalAccountId,
            CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);

        public Task WriteAsync(
            ProviderKind provider,
            Guid internalAccountId,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(
            ProviderKind provider,
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeRemove?.Invoke(provider, internalAccountId);
            Removed.Add((provider, internalAccountId));
            return RemoveException is null
                ? Task.CompletedTask
                : Task.FromException(RemoveException);
        }

        public Task QuarantineAsync(
            ProviderKind provider,
            Guid internalAccountId,
            TokenQuarantineReason reason,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingCacheStore : ICacheStore
    {
        public ConcurrentDictionary<Guid, AccountCache> Caches { get; } = new();

        public List<Guid> Removed { get; } = [];

        public Exception? RemoveException { get; init; }

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
            Removed.Add(internalAccountId);
            return RemoveException is null
                ? Task.CompletedTask
                : Task.FromException(RemoveException);
        }
    }

    private sealed class RecordingDeleteConfirmation(bool result) : IAccountDeleteConfirmationService
    {
        public List<string> DisplayNames { get; } = [];

        public bool ConfirmDelete(string accountDisplayName)
        {
            DisplayNames.Add(accountDisplayName);
            return result;
        }
    }

    private sealed class FixedLocalTimeZoneProvider(TimeZoneInfo timeZone) : ILocalTimeZoneProvider
    {
        public TimeZoneInfo GetCurrent() => timeZone;
    }

    private sealed class FailingSettingsStore(AppSettings settings) : ISettingsStore
    {
        public AppSettings Settings { get; } = settings;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveAsync(
            AppSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("settings failure"));
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
