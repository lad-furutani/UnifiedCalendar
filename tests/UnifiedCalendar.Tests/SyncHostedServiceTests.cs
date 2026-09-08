using System.Threading.Channels;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Sync;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Sync;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class SyncHostedServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 3, 14, 40, TimeSpan.Zero);

    [Fact]
    public async Task StartupNetworkFailureRetriesAtThirtySecondsExactlyThreeTimes()
    {
        var accountId = Guid.NewGuid();
        var time = new ManualTimeProvider(Now);
        var sync = new FakeCalendarSyncService
        {
            ResultFactory = reason => NetworkFailure(reason, accountId),
        };
        var hosted = new SyncSchedulerHostedService(
            sync,
            new ApplicationSettingsService(new SchedulerSettingsStore(new AppSettings())),
            new RecordingRefreshRequester(),
            new FakeResumeSignal(),
            time);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncTriggerReason.Startup, await sync.ReadReasonAsync());
        for (var retry = 0; retry < 3; retry++)
        {
            time.Advance(TimeSpan.FromSeconds(29));
            Assert.False(sync.Reasons.Reader.TryRead(out _));
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(SyncTriggerReason.StartupRetry, await sync.ReadReasonAsync());
        }

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(sync.Reasons.Reader.TryRead(out _));
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sync.InitializeCalls);
        Assert.Equal(1, sync.StopCalls);
        Assert.Equal(4, sync.RequestCalls);
        Assert.Null(sync.TargetAccountIds[0]);
        Assert.All(sync.TargetAccountIds.Skip(1), targets =>
            Assert.Equal([accountId], targets));
    }

    [Fact]
    public async Task SuccessfulStartupUsesConfiguredPeriodicInterval()
    {
        var time = new ManualTimeProvider(Now);
        var sync = new FakeCalendarSyncService();
        var settings = new SchedulerSettingsStore(
            new AppSettings(sync: new SyncPreferences(1)));
        var hosted = new SyncSchedulerHostedService(
            sync,
            new ApplicationSettingsService(settings),
            new RecordingRefreshRequester(),
            new FakeResumeSignal(),
            time);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncTriggerReason.Startup, await sync.ReadReasonAsync());
        await Phase6Data.WaitUntilAsync(() => time.PendingTimerCount == 1);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(sync.Reasons.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SyncTriggerReason.Scheduled, await sync.ReadReasonAsync());
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResumeRequestsInternalRefreshAndImmediateExternalSync()
    {
        var time = new ManualTimeProvider(Now);
        var sync = new FakeCalendarSyncService();
        var refresh = new RecordingRefreshRequester();
        var resume = new FakeResumeSignal();
        var hosted = new SyncSchedulerHostedService(
            sync,
            new ApplicationSettingsService(new SchedulerSettingsStore(new AppSettings())),
            refresh,
            resume,
            time);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncTriggerReason.Startup, await sync.ReadReasonAsync());
        resume.Raise();

        Assert.Equal(InternalRefreshReason.Resume, await refresh.ReadReasonAsync());
        Assert.Equal(SyncTriggerReason.Resume, await sync.ReadReasonAsync());
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SyncIntervalChangeRestartsWaitFromChangeMomentWithoutImmediateSync()
    {
        var time = new ManualTimeProvider(Now);
        var sync = new FakeCalendarSyncService();
        var store = new SchedulerSettingsStore(
            new AppSettings(sync: new SyncPreferences(5)));
        var settings = new ApplicationSettingsService(store);
        var hosted = new SyncSchedulerHostedService(
            sync,
            settings,
            new RecordingRefreshRequester(),
            new FakeResumeSignal(),
            time);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncTriggerReason.Startup, await sync.ReadReasonAsync());
        await Phase6Data.WaitUntilAsync(() => time.PendingTimerCount == 1);
        time.Advance(TimeSpan.FromMinutes(2));

        await settings.UpdateAsync(
            current => new AppSettings(
                current.Display,
                new SyncPreferences(1),
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules),
            TestContext.Current.CancellationToken);
        await Phase6Data.WaitUntilAsync(() =>
            store.LoadCount >= 3 && time.PendingTimerCount == 1);

        Assert.False(sync.Reasons.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(sync.Reasons.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SyncTriggerReason.Scheduled, await sync.ReadReasonAsync());
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WindowsOnlyChangeDoesNotRestartPeriodicWait()
    {
        var time = new ManualTimeProvider(Now);
        var sync = new FakeCalendarSyncService();
        var store = new SchedulerSettingsStore(
            new AppSettings(sync: new SyncPreferences(1)));
        var settings = new ApplicationSettingsService(store);
        var hosted = new SyncSchedulerHostedService(
            sync,
            settings,
            new RecordingRefreshRequester(),
            new FakeResumeSignal(),
            time);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncTriggerReason.Startup, await sync.ReadReasonAsync());
        await Phase6Data.WaitUntilAsync(() => time.PendingTimerCount == 1);
        time.Advance(TimeSpan.FromSeconds(30));
        await settings.UpdateAsync(
            current => new AppSettings(
                current.Display,
                current.Sync,
                current.General,
                new WindowPreferences(
                    new WindowPlacement(10d, 20d, 800d, 600d, @"\\.\DISPLAY1"),
                    current.Windows.Settings),
                current.Accounts,
                current.ColorRules),
            TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(sync.Reasons.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SyncTriggerReason.Scheduled, await sync.ReadReasonAsync());
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IntervalChangeDoesNotCancelRunningSync()
    {
        var time = new ManualTimeProvider(Now);
        var scheduledStarted = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduledCompletion = new TaskCompletionSource<SyncRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new FakeCalendarSyncService
        {
            RequestHandler = (reason, cancellationToken) =>
            {
                if (reason != SyncTriggerReason.Scheduled)
                {
                    return Task.FromResult(new SyncRunResult(reason, []));
                }

                scheduledStarted.TrySetResult(cancellationToken);
                return scheduledCompletion.Task;
            },
        };
        var store = new SchedulerSettingsStore(
            new AppSettings(sync: new SyncPreferences(1)));
        var settings = new ApplicationSettingsService(store);
        var hosted = new SyncSchedulerHostedService(
            sync,
            settings,
            new RecordingRefreshRequester(),
            new FakeResumeSignal(),
            time);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SyncTriggerReason.Startup, await sync.ReadReasonAsync());
        await Phase6Data.WaitUntilAsync(() => time.PendingTimerCount == 1);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(SyncTriggerReason.Scheduled, await sync.ReadReasonAsync());
        var syncCancellationToken = await scheduledStarted.Task;

        await settings.UpdateAsync(
            current => new AppSettings(
                current.Display,
                new SyncPreferences(5),
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules),
            TestContext.Current.CancellationToken);

        Assert.False(syncCancellationToken.IsCancellationRequested);
        Assert.False(scheduledCompletion.Task.IsCompleted);
        scheduledCompletion.SetResult(new SyncRunResult(SyncTriggerReason.Scheduled, []));
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MinuteRefreshAlignsToLocalMinuteAndDoesNotUseExternalSync()
    {
        var time = new ManualTimeProvider(Now);
        var refresh = new RecordingRefreshRequester();
        var hosted = new MinuteRefreshHostedService(refresh, time);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(19));
        Assert.False(refresh.Reasons.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(InternalRefreshReason.MinuteBoundary, await refresh.ReadReasonAsync());

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(InternalRefreshReason.MinuteBoundary, await refresh.ReadReasonAsync());
        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    private static SyncRunResult NetworkFailure(SyncTriggerReason reason, Guid accountId)
    {
        var state = new AccountSyncState(
            accountId,
            SyncStatus.Failed,
            Now,
            null,
            [
                new CalendarSyncState(
                    "safe-calendar-key",
                    SyncStatus.Failed,
                    Now,
                    null,
                    null,
                    SyncErrorCategory.Network),
            ]);
        return new SyncRunResult(
            reason,
            [new SyncAccountResult(accountId, SyncAccountResultKind.Failed, state)]);
    }

    private sealed class FakeCalendarSyncService : ICalendarSyncService
    {
        public Channel<SyncTriggerReason> Reasons { get; } = Channel.CreateUnbounded<SyncTriggerReason>();

        public Func<SyncTriggerReason, SyncRunResult> ResultFactory { get; init; } =
            reason => new SyncRunResult(reason, []);

        public Func<SyncTriggerReason, CancellationToken, Task<SyncRunResult>>? RequestHandler { get; init; }

        public int InitializeCalls { get; private set; }

        public int RequestCalls { get; private set; }

        public int StopCalls { get; private set; }

        public List<IReadOnlyCollection<Guid>?> TargetAccountIds { get; } = [];

        public event EventHandler<SyncSnapshotChangedEventArgs>? SnapshotChanged;

        public event EventHandler<AccountSyncStateChangedEventArgs>? AccountStateChanged;

        public SyncSnapshot CurrentSnapshot { get; } = new([], [], [], Now);

        public IReadOnlyList<AccountSyncState> AccountStates => [];

        public Task<StartupCacheResult> InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCalls++;
            return Task.FromResult(new StartupCacheResult(CurrentSnapshot, [], false));
        }

        public Task RefreshFromSettingsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<SyncRunResult> RequestSyncAsync(
            SyncTriggerReason reason,
            CancellationToken cancellationToken = default) =>
            RequestCoreAsync(reason, null, cancellationToken);

        public Task<SyncRunResult> RequestSyncAsync(
            SyncTriggerReason reason,
            IReadOnlyCollection<Guid> internalAccountIds,
            CancellationToken cancellationToken = default) =>
            RequestCoreAsync(reason, internalAccountIds.ToArray(), cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask<SyncTriggerReason> ReadReasonAsync() => Reasons.Reader.ReadAsync();

        private Task<SyncRunResult> RequestCoreAsync(
            SyncTriggerReason reason,
            IReadOnlyCollection<Guid>? internalAccountIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCalls++;
            TargetAccountIds.Add(internalAccountIds);
            Reasons.Writer.TryWrite(reason);
            return RequestHandler?.Invoke(reason, cancellationToken)
                ?? Task.FromResult(ResultFactory(reason));
        }

        public void RaiseUnusedEventsForCompiler()
        {
            SnapshotChanged?.Invoke(this, new SyncSnapshotChangedEventArgs(CurrentSnapshot));
            var state = AccountStates.FirstOrDefault();
            if (state is not null)
            {
                AccountStateChanged?.Invoke(this, new AccountSyncStateChangedEventArgs(state));
            }
        }
    }

    private sealed class SchedulerSettingsStore : ISettingsStore
    {
        private AppSettings _settings;

        public SchedulerSettingsStore(AppSettings settings)
        {
            _settings = settings;
        }

        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRefreshRequester : IInternalRefreshRequester
    {
        public Channel<InternalRefreshReason> Reasons { get; } = Channel.CreateUnbounded<InternalRefreshReason>();

        public ValueTask RequestRefreshAsync(
            InternalRefreshReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reasons.Writer.TryWrite(reason);
            return ValueTask.CompletedTask;
        }

        public ValueTask<InternalRefreshReason> ReadReasonAsync() => Reasons.Reader.ReadAsync();
    }

    private sealed class FakeResumeSignal : ISystemResumeSignal
    {
        public event EventHandler? Resumed;

        public void Raise() => Resumed?.Invoke(this, EventArgs.Empty);
    }
}
