using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Persistence;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class Phase7bSettingsTests
{
    [Fact]
    public async Task SubscriberCanReadSettingsSynchronouslyFromChangeNotification()
    {
        var service = new ApplicationSettingsService(new TestSettingsStore());
        AppSettings? notified = null;
        AppSettings? loaded = null;
        service.SettingsChanged += (_, eventArgs) =>
        {
            notified = eventArgs.Current;
            loaded = service.LoadAsync().GetAwaiter().GetResult();
        };

        var updateTask = Task.Run(() => service.UpdateAsync(
            current => new AppSettings(
                new DisplayPreferences(days: 14),
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules)));
        var completedTask = await Task.WhenAny(
            updateTask,
            Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.Same(updateTask, completedTask);
        _ = await updateTask;
        Assert.Same(notified, loaded);
    }

    [Fact]
    public async Task NotificationOrderMatchesWriteOrderUnderConcurrentUpdates()
    {
        var store = new OrderedRecordingSettingsStore();
        var service = new ApplicationSettingsService(store);
        var notifications = new ConcurrentQueue<AppSettings>();
        service.SettingsChanged += (_, eventArgs) =>
        {
            if (store.SavedSettings.TryPeek(out var firstSaved)
                && ReferenceEquals(firstSaved, eventArgs.Current))
            {
                Thread.Sleep(100);
            }

            notifications.Enqueue(eventArgs.Current);
        };

        await Task.WhenAll(
            new[] { 10, 20, 30, 40, 50 }
                .Select(days => service.UpdateAsync(
                    current => new AppSettings(
                        new DisplayPreferences(days: days),
                        current.Sync,
                        current.General,
                        current.Windows,
                        current.Accounts,
                        current.ColorRules))));

        var saved = store.SavedSettings.ToArray();
        var notified = notifications.ToArray();
        Assert.Equal(
            saved.Select(settings => settings.Display.Days),
            notified.Select(settings => settings.Display.Days));
        Assert.Same(saved[^1], notified[^1]);
    }

    [Fact]
    public async Task ConcurrentSettingsAndPlacementUpdatesDoNotLoseEitherChange()
    {
        var store = new DelayedSettingsStore();
        var settings = new ApplicationSettingsService(store);
        var placementService = new MainWindowPlacementService(settings, new TestMonitorProvider());
        var placement = new WindowPlacement(120d, 90d, 800d, 600d, @"\\.\DISPLAY1", 96d, 96d);

        var displayUpdate = settings.UpdateAsync(
            current => new AppSettings(
                new DisplayPreferences(days: 14),
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules),
            TestContext.Current.CancellationToken);
        var placementUpdate = placementService.SaveAsync(
            placement,
            TestContext.Current.CancellationToken);

        await Task.WhenAll(displayUpdate, placementUpdate);

        Assert.Equal(14, store.Settings.Display.Days);
        Assert.Equal(placement, store.Settings.Windows.Main);
        Assert.Equal(1, store.MaximumConcurrentAccessCount);
    }

    [Fact]
    public async Task WindowsOnlyUpdateDoesNotReprojectButDisplayUpdateDoes()
    {
        var store = new TestSettingsStore();
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var viewport = new RecordingTimelineViewport();
        using var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now),
            settings: store,
            viewport: viewport,
            settingsService: settings);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var initialProjectionCount = viewport.Restorations.Count;
        var initialEventRowMinHeight = viewModel.EventRowMinHeight;
        SettingsSection notifiedSections = SettingsSection.None;
        settings.SettingsChanged += (_, eventArgs) => notifiedSections |= eventArgs.ChangedSections;
        var displayProjectionCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

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
        await settings.UpdateAsync(
            current => new AppSettings(
                new DisplayPreferences(
                    days: current.Display.Days + 1,
                    fontSizeDip: DisplayPreferences.MaximumFontSizeDip,
                    density: current.Display.Density,
                    defaultEventColor: current.Display.DefaultEventColor),
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules),
            TestContext.Current.CancellationToken);
        await displayProjectionCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await Phase6Data.WaitUntilAsync(
            () => viewport.Restorations.Count == initialProjectionCount + 1);

        Assert.Equal(SettingsSection.Windows | SettingsSection.Display, notifiedSections);
        Assert.Equal(initialProjectionCount + 1, viewport.Restorations.Count);
        Assert.Equal(
            LayoutMetrics.CalculateEventRowMinHeight(
                viewModel.FontSizeDip,
                viewModel.EventRowMargin.Top,
                1d),
            viewModel.EventRowMinHeight);
        Assert.True(viewModel.EventRowMinHeight > initialEventRowMinHeight);

        void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(MainWindowViewModel.FontSizeDip))
            {
                displayProjectionCompleted.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task NoChangeUpdateDoesNotSaveRaiseEventOrAdvanceNotificationChain()
    {
        var store = new CountingSettingsStore();
        var service = new ApplicationSettingsService(store);
        var notificationCount = 0;
        service.SettingsChanged += (_, _) => notificationCount++;
        var initialNotificationTail = GetNotificationTail(service);

        var result = await service.UpdateAsync(
            current => current,
            TestContext.Current.CancellationToken);

        Assert.Same(store.Settings, result);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, notificationCount);
        Assert.Same(initialNotificationTail, GetNotificationTail(service));

        _ = await service.UpdateAsync(
            current => new AppSettings(
                new DisplayPreferences(days: current.Display.Days + 1),
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, notificationCount);
        Assert.NotSame(initialNotificationTail, GetNotificationTail(service));
    }

    private static Task GetNotificationTail(ApplicationSettingsService service) =>
        Assert.IsAssignableFrom<Task>(
            typeof(ApplicationSettingsService)
                .GetField("_notificationTail", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(service));

    private sealed class CountingSettingsStore : ISettingsStore
    {
        private AppSettings _settings = AppSettings.CreateDefault();

        public AppSettings Settings => _settings;

        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class DelayedSettingsStore : ISettingsStore
    {
        private readonly object _gate = new();
        private AppSettings _settings = AppSettings.CreateDefault();
        private int _activeAccessCount;

        public AppSettings Settings
        {
            get
            {
                lock (_gate)
                {
                    return _settings;
                }
            }
        }

        public int MaximumConcurrentAccessCount { get; private set; }

        public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            BeginAccess();
            try
            {
                await Task.Delay(20, cancellationToken);
                return Settings;
            }
            finally
            {
                EndAccess();
            }
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            BeginAccess();
            try
            {
                await Task.Delay(20, cancellationToken);
                lock (_gate)
                {
                    _settings = settings;
                }
            }
            finally
            {
                EndAccess();
            }
        }

        private void BeginAccess()
        {
            var active = Interlocked.Increment(ref _activeAccessCount);
            MaximumConcurrentAccessCount = Math.Max(MaximumConcurrentAccessCount, active);
        }

        private void EndAccess() => Interlocked.Decrement(ref _activeAccessCount);
    }

    private sealed class OrderedRecordingSettingsStore : ISettingsStore
    {
        private readonly object _gate = new();
        private AppSettings _settings = AppSettings.CreateDefault();

        public ConcurrentQueue<AppSettings> SavedSettings { get; } = new();

        public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(10, cancellationToken);
            lock (_gate)
            {
                return _settings;
            }
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            await Task.Delay(10, cancellationToken);
            lock (_gate)
            {
                _settings = settings;
                SavedSettings.Enqueue(settings);
            }
        }
    }
}
