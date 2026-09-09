using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Controls;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Sync;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase7XamlTests
{
    private readonly WpfApplicationFixture _fixture;

    public Phase7XamlTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void RuntimeThemeChangeReplacesOnlyThemeDictionaryAndOnlyWhenValueChanges()
    {
        _fixture.Invoke(() =>
        {
            var resources = new ResourceDictionary();
            var preserved = new ResourceDictionary { ["Preserved"] = "value" };
            resources.MergedDictionaries.Add(preserved);
            var preference = new MutableThemePreferenceReader();
            var startup = new StartupThemeService(preference);
            startup.Apply(resources);
            var initialTheme = Assert.Single(
                resources.MergedDictionaries,
                StartupThemeService.IsThemeDictionary);
            var signal = new RecordingThemeChangeSignal();
            using var runtime = new RuntimeThemeService(
                startup,
                signal,
                new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
            runtime.Start(resources);

            signal.Raise();
            PumpDispatcher();
            Assert.Same(initialTheme, Assert.Single(
                resources.MergedDictionaries,
                StartupThemeService.IsThemeDictionary));

            preference.IsDark = true;
            signal.Raise();
            PumpDispatcher();

            var darkTheme = Assert.Single(
                resources.MergedDictionaries,
                StartupThemeService.IsThemeDictionary);
            Assert.NotSame(initialTheme, darkTheme);
            Assert.Contains("Dark.xaml", darkTheme.Source!.OriginalString, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(preserved, resources.MergedDictionaries);
            Assert.Equal("value", resources["Preserved"]);
        });
    }

    [Fact]
    public void TrayHasFourActionsDisablesUnavailableSettingsAndUsesSameManualCommandState()
    {
        _fixture.Invoke(() =>
        {
            var time = new MutableTimeProvider(Phase6Data.Now);
            var sync = new TestCalendarSyncService();
            SyncTriggerReason? requestedReason = null;
            sync.RequestHandler = (reason, _) =>
            {
                requestedReason = reason;
                return Task.FromResult(new SyncRunResult(reason, []));
            };
            var viewModel = Phase6Data.CreateViewModel(sync, time);
            var trayAdapter = new RecordingTrayIconAdapter();
            var windowController = new RecordingMainWindowController();
            var unavailable = new RecordingSettingsLauncher { IsAvailable = false };
            using (var tray = new TrayIconService(
                trayAdapter,
                windowController,
                viewModel,
                unavailable,
                new ResourceUiTextService(),
                new ImmediateUiDispatcher()))
            {
                tray.Start();
                Assert.True(trayAdapter.Visible);
                Assert.Equal(4, tray.ContextMenu.Items.Count);
                Assert.Collection(
                    tray.ContextMenu.Items.Cast<System.Windows.Forms.ToolStripItem>(),
                    item => Assert.Equal("表示/非表示", item.Text),
                    item => Assert.Equal("今すぐ更新", item.Text),
                    item =>
                    {
                        Assert.Equal("設定", item.Text);
                        Assert.False(item.Enabled);
                    },
                    item => Assert.Equal("終了", item.Text));

                viewModel.IsSyncing = true;
                Assert.False(tray.ContextMenu.Items[1].Enabled);
                viewModel.IsSyncing = false;
                Assert.True(tray.ContextMenu.Items[1].Enabled);
                tray.ContextMenu.Items[1].PerformClick();
                Assert.Equal(SyncTriggerReason.Manual, requestedReason);

                tray.ContextMenu.Items[0].PerformClick();
                trayAdapter.RaiseDoubleClick();
                Assert.Equal(1, windowController.ToggleCount);
                Assert.Equal(1, windowController.ShowAndActivateCount);
                Assert.Equal(0, unavailable.ShowCount);
            }

            var available = new RecordingSettingsLauncher { IsAvailable = true };
            using (var tray = new TrayIconService(
                new RecordingTrayIconAdapter(),
                windowController,
                viewModel,
                available,
                new ResourceUiTextService(),
                new ImmediateUiDispatcher()))
            {
                tray.Start();
                Assert.True(tray.ContextMenu.Items[2].Enabled);
                tray.ContextMenu.Items[2].PerformClick();
                Assert.Equal(1, available.ShowCount);
            }

            viewModel.Dispose();
        });
    }

    [Fact]
    public void TrayRefreshRecoversWhenExecutionTaskCompletesAfterSyncingFlagClears()
    {
        _fixture.Invoke(() =>
        {
            var syncStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var syncCompletion = new TaskCompletionSource<SyncRunResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sync = new TestCalendarSyncService
            {
                RequestHandler = (_, _) =>
                {
                    syncStarted.TrySetResult();
                    return syncCompletion.Task;
                },
            };
            var dispatcher = new WpfUiDispatcher(Dispatcher.CurrentDispatcher);
            var viewModel = Phase6Data.CreateViewModel(
                sync,
                new MutableTimeProvider(Phase6Data.Now),
                dispatcher: dispatcher);
            using var tray = new TrayIconService(
                new RecordingTrayIconAdapter(),
                new RecordingMainWindowController(),
                viewModel,
                new RecordingSettingsLauncher(),
                new ResourceUiTextService(),
                dispatcher);
            tray.Start();
            var refreshItem = tray.ContextMenu.Items[1];

            refreshItem.PerformClick();
            PumpDispatcherUntil(() => syncStarted.Task.IsCompleted);
            PumpDispatcherUntil(() => !refreshItem.Enabled);
            Assert.False(refreshItem.Enabled);

            viewModel.IsSyncing = false;
            PumpDispatcher();
            Assert.False(refreshItem.Enabled);

            syncCompletion.SetResult(new SyncRunResult(SyncTriggerReason.Manual, []));
            PumpDispatcherUntil(() => viewModel.ManualRefreshCommand.CanExecute(null));
            PumpDispatcherUntil(() => refreshItem.Enabled);

            Assert.True(refreshItem.Enabled);
            viewModel.Dispose();
        });
    }

    [Fact]
    public void StickyOverlayDoesNotChangeTimelineViewportHeight()
    {
        _fixture.Invoke(() =>
        {
            var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
            var sync = new TestCalendarSyncService
            {
                CurrentSnapshot = Phase6Data.Snapshot(
                    [account],
                    [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                    [Phase6Data.Event("sticky-height")]),
            };
            var store = new TestSettingsStore();
            var viewModel = Phase6Data.CreateViewModel(
                sync,
                new MutableTimeProvider(Phase6Data.Now),
                settings: store,
                dispatcher: new ImmediateUiDispatcher());
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var window = new MainWindow(
                viewModel,
                new TimelineViewportCoordinator(),
                new TimeColumnWidthCalculator(new ResourceUiTextService()),
                new MainWindowPlacementService(store, new TestMonitorProvider()))
            {
                ShowInTaskbar = false,
            };
            window.Show();
            PumpDispatcherUntil(() => viewModel.TimelineItems.Count > 0);
            var list = Assert.IsType<ListBox>(window.FindName("TimelineList"));
            var header = Assert.Single(viewModel.TimelineItems.OfType<DayHeaderItemViewModel>());

            viewModel.UpdateStickyDate(null);
            PumpDispatcher();
            window.UpdateLayout();
            var heightWithoutText = list.ActualHeight;

            viewModel.UpdateStickyDate(header.Date);
            PumpDispatcher();
            window.UpdateLayout();

            Assert.True(heightWithoutText > 0d);
            Assert.Equal(heightWithoutText, list.ActualHeight, precision: 8);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }

    [Fact]
    public void StickyOverlayRightEdgeMatchesListContentArea()
    {
        _fixture.Invoke(() =>
        {
            var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
            var events = Enumerable.Range(0, 120)
                .Select(index => Phase6Data.Event(
                    $"sticky-edge-{index:D3}",
                    startUtc: Phase6Data.Now.AddMinutes(index * 30),
                    endUtc: Phase6Data.Now.AddMinutes((index * 30) + 20)))
                .ToArray();
            var sync = new TestCalendarSyncService
            {
                CurrentSnapshot = Phase6Data.Snapshot(
                    [account],
                    [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                    events),
            };
            var store = new TestSettingsStore();
            var viewModel = Phase6Data.CreateViewModel(
                sync,
                new MutableTimeProvider(Phase6Data.Now),
                settings: store,
                dispatcher: new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
            var window = new MainWindow(
                viewModel,
                new TimelineViewportCoordinator(),
                new TimeColumnWidthCalculator(new ResourceUiTextService()),
                new MainWindowPlacementService(store, new TestMonitorProvider()))
            {
                ShowInTaskbar = false,
            };
            window.Show();
            PumpDispatcherUntil(() =>
                viewModel.TimelineItems.OfType<EventRowViewModel>().Count() == events.Length);
            var list = Assert.IsType<ListBox>(window.FindName("TimelineList"));
            var overlay = Assert.IsType<Border>(window.FindName("StickyDateOverlay"));
            var scrollViewer = FindDescendant<ScrollViewer>(list);
            var allItems = viewModel.TimelineItems.ToArray();
            while (viewModel.TimelineItems.Count > 2)
            {
                viewModel.TimelineItems.RemoveAt(viewModel.TimelineItems.Count - 1);
            }

            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, scrollViewer.ComputedVerticalScrollBarVisibility);
            var heightWithoutScrollBar = list.ActualHeight;
            var differenceWithoutScrollBar = OverlayRightEdgeDifference(list, overlay);

            foreach (var item in allItems.Skip(2))
            {
                viewModel.TimelineItems.Add(item);
            }

            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, scrollViewer.ComputedVerticalScrollBarVisibility);
            var differenceWithScrollBar = OverlayRightEdgeDifference(list, overlay);
            Assert.Equal(heightWithoutScrollBar, list.ActualHeight, precision: 8);
            Assert.InRange(differenceWithScrollBar, 0d, 0.5d);
            Assert.InRange(differenceWithoutScrollBar, 0d, 0.5d);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }

    [Fact]
    public void ThemeLinkColorsMeetContrastRequirement()
    {
        _fixture.Invoke(() =>
        {
            foreach (var themeName in new[] { "Light", "Dark" })
            {
                var theme = LoadTheme(themeName);
                AssertContrastAtLeast(
                    themeName,
                    "LinkBrush",
                    Assert.IsType<SolidColorBrush>(theme["LinkBrush"]).Color,
                    "PanelBackgroundBrush",
                    Assert.IsType<SolidColorBrush>(theme["PanelBackgroundBrush"]).Color);
                AssertContrastAtLeast(
                    themeName,
                    "FocusForegroundBrush",
                    Assert.IsType<SolidColorBrush>(theme["FocusForegroundBrush"]).Color,
                    "FocusBrush",
                    Assert.IsType<SolidColorBrush>(theme["FocusBrush"]).Color);
            }
        });
    }

    [Fact]
    public void ScrollBarThemeColorsMeetNonTextContrast()
    {
        _fixture.Invoke(() =>
        {
            foreach (var themeName in new[] { "Light", "Dark" })
            {
                var theme = LoadTheme(themeName);
                AssertContrastAtLeast(
                    themeName,
                    "ScrollBarThumbBrush",
                    Assert.IsType<SolidColorBrush>(theme["ScrollBarThumbBrush"]).Color,
                    "ScrollBarTrackBrush",
                    Assert.IsType<SolidColorBrush>(theme["ScrollBarTrackBrush"]).Color,
                    3d);
            }
        });
    }

    [Fact]
    public void SharedControlDictionarySurvivesThemeSwap()
    {
        _fixture.Invoke(() =>
        {
            var controls = LoadControls();
            var preference = new MutableThemePreferenceReader();
            var startup = new StartupThemeService(preference);
            var vertical = new ScrollBar
            {
                Minimum = 0d,
                Maximum = 100d,
                Value = 50d,
                SmallChange = 1d,
                LargeChange = 10d,
                ViewportSize = 10d,
            };
            var horizontal = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Minimum = 0d,
                Maximum = 100d,
                Value = 50d,
                SmallChange = 1d,
                LargeChange = 10d,
                ViewportSize = 10d,
            };
            var panel = new StackPanel();
            panel.Children.Add(vertical);
            panel.Children.Add(horizontal);
            var list = new ListBox { Height = 100d };
            foreach (var index in Enumerable.Range(0, 100))
            {
                list.Items.Add($"Item {index}");
            }

            panel.Children.Add(list);
            var window = new Window
            {
                Content = panel,
                ShowInTaskbar = false,
            };
            window.Resources.MergedDictionaries.Add(controls);
            startup.Apply(window.Resources, useDarkTheme: false);
            window.Show();
            PumpDispatcher();
            window.UpdateLayout();

            Assert.False(StartupThemeService.IsThemeDictionary(controls));
            Assert.Contains(controls, window.Resources.MergedDictionaries);
            Assert.Single(window.Resources.MergedDictionaries, StartupThemeService.IsThemeDictionary);
            Assert.IsType<Style>(window.Resources[typeof(ScrollBar)]);
            Assert.Equal(SystemParameters.VerticalScrollBarWidth, vertical.Width);
            Assert.Equal(SystemParameters.HorizontalScrollBarHeight, horizontal.Height);
            var listScrollViewer = FindDescendant<ScrollViewer>(list);
            var listScrollBar = Assert.IsType<ScrollBar>(
                listScrollViewer.Template.FindName("PART_VerticalScrollBar", listScrollViewer));
            Assert.Equal(Visibility.Visible, listScrollBar.Visibility);
            Assert.Same(window.Resources[typeof(ScrollBar)], listScrollBar.Style);
            var verticalTrack = Assert.IsType<Track>(vertical.Template.FindName("PART_Track", vertical));
            var horizontalTrack = Assert.IsType<Track>(horizontal.Template.FindName("PART_Track", horizontal));
            Assert.NotNull(verticalTrack.DecreaseRepeatButton);
            Assert.NotNull(verticalTrack.IncreaseRepeatButton);
            Assert.NotNull(verticalTrack.Thumb);
            Assert.NotNull(horizontalTrack.DecreaseRepeatButton);
            Assert.NotNull(horizontalTrack.IncreaseRepeatButton);
            Assert.NotNull(horizontalTrack.Thumb);
            var lineDown = Assert.IsType<RepeatButton>(
                vertical.Template.FindName("PART_LineDownButton", vertical));
            Assert.Same(ScrollBar.LineDownCommand, lineDown.Command);
            Assert.Same(vertical, lineDown.CommandTarget);
            Assert.IsType<RoutedCommand>(lineDown.Command).Execute(null, lineDown.CommandTarget);
            Assert.Equal(51d, vertical.Value);
            Assert.Same(ScrollBar.PageDownCommand, verticalTrack.IncreaseRepeatButton.Command);
            Assert.Same(vertical, verticalTrack.IncreaseRepeatButton.CommandTarget);
            Assert.IsType<RoutedCommand>(verticalTrack.IncreaseRepeatButton.Command).Execute(
                null,
                verticalTrack.IncreaseRepeatButton.CommandTarget);
            Assert.Equal(61d, vertical.Value);
            verticalTrack.Value = 70d;
            Assert.Equal(70d, vertical.Value);
            var lineRight = Assert.IsType<RepeatButton>(
                horizontal.Template.FindName("PART_LineRightButton", horizontal));
            Assert.Same(ScrollBar.LineRightCommand, lineRight.Command);
            Assert.Same(horizontal, lineRight.CommandTarget);
            Assert.IsType<RoutedCommand>(lineRight.Command).Execute(null, lineRight.CommandTarget);
            Assert.Equal(51d, horizontal.Value);
            Assert.Same(ScrollBar.PageRightCommand, horizontalTrack.IncreaseRepeatButton.Command);
            Assert.Same(horizontal, horizontalTrack.IncreaseRepeatButton.CommandTarget);
            Assert.IsType<RoutedCommand>(horizontalTrack.IncreaseRepeatButton.Command).Execute(
                null,
                horizontalTrack.IncreaseRepeatButton.CommandTarget);
            Assert.Equal(61d, horizontal.Value);
            horizontalTrack.Value = 70d;
            Assert.Equal(70d, horizontal.Value);
            Assert.Equal(window.Resources["ScrollBarTrackBrush"], vertical.Background);
            Assert.Equal(window.Resources["ScrollBarThumbBrush"], verticalTrack.Thumb.Background);

            var lightThumb = verticalTrack.Thumb.Background;
            startup.Apply(window.Resources, useDarkTheme: true);
            PumpDispatcher();

            Assert.Contains(controls, window.Resources.MergedDictionaries);
            Assert.Single(window.Resources.MergedDictionaries, StartupThemeService.IsThemeDictionary);
            Assert.NotEqual(lightThumb, verticalTrack.Thumb.Background);
            Assert.Equal(window.Resources["ScrollBarTrackBrush"], vertical.Background);
            Assert.Equal(window.Resources["ScrollBarThumbBrush"], verticalTrack.Thumb.Background);

            window.Close();
        });
    }

    [Fact]
    public void FocusedLinkSwitchesForegroundAndBackgroundTogether()
    {
        _fixture.Invoke(() =>
        {
            var lightTheme = LoadTheme("Light");
            var darkTheme = LoadTheme("Dark");
            var textBox = new SelectableLinkTextBox
            {
                Text = "https://example.invalid/item",
            };
            var focusTarget = new Button { Content = "Target" };
            var panel = new StackPanel();
            panel.Children.Add(textBox);
            panel.Children.Add(focusTarget);
            var window = new Window
            {
                Content = panel,
                ShowInTaskbar = false,
            };
            window.Resources.MergedDictionaries.Add(lightTheme);
            window.Show();
            PumpDispatcher();
            var hyperlink = Assert.Single(textBox.FocusableLinks);

            Assert.Equal(lightTheme["LinkBrush"], hyperlink.Foreground);
            Assert.True(hyperlink.Focus());
            PumpDispatcher();
            Assert.Equal(lightTheme["FocusBrush"], hyperlink.Background);
            Assert.Equal(lightTheme["FocusForegroundBrush"], hyperlink.Foreground);

            window.Resources.MergedDictionaries.Clear();
            window.Resources.MergedDictionaries.Add(darkTheme);
            PumpDispatcher();
            Assert.Equal(darkTheme["FocusBrush"], hyperlink.Background);
            Assert.Equal(darkTheme["FocusForegroundBrush"], hyperlink.Foreground);

            Assert.True(focusTarget.Focus());
            PumpDispatcher();
            Assert.Null(hyperlink.Background);
            Assert.Equal(darkTheme["LinkBrush"], hyperlink.Foreground);

            window.Close();
        });
    }

    [Fact]
    public void DetailsPopupOpensOnClickCompletionNotOnPress()
    {
        _fixture.Invoke(() =>
        {
            var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
            var sourceUri = new Uri("https://calendar.example.invalid/events/click-a");
            var events = new[]
            {
                Phase6Data.Event("click-a", sourceDetailUri: sourceUri),
                Phase6Data.Event(
                    "click-b",
                    startUtc: Phase6Data.Now.AddHours(2),
                    endUtc: Phase6Data.Now.AddHours(3)),
            };
            var sync = new TestCalendarSyncService
            {
                CurrentSnapshot = Phase6Data.Snapshot(
                    [account],
                    [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                    events),
            };
            var store = new TestSettingsStore();
            var launcher = new RecordingUriLauncher();
            var viewModel = Phase6Data.CreateViewModel(
                sync,
                new MutableTimeProvider(Phase6Data.Now),
                settings: store,
                dispatcher: new ImmediateUiDispatcher(),
                uriLauncher: launcher);
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var window = new MainWindow(
                viewModel,
                new TimelineViewportCoordinator(),
                new TimeColumnWidthCalculator(new ResourceUiTextService()),
                new MainWindowPlacementService(store, new TestMonitorProvider()))
            {
                ShowInTaskbar = false,
            };
            window.Show();
            PumpDispatcherUntil(() =>
                viewModel.TimelineItems.OfType<EventRowViewModel>().Count() == events.Length);
            window.UpdateLayout();

            var list = Assert.IsType<ListBox>(window.FindName("TimelineList"));
            var rows = viewModel.TimelineItems.OfType<EventRowViewModel>().ToArray();
            var firstItem = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(rows[0]));
            var secondItem = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(rows[1]));

            RaisePreviewMouseButton(firstItem, Mouse.PreviewMouseDownEvent);
            Assert.False(viewModel.IsDetailsOpen);

            RaisePreviewMouseButton(firstItem, Mouse.PreviewMouseUpEvent);
            Assert.True(viewModel.IsDetailsOpen);
            Assert.Equal(rows[0].Key, Assert.IsType<EventDetailsViewModel>(viewModel.EventDetails).Key);

            viewModel.CloseDetails();
            PumpDispatcher();
            RaisePreviewMouseButton(firstItem, Mouse.PreviewMouseDownEvent);
            RaisePreviewMouseButton(secondItem, Mouse.PreviewMouseUpEvent);
            Assert.False(viewModel.IsDetailsOpen);

            RaisePreviewMouseButton(firstItem, Mouse.PreviewMouseDownEvent);
            RaisePreviewMouseButton(list, Mouse.PreviewMouseUpEvent);
            RaisePreviewMouseButton(firstItem, Mouse.PreviewMouseUpEvent);
            Assert.False(viewModel.IsDetailsOpen);

            RaisePreviewMouseButton(firstItem, Mouse.PreviewMouseDownEvent);
            RaisePreviewMouseButton(firstItem, Mouse.PreviewMouseUpEvent);
            Assert.True(viewModel.IsDetailsOpen);
            RaisePreviewMouseButton(firstItem, Mouse.PreviewMouseDownEvent, clickCount: 2);
            PumpDispatcherUntil(() => launcher.OpenedUris.Count == 1);
            Assert.Equal(sourceUri, launcher.OpenedUris[0]);
            Assert.False(viewModel.IsDetailsOpen);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }

    [Fact]
    public void DetailsPopupClosesOnWindowMoveAndResize()
    {
        _fixture.Invoke(() =>
        {
            var (window, viewModel, eventKey) = CreateShownMainWindow("pop016-window-change");

            OpenDetailsAndAssertOpen(window, viewModel, eventKey);
            window.Left += 12d;
            PumpDispatcher();
            window.UpdateLayout();
            Assert.False(viewModel.IsDetailsOpen);

            OpenDetailsAndAssertOpen(window, viewModel, eventKey);
            window.Width += 12d;
            PumpDispatcher();
            window.UpdateLayout();
            Assert.False(viewModel.IsDetailsOpen);

            OpenDetailsAndAssertOpen(window, viewModel, eventKey);
            window.Height += 12d;
            PumpDispatcher();
            window.UpdateLayout();
            Assert.False(viewModel.IsDetailsOpen);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }

    [Fact]
    public void DpiChangeClosesBothPopups()
    {
        _fixture.Invoke(() =>
        {
            var (window, viewModel, eventKey) = CreateShownMainWindow("pop016-dpi-change");
            var detailsPopup = Assert.IsType<Popup>(window.FindName("DetailsPopup"));
            var warningPopup = Assert.IsType<Popup>(window.FindName("WarningPopup"));
            var onDpiChanged = typeof(MainWindow).GetMethod(
                "OnDpiChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

            OpenDetailsAndAssertOpen(window, viewModel, eventKey);
            Assert.True(detailsPopup.IsOpen);
            onDpiChanged.Invoke(window, [new DpiScale(1d, 1d), new DpiScale(1.25d, 1.25d)]);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.False(viewModel.IsDetailsOpen);
            Assert.False(detailsPopup.IsOpen);

            warningPopup.IsOpen = true;
            PumpDispatcher();
            Assert.True(warningPopup.IsOpen);
            onDpiChanged.Invoke(window, [new DpiScale(1.25d, 1.25d), new DpiScale(1.5d, 1.5d)]);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.False(warningPopup.IsOpen);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }

    [Fact]
    public void MainWindowRestoresNormalHidesOnCloseClosesPopupOnMinimizeAndKeepsScroll()
    {
        _fixture.Invoke(() =>
        {
            var time = new MutableTimeProvider(Phase6Data.Now);
            var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
            var events = Enumerable.Range(0, 120)
                .Select(index => Phase6Data.Event(
                    $"event-{index:D3}",
                    startUtc: Phase6Data.Now.AddMinutes(index * 30),
                    endUtc: Phase6Data.Now.AddMinutes((index * 30) + 20)))
                .ToArray();
            var sync = new TestCalendarSyncService
            {
                CurrentSnapshot = Phase6Data.Snapshot(
                    [account],
                    [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                    events),
            };
            var store = new TestSettingsStore
            {
                Settings = new AppSettings(
                    windows: new WindowPreferences(new WindowPlacement(
                        40d,
                        50d,
                        700d,
                        500d,
                        @"\\.\DISPLAY1",
                        96d,
                        96d))),
            };
            var viewport = new TimelineViewportCoordinator();
            var viewModel = Phase6Data.CreateViewModel(
                sync,
                time,
                settings: store,
                dispatcher: new ImmediateUiDispatcher(),
                viewport: new RecordingTimelineViewport());
            viewModel.InitializeAsync().GetAwaiter().GetResult();
            var window = new MainWindow(
                viewModel,
                viewport,
                new TimeColumnWidthCalculator(new ResourceUiTextService()),
                new MainWindowPlacementService(store, new TestMonitorProvider()));
            Assert.True(window.ShowInTaskbar);
            window.ShowInTaskbar = false;
            window.WindowState = WindowState.Minimized;
            window.InitializeShellAsync().GetAwaiter().GetResult();
            Assert.Equal(WindowState.Normal, window.WindowState);
            window.Show();
            PumpDispatcherUntil(() => viewModel.TimelineItems.OfType<EventRowViewModel>().Any());
            window.UpdateLayout();

            Assert.False(window.Topmost);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            var handle = new WindowInteropHelper(window).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlStyle).ToInt64();
            Assert.Equal(0L, style & NativeMethods.WsMaximizeBox);

            var list = Assert.IsType<ListBox>(window.FindName("TimelineList"));
            var stickyOverlay = Assert.IsType<Border>(window.FindName("StickyDateOverlay"));
            var scrollViewer = FindDescendant<ScrollViewer>(list);
            scrollViewer.ScrollToVerticalOffset(240d);
            window.UpdateLayout();
            Assert.True(scrollViewer.VerticalOffset > 0d);
            var rows = viewModel.TimelineItems.OfType<EventRowViewModel>().ToArray();
            var firstVisibleRow = rows
                .Skip(1)
                .Select(row => list.ItemContainerGenerator.ContainerFromItem(row) as ListBoxItem)
                .Where(item => item is not null)
                .OrderBy(item => item!.TranslatePoint(new Point(), list).Y)
                .First(item => item!.TranslatePoint(new Point(), list).Y + item.ActualHeight > 0d)!;
            Assert.True(firstVisibleRow.Focus());
            RaisePreviewKey(list, Key.Up);
            var keyboardFocusedRow = Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);
            Assert.True(
                keyboardFocusedRow.TranslatePoint(new Point(), list).Y
                    >= stickyOverlay.ActualHeight - 0.5d,
                "Keyboard navigation must keep the focused row below the sticky overlay.");
            var offsetBeforeHide = scrollViewer.VerticalOffset;

            viewModel.OpenDetails(events[0].Key);
            PumpDispatcher();
            Assert.True(viewModel.IsDetailsOpen);
            window.WindowState = WindowState.Minimized;
            PumpDispatcher();
            Assert.False(viewModel.IsDetailsOpen);

            window.WindowState = WindowState.Normal;
            window.HideToTray();
            Assert.False(window.IsVisible);
            window.Show();
            PumpDispatcher();
            Assert.Equal(offsetBeforeHide, scrollViewer.VerticalOffset, precision: 3);

            window.Close();
            PumpDispatcher();
            Assert.False(window.IsVisible);
            window.Show();
            Assert.True(window.IsVisible);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }

    private static (MainWindow Window, MainWindowViewModel ViewModel, EventKey EventKey)
        CreateShownMainWindow(string sourceEventId)
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var calendarEvent = Phase6Data.Event(sourceEventId);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                [calendarEvent]),
        };
        var store = new TestSettingsStore();
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now),
            settings: store,
            dispatcher: new ImmediateUiDispatcher());
        viewModel.InitializeAsync().GetAwaiter().GetResult();
        var window = new MainWindow(
            viewModel,
            new TimelineViewportCoordinator(),
            new TimeColumnWidthCalculator(new ResourceUiTextService()),
            new MainWindowPlacementService(store, new TestMonitorProvider()))
        {
            ShowInTaskbar = false,
        };
        window.Show();
        PumpDispatcherUntil(() => viewModel.TimelineItems.OfType<EventRowViewModel>().Any());
        window.UpdateLayout();
        return (window, viewModel, calendarEvent.Key);
    }

    private static void OpenDetailsAndAssertOpen(
        MainWindow window,
        MainWindowViewModel viewModel,
        EventKey eventKey)
    {
        viewModel.OpenDetails(eventKey);
        PumpDispatcher();
        window.UpdateLayout();
        Assert.True(viewModel.IsDetailsOpen);
    }

    private static void RaisePreviewKey(UIElement element, Key key)
    {
        var eventArgs = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(element),
            0,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        element.RaiseEvent(eventArgs);
    }

    private static void RaisePreviewMouseButton(
        UIElement element,
        RoutedEvent routedEvent,
        int clickCount = 1)
    {
        var eventArgs = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = routedEvent,
        };
        typeof(MouseButtonEventArgs)
            .GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(eventArgs, [clickCount]);
        element.RaiseEvent(eventArgs);
    }

    private static T FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            try
            {
                return FindDescendant<T>(child);
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException($"A {typeof(T).Name} descendant was not found.");
    }

    private static double OverlayRightEdgeDifference(ListBox list, Border overlay)
    {
        var contentPresenter = FindDescendant<ScrollContentPresenter>(list);
        var overlayRight = overlay.TranslatePoint(new Point(overlay.ActualWidth, 0d), list).X;
        var contentRight = contentPresenter.TranslatePoint(
            new Point(contentPresenter.ActualWidth, 0d),
            list).X;
        return Math.Abs(overlayRight - contentRight);
    }

    private static ResourceDictionary LoadTheme(string themeName) => new()
    {
        Source = new Uri(
            $"/UnifiedCalendar.App;component/Resources/Themes/{themeName}.xaml",
        UriKind.Relative),
    };

    private static ResourceDictionary LoadControls() => new()
    {
        Source = new Uri(
            "/UnifiedCalendar.App;component/Resources/Controls.xaml",
            UriKind.Relative),
    };

    private static void AssertContrastAtLeast(
        string themeName,
        string foregroundName,
        Color foreground,
        string backgroundName,
        Color background,
        double minimumRatio = 4.5d)
    {
        var ratio = ContrastRatio(foreground, background);
        Assert.True(
            ratio >= minimumRatio,
            $"{themeName}: {foregroundName} on {backgroundName} was {ratio:F2}:1.");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126d * Linearize(color.R))
        + (0.7152d * Linearize(color.G))
        + (0.0722d * Linearize(color.B));

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045d
            ? value / 12.92d
            : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
    }

    private sealed class MutableThemePreferenceReader : IThemePreferenceReader
    {
        public bool IsDark { get; set; }

        public bool IsDarkTheme() => IsDark;
    }

    private sealed class RecordingThemeChangeSignal : IThemeChangeSignal
    {
        public event EventHandler? ThemeChanged;

        public void Start()
        {
        }

        public void Raise() => ThemeChanged?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingTrayIconAdapter : ITrayIconAdapter
    {
        public event EventHandler? DoubleClick;

        public System.Windows.Forms.ContextMenuStrip? ContextMenu { get; set; }

        public bool Visible { get; set; }

        public void ShowBalloonTip(int timeoutMilliseconds, string title, string text)
        {
        }

        public void RaiseDoubleClick() => DoubleClick?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingMainWindowController : IMainWindowController
    {
        public int ShowAndActivateCount { get; private set; }

        public int ToggleCount { get; private set; }

        public Task ShowAndActivateAsync()
        {
            ShowAndActivateCount++;
            return Task.CompletedTask;
        }

        public Task ToggleVisibilityAsync()
        {
            ToggleCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSettingsLauncher : ISettingsWindowLauncher
    {
        public bool IsAvailable { get; set; }

        public int ShowCount { get; private set; }

        public void Show() => ShowCount++;
    }
}
