using System.Collections.Concurrent;
using System.Reflection;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.Sync;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Phase7b3NonParallelCollection
{
    public const string Name = "Phase 7b-3 non-parallel tests";
}

[Collection(Phase7b3NonParallelCollection.Name)]
public sealed class Phase7b3Tests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task StartWithWindowsSavesBeforeRegistryUpdateAndOtherSettingsDoNotTouchRegistry()
    {
        var store = new RecordingSettingsStore(AppSettings.CreateDefault());
        var settings = new ApplicationSettingsService(store);
        var startup = new RecordingStartupRegistrationService(initiallyEnabled: true, store);
        var general = new GeneralSettingsViewModel(
            settings,
            startup,
            new FixedApplicationInfoProvider(),
            new RecordingAppLocalPathLauncher(),
            new ResourceUiTextService(),
            _ => Task.CompletedTask);
        general.Initialize(store.Settings);

        general.StartWithWindows = false;
        await general.WaitForPendingUpdatesAsync();
        general.StartWithWindows = true;
        await general.WaitForPendingUpdatesAsync();

        Assert.Equal([false, true], startup.Values);
        Assert.Equal(2, store.SaveCount);
        Assert.True(store.Settings.General.StartWithWindows);
        Assert.All(startup.SettingsAtCall, value => Assert.Equal(value.Enabled, value.SavedSetting));

        var update = new UpdateSettingsViewModel(settings, new ResourceUiTextService());
        update.Initialize(store.Settings);
        update.SelectedInterval = update.IntervalOptions.Single(option => option.Minutes == 10);
        await update.WaitForPendingUpdatesAsync();

        Assert.Equal(2, startup.Values.Count);
        Assert.Equal(10, store.Settings.Sync.IntervalMinutes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ApplicationStartupUsesSavedSettingAsSourceOfTruth(
        bool savedSetting,
        bool registrySetting)
    {
        var store = new RecordingSettingsStore(
            new AppSettings(general: new GeneralPreferences(savedSetting)));
        var startup = new RecordingStartupRegistrationService(registrySetting, store);
        var services = new ServiceCollection()
            .AddSingleton<ISettingsStore>(store)
            .AddSingleton<IStartupRegistrationService>(startup)
            .BuildServiceProvider();

        await UnifiedCalendar.App.App.ApplyStartupRegistrationAsync(services);

        Assert.Equal([savedSetting], startup.Values);
        Assert.Equal(savedSetting, startup.IsEnabled);
        Assert.Equal(savedSetting, store.Settings.General.StartWithWindows);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public void ApplicationInfoBuildTimeDoesNotDependOnAssemblyFileTimestamp()
    {
        var provider = new AssemblyApplicationInfoProvider();
        var assemblyPath = typeof(AssemblyApplicationInfoProvider).Assembly.Location;
        var originalFileTime = File.GetLastWriteTimeUtc(assemblyPath);
        var before = provider.Get();
        try
        {
            File.SetLastWriteTimeUtc(
                assemblyPath,
                new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            var after = provider.Get();

            Assert.Equal(before, after);
            Assert.NotEqual(File.GetLastWriteTimeUtc(assemblyPath), before.BuildTimeUtc.UtcDateTime);
        }
        finally
        {
            File.SetLastWriteTimeUtc(assemblyPath, originalFileTime);
        }
    }

    [Fact]
    public async Task LocalPathLauncherUsesAllFourAppPathsAndSelectsLatestMatchingLog()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        Directory.CreateDirectory(paths.SettingsDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
        File.WriteAllText(paths.SettingsFile, "{}");
        var oldLog = Path.Combine(paths.LogsDirectory, "unifiedcalendar-20260901.log");
        var latestLog = Path.Combine(paths.LogsDirectory, "unifiedcalendar-20260903.log");
        var ignored = Path.Combine(paths.LogsDirectory, "other.log");
        File.WriteAllText(oldLog, string.Empty);
        File.WriteAllText(latestLog, string.Empty);
        File.WriteAllText(ignored, string.Empty);
        File.SetLastWriteTimeUtc(oldLog, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(latestLog, new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(ignored, new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc));
        var adapter = new RecordingLocalPathOpenAdapter();
        var launcher = new AppLocalPathLauncher(paths, adapter);

        await launcher.OpenAsync(
            AppLocalPathTarget.RootDirectory,
            TestContext.Current.CancellationToken);
        await launcher.OpenAsync(
            AppLocalPathTarget.SettingsFile,
            TestContext.Current.CancellationToken);
        await launcher.OpenAsync(
            AppLocalPathTarget.LogsDirectory,
            TestContext.Current.CancellationToken);
        await launcher.OpenAsync(
            AppLocalPathTarget.LatestLogFile,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [paths.RootDirectory, paths.SettingsFile, paths.LogsDirectory, latestLog],
            adapter.Paths);
    }

    [Fact]
    public async Task LocalPathLauncherIsNoOpWhenTargetsDoNotExistButOpensExistingTarget()
    {
        using var temporary = new TemporaryDirectory(create: false);
        var paths = new AppPaths(temporary.Path);
        var adapter = new RecordingLocalPathOpenAdapter();
        var launcher = new AppLocalPathLauncher(paths, adapter);

        foreach (var target in Enum.GetValues<AppLocalPathTarget>())
        {
            await launcher.OpenAsync(target, TestContext.Current.CancellationToken);
        }

        Assert.Empty(adapter.Paths);

        Directory.CreateDirectory(paths.RootDirectory);
        await launcher.OpenAsync(
            AppLocalPathTarget.RootDirectory,
            TestContext.Current.CancellationToken);
        Assert.Equal([paths.RootDirectory], adapter.Paths);
    }

    [Fact]
    public async Task LocalPathFailureLogDoesNotContainFullPath()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppPaths(temporary.Path);
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var previousLogger = Log.Logger;
        Log.Logger = logger;
        try
        {
            var launcher = new AppLocalPathLauncher(
                paths,
                new ThrowingLocalPathOpenAdapter());

            await launcher.OpenAsync(
                AppLocalPathTarget.RootDirectory,
                TestContext.Current.CancellationToken);

            var logEvent = Assert.Single(sink.Events);
            var serialized = $"{logEvent.RenderMessage()} {string.Join(' ', logEvent.Properties.Values)}";
            Assert.Contains(nameof(AppLocalPathTarget.RootDirectory), serialized, StringComparison.Ordinal);
            Assert.Contains(nameof(InvalidOperationException), serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(paths.RootDirectory, serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task ResetUsesSingleUpdatePreservesExcludedDataAndPropagatesAllChanges()
    {
        var initial = CreateNonDefaultSettings();
        var store = new RecordingSettingsStore(initial);
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var time = new ManualTimeProvider(Now);
        var refreshSignal = new InternalRefreshSignal();
        var timeZoneProvider = new MutableLocalTimeZoneProvider(TimeZoneInfo.Utc);
        var viewport = new RecordingTimelineViewport();
        using var mainViewModel = CreateMainViewModel(
            sync,
            settings,
            refreshSignal,
            timeZoneProvider,
            viewport,
            time);
        await mainViewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var initialProjectionCount = viewport.Restorations.Count;
        var startup = new RecordingStartupRegistrationService(false, store);
        var confirmation = new RecordingConfirmationService(confirmReset: true);
        using var settingsViewModel = new SettingsWindowViewModel(
            new NoPendingSettingsChanges(),
            new ResourceUiTextService(),
            settings,
            sync,
            new NoOpColorPickerService(),
            new BrushCache(),
            startup,
            new FixedApplicationInfoProvider(),
            new RecordingAppLocalPathLauncher(),
            confirmation);
        await settingsViewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var notifications = new List<ApplicationSettingsChangedEventArgs>();
        settings.SettingsChanged += (_, eventArgs) => notifications.Add(eventArgs);
        using var scheduler = new SyncSchedulerHostedService(
            sync,
            settings,
            new RecordingRefreshRequester(),
            new NoOpResumeSignal(),
            time);
        await scheduler.StartAsync(TestContext.Current.CancellationToken);
        await Phase6Data.WaitUntilAsync(() =>
            sync.RequestCount == 1 && time.PendingTimerCount == 1);
        var loadCountBeforeReset = store.LoadCount;

        await settingsViewModel.General.ResetToDefaultsCommand.ExecuteAsync(null);
        await Phase6Data.WaitUntilAsync(() =>
            viewport.Restorations.Count == initialProjectionCount + 1);
        await Phase6Data.WaitUntilAsync(() =>
            store.LoadCount >= loadCountBeforeReset + 2 && time.PendingTimerCount == 1);

        var current = store.Settings;
        var defaults = AppSettings.CreateDefault();
        Assert.Equal(defaults.Display, current.Display);
        Assert.Equal(defaults.Sync, current.Sync);
        Assert.Equal(defaults.General, current.General);
        Assert.Equal(defaults.Notifications, current.Notifications);
        Assert.Empty(current.ColorRules);
        Assert.Same(initial.Accounts[0], current.Accounts[0]);
        Assert.Equal(initial.Accounts[0].Calendars, current.Accounts[0].Calendars);
        Assert.Same(initial.Windows, current.Windows);
        Assert.Same(initial.Windows.Main, current.Windows.Main);
        Assert.Same(initial.Windows.Settings, current.Windows.Settings);
        Assert.Equal(1, store.SaveCount);
        var notification = Assert.Single(notifications);
        Assert.Equal(
            SettingsSection.Display
                | SettingsSection.Sync
                | SettingsSection.General
                | SettingsSection.ColorRules
                | SettingsSection.Notifications,
            notification.ChangedSections);
        Assert.Equal(defaults.Notifications.Enabled, settingsViewModel.Notifications.Enabled);
        Assert.Equal(defaults.Notifications.LeadMinutes, settingsViewModel.Notifications.LeadMinutes);
        Assert.Equal([true], startup.Values);
        Assert.Equal(1, confirmation.ResetPromptCount);
        Assert.DoesNotContain(
            typeof(ITokenStore),
            typeof(SettingsWindowViewModel)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType));

        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal(1, sync.RequestCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await Phase6Data.WaitUntilAsync(() => sync.RequestCount == 2);
        await scheduler.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResetCancellationLeavesSettingsAndRegistryUnchanged()
    {
        var initial = CreateNonDefaultSettings();
        var store = new RecordingSettingsStore(initial);
        var startup = new RecordingStartupRegistrationService(false, store);
        using var viewModel = CreateSettingsViewModel(
            new ApplicationSettingsService(store),
            startup,
            new RecordingConfirmationService(confirmReset: false));
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        await viewModel.General.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Same(initial, store.Settings);
        Assert.Equal(0, store.SaveCount);
        Assert.Empty(startup.Values);
    }

    [Fact]
    public async Task TimeZoneSignalReprojectsOnlyWhenZoneEffectivelyChanges()
    {
        var settings = new ApplicationSettingsService(new TestSettingsStore());
        var sync = new TestCalendarSyncService();
        var refreshSignal = new InternalRefreshSignal();
        var provider = new MutableLocalTimeZoneProvider(TimeZoneInfo.Utc);
        var viewport = new RecordingTimelineViewport();
        var time = new MutableTimeProvider(Now);
        using var viewModel = CreateMainViewModel(
            sync,
            settings,
            refreshSignal,
            provider,
            viewport,
            time);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var initialProjectionCount = viewport.Restorations.Count;
        var requester = new RecordingRefreshRequester(refreshSignal);
        var signal = new TestTimeZoneChangeSignal();
        using var runtime = new RuntimeTimeZoneService(
            provider,
            signal,
            new ImmediateUiDispatcher(),
            requester);
        runtime.Start();

        signal.Raise();
        Assert.Empty(requester.Reasons);
        Assert.Equal(initialProjectionCount, viewport.Restorations.Count);

        provider.Current = TimeZoneInfo.CreateCustomTimeZone(
            "Phase7b3-Tokyo",
            TimeSpan.FromHours(9),
            "Phase7b3-Tokyo",
            "Phase7b3-Tokyo");
        signal.Raise();
        await Phase6Data.WaitUntilAsync(() =>
            viewport.Restorations.Count == initialProjectionCount + 1);
        Assert.Equal([InternalRefreshReason.TimeZoneChanged], requester.Reasons);

        provider.Current = TimeZoneInfo.CreateCustomTimeZone(
            "Phase7b3-Tokyo",
            TimeSpan.FromHours(9),
            "Equivalent display name",
            "Equivalent standard name");
        signal.Raise();
        Assert.Single(requester.Reasons);
        Assert.Equal(initialProjectionCount + 1, viewport.Restorations.Count);
    }

    [Fact]
    public async Task TimeZoneSignalFromWorkerThreadGoesThroughUiDispatcher()
    {
        var provider = new MutableLocalTimeZoneProvider(TimeZoneInfo.Utc);
        var signal = new TestTimeZoneChangeSignal();
        var dispatcher = new DeferredUiDispatcher();
        var requester = new RecordingRefreshRequester();
        using var runtime = new RuntimeTimeZoneService(provider, signal, dispatcher, requester);
        runtime.Start();
        provider.Current = TimeZoneInfo.CreateCustomTimeZone(
            "Phase7b3-Worker",
            TimeSpan.FromHours(2),
            "Phase7b3-Worker",
            "Phase7b3-Worker");

        await Task.Run(signal.Raise, TestContext.Current.CancellationToken);
        await dispatcher.InvocationRequested.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.Equal(0, provider.RefreshCount);
        Assert.Empty(requester.Reasons);

        await dispatcher.RunPendingAsync();
        Assert.Equal(1, provider.RefreshCount);
        Assert.Equal([InternalRefreshReason.TimeZoneChanged], requester.Reasons);
    }

    [Fact]
    public void SyncRangeReadsCurrentTimeZoneFromProviderForEveryCalculation()
    {
        var provider = new MutableLocalTimeZoneProvider(TimeZoneInfo.Utc);
        using var service = new CalendarSyncService(
            [],
            new TestSettingsStore(),
            new EmptyCacheStore(),
            new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero)),
            provider);
        var createRange = typeof(CalendarSyncService).GetMethod(
            "CreateTimeRange",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var utcRange = Assert.IsType<TimeRangeUtc>(createRange.Invoke(service, [7, null]));
        provider.Current = TimeZoneInfo.CreateCustomTimeZone(
            "Phase7b3-Pacific",
            TimeSpan.FromHours(-8),
            "Phase7b3-Pacific",
            "Phase7b3-Pacific");
        var pacificRange = Assert.IsType<TimeRangeUtc>(createRange.Invoke(service, [7, null]));

        Assert.NotEqual(utcRange, pacificRange);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), utcRange.StartUtc);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 8, 0, 0, TimeSpan.Zero), pacificRange.StartUtc);
        Assert.Equal(2, provider.GetCurrentCount);
    }

    [Fact]
    public void ProductionCompositionUsesDynamicTimeZoneAndDedicatedLocalPathServices()
    {
        var services = new ServiceCollection();
        services.AddUnifiedCalendarApplication();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<WindowsLocalTimeZoneProvider>(
            provider.GetRequiredService<ILocalTimeZoneProvider>());
        Assert.IsType<RuntimeTimeZoneService>(
            provider.GetRequiredService<RuntimeTimeZoneService>());
        Assert.IsType<AppLocalPathLauncher>(
            provider.GetRequiredService<IAppLocalPathLauncher>());
        Assert.IsType<AssemblyApplicationInfoProvider>(
            provider.GetRequiredService<IApplicationInfoProvider>());
        Assert.IsType<MainWindowViewModel>(
            provider.GetRequiredService<MainWindowViewModel>());
        Assert.DoesNotContain(
            typeof(IExternalUriLauncher),
            typeof(AppLocalPathLauncher)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType));
    }

    private static SettingsWindowViewModel CreateSettingsViewModel(
        IApplicationSettingsService settings,
        IStartupRegistrationService startup,
        ISettingsResetConfirmationService confirmation) => new(
            new NoPendingSettingsChanges(),
            new ResourceUiTextService(),
            settings,
            new TestCalendarSyncService(),
            new NoOpColorPickerService(),
            new BrushCache(),
            startup,
            new FixedApplicationInfoProvider(),
            new RecordingAppLocalPathLauncher(),
            confirmation);

    private static MainWindowViewModel CreateMainViewModel(
        TestCalendarSyncService sync,
        IApplicationSettingsService settings,
        InternalRefreshSignal refreshSignal,
        ILocalTimeZoneProvider timeZoneProvider,
        RecordingTimelineViewport viewport,
        TimeProvider timeProvider)
    {
        var interactions = new RecordingAccountInteractionService();
        return new MainWindowViewModel(
            sync,
            refreshSignal,
            new CalendarPresentationService(timeProvider, ColorMetrics.ProgressLightnessDelta),
            settings,
            new ImmediateUiDispatcher(),
            new ResourceUiTextService(),
            interactions,
            interactions,
            new RecordingUriLauncher(),
            new BrushCache(),
            new SnapshotDiffer(),
            viewport,
            timeProvider,
            timeZoneProvider);
    }

    private static AppSettings CreateNonDefaultSettings()
    {
        var accountId = Guid.Parse("d8b9bba4-a856-4d0b-8d55-880857ef8b14");
        var account = new AccountSettings(
            accountId,
            ProviderKind.Google,
            "provider-subject",
            "Display Name",
            "person@example.invalid",
            true,
            $"google/{accountId:D}",
            [new CalendarSetting("visible", true), new CalendarSetting("hidden", false)]);
        var windows = new WindowPreferences(
            new WindowPlacement(10d, 20d, 700d, 500d, @"\\.\DISPLAY1"),
            new WindowPlacement(30d, 40d, 800d, 600d, @"\\.\DISPLAY1"));
        var rule = new ColorRule(
            "Rule",
            true,
            ColorRuleOperator.All,
            [ColorRuleCondition.ForTitle(TextMatchKind.Contains, "meeting")],
            new RgbColor(10, 20, 30));
        return new AppSettings(
            new DisplayPreferences(30, 20, DisplayDensity.Compact, new RgbColor(40, 50, 60)),
            new SyncPreferences(60),
            new GeneralPreferences(false),
            windows,
            [account],
            [rule],
            new NotificationPreferences(false, 45));
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public RecordingSettingsStore(AppSettings settings)
        {
            Settings = settings;
        }

        public AppSettings Settings { get; private set; }

        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStartupRegistrationService : IStartupRegistrationService
    {
        private readonly RecordingSettingsStore? _store;

        public RecordingStartupRegistrationService(
            bool initiallyEnabled,
            RecordingSettingsStore? store = null)
        {
            IsEnabled = initiallyEnabled;
            _store = store;
        }

        public bool IsEnabled { get; private set; }

        public List<bool> Values { get; } = [];

        public List<(bool Enabled, bool SavedSetting)> SettingsAtCall { get; } = [];

        public void SetEnabled(bool enabled)
        {
            Values.Add(enabled);
            if (_store is not null)
            {
                SettingsAtCall.Add((enabled, _store.Settings.General.StartWithWindows));
            }

            IsEnabled = enabled;
        }
    }

    private sealed class FixedApplicationInfoProvider : IApplicationInfoProvider
    {
        public ApplicationInfo Get() => new(
            "UnifiedCalendar",
            "1.0.0",
            new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
    }

    private sealed class RecordingAppLocalPathLauncher : IAppLocalPathLauncher
    {
        public List<AppLocalPathTarget> Targets { get; } = [];

        public Task OpenAsync(
            AppLocalPathTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Targets.Add(target);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLocalPathOpenAdapter : ILocalPathOpenAdapter
    {
        public List<string> Paths { get; } = [];

        public void Open(string path) => Paths.Add(path);
    }

    private sealed class ThrowingLocalPathOpenAdapter : ILocalPathOpenAdapter
    {
        public void Open(string path) =>
            throw new InvalidOperationException($"Unable to open {path}");
    }

    private sealed class RecordingConfirmationService(bool confirmReset)
        : ISettingsConfirmationService, ISettingsResetConfirmationService
    {
        public int ResetPromptCount { get; private set; }

        public bool ConfirmDiscard() => true;

        public SettingsExitDecision ConfirmApplicationExit() => SettingsExitDecision.Apply;

        public bool ConfirmReset()
        {
            ResetPromptCount++;
            return confirmReset;
        }
    }

    private sealed class NoOpColorPickerService : IColorPickerService
    {
        public RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor) => null;
    }

    private sealed class NoOpResumeSignal : ISystemResumeSignal
    {
        public event EventHandler? Resumed
        {
            add { }
            remove { }
        }
    }

    private sealed class MutableLocalTimeZoneProvider(TimeZoneInfo current)
        : IRefreshableLocalTimeZoneProvider
    {
        public TimeZoneInfo Current { get; set; } = current;

        public int GetCurrentCount { get; private set; }

        public int RefreshCount { get; private set; }

        public TimeZoneInfo GetCurrent()
        {
            GetCurrentCount++;
            return Current;
        }

        public TimeZoneInfo Refresh()
        {
            RefreshCount++;
            return Current;
        }
    }

    private sealed class TestTimeZoneChangeSignal : ITimeZoneChangeSignal
    {
        public event EventHandler? TimeZoneChanged;

        public void Start()
        {
        }

        public void Dispose()
        {
        }

        public void Raise() => TimeZoneChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class RecordingRefreshRequester(
        IInternalRefreshRequester? inner = null) : IInternalRefreshRequester
    {
        public List<InternalRefreshReason> Reasons { get; } = [];

        public async ValueTask RequestRefreshAsync(
            InternalRefreshReason reason,
            CancellationToken cancellationToken = default)
        {
            Reasons.Add(reason);
            if (inner is not null)
            {
                await inner.RequestRefreshAsync(reason, cancellationToken);
            }
        }
    }

    private sealed class DeferredUiDispatcher : IUiDispatcher
    {
        private Func<Task>? _pending;
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int InvocationCount { get; private set; }

        public TaskCompletionSource InvocationRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            _pending = callback;
            InvocationRequested.TrySetResult();
            return _completion.Task;
        }

        public async Task RunPendingAsync()
        {
            await Assert.IsAssignableFrom<Func<Task>>(_pending)();
            _completion.TrySetResult();
        }
    }

    private sealed class EmptyCacheStore : ICacheStore
    {
        public Task<AccountCache?> LoadAccountAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default) => Task.FromResult<AccountCache?>(null);

        public Task SaveAccountAsync(
            AccountCache accountCache,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAccountAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(bool create = true)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "UnifiedCalendar.Tests",
                Guid.NewGuid().ToString("N"));
            if (create)
            {
                Directory.CreateDirectory(Path);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            var allowedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "UnifiedCalendar.Tests"));
            if (fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }
}
