using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UnifiedCalendar.App;
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
public sealed class MainWindowMinimumWidthTests
{
    private readonly WpfApplicationFixture _fixture;

    public MainWindowMinimumWidthTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(10d)]
    [InlineData(14d)]
    [InlineData(24d)]
    public void StatusMinimumContentWidthFitsApprovedMinimum(double fontSize)
    {
        var measurement = _fixture.Invoke(() => new StatusMinimumWidthCalculator(
            new ResourceUiTextService()).Calculate(
                new FontFamily("Yu Gothic UI"),
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal,
                fontSize,
                1d));

        TestContext.Current.TestOutputHelper?.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "font={0:F0} time={1:F2} refreshText={2:F2} refreshButton={3:F2} "
            + "fixed={4:F2} margins={5:F2} total={6:F2}",
            fontSize,
            measurement.CurrentTimeTextWidth,
            measurement.RefreshTextWidth,
            measurement.RefreshButtonWidth,
            measurement.FixedControlWidth,
            measurement.MarginAndBorderPaddingWidth,
            measurement.TotalWidth));
        Assert.True(
            measurement.TotalWidth <= 370d,
            $"The {fontSize:F0}dip status content requires {measurement.TotalWidth:F2}dip.");
    }

    [Fact]
    public void ApprovedMinimumWidthPreservesEveryOtherWindowAndPopupDimension()
    {
        Assert.Equal(370d, LayoutMetrics.MinimumMainWidth);
        Assert.Equal(400d, LayoutMetrics.MinimumMainHeight);
        Assert.Equal(820d, LayoutMetrics.InitialMainWidth);
        Assert.Equal(640d, LayoutMetrics.InitialMainHeight);
        Assert.Equal(760d, LayoutMetrics.InitialSettingsWidth);
        Assert.Equal(560d, LayoutMetrics.InitialSettingsHeight);
        Assert.Equal(640d, LayoutMetrics.MinimumSettingsWidth);
        Assert.Equal(440d, LayoutMetrics.MinimumSettingsHeight);
        Assert.Equal(520d, LayoutMetrics.PopupMaximumWidth);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(24)]
    public void MainWindowAtApprovedMinimumKeepsFixedStatusAndTimelineContentVisible(
        int fontSize)
    {
        _fixture.Invoke(() =>
        {
            var context = CreateWindow(fontSize);
            try
            {
                context.Window.Measure(new Size(
                    LayoutMetrics.MinimumMainWidth,
                    LayoutMetrics.MinimumMainHeight));
                context.Window.Arrange(new Rect(
                    0d,
                    0d,
                    LayoutMetrics.MinimumMainWidth,
                    LayoutMetrics.MinimumMainHeight));
                context.Window.Show();
                PumpDispatcherUntil(() => context.ViewModel.TimelineItems
                    .OfType<EventRowViewModel>()
                    .Any());
                context.ViewModel.LastUpdateText = new ResourceUiTextService().Get(
                    UiResourceKeys.LastUpdateOtherDay,
                    Phase6Data.Now.AddDays(-1));
                PumpDispatcher();
                context.Window.UpdateLayout();

                var statusArea = Assert.IsType<Border>(context.Window.FindName("StatusArea"));
                var statusGrid = Assert.IsType<Grid>(statusArea.Child);
                var currentTime = Assert.IsType<TextBlock>(
                    context.Window.FindName("CurrentTimeTextBlock"));
                var lastUpdate = Assert.IsType<TextBlock>(
                    context.Window.FindName("LastUpdateTextBlock"));
                var warning = Assert.IsType<Button>(context.Window.FindName("WarningButton"));
                var refresh = Assert.IsType<Button>(context.Window.FindName("RefreshButton"));
                var settings = Assert.IsType<Button>(context.Window.FindName("SettingsButton"));

                var pixelsPerDip = VisualTreeHelper.GetDpi(context.Window).PixelsPerDip;
                Assert.InRange(
                    context.Window.Width,
                    LayoutMetrics.MinimumMainWidth,
                    LayoutMetrics.MinimumMainWidth + (1d / pixelsPerDip));
                Assert.InRange(
                    context.Window.Height,
                    LayoutMetrics.MinimumMainHeight,
                    LayoutMetrics.MinimumMainHeight + (1d / pixelsPerDip));
                Assert.Equal(5, statusGrid.ColumnDefinitions.Count);
                Assert.Equal(GridUnitType.Auto, statusGrid.ColumnDefinitions[0].Width.GridUnitType);
                Assert.Equal(GridUnitType.Star, statusGrid.ColumnDefinitions[1].Width.GridUnitType);
                Assert.Equal(GridUnitType.Auto, statusGrid.ColumnDefinitions[2].Width.GridUnitType);
                Assert.Equal(GridUnitType.Auto, statusGrid.ColumnDefinitions[3].Width.GridUnitType);
                Assert.Equal(GridUnitType.Auto, statusGrid.ColumnDefinitions[4].Width.GridUnitType);
                Assert.Equal(TextTrimming.CharacterEllipsis, lastUpdate.TextTrimming);
                Assert.Equal(TextWrapping.NoWrap, lastUpdate.TextWrapping);
                Assert.Equal(new Thickness(0d, 0d, 16d, 0d), lastUpdate.Margin);
                Assert.Equal(DependencyProperty.UnsetValue, warning.ReadLocalValue(
                    FrameworkElement.HorizontalAlignmentProperty));
                Assert.Equal(new Thickness(8d, 0d, 0d, 0d), settings.Margin);

                Assert.Equal(Visibility.Visible, currentTime.Visibility);
                Assert.Equal(Visibility.Visible, lastUpdate.Visibility);
                Assert.Equal(Visibility.Visible, warning.Visibility);
                Assert.Equal(Visibility.Visible, refresh.Visibility);
                Assert.Equal(Visibility.Visible, settings.Visibility);
                Assert.True(currentTime.ActualWidth + 0.5d >= NaturalTextWidth(currentTime));
                Assert.Equal(32d, warning.ActualWidth);
                Assert.True(refresh.ActualWidth + 0.5d >= refresh.DesiredSize.Width);
                Assert.Equal(32d, settings.ActualWidth);
                Assert.True(lastUpdate.ActualWidth > 0d);
                var lastUpdateNaturalWidth = NaturalTextWidth(lastUpdate);
                Assert.True(
                    lastUpdate.ActualWidth + 0.5d >= lastUpdateNaturalWidth
                    || lastUpdate.TextTrimming == TextTrimming.CharacterEllipsis);

                var row = Assert.Single(context.ViewModel.TimelineItems.OfType<EventRowViewModel>());
                var list = Assert.IsType<ListBox>(context.Window.FindName("TimelineList"));
                list.ScrollIntoView(row);
                list.UpdateLayout();
                var container = Assert.IsType<ListBoxItem>(
                    list.ItemContainerGenerator.ContainerFromItem(row));
                var rowTexts = FindDescendants<TextBlock>(container)
                    .Where(text => ReferenceEquals(text.DataContext, row))
                    .ToArray();
                var time = Assert.Single(rowTexts, text => Grid.GetColumn(text) == 0);
                var title = Assert.Single(rowTexts, text => Grid.GetColumn(text) == 1);
                var attention = Assert.Single(rowTexts, text => text.Name == "AttentionIcon");
                var provider = Assert.Single(rowTexts, text => Grid.GetColumn(text) == 4);
                var meeting = Assert.Single(
                    FindDescendants<Button>(container),
                    button => button.Name == "MeetingButton");

                Assert.Equal(Visibility.Visible, time.Visibility);
                Assert.Equal(Visibility.Visible, attention.Visibility);
                Assert.Equal(Visibility.Visible, meeting.Visibility);
                Assert.Equal(Visibility.Visible, provider.Visibility);
                Assert.True(time.ActualWidth > 0d);
                Assert.True(attention.ActualWidth > 0d);
                Assert.True(meeting.ActualWidth > 0d);
                Assert.True(provider.ActualWidth > 0d);
                Assert.True(title.ActualWidth > 0d);
                Assert.Equal(TextTrimming.CharacterEllipsis, title.TextTrimming);
                Assert.True(
                    NaturalTextWidth(title) > title.ActualWidth + 0.5d,
                    "The long fixture title should be trimmed at the approved minimum width.");

                TestContext.Current.TestOutputHelper?.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "font={0} window={1:F2} status={2:F2} current={3:F2}/{4:F2} "
                    + "last={5:F2}/{6:F2} natural={7:F2} warning={8:F2}/{9:F2} "
                    + "refresh={10:F2}/{11:F2} settings={12:F2}/{13:F2}",
                    fontSize,
                    context.Window.ActualWidth,
                    statusArea.ActualWidth,
                    currentTime.ActualWidth,
                    currentTime.DesiredSize.Width,
                    lastUpdate.ActualWidth,
                    lastUpdate.DesiredSize.Width,
                    lastUpdateNaturalWidth,
                    warning.ActualWidth,
                    warning.DesiredSize.Width,
                    refresh.ActualWidth,
                    refresh.DesiredSize.Width,
                    settings.ActualWidth,
                    settings.DesiredSize.Width));
                TestContext.Current.TestOutputHelper?.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "font={0} timeline={1:F2} time={2:F2}/{3:F2} title={4:F2}/{5:F2} "
                    + "attention={6:F2}/{7:F2} meeting={8:F2}/{9:F2} provider={10:F2}/{11:F2}",
                    fontSize,
                    list.ActualWidth,
                    time.ActualWidth,
                    time.DesiredSize.Width,
                    title.ActualWidth,
                    title.DesiredSize.Width,
                    attention.ActualWidth,
                    attention.DesiredSize.Width,
                    meeting.ActualWidth,
                    meeting.DesiredSize.Width,
                    provider.ActualWidth,
                    provider.DesiredSize.Width));
            }
            finally
            {
                context.Window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
                context.Window.Close();
                context.ViewModel.Dispose();
            }
        });
    }

    private static WindowContext CreateWindow(int fontSize)
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var calendarEvent = Phase6Data.Event(
            "minimum-width",
            title: "最小幅で省略記号になることを確認するための非常に長い予定件名",
            meetingUri: new Uri("https://meeting.example.invalid/minimum-width"),
            responseStatus: AttendeeResponse.Tentative);
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
                    error: SyncErrorCategory.Network),
            ],
        };
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(display: new DisplayPreferences(fontSizeDip: fontSize)),
        };
        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now),
            settings: store,
            dispatcher: new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            settingsLauncher: new AvailableSettingsWindowLauncher());
        var window = new MainWindow(
            viewModel,
            new TimelineViewportCoordinator(),
            new TimeColumnWidthCalculator(new ResourceUiTextService()),
            new MainWindowPlacementService(store, new TestMonitorProvider()))
        {
            Width = LayoutMetrics.MinimumMainWidth,
            Height = LayoutMetrics.MinimumMainHeight,
            ShowInTaskbar = false,
        };
        return new WindowContext(window, viewModel);
    }

    private static double NaturalTextWidth(TextBlock textBlock)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(textBlock).PixelsPerDip;
        return new FormattedText(
            textBlock.Text,
            CultureInfo.GetCultureInfo("ja-JP"),
            FlowDirection.LeftToRight,
            new Typeface(
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch),
            textBlock.FontSize,
            textBlock.Foreground,
            pixelsPerDip).WidthIncludingTrailingWhitespace;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class AvailableSettingsWindowLauncher : ISettingsWindowLauncher
    {
        public bool IsAvailable => true;

        public void Show()
        {
        }
    }

    private sealed record WindowContext(MainWindow Window, MainWindowViewModel ViewModel);
}
