using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class MainWindowSettingsButtonTests
{
    private readonly WpfApplicationFixture _fixture;

    public MainWindowSettingsButtonTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void OpenSettingsCommand_ShowsAvailableSettingsWindowExactlyOnce()
    {
        var launcher = new RecordingSettingsWindowLauncher { IsAvailable = true };
        using var viewModel = CreateViewModel(launcher);

        Assert.True(viewModel.OpenSettingsCommand.CanExecute(null));

        viewModel.OpenSettingsCommand.Execute(null);

        Assert.Equal(1, launcher.ShowCount);
    }

    [Fact]
    public void OpenSettingsCommand_IsDisabledForUnavailableOrMissingLauncher()
    {
        var unavailable = new RecordingSettingsWindowLauncher { IsAvailable = false };
        using var unavailableViewModel = CreateViewModel(unavailable);
        using var missingViewModel = CreateViewModel();

        Assert.False(unavailableViewModel.OpenSettingsCommand.CanExecute(null));
        Assert.False(missingViewModel.OpenSettingsCommand.CanExecute(null));
    }

    [Fact]
    public void OpenSettingsCommand_SwallowsFailureAndWritesOneStructuredWarning()
    {
        var launcher = new RecordingSettingsWindowLauncher
        {
            IsAvailable = true,
            Exception = new InvalidOperationException("fixture failure"),
        };
        using var viewModel = CreateViewModel(launcher);
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var originalLogger = Log.Logger;

        try
        {
            Log.Logger = logger;
            var exception = Record.Exception(() => viewModel.OpenSettingsCommand.Execute(null));

            Assert.Null(exception);
        }
        finally
        {
            Log.Logger = originalLogger;
        }

        var warning = Assert.Single(sink.Events, logEvent =>
            logEvent.MessageTemplate.Text
                == "MainWindowActionFailed {Stage} {ErrorCategory}");
        Assert.Equal(LogEventLevel.Warning, warning.Level);
        Assert.Equal("Settings", ScalarText(warning, "Stage"));
        Assert.Equal("InvalidOperationException", ScalarText(warning, "ErrorCategory"));
    }

    [Fact]
    public void SettingsButton_HasStableLayoutAccessibilityTabOrderAndSyncBehavior()
    {
        _fixture.Invoke(() =>
        {
            var launcher = new RecordingSettingsWindowLauncher { IsAvailable = true };
            using var viewModel = CreateViewModel(launcher);
            var store = new TestSettingsStore();
            var window = CreateWindow(viewModel, store);

            try
            {
                window.Show();
                window.UpdateLayout();

                var statusArea = Assert.IsType<Border>(window.FindName("StatusArea"));
                var statusGrid = Assert.IsType<Grid>(statusArea.Child);
                var warningButton = Assert.IsType<Button>(window.FindName("WarningButton"));
                var refreshButton = Assert.IsType<Button>(window.FindName("RefreshButton"));
                var settingsButton = Assert.IsType<Button>(window.FindName("SettingsButton"));
                var timeline = Assert.IsType<ListBox>(window.FindName("TimelineList"));
                var addGoogle = Assert.IsType<Button>(window.FindName("AddGoogleAccountButton"));
                var addMicrosoft = Assert.IsType<Button>(
                    window.FindName("AddMicrosoftAccountButton"));
                var spinner = Assert.Single(statusGrid.Children.OfType<Grid>());
                var statusTexts = statusGrid.Children.OfType<TextBlock>().ToArray();

                Assert.Equal(820d, LayoutMetrics.InitialMainWidth);
                Assert.Equal(370d, LayoutMetrics.MinimumMainWidth);
                Assert.Equal(LayoutMetrics.InitialMainWidth, window.Width);
                Assert.Equal(LayoutMetrics.MinimumMainWidth, window.MinWidth);
                Assert.Equal(LayoutMetrics.StatusHeight, statusArea.ActualHeight);
                Assert.Equal(new Thickness(10d, 7d, 10d, 7d), statusArea.Padding);

                Assert.Equal(5, statusGrid.ColumnDefinitions.Count);
                Assert.Equal(GridUnitType.Auto, statusGrid.ColumnDefinitions[0].Width.GridUnitType);
                Assert.Equal(GridUnitType.Star, statusGrid.ColumnDefinitions[1].Width.GridUnitType);
                Assert.Equal(GridUnitType.Auto, statusGrid.ColumnDefinitions[2].Width.GridUnitType);
                Assert.Equal(GridUnitType.Auto, statusGrid.ColumnDefinitions[3].Width.GridUnitType);
                Assert.Equal(GridUnitType.Auto, statusGrid.ColumnDefinitions[4].Width.GridUnitType);
                Assert.Contains(statusTexts, text => Grid.GetColumn(text) == 0);
                Assert.Contains(statusTexts, text => Grid.GetColumn(text) == 1);
                Assert.Equal(2, Grid.GetColumn(warningButton));
                Assert.Equal(3, Grid.GetColumn(refreshButton));
                Assert.Equal(3, Grid.GetColumn(spinner));
                Assert.Equal(4, Grid.GetColumn(settingsButton));

                Assert.Equal(32d, settingsButton.Width);
                Assert.Equal(viewModel.WarningButtonHeight, settingsButton.Height);
                Assert.Equal(new Thickness(8d, 0d, 0d, 0d), settingsButton.Margin);
                Assert.Equal("Segoe MDL2 Assets", settingsButton.FontFamily.Source);
                Assert.Equal(14d, settingsButton.FontSize);
                Assert.Equal("\uE713", settingsButton.Content);
                Assert.Equal("設定", settingsButton.ToolTip);
                Assert.Equal("設定", AutomationProperties.GetName(settingsButton));
                Assert.Same(viewModel.OpenSettingsCommand, settingsButton.Command);
                Assert.Equal(Visibility.Visible, settingsButton.Visibility);
                Assert.True(settingsButton.IsEnabled);

                Assert.Equal(0, KeyboardNavigation.GetTabIndex(refreshButton));
                Assert.Equal(1, KeyboardNavigation.GetTabIndex(settingsButton));
                Assert.Equal(2, KeyboardNavigation.GetTabIndex(timeline));
                Assert.Equal(3, KeyboardNavigation.GetTabIndex(addGoogle));
                Assert.Equal(4, KeyboardNavigation.GetTabIndex(addMicrosoft));

                viewModel.IsSyncing = true;
                window.UpdateLayout();

                Assert.Equal(Visibility.Visible, settingsButton.Visibility);
                Assert.True(settingsButton.IsEnabled);
            }
            finally
            {
                CloseWindow(window);
            }

            using var missingLauncherViewModel = CreateViewModel();
            var unavailableWindow = CreateWindow(missingLauncherViewModel, new TestSettingsStore());
            try
            {
                unavailableWindow.Show();
                unavailableWindow.UpdateLayout();
                var settingsButton = Assert.IsType<Button>(
                    unavailableWindow.FindName("SettingsButton"));

                Assert.Equal(Visibility.Visible, settingsButton.Visibility);
                Assert.False(settingsButton.IsEnabled);
            }
            finally
            {
                CloseWindow(unavailableWindow);
            }
        });
    }

    [Fact]
    public void StatusSettingsResource_IsDedicatedAndResolvesToJapaneseLabel()
    {
        var textService = new ResourceUiTextService();

        Assert.Equal("Status.Settings", UiResourceKeys.StatusSettings);
        Assert.NotEqual(UiResourceKeys.TraySettings, UiResourceKeys.StatusSettings);
        Assert.Equal("設定", textService.Get(UiResourceKeys.StatusSettings));
    }

    private static MainWindowViewModel CreateViewModel(
        ISettingsWindowLauncher? settingsLauncher = null) => Phase6Data.CreateViewModel(
            new TestCalendarSyncService(),
            new MutableTimeProvider(Phase6Data.Now),
            settingsLauncher: settingsLauncher);

    private static MainWindow CreateWindow(
        MainWindowViewModel viewModel,
        TestSettingsStore store) => new(
            viewModel,
            new TimelineViewportCoordinator(),
            new TimeColumnWidthCalculator(new ResourceUiTextService()),
            new MainWindowPlacementService(store, new TestMonitorProvider()))
        {
            ShowInTaskbar = false,
        };

    private static void CloseWindow(MainWindow window)
    {
        window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
        window.Close();
    }

    private static string? ScalarText(LogEvent logEvent, string propertyName) =>
        logEvent.Properties.TryGetValue(propertyName, out var value)
            ? (value as ScalarValue)?.Value?.ToString()
            : null;

    private sealed class RecordingSettingsWindowLauncher : ISettingsWindowLauncher
    {
        public bool IsAvailable { get; init; }

        public Exception? Exception { get; init; }

        public int ShowCount { get; private set; }

        public void Show()
        {
            ShowCount++;
            if (Exception is not null)
            {
                throw Exception;
            }
        }
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
