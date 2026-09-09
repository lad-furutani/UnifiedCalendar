using System.Windows;
using System.Windows.Threading;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.Core.Persistence;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class WindowPlacementCaptureTests
{
    private readonly WpfApplicationFixture _fixture;

    public WindowPlacementCaptureTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureNormalWindowUsesActualBoundsAndMonitorMetadata(bool settingsWindow)
    {
        _fixture.Invoke(() =>
        {
            var window = CreateWindow();
            window.Show();
            SetBounds(window, 120d, 90d, 720d, 520d);
            var expected = ActualBounds(window);
            var monitorProvider = new TestMonitorProvider();

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(window.Width, window.ActualWidth, precision: 6);
            Assert.Equal(window.Height, window.ActualHeight, precision: 6);

            var captured = Capture(settingsWindow, window, monitorProvider);

            AssertPlacement(expected, monitorProvider.Monitors[0], captured);
            window.Close();
        });
    }

    [Theory]
    [InlineData(false, WindowState.Maximized)]
    [InlineData(false, WindowState.Minimized)]
    [InlineData(true, WindowState.Maximized)]
    [InlineData(true, WindowState.Minimized)]
    public void CaptureNonNormalWindowUsesRestoreBounds(
        bool settingsWindow,
        WindowState windowState)
    {
        _fixture.Invoke(() =>
        {
            var window = CreateWindow();
            window.Show();
            SetBounds(window, 140d, 110d, 740d, 540d);
            var monitorProvider = new TestMonitorProvider();

            window.WindowState = windowState;
            PumpDispatcherUntil(() => window.WindowState == windowState);
            window.UpdateLayout();

            var expected = window.RestoreBounds;
            if (windowState == WindowState.Maximized)
            {
                Assert.True(
                    Math.Abs(window.ActualWidth - expected.Width) > 1d
                    || Math.Abs(window.ActualHeight - expected.Height) > 1d,
                    "Maximized bounds must differ from RestoreBounds for this regression test.");
            }

            var captured = Capture(settingsWindow, window, monitorProvider);

            AssertPlacement(expected, monitorProvider.Monitors[0], captured);
            window.WindowState = WindowState.Normal;
            PumpDispatcher();
            window.Close();
        });
    }

    [Fact]
    public void MainWindowExitAfterHideSavesLastActualBounds()
    {
        _fixture.Invoke(() =>
        {
            var store = new TestSettingsStore();
            var settingsService = new ApplicationSettingsService(store);
            var monitorProvider = new TestMonitorProvider();
            using var viewModel = Phase6Data.CreateViewModel(
                new TestCalendarSyncService(),
                new MutableTimeProvider(Phase6Data.Now),
                settings: store,
                dispatcher: new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
                settingsService: settingsService);
            var initialization = viewModel.InitializeAsync(CancellationToken.None);
            PumpDispatcherUntil(() => initialization.IsCompleted);
            initialization.GetAwaiter().GetResult();
            var window = new MainWindow(
                viewModel,
                new TimelineViewportCoordinator(),
                new TimeColumnWidthCalculator(new ResourceUiTextService()),
                new MainWindowPlacementService(settingsService, monitorProvider))
            {
                ShowInTaskbar = false,
            };
            window.InitializeShellAsync(CancellationToken.None).GetAwaiter().GetResult();
            window.Show();
            SetBounds(window, 160d, 130d, 760d, 560d);
            var visibleBounds = ActualBounds(window);

            window.Hide();
            PumpDispatcher();

            Assert.False(window.IsVisible);
            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.Equal(visibleBounds, ActualBounds(window));
            Assert.True(window.ActualWidth > 0d);
            Assert.True(window.ActualHeight > 0d);

            window.PrepareForApplicationExitAsync(CancellationToken.None).GetAwaiter().GetResult();

            var saved = Assert.IsType<WindowPlacement>(store.Settings.Windows.Main);
            AssertPlacement(visibleBounds, monitorProvider.Monitors[0], saved);
            window.Close();
        });
    }

    private static WindowPlacement Capture(
        bool settingsWindow,
        Window window,
        TestMonitorProvider monitorProvider)
    {
        var store = new TestSettingsStore();
        return settingsWindow
            ? new SettingsWindowPlacementService(
                new ApplicationSettingsService(store),
                monitorProvider).Capture(window)
            : new MainWindowPlacementService(store, monitorProvider).Capture(window);
    }

    private static void AssertPlacement(
        Rect expected,
        DisplayMonitor expectedMonitor,
        WindowPlacement actual)
    {
        Assert.Equal(expected.Left, actual.LeftDip, precision: 6);
        Assert.Equal(expected.Top, actual.TopDip, precision: 6);
        Assert.Equal(expected.Width, actual.WidthDip, precision: 6);
        Assert.Equal(expected.Height, actual.HeightDip, precision: 6);
        Assert.Equal(expectedMonitor.DeviceName, actual.MonitorDeviceName);
        Assert.Equal(expectedMonitor.DpiX, actual.SavedDpiX.GetValueOrDefault(), precision: 6);
        Assert.Equal(expectedMonitor.DpiY, actual.SavedDpiY.GetValueOrDefault(), precision: 6);
        Assert.True(actual.SavedDpiX.HasValue);
        Assert.True(actual.SavedDpiY.HasValue);
    }

    private static Window CreateWindow() => new()
    {
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
    };

    private static void SetBounds(
        Window window,
        double left,
        double top,
        double width,
        double height)
    {
        window.Left = left;
        window.Top = top;
        window.Width = width;
        window.Height = height;
        PumpDispatcher();
        window.UpdateLayout();
    }

    private static Rect ActualBounds(Window window) => new(
        window.Left,
        window.Top,
        window.ActualWidth,
        window.ActualHeight);
}
