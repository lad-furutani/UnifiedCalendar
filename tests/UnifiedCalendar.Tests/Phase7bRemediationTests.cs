using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase7bRemediationTests
{
    private static readonly DateTimeOffset BuildTimeUtc =
        new(2026, 9, 3, 1, 2, 3, TimeSpan.Zero);

    private readonly WpfApplicationFixture _fixture;

    public Phase7bRemediationTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void StatusHeightTracksFontSizeAndContainsButtonDesiredHeight()
    {
        _fixture.Invoke(() =>
        {
            var calculateStatusHeight = Assert.IsAssignableFrom<MethodInfo>(
                typeof(LayoutMetrics).GetMethod(
                    "CalculateStatusHeight",
                    BindingFlags.Public | BindingFlags.Static));
            var calculateWarningHeight = Assert.IsAssignableFrom<MethodInfo>(
                typeof(LayoutMetrics).GetMethod(
                    "CalculateWarningButtonHeight",
                    BindingFlags.Public | BindingFlags.Static));
            var heights = new[] { 10d, 14d, 24d }
                .Select(fontSize => InvokeMetric(calculateStatusHeight, fontSize))
                .ToArray();
            var warningHeights = new[] { 10d, 14d, 24d }
                .Select(fontSize => InvokeMetric(calculateWarningHeight, fontSize))
                .ToArray();

            Assert.True(heights[0] < heights[1]);
            Assert.True(heights[1] < heights[2]);
            Assert.Equal(48d, heights[1]);
            Assert.True(warningHeights[0] < warningHeights[1]);
            Assert.True(warningHeights[1] < warningHeights[2]);

            var store = new TestSettingsStore
            {
                Settings = new AppSettings(
                    display: new DisplayPreferences(fontSizeDip: 24)),
            };
            var settingsService = new ApplicationSettingsService(store);
            using var viewModel = Phase6Data.CreateViewModel(
                new TestCalendarSyncService(),
                new MutableTimeProvider(Phase6Data.Now),
                settings: store,
                dispatcher: new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
                settingsService: settingsService);
            var initialization = viewModel.InitializeAsync(CancellationToken.None);
            PumpDispatcherUntil(() => initialization.IsCompleted);
            initialization.GetAwaiter().GetResult();
            var window = CreateMainWindow(viewModel);
            window.Show();
            window.UpdateLayout();
            var statusArea = Assert.IsType<Border>(window.FindName("StatusArea"));
            var refreshButton = Assert.IsType<Button>(window.FindName("RefreshButton"));
            var warningButton = Assert.IsType<Button>(window.FindName("WarningButton"));
            var availableContentHeight = statusArea.ActualHeight
                - statusArea.Padding.Top
                - statusArea.Padding.Bottom
                - statusArea.BorderThickness.Top
                - statusArea.BorderThickness.Bottom;
            var refreshProbe = MeasureLike(refreshButton);
            var warningProbe = MeasureLike(warningButton);

            for (var index = 0; index < heights.Length; index++)
            {
                var fontSize = new[] { 10d, 14d, 24d }[index];
                var availableAtFontSize = heights[index]
                    - statusArea.Padding.Top
                    - statusArea.Padding.Bottom
                    - statusArea.BorderThickness.Top
                    - statusArea.BorderThickness.Bottom;
                Assert.True(MeasureLike(refreshButton, fontSize).DesiredSize.Height <= availableAtFontSize);
                Assert.True(MeasureLike(warningButton, fontSize).DesiredSize.Height <= availableAtFontSize);
            }

            Assert.Equal(heights[2], statusArea.ActualHeight, precision: 6);
            Assert.True(refreshProbe.DesiredSize.Height <= availableContentHeight);
            Assert.True(warningProbe.DesiredSize.Height <= availableContentHeight);
            Assert.Equal(warningHeights[2], warningButton.Height, precision: 6);

            var statusHeightBeforeDensityChange = statusArea.ActualHeight;
            var densityUpdate = settingsService.UpdateAsync(current => new AppSettings(
                new DisplayPreferences(
                    current.Display.Days,
                    current.Display.FontSizeDip,
                    DisplayDensity.Comfortable,
                    current.Display.DefaultEventColor),
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules));
            PumpDispatcherUntil(() => densityUpdate.IsCompleted);
            densityUpdate.GetAwaiter().GetResult();
            PumpDispatcherUntil(() => viewModel.EventRowMargin.Top == 8d);
            window.UpdateLayout();
            Assert.Equal(statusHeightBeforeDensityChange, statusArea.ActualHeight, precision: 6);

            window.Close();
        });
    }

    [Fact]
    public void CategoryShellProvidesSingleVisibleScrollPathAtMinimumSize()
    {
        _fixture.Invoke(() =>
        {
            var (owner, window, viewModel) = CreateSettingsWindow();
            viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "display");
            window.Show();
            window.Width = LayoutMetrics.MinimumSettingsWidth;
            window.Height = LayoutMetrics.MinimumSettingsHeight;
            PumpDispatcher();
            window.UpdateLayout();
            var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));
            var scrollViewer = Assert.IsType<ScrollViewer>(window.FindName("CategoryScrollViewer"));
            var customColorButton = FindDescendants<Button>(content)
                .Single(button => Equals(button.Content, viewModel.Display.CustomColorText));

            Assert.Equal(ScrollBarVisibility.Auto, scrollViewer.VerticalScrollBarVisibility);
            Assert.Equal(Visibility.Visible, scrollViewer.ComputedVerticalScrollBarVisibility);
            Assert.True(scrollViewer.ScrollableHeight > 0d);
            scrollViewer.ScrollToEnd();
            window.UpdateLayout();

            var buttonTop = customColorButton.TranslatePoint(new Point(), scrollViewer).Y;
            Assert.InRange(buttonTop, 0d, scrollViewer.ViewportHeight);
            Assert.True(buttonTop + customColorButton.ActualHeight <= scrollViewer.ViewportHeight + 1d);

            foreach (var category in viewModel.Categories)
            {
                viewModel.SelectedCategory = category;
                PumpDispatcher();
                window.UpdateLayout();
                var viewers = new[] { scrollViewer }
                    .Concat(FindDescendants<ScrollViewer>(content)
                        .Where(viewer => viewer.TemplatedParent is not TextBox))
                    .ToArray();
                Assert.Single(viewers);
                Assert.Same(scrollViewer, viewers[0]);
            }

            window.Height = 900d;
            viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "display");
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, scrollViewer.ComputedVerticalScrollBarVisibility);

            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void PaletteUsesEightHorizontalAccessibleSwatchesWithoutVisibleNames()
    {
        _fixture.Invoke(() =>
        {
            var (owner, window, viewModel) = CreateSettingsWindow();
            viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "display");
            window.Show();
            PumpDispatcher();
            window.UpdateLayout();
            var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));
            var palette = Assert.Single(FindDescendants<ListBox>(content));
            var categoryScrollViewer = Assert.IsType<ScrollViewer>(
                window.FindName("CategoryScrollViewer"));
            var expectedColors = SettingsColorPalette.Colors.ToArray();

            Assert.Equal(expectedColors, viewModel.Display.ColorOptions.Select(option => option.Color));
            Assert.Equal(8, palette.Items.Count);
            var swatchTops = new List<double>();
            for (var index = 0; index < palette.Items.Count; index++)
            {
                var option = viewModel.Display.ColorOptions[index];
                var container = Assert.IsType<ListBoxItem>(
                    palette.ItemContainerGenerator.ContainerFromIndex(index));
                Assert.DoesNotContain(
                    FindDescendants<TextBlock>(container),
                    text => Equals(text.Text, option.Name));
                var swatch = FindDescendants<Border>(container).Single(border =>
                    AutomationProperties.GetName(border) == option.Name);
                Assert.Equal(option.Name, swatch.ToolTip);
                swatchTops.Add(swatch.TranslatePoint(new Point(), palette).Y);
            }

            Assert.All(swatchTops, top => Assert.InRange(Math.Abs(top - swatchTops[0]), 0d, 0.5d));
            var lastContainer = Assert.IsType<ListBoxItem>(
                palette.ItemContainerGenerator.ContainerFromIndex(palette.Items.Count - 1));
            var lastRight = lastContainer.TranslatePoint(new Point(), categoryScrollViewer).X
                + lastContainer.ActualWidth;
            Assert.True(lastRight <= categoryScrollViewer.ViewportWidth + 1d);
            var selected = Assert.IsType<ListBoxItem>(
                palette.ItemContainerGenerator.ContainerFromItem(palette.SelectedItem));
            Assert.Equal(window.FindResource("FocusBrush"), selected.BorderBrush);

            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void SettingsControlForegroundsResolveAndTrackBothThemes()
    {
        _fixture.Invoke(() =>
        {
            var resources = _fixture.Application.Resources;
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            themeService.Apply(resources, useDarkTheme: false);
            var (owner, window, viewModel) = CreateSettingsWindow();
            window.Show();
            VerifySettingsControlStylesExist(window);
            VerifyEffectiveForegrounds(window, viewModel, resources["PrimaryTextBrush"]);

            viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "general");
            PumpDispatcher();
            window.UpdateLayout();
            var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));
            var checkBox = Assert.Single(FindDescendants<CheckBox>(content));
            var lightForeground = checkBox.Foreground;

            themeService.Apply(resources, useDarkTheme: true);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.NotEqual(lightForeground, checkBox.Foreground);
            Assert.Equal(resources["PrimaryTextBrush"], checkBox.Foreground);
            VerifyEffectiveForegrounds(window, viewModel, resources["PrimaryTextBrush"]);

            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void SelectionControlStateColorsRenderFromThemeAndTrackRuntimeSwitch()
    {
        _fixture.Invoke(() =>
        {
            var resources = _fixture.Application.Resources;
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            themeService.Apply(resources, useDarkTheme: false);
            var (owner, window, viewModel) = CreateSettingsWindow();
            window.Show();

            VerifyRenderedSelectionStates(window, viewModel, resources);

            themeService.Apply(resources, useDarkTheme: true);
            PumpDispatcher();
            window.UpdateLayout();
            VerifyRenderedSelectionStates(window, viewModel, resources);

            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void BuildTimeUsesProviderZoneAndUpdatesAfterTimeZoneRefresh()
    {
        var applicationInfo = new FixedApplicationInfoProvider();
        var timeZone = new MutableLocalTimeZoneProvider(TimeZoneInfo.Utc);
        var refreshSignal = new InternalRefreshSignal();
        var textService = new ResourceUiTextService();
        var viewModel = Assert.IsType<GeneralSettingsViewModel>(Activator.CreateInstance(
            typeof(GeneralSettingsViewModel),
            new TestApplicationSettingsService(),
            new NoOpStartupRegistrationService(),
            applicationInfo,
            new NoOpLocalPathLauncher(),
            textService,
            new Func<CancellationToken, Task>(_ => Task.CompletedTask),
            timeZone,
            refreshSignal));
        using var disposable = Assert.IsAssignableFrom<IDisposable>(viewModel);

        var embeddedBefore = applicationInfo.Get().BuildTimeUtc;
        Assert.Equal(
            textService.Get(UiResourceKeys.SettingsGeneralBuildTime, BuildTimeUtc),
            viewModel.BuildTimeText);
        Assert.DoesNotContain("UTC", viewModel.BuildTimeText, StringComparison.OrdinalIgnoreCase);

        timeZone.Current = TimeZoneInfo.CreateCustomTimeZone(
            "Phase7bRemediation-PlusNine",
            TimeSpan.FromHours(9),
            "Phase7bRemediation-PlusNine",
            "Phase7bRemediation-PlusNine");
        _ = refreshSignal.RequestRefreshAsync(
            InternalRefreshReason.TimeZoneChanged,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            textService.Get(
                UiResourceKeys.SettingsGeneralBuildTime,
                BuildTimeUtc.ToOffset(TimeSpan.FromHours(9))),
            viewModel.BuildTimeText);
        Assert.Equal(embeddedBefore, applicationInfo.Get().BuildTimeUtc);
    }

    private static double InvokeMetric(MethodInfo method, double fontSize) =>
        Assert.IsType<double>(method.Invoke(null, [fontSize]));

    private static Button MeasureLike(Button source, double? fontSize = null)
    {
        var probe = new Button
        {
            Content = source.Content,
            FontFamily = source.FontFamily,
            FontSize = fontSize ?? source.FontSize,
            FontWeight = source.FontWeight,
            Padding = source.Padding,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe;
    }

    private static MainWindow CreateMainWindow(MainWindowViewModel viewModel)
    {
        var viewport = new TimelineViewportCoordinator();
        return new MainWindow(
            viewModel,
            viewport,
            new TimeColumnWidthCalculator(new ResourceUiTextService()),
            new MainWindowPlacementService(new TestSettingsStore(), new TestMonitorProvider()))
        {
            ShowInTaskbar = false,
        };
    }

    private static (Window Owner, SettingsWindow Window, SettingsWindowViewModel ViewModel)
        CreateSettingsWindow()
    {
        var owner = new Window { ShowInTaskbar = false };
        owner.Show();
        owner.Hide();
        var settings = new ApplicationSettingsService(new TestSettingsStore());
        var viewModel = new SettingsWindowViewModel(
            new NoPendingSettingsChanges(),
            new ResourceUiTextService(),
            settings,
            new TestCalendarSyncService(),
            new NoOpColorPickerService(),
            new BrushCache());
        var window = new SettingsWindow(
            viewModel,
            new SettingsWindowPlacementService(settings, new TestMonitorProvider()),
            new NoOpSettingsConfirmationService())
        {
            Owner = owner,
            ShowInTaskbar = false,
        };
        var initialization = window.InitializeShellAsync(CancellationToken.None);
        PumpDispatcherUntil(() => initialization.IsCompleted);
        initialization.GetAwaiter().GetResult();
        return (owner, window, viewModel);
    }

    private static void VerifySettingsControlStylesExist(SettingsWindow window)
    {
        var expectedControlTypes = new[]
        {
            typeof(Button),
            typeof(RepeatButton),
            typeof(TextBox),
            typeof(ComboBox),
            typeof(ComboBoxItem),
            typeof(ListBox),
            typeof(ListBoxItem),
            typeof(RadioButton),
            typeof(CheckBox),
            typeof(GroupBox),
        };
        Assert.All(expectedControlTypes, type =>
            Assert.IsType<Style>(window.FindResource(type)));
    }

    private static void VerifyEffectiveForegrounds(
        SettingsWindow window,
        SettingsWindowViewModel viewModel,
        object expectedForeground)
    {
        var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));
        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "display");
        PumpDispatcher();
        window.UpdateLayout();
        Assert.All(FindDescendants<RadioButton>(content), control =>
            Assert.Equal(expectedForeground, control.Foreground));
        Assert.All(FindDescendants<ListBoxItem>(content), control =>
            Assert.Equal(
                control.IsSelected
                    ? window.FindResource("FocusForegroundBrush")
                    : expectedForeground,
                control.Foreground));
        Assert.All(FindDescendants<TextBox>(content), control =>
            Assert.Equal(expectedForeground, control.Foreground));
        Assert.All(FindDescendants<Button>(content), control =>
            Assert.Equal(expectedForeground, control.Foreground));

        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "update");
        PumpDispatcher();
        window.UpdateLayout();
        Assert.All(FindDescendants<ComboBox>(content), control =>
            Assert.Equal(expectedForeground, control.Foreground));

        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "general");
        PumpDispatcher();
        window.UpdateLayout();
        Assert.All(FindDescendants<CheckBox>(content), control =>
            Assert.Equal(expectedForeground, control.Foreground));
        Assert.All(FindDescendants<Button>(content), control =>
            Assert.Equal(expectedForeground, control.Foreground));

        var categoryList = Assert.IsType<ListBox>(window.FindName("CategoryList"));
        Assert.Equal(expectedForeground, categoryList.Foreground);
        Assert.All(FindDescendants<ListBoxItem>(categoryList), control =>
            Assert.Equal(
                control.IsSelected
                    ? window.FindResource("FocusForegroundBrush")
                    : expectedForeground,
                control.Foreground));
        Assert.Equal(expectedForeground, Assert.IsType<Button>(window.FindName("OkButton")).Foreground);
        Assert.Equal(
            window.FindResource("SecondaryTextBrush"),
            Assert.IsType<Button>(window.FindName("ApplyButton")).Foreground);
        Assert.Equal(expectedForeground, Assert.IsType<Button>(window.FindName("CancelButton")).Foreground);
    }

    private static void VerifyRenderedSelectionStates(
        SettingsWindow window,
        SettingsWindowViewModel viewModel,
        ResourceDictionary resources)
    {
        var primary = Assert.IsType<SolidColorBrush>(resources["PrimaryTextBrush"]);
        var secondary = Assert.IsType<SolidColorBrush>(resources["SecondaryTextBrush"]);
        var panel = Assert.IsType<SolidColorBrush>(resources["PanelBackgroundBrush"]);
        var focus = Assert.IsType<SolidColorBrush>(resources["FocusBrush"]);
        var focusForeground = Assert.IsType<SolidColorBrush>(resources["FocusForegroundBrush"]);
        var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));

        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "update");
        PumpDispatcher();
        window.UpdateLayout();
        var comboBox = Assert.Single(FindDescendants<ComboBox>(content));
        AssertRenderedPair(comboBox, panel.Color, primary.Color);

        comboBox.IsDropDownOpen = true;
        PumpDispatcherUntil(() => comboBox.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated);
        window.UpdateLayout();
        var selectedItem = Assert.IsType<ComboBoxItem>(
            comboBox.ItemContainerGenerator.ContainerFromItem(comboBox.SelectedItem));
        AssertRenderedPair(selectedItem, focus.Color, focusForeground.Color);

        var normalItem = Assert.IsType<ComboBoxItem>(
            comboBox.ItemContainerGenerator.ContainerFromIndex(0));
        if (ReferenceEquals(normalItem, selectedItem))
        {
            normalItem = Assert.IsType<ComboBoxItem>(
                comboBox.ItemContainerGenerator.ContainerFromIndex(1));
        }

        Assert.False(normalItem.IsSelected);
        Assert.False(normalItem.IsHighlighted);
        AssertRenderedPair(normalItem, panel.Color, primary.Color);

        var highlightedItem = Assert.IsType<ComboBoxItem>(
            comboBox.ItemContainerGenerator.ContainerFromIndex(comboBox.Items.Count - 1));
        SetComboBoxItemHighlighted(highlightedItem, isHighlighted: true);
        PumpDispatcher();
        window.UpdateLayout();
        Assert.True(highlightedItem.IsHighlighted);
        Assert.Contains(
            highlightedItem.Style.Triggers.OfType<Trigger>(),
            trigger => trigger.Property == UIElement.IsMouseOverProperty && Equals(trigger.Value, true));
        AssertRenderedPair(highlightedItem, focus.Color, focusForeground.Color);

        SetComboBoxItemHighlighted(highlightedItem, isHighlighted: false);
        highlightedItem.IsEnabled = false;
        PumpDispatcher();
        window.UpdateLayout();
        AssertRenderedPair(highlightedItem, panel.Color, secondary.Color);
        highlightedItem.IsEnabled = true;

        _ = Keyboard.Focus(normalItem);
        PumpDispatcher();
        window.UpdateLayout();
        Assert.True(normalItem.IsKeyboardFocusWithin);
        AssertRenderedPair(normalItem, focus.Color, focusForeground.Color);

        comboBox.IsDropDownOpen = false;
        comboBox.IsEnabled = false;
        PumpDispatcher();
        window.UpdateLayout();
        AssertRenderedPair(comboBox, panel.Color, secondary.Color);

        comboBox.IsEnabled = true;
        _ = Keyboard.Focus(comboBox);
        PumpDispatcher();
        window.UpdateLayout();
        AssertRenderedPair(comboBox, panel.Color, primary.Color);
        Assert.Equal(focus.Color, FindRenderedBorder(comboBox).BorderBrush is SolidColorBrush border
            ? border.Color
            : default);

        var categoryList = Assert.IsType<ListBox>(window.FindName("CategoryList"));
        var selectedCategory = Assert.IsType<ListBoxItem>(
            categoryList.ItemContainerGenerator.ContainerFromItem(categoryList.SelectedItem));
        AssertRenderedPair(selectedCategory, focus.Color, focusForeground.Color);

        viewModel.SelectedCategory = viewModel.Categories.Single(category => category.Key == "display");
        PumpDispatcher();
        window.UpdateLayout();
        var palette = Assert.Single(FindDescendants<ListBox>(content));
        var selectedSwatch = Assert.IsType<ListBoxItem>(
            palette.ItemContainerGenerator.ContainerFromItem(palette.SelectedItem));
        Assert.Equal(focus.Color, FindRenderedBorder(selectedSwatch).Background is SolidColorBrush swatchBackground
            ? swatchBackground.Color
            : default);
    }

    private static void AssertRenderedPair(
        Control control,
        Color expectedBackground,
        Color expectedForeground)
    {
        var border = FindRenderedBorder(control);
        Assert.Equal(
            expectedBackground,
            Assert.IsType<SolidColorBrush>(border.Background).Color);
        AssertRenderedTextForeground(control, expectedForeground);
        AssertContrastAtLeast(expectedForeground, expectedBackground);
    }

    private static void AssertRenderedTextForeground(DependencyObject control, Color expected)
    {
        var text = FindDescendants<TextBlock>(control)
            .FirstOrDefault(candidate => candidate.IsVisible && candidate.ActualWidth > 0d);
        Assert.NotNull(text);
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
    }

    private static Border FindRenderedBorder(DependencyObject control) =>
        Assert.Single(
            FindDescendants<Border>(control)
                .Where(border =>
                    border.IsVisible
                    && border.ActualWidth > 0d
                    && border.ActualHeight > 0d
                    && border.Background is SolidColorBrush { Color.A: > 0 })
                .OrderByDescending(border => border.ActualWidth * border.ActualHeight)
                .Take(1));

    private static void AssertContrastAtLeast(Color foreground, Color background)
    {
        var foregroundColor = new RgbColor(foreground.R, foreground.G, foreground.B);
        var backgroundColor = new RgbColor(background.R, background.G, background.B);
        Assert.True(
            foregroundColor.GetContrastRatio(backgroundColor) >= 4.5d,
            $"Expected contrast >= 4.5:1, but was {foregroundColor.GetContrastRatio(backgroundColor):F2}:1.");
    }

    private static void SetComboBoxItemHighlighted(
        ComboBoxItem item,
        bool isHighlighted)
    {
        var setter = Assert.IsAssignableFrom<MethodInfo>(
            typeof(ComboBoxItem)
                .GetProperty(nameof(ComboBoxItem.IsHighlighted))
                ?.GetSetMethod(nonPublic: true));
        _ = setter.Invoke(item, [isHighlighted]);
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

    private sealed class FixedThemePreferenceReader : IThemePreferenceReader
    {
        public bool IsDarkTheme() => false;
    }

    private sealed class NoOpColorPickerService : IColorPickerService
    {
        public RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor) => null;
    }

    private sealed class NoOpSettingsConfirmationService : ISettingsConfirmationService
    {
        public bool ConfirmDiscard() => true;

        public SettingsExitDecision ConfirmApplicationExit() => SettingsExitDecision.Discard;
    }

    private sealed class FixedApplicationInfoProvider : IApplicationInfoProvider
    {
        public ApplicationInfo Get() => new("UnifiedCalendar", "1.0.0", BuildTimeUtc);
    }

    private sealed class MutableLocalTimeZoneProvider(TimeZoneInfo current) : ILocalTimeZoneProvider
    {
        public TimeZoneInfo Current { get; set; } = current;

        public TimeZoneInfo GetCurrent() => Current;
    }

    private sealed class TestApplicationSettingsService : IApplicationSettingsService
    {
        public event EventHandler<ApplicationSettingsChangedEventArgs>? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppSettings.CreateDefault());

        public Task<AppSettings> UpdateAsync(
            Func<AppSettings, AppSettings> update,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(update(AppSettings.CreateDefault()));
    }

    private sealed class NoOpStartupRegistrationService : IStartupRegistrationService
    {
        public bool IsEnabled => false;

        public void SetEnabled(bool enabled)
        {
        }
    }

    private sealed class NoOpLocalPathLauncher : IAppLocalPathLauncher
    {
        public Task OpenAsync(
            AppLocalPathTarget target,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
