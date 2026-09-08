using System.Collections.Concurrent;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Sync;
using System.Windows.Media;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task WaitingProjectionReadsLatestSnapshotAfterEnteringSemaphore()
    {
        var google = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var microsoft = Phase6Data.Account(Phase6Data.MicrosoftAccountId, ProviderKind.Microsoft);
        var selections = new[]
        {
            new CalendarSelection(google.InternalAccountId, "google-calendar", true),
            new CalendarSelection(microsoft.InternalAccountId, "microsoft-calendar", true),
        };
        var staleGeneration = Phase6Data.Snapshot(
            [google, microsoft],
            selections);
        var firstGeneration = Phase6Data.Snapshot(
            [google, microsoft],
            selections,
            [Phase6Data.Event("google-event", calendarId: "google-calendar")]);
        var latestGeneration = Phase6Data.Snapshot(
            [google, microsoft],
            selections,
            [
                Phase6Data.Event("google-event", calendarId: "google-calendar"),
                Phase6Data.Event(
                    "microsoft-event",
                    Phase6Data.MicrosoftAccountId,
                    ProviderKind.Microsoft,
                    "microsoft-calendar",
                    startUtc: Phase6Data.Now.AddHours(2),
                    endUtc: Phase6Data.Now.AddHours(3)),
            ]);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = staleGeneration,
            AccountStates =
            [
                Phase6Data.State(
                    google.InternalAccountId,
                    SyncStatus.Succeeded,
                    Phase6Data.Now,
                    "google-calendar"),
                Phase6Data.State(
                    microsoft.InternalAccountId,
                    SyncStatus.Succeeded,
                    Phase6Data.Now,
                    "microsoft-calendar"),
            ],
        };
        var appliedGenerations = new ConcurrentQueue<string>();
        var dispatcher = new ImmediateUiDispatcher();
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now),
            dispatcher: dispatcher);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var firstApplyEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstApply = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeforeInvocation = async () =>
        {
            dispatcher.BeforeInvocation = null;
            firstApplyEntered.TrySetResult();
            await releaseFirstApply.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        dispatcher.AfterInvocation = () => appliedGenerations.Enqueue(string.Join(
            ",",
            viewModel.TimelineItems
                .OfType<EventRowViewModel>()
                .Select(row => row.Key.SourceEventId)));

        sync.CurrentSnapshot = firstGeneration;
        sync.PublishSnapshotNotification(staleGeneration);
        await firstApplyEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        sync.PublishSnapshotNotification(staleGeneration);
        sync.CurrentSnapshot = latestGeneration;
        await Task.Run(() => sync.PublishState(Phase6Data.State(
                microsoft.InternalAccountId,
                SyncStatus.Succeeded,
                Phase6Data.Now,
                "microsoft-calendar")),
            TestContext.Current.CancellationToken);
        releaseFirstApply.TrySetResult();
        await Phase6Data.WaitUntilAsync(() => appliedGenerations.Count == 3);

        Assert.Equal(
            [
                "google-event",
                "google-event,microsoft-event",
                "google-event,microsoft-event",
            ],
            appliedGenerations);
        Assert.Equal(
            ["google-event", "microsoft-event"],
            viewModel.TimelineItems
                .OfType<EventRowViewModel>()
                .Select(row => row.Key.SourceEventId));
    }

    [Fact]
    public async Task UnregisteredStateShowsBothUnavailableAccountCommandsAsDisabled()
    {
        var sync = new TestCalendarSyncService();
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now));

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsUnregistered);
        Assert.Equal(
            new ResourceUiTextService().Get(UiResourceKeys.Unregistered),
            viewModel.StateMessage);
        Assert.False(viewModel.AddGoogleAccountCommand.CanExecute(null));
        Assert.False(viewModel.AddMicrosoftAccountCommand.CanExecute(null));
        Assert.NotEmpty(viewModel.AddGoogleAccountText);
        Assert.NotEmpty(viewModel.AddMicrosoftAccountText);
    }

    [Fact]
    public async Task AvailableAccountCommandsIssueTheCorrectProviderArguments()
    {
        var interactions = new RecordingAccountInteractionService { IsAvailable = true };
        var viewModel = Phase6Data.CreateViewModel(
            new TestCalendarSyncService(),
            new MutableTimeProvider(Phase6Data.Now),
            interactions: interactions);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        await viewModel.AddGoogleAccountCommand.ExecuteAsync(null);
        await viewModel.AddMicrosoftAccountCommand.ExecuteAsync(null);

        Assert.Equal(
            [ProviderKind.Google, ProviderKind.Microsoft],
            interactions.AddedProviders);
    }

    [Fact]
    public async Task UnregisteredStateEnablesOnlyTheConfiguredProviderAndExplainsTheOther()
    {
        var interactions = new RecordingAccountInteractionService
        {
            IsAvailable = true,
            AvailableProviders = [ProviderKind.Google],
        };
        var viewModel = Phase6Data.CreateViewModel(
            new TestCalendarSyncService(),
            new MutableTimeProvider(Phase6Data.Now),
            interactions: interactions);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsUnregistered);
        Assert.True(viewModel.AddGoogleAccountCommand.CanExecute(null));
        Assert.False(viewModel.AddMicrosoftAccountCommand.CanExecute(null));
        Assert.Null(viewModel.AddGoogleAccountToolTip);
        Assert.False(string.IsNullOrEmpty(viewModel.AddMicrosoftAccountToolTip));
    }

    [Fact]
    public async Task AllOffAccountIsExcludedFromMainWarnings()
    {
        var google = Phase6Data.Account(
            Phase6Data.GoogleAccountId,
            ProviderKind.Google,
            displayName: "Visible account");
        var microsoft = Phase6Data.Account(
            Phase6Data.MicrosoftAccountId,
            ProviderKind.Microsoft,
            displayName: "All off account");
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [google, microsoft],
                [
                    new CalendarSelection(google.InternalAccountId, "calendar", true),
                    new CalendarSelection(microsoft.InternalAccountId, "calendar", false),
                ]),
            AccountStates =
            [
                Phase6Data.State(
                    google.InternalAccountId,
                    SyncStatus.AuthenticationRequired,
                    error: SyncErrorCategory.AuthenticationRequired),
                Phase6Data.State(
                    microsoft.InternalAccountId,
                    SyncStatus.AuthenticationRequired,
                    error: SyncErrorCategory.AuthenticationRequired),
            ],
        };
        var interactions = new RecordingAccountInteractionService { IsAvailable = true };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now),
            interactions: interactions);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var warning = Assert.Single(viewModel.AccountWarnings);
        await warning.ReauthenticateCommand.ExecuteAsync(null);

        Assert.Equal(google.InternalAccountId, warning.InternalAccountId);
        Assert.DoesNotContain("All off account", warning.Message, StringComparison.Ordinal);
        Assert.Equal(google.InternalAccountId, Assert.Single(interactions.ReauthenticatedAccounts));
    }

    [Fact]
    public async Task RetainedSuccessfulTimesAreHiddenWhileAnyProviderIsNotCurrentlySuccessful()
    {
        var google = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var microsoft = Phase6Data.Account(Phase6Data.MicrosoftAccountId, ProviderKind.Microsoft);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [google, microsoft],
                [
                    new CalendarSelection(google.InternalAccountId, "calendar", true),
                    new CalendarSelection(microsoft.InternalAccountId, "calendar", true),
                ]),
            AccountStates =
            [
                Phase6Data.State(
                    google.InternalAccountId,
                    SyncStatus.Failed,
                    Phase6Data.Now.AddMinutes(-5),
                    error: SyncErrorCategory.Network),
                Phase6Data.State(
                    microsoft.InternalAccountId,
                    SyncStatus.Failed,
                    Phase6Data.Now.AddMinutes(-20),
                    error: SyncErrorCategory.Network),
            ],
        };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now));

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.EndsWith("--", viewModel.LastUpdateText, StringComparison.Ordinal);
        Assert.Equal(2, viewModel.AccountWarnings.Count);
        Assert.True(viewModel.HasAccountWarnings);
        Assert.Equal(2, viewModel.AccountWarningsText.Split(Environment.NewLine).Length);
        Assert.All(
            viewModel.AccountWarnings,
            warning => Assert.False(warning.ReauthenticateCommand.CanExecute(null)));
        Assert.True(viewModel.IsError);
        Assert.Equal(
            new ResourceUiTextService().Get(UiResourceKeys.Error),
            viewModel.StateMessage);
        Assert.DoesNotContain("前回", viewModel.StateMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullySuccessfulAccountsUseTheOldestAccountTimestamp()
    {
        var google = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var microsoft = Phase6Data.Account(Phase6Data.MicrosoftAccountId, ProviderKind.Microsoft);
        var oldest = Phase6Data.Now.AddMinutes(-20);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [google, microsoft],
                [
                    new CalendarSelection(google.InternalAccountId, "calendar", true),
                    new CalendarSelection(microsoft.InternalAccountId, "calendar", true),
                ]),
            AccountStates =
            [
                Phase6Data.State(
                    google.InternalAccountId,
                    SyncStatus.Succeeded,
                    Phase6Data.Now.AddMinutes(-5)),
                Phase6Data.State(
                    microsoft.InternalAccountId,
                    SyncStatus.Succeeded,
                    oldest),
            ],
        };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now));

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Contains(oldest.ToString("HH:mm"), viewModel.LastUpdateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSuccessfulTimeUsesUnavailableText()
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [new CalendarSelection(account.InternalAccountId, "calendar", true)]),
            AccountStates = [Phase6Data.State(account.InternalAccountId, SyncStatus.NotStarted)],
        };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now));

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.EndsWith("--", viewModel.LastUpdateText, StringComparison.Ordinal);
        Assert.True(viewModel.IsLoading);
        Assert.Equal(
            new ResourceUiTextService().Get(UiResourceKeys.Loading),
            viewModel.StateMessage);
    }

    [Fact]
    public async Task StaleEventsRemainVisibleAlongsideProviderWarning()
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var calendarEvent = Phase6Data.Event("stale");
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                [calendarEvent]),
            AccountStates =
            [
                Phase6Data.State(
                    account.InternalAccountId,
                    SyncStatus.Failed,
                    Phase6Data.Now.AddMinutes(-5),
                    error: SyncErrorCategory.Network),
            ],
        };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now));

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsTimelineVisible);
        Assert.Single(viewModel.TimelineItems.OfType<EventRowViewModel>());
        Assert.Single(viewModel.AccountWarnings);
    }

    [Fact]
    public async Task GoogleAndMicrosoftAccountsRemainIndependentInOneTimeline()
    {
        var google = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var microsoft = Phase6Data.Account(Phase6Data.MicrosoftAccountId, ProviderKind.Microsoft);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [google, microsoft],
                [
                    new CalendarSelection(google.InternalAccountId, "google-calendar", true),
                    new CalendarSelection(microsoft.InternalAccountId, "microsoft-calendar", true),
                ],
                [
                    Phase6Data.Event("google-event", calendarId: "google-calendar"),
                    Phase6Data.Event(
                        "microsoft-event",
                        Phase6Data.MicrosoftAccountId,
                        ProviderKind.Microsoft,
                        "microsoft-calendar",
                        startUtc: Phase6Data.Now.AddHours(2),
                        endUtc: Phase6Data.Now.AddHours(3)),
                ]),
            AccountStates =
            [
                Phase6Data.State(
                    google.InternalAccountId,
                    SyncStatus.Succeeded,
                    Phase6Data.Now,
                    "google-calendar"),
                Phase6Data.State(
                    microsoft.InternalAccountId,
                    SyncStatus.Succeeded,
                    Phase6Data.Now,
                    "microsoft-calendar"),
            ],
        };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now));

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["G", "M"],
            viewModel.TimelineItems.OfType<EventRowViewModel>().Select(row => row.ProviderMark));
    }

    [Fact]
    public async Task InternalMinuteRefreshReprojectsWithoutCallingAProvider()
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var ending = Phase6Data.Event(
            "ending",
            startUtc: Phase6Data.Now.AddHours(-1),
            endUtc: Phase6Data.Now.AddMinutes(1));
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                [ending]),
            AccountStates =
            [Phase6Data.State(account.InternalAccountId, SyncStatus.Succeeded, Phase6Data.Now)],
        };
        var time = new MutableTimeProvider(Phase6Data.Now);
        var refresh = new InternalRefreshSignal();
        var viewModel = Phase6Data.CreateViewModel(sync, time, refreshSignal: refresh);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Single(viewModel.TimelineItems.OfType<EventRowViewModel>());

        time.SetUtcNow(Phase6Data.Now.AddMinutes(2));
        await refresh.RequestRefreshAsync(
            InternalRefreshReason.MinuteBoundary,
            TestContext.Current.CancellationToken);
        await Phase6Data.WaitUntilAsync(() => viewModel.IsEmpty);

        Assert.Equal(0, sync.RequestCount);
        Assert.True(viewModel.IsEmpty);
        Assert.Equal(
            new ResourceUiTextService().Get(UiResourceKeys.Empty, 7),
            viewModel.StateMessage);
        Assert.Equal(Phase6Data.Now.AddMinutes(2).ToString("HH:mm"), viewModel.CurrentTimeText);
    }

    [Fact]
    public async Task InternalMinuteRefreshAdvancesOngoingProgressWithoutExternalSync()
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var ongoing = Phase6Data.Event(
            "ongoing",
            startUtc: Phase6Data.Now.AddMinutes(-30),
            endUtc: Phase6Data.Now.AddMinutes(30));
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                [ongoing]),
            AccountStates =
            [Phase6Data.State(account.InternalAccountId, SyncStatus.Succeeded, Phase6Data.Now)],
        };
        var time = new MutableTimeProvider(Phase6Data.Now);
        var refresh = new InternalRefreshSignal();
        var viewModel = Phase6Data.CreateViewModel(sync, time, refreshSignal: refresh);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var row = Assert.Single(viewModel.TimelineItems.OfType<EventRowViewModel>());
        var initial = Assert.IsType<LinearGradientBrush>(row.Background).GradientStops[0].Offset;

        time.SetUtcNow(Phase6Data.Now.AddMinutes(10));
        await refresh.RequestRefreshAsync(
            InternalRefreshReason.MinuteBoundary,
            TestContext.Current.CancellationToken);
        await Phase6Data.WaitUntilAsync(() =>
            Assert.IsType<LinearGradientBrush>(row.Background).GradientStops[0].Offset > initial);

        Assert.Same(row, Assert.Single(viewModel.TimelineItems.OfType<EventRowViewModel>()));
        Assert.Equal(0, sync.RequestCount);
    }

    [Fact]
    public async Task DetailViewModelIsUpdatedInPlaceThenClosedWhenEventDisappears()
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var selection = new CalendarSelection(account.InternalAccountId, "calendar", true);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [selection],
                [Phase6Data.Event("event", title: "First")]),
            AccountStates =
            [Phase6Data.State(account.InternalAccountId, SyncStatus.Succeeded, Phase6Data.Now)],
        };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var key = Assert.Single(viewModel.TimelineItems.OfType<EventRowViewModel>()).Key;
        viewModel.OpenDetails(key);
        var details = Assert.IsType<EventDetailsViewModel>(viewModel.EventDetails);

        sync.CurrentSnapshot = Phase6Data.Snapshot(
            [account],
            [selection],
            [Phase6Data.Event("event", title: "Changed")]);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Same(details, viewModel.EventDetails);
        Assert.Equal("Changed", details.Title);

        sync.CurrentSnapshot = Phase6Data.Snapshot([account], [selection]);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsDetailsOpen);
    }

    [Fact]
    public async Task UpcomingEventAutoScrollOccursOnlyOnFirstSyncCompletion()
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                [Phase6Data.Event("upcoming")]),
            AccountStates = [Phase6Data.State(account.InternalAccountId, SyncStatus.NotStarted)],
        };
        var viewport = new RecordingTimelineViewport();
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now),
            viewport: viewport);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(viewport.Restorations).ScrollToUpcoming);

        sync.AccountStates =
            [Phase6Data.State(account.InternalAccountId, SyncStatus.Succeeded, Phase6Data.Now)];
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(viewport.Restorations[^1].ScrollToUpcoming);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.False(viewport.Restorations[^1].ScrollToUpcoming);
    }

    [Fact]
    public async Task SettingsStorageFailureFallsBackToDefaultsWithoutBlockingTheWindow()
    {
        var settings = new TestSettingsStore { LoadException = new IOException("temporary") };
        var dispatcher = new ImmediateUiDispatcher();
        var viewModel = Phase6Data.CreateViewModel(
            new TestCalendarSyncService(),
            new MutableTimeProvider(Phase6Data.Now),
            settings,
            dispatcher);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsUnregistered);
        Assert.Equal(14d, viewModel.FontSizeDip);
        Assert.True(dispatcher.InvocationCount > 0);
    }

    [Fact]
    public async Task InitializationCancellationPropagatesAsNormalControlFlow()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var settings = new TestSettingsStore { CancelLoad = true };
        var viewModel = Phase6Data.CreateViewModel(
            new TestCalendarSyncService(),
            new MutableTimeProvider(Phase6Data.Now),
            settings);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            viewModel.InitializeAsync(cancellation.Token));
    }

    [Fact]
    public async Task ManualRefreshIsCancelledThroughTheCommandTokenAndBecomesAvailableAgain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var sync = new TestCalendarSyncService
        {
            RequestHandler = async (reason, cancellationToken) =>
            {
                Assert.Equal(SyncTriggerReason.Manual, reason);
                observedToken = cancellationToken;
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new SyncRunResult(reason, []);
            },
        };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        var execution = viewModel.ManualRefreshCommand.ExecuteAsync(null);
        await entered.Task;
        Assert.True(viewModel.IsSyncing);
        Assert.False(viewModel.ManualRefreshCommand.CanExecute(null));
        viewModel.ManualRefreshCommand.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        Assert.True(observedToken.CanBeCanceled);
        Assert.False(viewModel.IsSyncing);
        Assert.True(viewModel.ManualRefreshCommand.CanExecute(null));
    }
}
