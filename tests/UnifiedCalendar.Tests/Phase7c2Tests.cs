using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Media;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.App.Presentation;
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
public sealed class Phase7c2Tests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 9, 6, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshLoadsAllAccountsInRequiredOrderAndNeverPublishesCalendarId()
    {
        var googleZulu = CreateAccount(ProviderKind.Google, "Zulu", ("private-google-z", true));
        var microsoft = CreateAccount(ProviderKind.Microsoft, "Alpha", ("private-microsoft", true));
        var googleAlpha = CreateAccount(ProviderKind.Google, "Alpha", ("private-google-a", false));
        var settings = new AppSettings(accounts: [microsoft, googleZulu, googleAlpha]);
        var catalog = new ScriptedCatalogService
        {
            ListHandler = (account, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>(
            [
                Descriptor(
                    account.Calendars[0].CalendarId,
                    $"Calendar for {account.DisplayName}",
                    sourceColor: account == googleZulu ? RgbColor.Parse("#336699") : null),
            ]),
        };
        using var subject = CreateSubject(settings, catalog);

        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [googleAlpha.InternalAccountId, googleZulu.InternalAccountId, microsoft.InternalAccountId],
            subject.ViewModel.AccountGroups.Select(group => group.InternalAccountId));
        Assert.Equal(3, catalog.ListCalls.Count);
        var coloredRow = Assert.Single(subject.ViewModel.AccountGroups[1].Calendars);
        Assert.Equal("Calendar for Zulu", coloredRow.Name);
        Assert.Equal("Zulu", coloredRow.AccountDisplayIdentity);
        Assert.Equal("Google Calendar", coloredRow.ProviderText);
        Assert.NotNull(coloredRow.SourceColorBrush);
        Assert.Null(Assert.Single(subject.ViewModel.AccountGroups[0].Calendars).SourceColorBrush);

        var publicProperties = typeof(CalendarListItemViewModel).GetProperties(
            BindingFlags.Instance | BindingFlags.Public);
        Assert.DoesNotContain(publicProperties, property => property.Name == "CalendarId");
        var renderedStrings = subject.ViewModel.AccountGroups
            .SelectMany(group => group.Calendars)
            .SelectMany(row => publicProperties
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => property.GetValue(row) as string))
            .Where(value => value is not null)
            .ToArray();
        Assert.DoesNotContain(renderedStrings, value =>
            value is "private-google-z" or "private-google-a" or "private-microsoft");
    }

    [Fact]
    public async Task FailedRefreshKeepsPreviousCatalogAndUsesCurrentVisibility()
    {
        var account = CreateAccount(ProviderKind.Google, "Account", ("calendar", true));
        var call = 0;
        var catalog = new ScriptedCatalogService
        {
            ListHandler = (_, _) => ++call == 1
                ? Task.FromResult<IReadOnlyList<CalendarDescriptor>>([Descriptor("calendar", "Fetched name")])
                : Task.FromException<IReadOnlyList<CalendarDescriptor>>(new IOException("safe")),
        };
        using var subject = CreateSubject(new AppSettings(accounts: [account]), catalog);
        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var originalRow = Assert.Single(Assert.Single(subject.ViewModel.AccountGroups).Calendars);

        await subject.Settings.UpdateAsync(current => CopyWithVisibility(
            current,
            account.InternalAccountId,
            "calendar",
            false), TestContext.Current.CancellationToken);
        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var group = Assert.Single(subject.ViewModel.AccountGroups);
        var row = Assert.Single(group.Calendars);
        Assert.Same(originalRow, row);
        Assert.Equal("Fetched name", row.Name);
        Assert.False(row.IsVisible);
        Assert.True(group.HasDiscoveryError);
        Assert.Equal(2, catalog.ListCalls.Count);
    }

    [Fact]
    public async Task FirstFailedRefreshFallsBackToSettingsWithAndWithoutCache()
    {
        var cachedAccount = CreateAccount(ProviderKind.Google, "Cached", ("cached-id", true));
        var uncachedAccount = CreateAccount(ProviderKind.Microsoft, "Uncached", ("uncached-id", false));
        var catalog = new ScriptedCatalogService
        {
            ListHandler = (_, _) => Task.FromException<IReadOnlyList<CalendarDescriptor>>(
                new IOException("safe")),
        };
        catalog.Caches[cachedAccount.InternalAccountId] = new AccountCache(
            cachedAccount.InternalAccountId,
            cachedAccount.Provider,
            NowUtc,
            [new CachedCalendar(
                "cached-id",
                "Name from cache",
                RgbColor.Parse("#112233"),
                NowUtc,
                [])]);
        using var subject = CreateSubject(
            new AppSettings(accounts: [cachedAccount, uncachedAccount]),
            catalog);

        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var cachedRow = Assert.Single(subject.ViewModel.AccountGroups[0].Calendars);
        Assert.Equal("Name from cache", cachedRow.Name);
        Assert.True(cachedRow.IsVisible);
        Assert.NotNull(cachedRow.SourceColorBrush);
        var uncachedRow = Assert.Single(subject.ViewModel.AccountGroups[1].Calendars);
        Assert.Equal("名称を取得できていません", uncachedRow.Name);
        Assert.False(uncachedRow.IsVisible);
        Assert.All(subject.ViewModel.AccountGroups, group => Assert.True(group.HasDiscoveryError));
    }

    [Fact]
    public async Task BackgroundSyncStateDoesNotReplaceDisplayedCatalog()
    {
        var account = CreateAccount(ProviderKind.Google, "Account", ("calendar", true));
        var catalogName = "Original catalog";
        var catalog = new ScriptedCatalogService
        {
            ListHandler = (_, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>(
                [Descriptor("calendar", catalogName)]),
        };
        var sync = new TestCalendarSyncService();
        using var subject = CreateSubject(new AppSettings(accounts: [account]), catalog, sync);
        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);
        catalogName = "Changed behind the settings window";

        sync.PublishState(Phase6Data.State(account.InternalAccountId, SyncStatus.Succeeded));

        Assert.Equal(
            "Original catalog",
            Assert.Single(Assert.Single(subject.ViewModel.AccountGroups).Calendars).Name);
        Assert.Single(catalog.ListCalls);
    }

    [Fact]
    public async Task UnreadableCalendarShowsDistinctReasonCanOnlyBeTurnedOffAndClearsWarning()
    {
        var account = CreateAccount(ProviderKind.Google, "Account", ("calendar", true));
        var catalog = SuccessfulCatalog(account, Descriptor("calendar", "Unreadable", canReadEvents: false));
        var sync = new TestCalendarSyncService
        {
            AccountStates =
            [
                Phase6Data.State(
                    account.InternalAccountId,
                    SyncStatus.Failed,
                    calendarId: "calendar",
                    error: SyncErrorCategory.Network),
            ],
        };
        using var subject = CreateSubject(new AppSettings(accounts: [account]), catalog, sync);
        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(Assert.Single(subject.ViewModel.AccountGroups).Calendars);

        Assert.True(row.ShowReadUnavailableReason);
        Assert.True(row.HasSyncWarning);
        Assert.NotEqual(row.ReadUnavailableText, row.SyncWarningToolTip);
        Assert.True(row.ToggleVisibilityCommand.CanExecute(null));

        await row.ToggleVisibilityCommand.ExecuteAsync(null);

        Assert.False(row.IsVisible);
        Assert.False(row.HasSyncWarning);
        Assert.False(row.ToggleVisibilityCommand.CanExecute(null));
        Assert.Empty(sync.TargetAccountIds);
        Assert.Single(catalog.ListCalls);
        Assert.Single(subject.Store.Settings.Accounts[0].Calendars);
    }

    [Fact]
    public async Task TurningCalendarOnStartsOnlyTargetAccountSyncWithoutWaitingForIt()
    {
        var account = CreateAccount(ProviderKind.Microsoft, "Account", ("calendar", false));
        var syncCompletion = new TaskCompletionSource<SyncRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new TestCalendarSyncService
        {
            RequestHandler = (reason, _) => syncCompletion.Task,
        };
        var catalog = SuccessfulCatalog(account, Descriptor("calendar", "Calendar"));
        var subject = CreateSubject(new AppSettings(accounts: [account]), catalog, sync);
        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(Assert.Single(subject.ViewModel.AccountGroups).Calendars);

        var toggle = row.ToggleVisibilityCommand.ExecuteAsync(null);
        await toggle.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(row.IsVisible);
        Assert.Equal(1, sync.RefreshCount);
        Assert.Equal([account.InternalAccountId], Assert.Single(sync.TargetAccountIds));
        Assert.False(syncCompletion.Task.IsCompleted);
        subject.ViewModel.Dispose();
        Assert.True(subject.Store.Settings.Accounts[0].Calendars[0].IsVisible);
        syncCompletion.SetResult(new SyncRunResult(SyncTriggerReason.Manual, []));
    }

    [Fact]
    public async Task NewlyDiscoveredCalendarIsPersistedWhenTurnedOnAndRetainedWhenTurnedOff()
    {
        var account = CreateAccount(ProviderKind.Google, "Account", ("known", true));
        var catalog = SuccessfulCatalog(
            account,
            Descriptor("known", "Known"),
            Descriptor("new", "New"));
        using var subject = CreateSubject(new AppSettings(accounts: [account]), catalog);
        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var newRow = Assert.Single(
            Assert.Single(subject.ViewModel.AccountGroups).Calendars,
            row => row.Name == "New");

        Assert.False(newRow.IsVisible);
        await newRow.ToggleVisibilityCommand.ExecuteAsync(null);

        var enabled = Assert.Single(
            subject.Store.Settings.Accounts[0].Calendars,
            calendar => calendar.CalendarId == "new");
        Assert.True(enabled.IsVisible);

        await newRow.ToggleVisibilityCommand.ExecuteAsync(null);

        var disabled = Assert.Single(
            subject.Store.Settings.Accounts[0].Calendars,
            calendar => calendar.CalendarId == "new");
        Assert.False(disabled.IsVisible);
    }

    [Fact]
    public async Task TurningCalendarOffImmediatelyRemovesCachedEventWithoutProviderCalls()
    {
        var account = CreateAccount(ProviderKind.Google, "Account", ("calendar", true));
        var calendarEvent = Phase6Data.Event(
            "event",
            account.InternalAccountId,
            account.Provider,
            "calendar");
        var store = new TestSettingsStore { Settings = new AppSettings(accounts: [account]) };
        var cache = new MemoryCacheStore();
        cache.Caches[account.InternalAccountId] = new AccountCache(
            account.InternalAccountId,
            account.Provider,
            NowUtc,
            [new CachedCalendar("calendar", "Calendar", null, NowUtc, [calendarEvent])]);
        var provider = new RecordingProvider(account.Provider);
        await using var sync = new CalendarSyncService(
            [provider],
            store,
            cache,
            new MutableTimeProvider(NowUtc),
            localTimeZone: TimeZoneInfo.Utc);
        await sync.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Single(sync.CurrentSnapshot.Events);
        var settings = new ApplicationSettingsService(store);
        using var viewModel = new CalendarSelectionSettingsViewModel(
            settings,
            sync,
            new ScriptedCatalogService(),
            new ResourceUiTextService(),
            new ImmediateUiDispatcher(),
            new BrushCache());
        viewModel.ShowRegisteredAccount(new AccountRegistrationResult(
            account,
            [Descriptor("calendar", "Calendar")]));
        var row = Assert.Single(Assert.Single(viewModel.AccountGroups).Calendars);

        await row.ToggleVisibilityCommand.ExecuteAsync(null);

        Assert.Empty(sync.CurrentSnapshot.Events);
        Assert.Equal(0, provider.ListCalls);
        Assert.Equal(0, provider.GetEventCalls);
        Assert.False(store.Settings.Accounts[0].Calendars[0].IsVisible);
    }

    [Fact]
    public async Task VisibilityUpdateKeepsSettingsSchemaAndJsonShapeUnchanged()
    {
        using var temporary = new TemporaryAppDirectory();
        var account = CreateAccount(ProviderKind.Google, "Account", ("calendar", true));
        var store = new SettingsJsonStore(
            temporary.Paths,
            new AtomicFileWriter(),
            new MutableTimeProvider(NowUtc));
        await store.SaveAsync(
            new AppSettings(accounts: [account]),
            TestContext.Current.CancellationToken);
        var before = JsonNode.Parse(await File.ReadAllTextAsync(
            temporary.Paths.SettingsFile,
            TestContext.Current.CancellationToken))!;
        var settings = new ApplicationSettingsService(store);
        using var viewModel = new CalendarSelectionSettingsViewModel(
            settings,
            new TestCalendarSyncService(),
            new ScriptedCatalogService(),
            new ResourceUiTextService(),
            new ImmediateUiDispatcher(),
            new BrushCache());
        viewModel.ShowRegisteredAccount(new AccountRegistrationResult(
            account,
            [Descriptor("calendar", "Calendar")]));

        await Assert.Single(Assert.Single(viewModel.AccountGroups).Calendars)
            .ToggleVisibilityCommand.ExecuteAsync(null);

        var after = JsonNode.Parse(await File.ReadAllTextAsync(
            temporary.Paths.SettingsFile,
            TestContext.Current.CancellationToken))!;
        Assert.Equal(SettingsJsonStore.CurrentSchemaVersion, after["schemaVersion"]!.GetValue<int>());
        Assert.Equal(GetJsonPropertyPaths(before), GetJsonPropertyPaths(after));
    }

    [Fact]
    public async Task RegistrationShowsSharedCalendarViewWithoutSecondDiscoveryAndSurvivesClose()
    {
        using var temporary = new TemporaryAppDirectory();
        var accountId = Guid.NewGuid();
        var provider = new RecordingProvider(ProviderKind.Google)
        {
            Authentication = AuthAccountResult.Success(
                accountId,
                "provider-subject",
                "New account",
                "new@example.invalid"),
            Calendars =
            [
                Descriptor("primary", "Primary", isPrimary: true),
                Descriptor("other", "Other"),
            ],
        };
        var store = new TestSettingsStore();
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var interactions = new AccountInteractionService(
            [provider],
            settings,
            sync,
            new MemoryTokenStore(),
            temporary.Paths);
        var cache = new MemoryCacheStore();
        var text = new ResourceUiTextService();
        var accounts = new AccountSettingsViewModel(
            settings,
            sync,
            interactions,
            interactions,
            new MemoryTokenStore(),
            cache,
            text,
            new ImmediateUiDispatcher(),
            new MutableTimeProvider(NowUtc),
            new FixedZoneProvider());
        var catalog = new ScriptedCatalogService
        {
            ListHandler = (_, _) => Task.FromException<IReadOnlyList<CalendarDescriptor>>(
                new InvalidOperationException("The registration result must be reused.")),
        };
        var calendars = new CalendarSelectionSettingsViewModel(
            settings,
            sync,
            catalog,
            text,
            new ImmediateUiDispatcher(),
            new BrushCache());
        using var shell = new SettingsWindowViewModel(
            new NoPendingSettingsChanges(),
            text,
            settings,
            sync,
            new NoOpColorPickerService(),
            new BrushCache(),
            accounts: accounts,
            calendarSelection: calendars);
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        await accounts.AddGoogleAccountCommand.ExecuteAsync(null);

        Assert.Same(calendars, shell.SelectedCategory!.Content);
        Assert.True(calendars.IsPostRegistrationFlow);
        Assert.Empty(catalog.ListCalls);
        Assert.Equal(1, provider.ListCalls);
        Assert.Equal([true, false], Assert.Single(store.Settings.Accounts).Calendars.Select(value => value.IsVisible));
        Assert.Equal(2, Assert.Single(calendars.AccountGroups).Calendars.Count);
        shell.Dispose();
        Assert.Equal([true, false], Assert.Single(store.Settings.Accounts).Calendars.Select(value => value.IsVisible));
    }

    [Fact]
    public async Task LeavingPostRegistrationForAnotherCategoryEndsFlowAndNextOpenShowsAllAccounts()
    {
        var first = CreateAccount(ProviderKind.Google, "First", ("first-calendar", true));
        var second = CreateAccount(ProviderKind.Microsoft, "Second", ("second-calendar", true));
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [first, second]),
        };
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var text = new ResourceUiTextService();
        var interactions = new RecordingAccountInteractionService();
        var accounts = new AccountSettingsViewModel(
            settings,
            sync,
            interactions,
            interactions,
            new MemoryTokenStore(),
            new MemoryCacheStore(),
            text,
            new ImmediateUiDispatcher(),
            new MutableTimeProvider(NowUtc),
            new FixedZoneProvider());
        var catalog = new ScriptedCatalogService
        {
            ListHandler = (account, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>(
            [
                Descriptor(account.Calendars[0].CalendarId, $"Calendar {account.Provider}"),
            ]),
        };
        var calendars = new CalendarSelectionSettingsViewModel(
            settings,
            sync,
            catalog,
            text,
            new ImmediateUiDispatcher(),
            new BrushCache());
        using var shell = new SettingsWindowViewModel(
            new NoPendingSettingsChanges(),
            text,
            settings,
            sync,
            new NoOpColorPickerService(),
            new BrushCache(),
            accounts: accounts,
            calendarSelection: calendars);
        await shell.InitializeAsync(TestContext.Current.CancellationToken);
        var accountCategory = shell.Categories.Single(category => category.Key == "account");
        var calendarCategory = shell.Categories.Single(category => category.Key == "calendar-selection");
        var displayCategory = shell.Categories.Single(category => category.Key == "display");
        var registration = new AccountRegistrationResult(
            first,
            [Descriptor("first-calendar", "First calendar")]);

        calendars.ShowRegisteredAccount(registration);
        shell.SelectedCategory = calendarCategory;
        Assert.True(calendars.IsPostRegistrationFlow);
        Assert.Single(calendars.AccountGroups);
        Assert.Empty(catalog.ListCalls);

        shell.SelectedCategory = displayCategory;

        Assert.Same(displayCategory, shell.SelectedCategory);
        Assert.False(calendars.IsPostRegistrationFlow);

        shell.SelectedCategory = calendarCategory;
        await Phase6Data.WaitUntilAsync(() => calendars.AccountGroups.Count == 2);

        Assert.Equal(2, calendars.AccountGroups.Count);
        Assert.Equal(
            [first.InternalAccountId, second.InternalAccountId],
            catalog.ListCalls);

        shell.SelectedCategory = accountCategory;
        calendars.ShowRegisteredAccount(registration);
        shell.SelectedCategory = calendarCategory;
        calendars.BackCommand.Execute(null);

        Assert.False(calendars.IsPostRegistrationFlow);
        Assert.Same(accountCategory, shell.SelectedCategory);
        Assert.Equal(2, catalog.ListCalls.Count);
    }

    [Fact]
    public async Task CalendarWarningsRecoverIndependentlyAndOffRowsStayClear()
    {
        var account = CreateAccount(
            ProviderKind.Google,
            "Account",
            ("failed", true),
            ("healthy", true),
            ("off", false));
        var failed = new CalendarSyncState(
            "failed",
            SyncStatus.Failed,
            NowUtc,
            null,
            null,
            SyncErrorCategory.Network);
        var healthy = new CalendarSyncState(
            "healthy",
            SyncStatus.Succeeded,
            NowUtc,
            NowUtc,
            null,
            SyncErrorCategory.None);
        var offFailure = new CalendarSyncState(
            "off",
            SyncStatus.Failed,
            NowUtc,
            null,
            null,
            SyncErrorCategory.PermissionDenied);
        var sync = new TestCalendarSyncService
        {
            AccountStates =
            [
                new AccountSyncState(
                    account.InternalAccountId,
                    SyncStatus.PartiallySucceeded,
                    NowUtc,
                    null,
                    [failed, healthy, offFailure]),
            ],
        };
        var catalog = SuccessfulCatalog(
            account,
            Descriptor("failed", "Failed"),
            Descriptor("healthy", "Healthy"),
            Descriptor("off", "Off"));
        using var subject = CreateSubject(new AppSettings(accounts: [account]), catalog, sync);
        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);
        var rows = Assert.Single(subject.ViewModel.AccountGroups).Calendars;

        Assert.True(rows[0].HasSyncWarning);
        Assert.False(rows[1].HasSyncWarning);
        Assert.False(rows[2].HasSyncWarning);
        Assert.True(sync.AccountStates[0].HasWarning);

        var recovered = new AccountSyncState(
            account.InternalAccountId,
            SyncStatus.Succeeded,
            NowUtc,
            NowUtc,
            [
                new CalendarSyncState(
                    failed.CalendarId,
                    SyncStatus.Succeeded,
                    failed.LastAttemptUtc,
                    NowUtc,
                    null,
                    SyncErrorCategory.None),
                healthy,
            ]);
        sync.PublishState(recovered);

        Assert.All(rows, row => Assert.False(row.HasSyncWarning));
        Assert.False(recovered.HasWarning);
    }

    [Fact]
    public async Task AllOffReasonAndMicrosoftSharedGuidanceAreScopedCorrectly()
    {
        var google = CreateAccount(ProviderKind.Google, "Google", ("google", false));
        var microsoft = CreateAccount(ProviderKind.Microsoft, "Microsoft", ("microsoft", false));
        var settingsValue = new AppSettings(accounts: [google, microsoft]);
        var catalog = new ScriptedCatalogService
        {
            ListHandler = (account, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>(
                [Descriptor(account.Calendars[0].CalendarId, "Calendar")]),
        };
        using var subject = CreateSubject(settingsValue, catalog);
        await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.All(subject.ViewModel.AccountGroups, group => Assert.True(group.IsAllOff));
        Assert.False(subject.ViewModel.AccountGroups[0].ShowSharedCalendarGuidance);
        Assert.True(subject.ViewModel.AccountGroups[1].ShowSharedCalendarGuidance);

        var interactions = new RecordingAccountInteractionService();
        using var accounts = new AccountSettingsViewModel(
            subject.Settings,
            subject.Sync,
            interactions,
            interactions,
            new MemoryTokenStore(),
            new MemoryCacheStore(),
            new ResourceUiTextService(),
            new ImmediateUiDispatcher(),
            new MutableTimeProvider(NowUtc),
            new FixedZoneProvider());
        accounts.Initialize(settingsValue);
        Assert.All(accounts.Accounts, row => Assert.NotEmpty(row.SyncTargetReasonText));

        subject.Sync.AccountStates =
        [
            Phase6Data.State(
                google.InternalAccountId,
                SyncStatus.Failed,
                calendarId: "google",
                error: SyncErrorCategory.Network),
        ];
        using var main = Phase6Data.CreateViewModel(
            subject.Sync,
            new MutableTimeProvider(NowUtc),
            subject.Store,
            settingsService: subject.Settings);
        await main.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Empty(main.AccountWarnings);
    }

    [Fact]
    public async Task CalendarSelectionFailureLogsOnlySafeMetadataAndHasNoBulkCommands()
    {
        const string privateCalendarId = "private-user@example.invalid";
        const string privateCalendarName = "Private calendar name";
        var account = CreateAccount(ProviderKind.Google, "Private display name", (privateCalendarId, true));
        var catalog = new ScriptedCatalogService
        {
            ListHandler = (_, _) => Task.FromException<IReadOnlyList<CalendarDescriptor>>(
                new IOException(privateCalendarName)),
        };
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            using var subject = CreateSubject(new AppSettings(accounts: [account]), catalog);
            await subject.ViewModel.RefreshAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Log.Logger = previous;
        }

        var rendered = string.Join(
            Environment.NewLine,
            sink.Events.Select(value => $"{value.RenderMessage()} {string.Join(' ', value.Properties.Values)}"));
        Assert.DoesNotContain(privateCalendarId, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(privateCalendarName, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Private display name", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(CalendarSelectionSettingsViewModel).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => typeof(System.Windows.Input.ICommand).IsAssignableFrom(property.PropertyType)
                && property.Name.Contains("All", StringComparison.OrdinalIgnoreCase));
    }

    private static TestSubject CreateSubject(
        AppSettings settings,
        ScriptedCatalogService catalog,
        TestCalendarSyncService? sync = null)
    {
        var store = new TestSettingsStore { Settings = settings };
        var settingsService = new ApplicationSettingsService(store);
        sync ??= new TestCalendarSyncService();
        return new TestSubject(
            new CalendarSelectionSettingsViewModel(
                settingsService,
                sync,
                catalog,
                new ResourceUiTextService(),
                new ImmediateUiDispatcher(),
                new BrushCache()),
            store,
            settingsService,
            sync);
    }

    private static ScriptedCatalogService SuccessfulCatalog(
        AccountSettings account,
        params CalendarDescriptor[] descriptors) => new()
        {
            ListHandler = (value, _) => value.InternalAccountId == account.InternalAccountId
                ? Task.FromResult<IReadOnlyList<CalendarDescriptor>>(descriptors)
                : Task.FromResult<IReadOnlyList<CalendarDescriptor>>([]),
        };

    private static AccountSettings CreateAccount(
        ProviderKind provider,
        string displayName,
        params (string CalendarId, bool IsVisible)[] calendars)
    {
        var id = Guid.NewGuid();
        return new AccountSettings(
            id,
            provider,
            $"subject-{id:N}",
            displayName,
            $"{id:N}@example.invalid",
            true,
            $"{provider.ToString().ToLowerInvariant()}/{id:D}",
            calendars.Select(calendar => new CalendarSetting(calendar.CalendarId, calendar.IsVisible)));
    }

    private static CalendarDescriptor Descriptor(
        string calendarId,
        string name,
        bool isPrimary = false,
        bool canReadEvents = true,
        RgbColor? sourceColor = null) => new(
            calendarId,
            $"locator-{calendarId.Length}",
            name,
            isPrimary,
            canReadEvents,
            sourceColor);

    private static AppSettings CopyWithVisibility(
        AppSettings current,
        Guid accountId,
        string calendarId,
        bool visible) => new(
            current.Display,
            current.Sync,
            current.General,
            current.Windows,
            current.Accounts.Select(account => account.InternalAccountId == accountId
                ? new AccountSettings(
                    account.InternalAccountId,
                    account.Provider,
                    account.ProviderSubjectId,
                    account.DisplayName,
                    account.Email,
                    account.Enabled,
                    account.TokenRef,
                    account.Calendars.Select(calendar => calendar.CalendarId == calendarId
                        ? new CalendarSetting(calendar.CalendarId, visible)
                        : calendar))
                : account),
            current.ColorRules);

    private static string[] GetJsonPropertyPaths(JsonNode node)
    {
        var paths = new List<string>();
        CollectJsonPropertyPaths(node, string.Empty, paths);
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    private static void CollectJsonPropertyPaths(JsonNode? node, string path, ICollection<string> paths)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
            {
                var propertyPath = $"{path}/{property.Key}";
                paths.Add(propertyPath);
                CollectJsonPropertyPaths(property.Value, propertyPath, paths);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                CollectJsonPropertyPaths(item, $"{path}[]", paths);
            }
        }
    }

    private sealed class TestSubject : IDisposable
    {
        public TestSubject(
            CalendarSelectionSettingsViewModel viewModel,
            TestSettingsStore store,
            ApplicationSettingsService settings,
            TestCalendarSyncService sync)
        {
            ViewModel = viewModel;
            Store = store;
            Settings = settings;
            Sync = sync;
        }

        public CalendarSelectionSettingsViewModel ViewModel { get; }

        public TestSettingsStore Store { get; }

        public ApplicationSettingsService Settings { get; }

        public TestCalendarSyncService Sync { get; }

        public void Dispose() => ViewModel.Dispose();
    }

    private sealed class ScriptedCatalogService : ICalendarCatalogService
    {
        public Func<AccountSettings, CancellationToken, Task<IReadOnlyList<CalendarDescriptor>>> ListHandler
            { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>([]);

        public List<Guid> ListCalls { get; } = [];

        public Dictionary<Guid, AccountCache> Caches { get; } = [];

        public Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
            AccountSettings account,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls.Add(account.InternalAccountId);
            return ListHandler(account, cancellationToken);
        }

        public Task<AccountCache?> LoadCacheAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Caches.GetValueOrDefault(internalAccountId));
        }
    }

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

    private sealed class RecordingProvider(ProviderKind provider) : ICalendarProvider
    {
        public ProviderKind Provider { get; } = provider;

        public AuthAccountResult Authentication { get; set; } = AuthAccountResult.Failure(
            new ProviderError(ProviderErrorCategory.Cancelled));

        public IReadOnlyList<CalendarDescriptor> Calendars { get; set; } = [];

        public int ListCalls { get; private set; }

        public int GetEventCalls { get; private set; }

        public Task<AuthAccountResult> AuthenticateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Authentication);
        }

        public Task<AuthAccountResult> ReauthenticateAsync(
            CalendarAccount account,
            CancellationToken cancellationToken) => Task.FromResult(Authentication);

        public Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
            CalendarAccount account,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            return Task.FromResult(Calendars);
        }

        public Task<ProviderCalendarResult> GetEventsAsync(
            CalendarAccount account,
            CalendarDescriptor calendar,
            TimeRangeUtc range,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetEventCalls++;
            return Task.FromResult(ProviderCalendarResult.Success([]));
        }
    }

    private sealed class FixedZoneProvider : ILocalTimeZoneProvider
    {
        public TimeZoneInfo GetCurrent() => TimeZoneInfo.Utc;
    }

    private sealed class NoOpColorPickerService : IColorPickerService
    {
        public RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor) => null;
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyList<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
