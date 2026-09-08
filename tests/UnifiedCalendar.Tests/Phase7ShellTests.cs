using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.Core;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class Phase7ShellTests
{
    private static readonly DisplayMonitor PrimaryMonitor = new(
        @"\\.\DISPLAY1",
        new PixelRect(0, 0, 1920, 1040),
        96d,
        96d,
        true);

    [Fact]
    public void SavedBoundsRestoreOnNamedMonitor()
    {
        var saved = new WindowPlacement(
            100d,
            80d,
            800d,
            600d,
            PrimaryMonitor.DeviceName,
            96d,
            96d);

        var restored = WindowPlacementCalculator.RestoreMainWindow(saved, [PrimaryMonitor]);

        Assert.Equal(new DipRect(100d, 80d, 800d, 600d), restored);
    }

    [Fact]
    public void MissingSavedMonitorFallsBackToPrimaryRightEdge()
    {
        var saved = new WindowPlacement(20d, 30d, 700d, 500d, @"\\.\MISSING", 96d, 96d);

        var restored = WindowPlacementCalculator.RestoreMainWindow(saved, [PrimaryMonitor]);

        Assert.Equal(PrimaryMonitor.WorkAreaDip.Right - LayoutMetrics.InitialMainWidth, restored.Left);
        Assert.Equal(PrimaryMonitor.WorkAreaDip.Top, restored.Top);
        Assert.Equal(LayoutMetrics.InitialMainWidth, restored.Width);
        Assert.Equal(LayoutMetrics.InitialMainHeight, restored.Height);
    }

    [Fact]
    public void OffScreenBoundsAreClampedIntoWorkArea()
    {
        var saved = new WindowPlacement(
            5000d,
            4000d,
            800d,
            600d,
            PrimaryMonitor.DeviceName,
            96d,
            96d);

        var restored = WindowPlacementCalculator.RestoreMainWindow(saved, [PrimaryMonitor]);

        Assert.Equal(PrimaryMonitor.WorkAreaDip.Right, restored.Right);
        Assert.Equal(PrimaryMonitor.WorkAreaDip.Bottom, restored.Bottom);
    }

    [Fact]
    public void BoundsBelowMinimumAreExpandedBeforeClamp()
    {
        var saved = new WindowPlacement(
            100d,
            80d,
            200d,
            100d,
            PrimaryMonitor.DeviceName,
            96d,
            96d);

        var restored = WindowPlacementCalculator.RestoreMainWindow(saved, [PrimaryMonitor]);

        Assert.Equal(LayoutMetrics.MinimumMainWidth, restored.Width);
        Assert.Equal(LayoutMetrics.MinimumMainHeight, restored.Height);
    }

    [Fact]
    public void SavedDpiConvertsPositionButKeepsDipSize()
    {
        var saved = new WindowPlacement(
            100d,
            80d,
            600d,
            500d,
            PrimaryMonitor.DeviceName,
            144d,
            144d);

        var restored = WindowPlacementCalculator.RestoreMainWindow(saved, [PrimaryMonitor]);

        Assert.Equal(150d, restored.Left);
        Assert.Equal(120d, restored.Top);
        Assert.Equal(600d, restored.Width);
        Assert.Equal(500d, restored.Height);
    }

    [Fact]
    public void MixedDpiNonOriginMonitorRestoresInsideItsDipWorkAreaWithoutScalingSize()
    {
        var secondary = new DisplayMonitor(
            @"\\.\DISPLAY2",
            new PixelRect(1920, 0, 3840, 1080),
            144d,
            144d,
            false);
        var saved = new WindowPlacement(
            1400d,
            80d,
            600d,
            500d,
            secondary.DeviceName,
            144d,
            144d);

        var restored = WindowPlacementCalculator.RestoreMainWindow(saved, [PrimaryMonitor, secondary]);

        Assert.True(restored.Left >= secondary.WorkAreaDip.Left);
        Assert.True(restored.Top >= secondary.WorkAreaDip.Top);
        Assert.True(restored.Right <= secondary.WorkAreaDip.Right);
        Assert.True(restored.Bottom <= secondary.WorkAreaDip.Bottom);
        Assert.Equal(600d, restored.Width);
        Assert.Equal(500d, restored.Height);
    }

    [Fact]
    public void ChangedTargetDpiConvertsLocationIntoNonOriginWorkAreaWithoutScalingSize()
    {
        var secondary = new DisplayMonitor(
            @"\\.\DISPLAY2",
            new PixelRect(1920, 0, 3840, 1080),
            96d,
            96d,
            false);
        var saved = new WindowPlacement(
            1400d,
            80d,
            600d,
            500d,
            secondary.DeviceName,
            144d,
            144d);

        var restored = WindowPlacementCalculator.RestoreMainWindow(saved, [PrimaryMonitor, secondary]);

        Assert.True(restored.Left >= secondary.WorkAreaDip.Left);
        Assert.True(restored.Top >= secondary.WorkAreaDip.Top);
        Assert.True(restored.Right <= secondary.WorkAreaDip.Right);
        Assert.True(restored.Bottom <= secondary.WorkAreaDip.Bottom);
        Assert.Equal(600d, restored.Width);
        Assert.Equal(500d, restored.Height);
    }

    [Fact]
    public async Task PlacementSavePreservesAllOtherSettingsAndSettingsWindowBounds()
    {
        var settingsWindow = new WindowPlacement(20d, 30d, 640d, 480d, @"\\.\DISPLAY2");
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(
                display: new DisplayPreferences(days: 14),
                sync: new SyncPreferences(intervalMinutes: 15),
                general: new GeneralPreferences(StartWithWindows: false),
                windows: new WindowPreferences(Settings: settingsWindow)),
        };
        var service = new MainWindowPlacementService(store, new TestMonitorProvider());
        var main = new WindowPlacement(100d, 80d, 800d, 600d, @"\\.\DISPLAY1", 96d, 96d);

        await service.SaveAsync(main, TestContext.Current.CancellationToken);

        Assert.Equal(14, store.Settings.Display.Days);
        Assert.Equal(15, store.Settings.Sync.IntervalMinutes);
        Assert.False(store.Settings.General.StartWithWindows);
        Assert.Equal(main, store.Settings.Windows.Main);
        Assert.Equal(settingsWindow, store.Settings.Windows.Settings);
    }

    [Fact]
    public void SingleInstanceNamesContainCurrentUserSid()
    {
        const string sid = "S-1-5-21-100-200-300-400";

        var names = SingleInstanceNames.ForSid(sid);

        Assert.Equal($@"Local\UnifiedCalendar.{sid}", names.MutexName);
        Assert.Equal($"UnifiedCalendar.Activation.{sid}", names.PipeName);
    }

    [Fact]
    public async Task ActivationHandshakeGrantsForegroundBeforePrimaryActivates()
    {
        var uniqueName = $"test-{Guid.NewGuid():N}";
        var names = new SingleInstanceNames($@"Local\UnifiedCalendar.{uniqueName}", $"UnifiedCalendar.{uniqueName}");
        var calls = new ConcurrentQueue<string>();
        var foreground = new RecordingForegroundPermissionService(calls);
        await using var primary = new SingleInstanceCoordinator(names);
        await using var secondary = new SingleInstanceCoordinator(names, foreground);
        Assert.True(primary.TryAcquirePrimary());
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.StartListening(() =>
        {
            calls.Enqueue("activate");
            activated.SetResult();
            return Task.CompletedTask;
        });

        Assert.True(await secondary.SignalPrimaryAsync(TestContext.Current.CancellationToken));
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["grant", "activate"], calls.ToArray());
        Assert.Equal(Environment.ProcessId, foreground.ProcessId);
    }

    [Fact]
    public async Task ActivationListenerContinuesAfterHandlerThrows()
    {
        var uniqueName = $"test-{Guid.NewGuid():N}";
        var names = new SingleInstanceNames(
            $@"Local\UnifiedCalendar.{uniqueName}",
            $"UnifiedCalendar.{uniqueName}");
        await using var primary = new SingleInstanceCoordinator(names);
        await using var secondary = new SingleInstanceCoordinator(
            names,
            new PermissiveForegroundPermissionService());
        Assert.True(primary.TryAcquirePrimary());
        var firstAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        primary.StartListening(() =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstAttempted.SetResult();
                throw new InvalidOperationException("Expected test failure.");
            }

            secondHandled.SetResult();
            return Task.CompletedTask;
        });

        Assert.True(await secondary.SignalPrimaryAsync(TestContext.Current.CancellationToken));
        await firstAttempted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(await secondary.SignalPrimaryAsync(TestContext.Current.CancellationToken));
        await secondHandled.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public void StartupCommandQuotesAbsoluteExecutablePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "Unified Calendar", "UnifiedCalendar.App.exe");

        var command = StartupCommandLine.FromExecutablePath(path);

        Assert.Equal($"\"{Path.GetFullPath(path)}\"", command);
    }

    [Fact]
    public void ProductIdentityIsSharedByPersistenceAndShell()
    {
        Assert.Equal(AppIdentity.ProductDirectoryName, AppPaths.ProductDirectoryName);
        Assert.Equal("UnifiedCalendar", AppIdentity.ProductName);
    }

    [Fact]
    public void UnavailableSettingsLauncherFailsFast()
    {
        var launcher = new UnavailableSettingsWindowLauncher();

        Assert.False(launcher.IsAvailable);
        Assert.Throws<InvalidOperationException>(launcher.Show);
    }

    [Fact]
    public void ProductionCompositionProvidesPhase7bSettingsLauncher()
    {
        var services = new ServiceCollection();
        services.AddUnifiedCalendarApplication();
        using var provider = services.BuildServiceProvider();

        var launcher = provider.GetRequiredService<ISettingsWindowLauncher>();

        Assert.IsType<SettingsWindowLauncher>(launcher);
        Assert.True(launcher.IsAvailable);
        Assert.Same(
            launcher,
            provider.GetRequiredService<ISettingsWindowLifetime>());
        Assert.IsType<WindowsStartupRegistrationService>(
            provider.GetRequiredService<IStartupRegistrationService>());
    }

    [Fact]
    public void TrayAdapterContractHasNoNotificationOrMutableIconApi()
    {
        var memberNames = typeof(ITrayIconAdapter).GetMembers().Select(member => member.Name).ToArray();

        Assert.DoesNotContain("ShowBalloonTip", memberNames);
        Assert.DoesNotContain("BalloonTipText", memberNames);
        Assert.DoesNotContain("BalloonTipTitle", memberNames);
        Assert.DoesNotContain("Icon", memberNames);
        Assert.DoesNotContain("PlaySound", memberNames);
    }

    [Fact]
    public void EventRowMinimumHeightIsRoundedForCurrentDpi()
    {
        var at100 = LayoutMetrics.CalculateEventRowMinHeight(14d, 5d, 1d);
        var at150 = LayoutMetrics.CalculateEventRowMinHeight(14d, 5d, 1.5d);

        Assert.True(at100 >= LayoutMetrics.EventRowIconMaximumSize + 10d);
        Assert.Equal(Math.Ceiling(at100 * 1.5d), at150 * 1.5d, precision: 8);
    }

    private sealed class RecordingForegroundPermissionService(
        ConcurrentQueue<string> calls) : IForegroundPermissionService
    {
        public int ProcessId { get; private set; }

        public bool AllowSetForegroundWindow(int processId)
        {
            ProcessId = processId;
            calls.Enqueue("grant");
            return true;
        }
    }
}

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase7ShellWpfTests
{
    private readonly WpfApplicationFixture _fixture;

    public Phase7ShellWpfTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void PreAttachActivationsAreCoalescedAndDispatchedOnceOnAttach()
    {
        _fixture.Invoke(() =>
        {
            var store = new TestSettingsStore();
            var viewModel = Phase6Data.CreateViewModel(
                new TestCalendarSyncService(),
                new MutableTimeProvider(Phase6Data.Now),
                settings: store,
                dispatcher: new ImmediateUiDispatcher(),
                viewport: new RecordingTimelineViewport());
            var window = new MainWindow(
                viewModel,
                new TimelineViewportCoordinator(),
                new TimeColumnWidthCalculator(new ResourceUiTextService()),
                new MainWindowPlacementService(store, new TestMonitorProvider()))
            {
                ShowInTaskbar = false,
            };
            var dispatcher = new ImmediateUiDispatcher();
            var controller = new MainWindowController(dispatcher);

            controller.ShowAndActivateAsync().GetAwaiter().GetResult();
            controller.ShowAndActivateAsync().GetAwaiter().GetResult();

            Assert.Equal(0, dispatcher.InvocationCount);
            Assert.False(window.IsVisible);

            controller.Attach(window);

            Assert.Equal(1, dispatcher.InvocationCount);
            Assert.True(window.IsVisible);
            Assert.Equal(System.Windows.WindowState.Normal, window.WindowState);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }
}
