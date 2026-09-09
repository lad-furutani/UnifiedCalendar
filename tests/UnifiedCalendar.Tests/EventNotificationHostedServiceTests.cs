using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Notifications;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class EventNotificationHostedServiceTests
{
    [Fact]
    public async Task MinuteBoundaryShowsDueEventThroughUiDispatcher()
    {
        var time = new MutableTimeProvider(Phase6Data.Now);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Snapshot(EventStartingIn(minutes: 6)),
        };
        var dispatcher = new TrackingUiDispatcher();
        var notifier = new RecordingTrayNotifier(dispatcher);
        var refreshSignal = new InternalRefreshSignal();
        var service = CreateService(
            sync,
            new TestSettingsStore(),
            refreshSignal,
            time,
            dispatcher,
            notifier,
            out _);

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.WaitForPendingEvaluationsAsync();
            Assert.Empty(notifier.Notifications);

            time.SetUtcNow(Phase6Data.Now.AddMinutes(1));
            await refreshSignal.RequestRefreshAsync(
                InternalRefreshReason.MinuteBoundary,
                TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();

            var notification = Assert.Single(notifier.Notifications);
            Assert.Equal("Sensitive planning title", notification.Title);
            Assert.True(notifier.CalledWithinDispatcher);
            Assert.Equal(1, dispatcher.InvocationCount);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SnapshotChangeNormallyEvaluatesNewlyVisibleEvent()
    {
        var time = new MutableTimeProvider(Phase6Data.Now);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Snapshot(EventStartingIn(minutes: 30, eventId: "initial-event")),
        };
        var notifier = new RecordingTrayNotifier();
        var service = CreateService(
            sync,
            new TestSettingsStore(),
            new InternalRefreshSignal(),
            time,
            new ImmediateUiDispatcher(),
            notifier,
            out _);

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.WaitForPendingEvaluationsAsync();
            sync.PublishSnapshot(Snapshot(EventStartingIn(minutes: 5)));
            await service.WaitForPendingEvaluationsAsync();

            Assert.Single(notifier.Notifications);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task EmptyStartupSnapshotDefersSuppressionUntilTimedCandidatesArrive()
    {
        var time = new MutableTimeProvider(Phase6Data.Now);
        var sync = new TestCalendarSyncService();
        var refreshSignal = new InternalRefreshSignal();
        var notifier = new RecordingTrayNotifier();
        var service = CreateService(
            sync,
            new TestSettingsStore(),
            refreshSignal,
            time,
            new ImmediateUiDispatcher(),
            notifier,
            out _);

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.WaitForPendingEvaluationsAsync();
            for (var index = 0; index < 2; index++)
            {
                await refreshSignal.RequestRefreshAsync(
                    InternalRefreshReason.MinuteBoundary,
                    TestContext.Current.CancellationToken);
                await service.WaitForPendingEvaluationsAsync();
                Assert.Empty(notifier.Notifications);
            }

            sync.PublishSnapshot(Snapshot(
                EventStartingIn(4, "startup-due", title: "Startup due"),
                EventStartingIn(6, "next-minute", title: "Next minute")));
            await service.WaitForPendingEvaluationsAsync();
            Assert.Empty(notifier.Notifications);

            time.SetUtcNow(Phase6Data.Now.AddMinutes(1));
            await refreshSignal.RequestRefreshAsync(
                InternalRefreshReason.MinuteBoundary,
                TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();

            var notification = Assert.Single(notifier.Notifications);
            Assert.Equal("Next minute", notification.Title);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DisabledNotificationsStaySilentAndReenablingSuppressesAlreadyDueEvent()
    {
        var time = new MutableTimeProvider(Phase6Data.Now);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Snapshot(EventStartingIn(minutes: 5)),
        };
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(
                notifications: new NotificationPreferences(enabled: false)),
        };
        var refreshSignal = new InternalRefreshSignal();
        var notifier = new RecordingTrayNotifier();
        var service = CreateService(
            sync,
            store,
            refreshSignal,
            time,
            new ImmediateUiDispatcher(),
            notifier,
            out var settingsService);

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.WaitForPendingEvaluationsAsync();
            await refreshSignal.RequestRefreshAsync(
                InternalRefreshReason.MinuteBoundary,
                TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();
            Assert.Empty(notifier.Notifications);

            _ = await settingsService.UpdateAsync(
                current => WithNotifications(current, new NotificationPreferences(enabled: true)),
                TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();
            Assert.Empty(notifier.Notifications);

            time.SetUtcNow(Phase6Data.Now.AddMinutes(1));
            await refreshSignal.RequestRefreshAsync(
                InternalRefreshReason.MinuteBoundary,
                TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();
            Assert.Empty(notifier.Notifications);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LeadMinuteChangeSuppressesEventsNewlyInsideTheWindow()
    {
        var time = new MutableTimeProvider(Phase6Data.Now);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Snapshot(EventStartingIn(minutes: 30)),
        };
        var store = new TestSettingsStore();
        var refreshSignal = new InternalRefreshSignal();
        var notifier = new RecordingTrayNotifier();
        var service = CreateService(
            sync,
            store,
            refreshSignal,
            time,
            new ImmediateUiDispatcher(),
            notifier,
            out var settingsService);

        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await service.WaitForPendingEvaluationsAsync();
            _ = await settingsService.UpdateAsync(
                current => WithNotifications(current, new NotificationPreferences(leadMinutes: 30)),
                TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();

            time.SetUtcNow(Phase6Data.Now.AddMinutes(1));
            await refreshSignal.RequestRefreshAsync(
                InternalRefreshReason.MinuteBoundary,
                TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();

            Assert.Empty(notifier.Notifications);
        }
        finally
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task NotificationLoggingDoesNotContainEventPii()
    {
        const string title = "Sensitive planning title";
        const string location = "Sensitive Tokyo office";
        const string sourceUrl = "https://calendar.example.invalid/sensitive-event";
        var previousLogger = Log.Logger;
        var sink = new CollectingLogSink();
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var time = new MutableTimeProvider(Phase6Data.Now);
        var calendarEvent = EventStartingIn(
            minutes: 6,
            title: title,
            location: location,
            sourceDetailUri: new Uri(sourceUrl));
        var sync = new TestCalendarSyncService { CurrentSnapshot = Snapshot(calendarEvent) };
        var refreshSignal = new InternalRefreshSignal();
        var service = CreateService(
            sync,
            new TestSettingsStore(),
            refreshSignal,
            time,
            new ImmediateUiDispatcher(),
            new RecordingTrayNotifier(),
            out _);

        try
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();
            time.SetUtcNow(Phase6Data.Now.AddMinutes(1));
            await refreshSignal.RequestRefreshAsync(
                InternalRefreshReason.MinuteBoundary,
                TestContext.Current.CancellationToken);
            await service.WaitForPendingEvaluationsAsync();
            await service.StopAsync(TestContext.Current.CancellationToken);

            var rendered = string.Join(
                '\n',
                sink.Events.Select(value =>
                    $"{value.RenderMessage()} {string.Join(' ', value.Properties.Values)}"));
            Assert.DoesNotContain(title, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(location, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain(sourceUrl, rendered, StringComparison.Ordinal);
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previousLogger;
        }
    }

    private static EventNotificationHostedService CreateService(
        TestCalendarSyncService sync,
        TestSettingsStore store,
        InternalRefreshSignal refreshSignal,
        MutableTimeProvider time,
        IUiDispatcher dispatcher,
        ITrayNotifier notifier,
        out ApplicationSettingsService settingsService)
    {
        settingsService = new ApplicationSettingsService(store);
        return new EventNotificationHostedService(
            sync,
            settingsService,
            refreshSignal,
            new CalendarPresentationService(time, ColorMetrics.ProgressLightnessDelta),
            new EventNotificationPlanner(),
            notifier,
            dispatcher,
            time,
            new FixedLocalTimeZoneProvider(TimeZoneInfo.Utc));
    }

    private static AppSettings WithNotifications(
        AppSettings current,
        NotificationPreferences notifications) => new(
            current.Display,
            current.Sync,
            current.General,
            current.Windows,
            current.Accounts,
            current.ColorRules,
            notifications);

    private static CalendarEvent EventStartingIn(
        int minutes,
        string eventId = "notification-event",
        string title = "Sensitive planning title",
        string? location = null,
        Uri? sourceDetailUri = null) => Phase6Data.Event(
            eventId,
            title: title,
            startUtc: Phase6Data.Now.AddMinutes(minutes),
            endUtc: Phase6Data.Now.AddHours(1),
            location: location,
            sourceDetailUri: sourceDetailUri);

    private static SyncSnapshot Snapshot(params CalendarEvent[] calendarEvents)
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        return Phase6Data.Snapshot(
            [account],
            [new CalendarSelection(account.InternalAccountId, "calendar", true)],
            calendarEvents);
    }

    private sealed class TrackingUiDispatcher : IUiDispatcher
    {
        public bool IsInvoking { get; private set; }

        public int InvocationCount { get; private set; }

        public async Task InvokeAsync(
            Func<Task> callback,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            IsInvoking = true;
            try
            {
                await callback();
            }
            finally
            {
                IsInvoking = false;
            }
        }
    }

    private sealed class RecordingTrayNotifier(TrackingUiDispatcher? dispatcher = null) : ITrayNotifier
    {
        public List<EventNotificationMessage> Notifications { get; } = [];

        public bool CalledWithinDispatcher { get; private set; }

        public void ShowEventStarting(EventNotificationMessage notification)
        {
            CalledWithinDispatcher = dispatcher?.IsInvoking ?? true;
            Notifications.Add(notification);
        }
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}

public sealed class TrayNotifierTests
{
    [Fact]
    public void FormatsAggregatedNotificationAndUsesFixedBalloonArguments()
    {
        var adapter = new RecordingTrayIconAdapter();
        var notifier = new TrayNotifier(adapter, new ResourceUiTextService());
        var start = new DateTimeOffset(2026, 9, 9, 10, 5, 0, TimeSpan.FromHours(9));

        notifier.ShowEventStarting(new EventNotificationMessage(start, "企画会議", 2));

        Assert.Equal(10_000, adapter.TimeoutMilliseconds);
        Assert.Equal("まもなく開始", adapter.Title);
        Assert.Equal("10:05 企画会議 ほか2件", adapter.Text);
    }

    [Fact]
    public void TruncatesLongBodyAtTwoHundredCharactersAndAppendsEllipsis()
    {
        var adapter = new RecordingTrayIconAdapter();
        var notifier = new TrayNotifier(adapter, new ResourceUiTextService());
        var start = new DateTimeOffset(2026, 9, 9, 10, 5, 0, TimeSpan.FromHours(9));

        notifier.ShowEventStarting(new EventNotificationMessage(start, new string('長', 250), 0));

        Assert.Equal(201, adapter.Text.Length);
        Assert.EndsWith("…", adapter.Text, StringComparison.Ordinal);
    }

    private sealed class RecordingTrayIconAdapter : ITrayIconAdapter
    {
        public event EventHandler? DoubleClick;

        public System.Windows.Forms.ContextMenuStrip? ContextMenu { get; set; }

        public bool Visible { get; set; }

        public int TimeoutMilliseconds { get; private set; }

        public string Title { get; private set; } = string.Empty;

        public string Text { get; private set; } = string.Empty;

        public void ShowBalloonTip(int timeoutMilliseconds, string title, string text)
        {
            TimeoutMilliseconds = timeoutMilliseconds;
            Title = title;
            Text = text;
        }

        public void Dispose()
        {
            _ = DoubleClick;
        }
    }
}
