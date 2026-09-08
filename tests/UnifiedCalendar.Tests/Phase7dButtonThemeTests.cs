using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
using UnifiedCalendar.Core.Time;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase7dButtonThemeTests
{
    private static readonly DependencyPropertyKey IsMouseOverPropertyKey =
        Assert.IsType<DependencyPropertyKey>(typeof(UIElement)
            .GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null));

    private static readonly MethodInfo SetReadOnlyBooleanValue =
        Assert.IsAssignableFrom<MethodInfo>(typeof(DependencyObject).GetMethod(
            "SetValue",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(DependencyPropertyKey), typeof(bool)],
            modifiers: null));

    private static readonly PropertyInfo IsPressedProperty =
        Assert.IsAssignableFrom<PropertyInfo>(typeof(ButtonBase).GetProperty(
            nameof(ButtonBase.IsPressed)));

    private readonly WpfApplicationFixture _fixture;

    public Phase7dButtonThemeTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ButtonFamilyRenderedStatesUseThemeBrushesAndMeetContrastAcrossRuntimeSwitch()
    {
        _fixture.Invoke(() =>
        {
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            var (owner, window, _) = CreateSettingsWindow();
            var probes = AddButtonFamilyProbes(window);
            window.Show();
            PumpDispatcher();
            window.UpdateLayout();

            VerifyButtonFamilyTheme(window, probes);
            VerifySelectionState(window, probes.CheckBox, probes.RadioButton);

            themeService.Apply(_fixture.Application.Resources, useDarkTheme: true);
            PumpDispatcher();
            window.UpdateLayout();
            VerifyButtonFamilyTheme(window, probes);
            VerifySelectionState(window, probes.CheckBox, probes.RadioButton);

            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void TextEntryRenderedStatesUseThemeBrushesAcrossRuntimeSwitch()
    {
        _fixture.Invoke(() =>
        {
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            var (owner, window, _) = CreateSettingsWindow();
            var probes = AddTextEntryProbes(window);
            window.Show();
            PumpDispatcher();
            window.UpdateLayout();

            VerifyTextEntryTheme(window, probes.TextBox, probes.PasswordBox);

            themeService.Apply(_fixture.Application.Resources, useDarkTheme: true);
            PumpDispatcher();
            window.UpdateLayout();
            VerifyTextEntryTheme(window, probes.TextBox, probes.PasswordBox);

            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void EveryRenderedSettingsControlTypeHasAnExplicitLocalStyleGuard()
    {
        _fixture.Invoke(() =>
        {
            var (owner, window, viewModel) = CreateSettingsWindow();
            window.Show();
            var renderedTypes = CollectRenderedControlTypes(window, viewModel);

            AssertExplicitStyles(window, renderedTypes);

            var buttonStyleResources = FindResourceDictionary(
                _fixture.Application.Resources,
                typeof(Button));
            var buttonStyle = Assert.IsType<Style>(buttonStyleResources[typeof(Button)]);
            buttonStyleResources.Remove(typeof(Button));
            var guardFailure = Record.Exception(() => AssertExplicitStyles(window, renderedTypes));
            Assert.NotNull(guardFailure);
            Assert.Contains(nameof(Button), guardFailure.Message, StringComparison.Ordinal);
            buttonStyleResources.Add(typeof(Button), buttonStyle);
            AssertExplicitStyles(window, renderedTypes);

            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void AccountCategoryTextPaletteMeetsContrastAcrossRuntimeSwitch()
    {
        _fixture.Invoke(() =>
        {
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            var (owner, window, viewModel) = CreateSettingsWindow();
            window.Show();
            viewModel.SelectedCategory = viewModel.Categories.Single(category =>
                category.Key == "account");

            foreach (var useDarkTheme in new[] { false, true })
            {
                themeService.Apply(_fixture.Application.Resources, useDarkTheme);
                PumpDispatcher();
                window.UpdateLayout();
                Assert.Contains(FindDescendants<TextBlock>(window), value => value.IsVisible);
                foreach (var foregroundKey in new[]
                {
                    "PrimaryTextBrush",
                    "SecondaryTextBrush",
                    "ErrorBrush",
                })
                {
                    AssertContrastAtLeast(
                        ThemeColor(window, foregroundKey),
                        ThemeColor(window, "PanelBackgroundBrush"));
                    AssertContrastAtLeast(
                        ThemeColor(window, foregroundKey),
                        ThemeColor(window, "ButtonBackgroundBrush"));
                }
            }

            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void AccountRowsAreNotSelectableAndTheirEnabledButtonsRemainKeyboardReachable()
    {
        _fixture.Invoke(() =>
        {
            var (owner, window, viewModel) = CreateSettingsWindow();
            window.Show();
            viewModel.SelectedCategory = viewModel.Categories.Single(category =>
                category.Key == "account");
            PumpDispatcher();
            window.UpdateLayout();

            var accountItems = Assert.Single(
                FindDescendants<ItemsControl>(window),
                control => control.DataContext is AccountSettingsViewModel && control is not ListBox);
            Assert.Empty(FindDescendants<ListBoxItem>(accountItems));
            var rowButtons = FindDescendants<Button>(accountItems)
                .Where(button => button.DataContext is AccountListItemViewModel && button.IsEnabled)
                .ToArray();
            Assert.True(rowButtons.Length > 0);
            Assert.All(rowButtons, button =>
            {
                Assert.True(button.Focusable);
                Assert.True(button.IsTabStop);
                Assert.Null(FindAncestor<ListBoxItem>(button));
            });
            Assert.True(rowButtons[0].Focus());

            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void ColorRuleEditorPaletteSelectionRemainsFunctional()
    {
        _fixture.Invoke(() =>
        {
            var (owner, window, viewModel) = CreateSettingsWindow();
            window.Show();
            viewModel.SelectedCategory = viewModel.Categories.Single(category =>
                category.Key == "color-rules");
            viewModel.ColorRules.AddRuleCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();

            var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));
            var palette = Assert.Single(FindDescendants<ListBox>(content));
            Assert.True(palette.Items.Count > 1);

            palette.SelectedIndex = 1;
            PumpDispatcher();
            window.UpdateLayout();

            Assert.Same(palette.SelectedItem, viewModel.ColorRules.SelectedColorOption);
            var selectedItem = Assert.IsType<ListBoxItem>(
                palette.ItemContainerGenerator.ContainerFromItem(palette.SelectedItem));
            Assert.True(selectedItem.IsSelected);

            window.Close();
            owner.Close();
        });
    }

    private static (Window Owner, SettingsWindow Window, SettingsWindowViewModel ViewModel)
        CreateSettingsWindow()
    {
        var owner = new Window { ShowInTaskbar = false };
        owner.Show();
        owner.Hide();
        var accountId = Guid.NewGuid();
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts:
            [
                new AccountSettings(
                    accountId,
                    ProviderKind.Google,
                    "style-guard-subject",
                    "Account style guard",
                    "style-guard@example.invalid",
                    true,
                    $"google/{accountId:D}",
                    [new CalendarSetting("primary", true)]),
            ]),
        };
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var text = new ResourceUiTextService();
        var interactions = new UnavailableAccountInteractionService();
        var accounts = new AccountSettingsViewModel(
            settings,
            sync,
            interactions,
            interactions,
            new MemoryTokenStore(),
            new EmptyCacheStore(),
            text,
            new ImmediateUiDispatcher(),
            new MutableTimeProvider(Phase6Data.Now),
            new FixedLocalTimeZoneProvider());
        var calendarSelection = new CalendarSelectionSettingsViewModel(
            settings,
            sync,
            new StaticCalendarCatalogService(),
            text,
            new ImmediateUiDispatcher(),
            new BrushCache());
        calendarSelection.ShowRegisteredAccount(new AccountRegistrationResult(
            store.Settings.Accounts[0],
            [new CalendarDescriptor(
                "primary",
                "primary",
                "Style guard calendar",
                true,
                true,
                new RgbColor(66, 133, 244))]));
        var viewModel = new SettingsWindowViewModel(
            new NoPendingSettingsChanges(),
            text,
            settings,
            sync,
            new NoOpColorPickerService(),
            new BrushCache(),
            accounts: accounts,
            calendarSelection: calendarSelection);
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

    private static ButtonFamilyProbes AddButtonFamilyProbes(SettingsWindow window)
    {
        var root = Assert.IsType<Grid>(window.Content);
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(panel, 0);
        Panel.SetZIndex(panel, int.MaxValue);
        var button = new Button { Content = "Button probe" };
        var repeatButton = new RepeatButton { Content = "Repeat probe" };
        var checkBox = new CheckBox { Content = "Check probe" };
        var radioButton = new RadioButton { Content = "Radio probe" };
        panel.Children.Add(button);
        panel.Children.Add(repeatButton);
        panel.Children.Add(checkBox);
        panel.Children.Add(radioButton);
        root.Children.Add(panel);
        return new ButtonFamilyProbes(button, repeatButton, checkBox, radioButton);
    }

    private static TextEntryProbes AddTextEntryProbes(SettingsWindow window)
    {
        var root = Assert.IsType<Grid>(window.Content);
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var textBox = new TextBox { Text = "text probe" };
        var passwordBox = new PasswordBox
        {
            Password = "password probe",
        };
        panel.Children.Add(textBox);
        panel.Children.Add(passwordBox);
        Grid.SetRow(panel, 0);
        Panel.SetZIndex(panel, int.MaxValue);
        root.Children.Add(panel);
        return new TextEntryProbes(textBox, passwordBox);
    }

    private static void VerifyButtonFamilyTheme(
        SettingsWindow window,
        ButtonFamilyProbes probes)
    {
        var normalBackground = ThemeColor(window, "ButtonBackgroundBrush");
        var normalForeground = ThemeColor(window, "PrimaryTextBrush");
        var focusBackground = ThemeColor(window, "FocusBrush");
        var focusForeground = ThemeColor(window, "FocusForegroundBrush");
        var disabledForeground = ThemeColor(window, "SecondaryTextBrush");
        foreach (var control in probes.Controls)
        {
            var isPlainButton = control is Button or RepeatButton;
            var expectedNormalBackground = isPlainButton ? normalBackground : Colors.Transparent;
            control.IsEnabled = true;
            SetMouseOver(control, false);
            SetPressed(control, false);
            Keyboard.ClearFocus();
            PumpDispatcher();
            window.UpdateLayout();
            AssertRenderedState(control, expectedNormalBackground, normalForeground, requireContrast: false);

            SetMouseOver(control, true);
            PumpDispatcher();
            window.UpdateLayout();
            AssertRenderedState(control, focusBackground, focusForeground, requireContrast: true);

            SetMouseOver(control, false);
            SetPressed(control, true);
            PumpDispatcher();
            window.UpdateLayout();
            AssertRenderedState(control, focusBackground, focusForeground, requireContrast: true);

            SetPressed(control, false);
            SetMouseOver(control, false);
            Assert.True(Keyboard.Focus(control) is not null);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.True(control.IsKeyboardFocused);
            AssertRenderedState(control, focusBackground, focusForeground, requireContrast: true);

            Keyboard.ClearFocus();
            control.IsEnabled = false;
            PumpDispatcher();
            window.UpdateLayout();
            AssertRenderedState(control, expectedNormalBackground, disabledForeground, requireContrast: false);
            control.IsEnabled = true;
        }
    }

    private static void VerifyTextEntryTheme(
        SettingsWindow window,
        TextBox textBox,
        PasswordBox passwordBox)
    {
        var panel = ThemeColor(window, "PanelBackgroundBrush");
        var primary = ThemeColor(window, "PrimaryTextBrush");
        var secondary = ThemeColor(window, "SecondaryTextBrush");
        var focus = ThemeColor(window, "FocusBrush");
        var focusForeground = ThemeColor(window, "FocusForegroundBrush");
        foreach (var control in new Control[] { textBox, passwordBox })
        {
            var (selectionBrush, selectionTextBrush) = control switch
            {
                TextBox candidate => (candidate.SelectionBrush, candidate.SelectionTextBrush),
                PasswordBox candidate => (candidate.SelectionBrush, candidate.SelectionTextBrush),
                _ => throw new InvalidOperationException($"Unexpected text entry type: {control.GetType().FullName}"),
            };
            Assert.Equal(focus, Assert.IsType<SolidColorBrush>(selectionBrush).Color);
            Assert.Equal(focusForeground, Assert.IsType<SolidColorBrush>(selectionTextBrush).Color);
            control.IsEnabled = true;
            SetMouseOver(control, false);
            Keyboard.ClearFocus();
            PumpDispatcher();
            window.UpdateLayout();
            AssertRenderedState(control, panel, primary, requireContrast: true);

            SetMouseOver(control, true);
            PumpDispatcher();
            window.UpdateLayout();
            AssertRenderedState(control, panel, primary, requireContrast: true);
            Assert.Equal(focus, RenderedStateBorder(control).BorderBrushColor);

            SetMouseOver(control, false);
            Assert.True(Keyboard.Focus(control) is not null);
            PumpDispatcher();
            window.UpdateLayout();
            AssertRenderedState(control, panel, primary, requireContrast: true);
            Assert.Equal(focus, RenderedStateBorder(control).BorderBrushColor);

            Keyboard.ClearFocus();
            control.IsEnabled = false;
            PumpDispatcher();
            window.UpdateLayout();
            AssertRenderedState(
                control,
                normalBackground: ThemeColor(window, "ButtonBackgroundBrush"),
                expectedForeground: secondary,
                requireContrast: false);
            control.IsEnabled = true;
        }
    }

    private static void VerifySelectionState(
        SettingsWindow window,
        CheckBox checkBox,
        RadioButton radioButton)
    {
        var focus = ThemeColor(window, "FocusBrush");
        var focusForeground = ThemeColor(window, "FocusForegroundBrush");
        var secondary = ThemeColor(window, "SecondaryTextBrush");

        checkBox.IsChecked = true;
        PumpDispatcher();
        window.UpdateLayout();
        var checkIndicator = Assert.IsType<Border>(
            checkBox.Template.FindName("SelectionIndicator", checkBox));
        var checkMark = Assert.IsType<System.Windows.Shapes.Path>(
            checkBox.Template.FindName("CheckMark", checkBox));
        Assert.Equal(focus, Assert.IsType<SolidColorBrush>(checkIndicator.Background).Color);
        Assert.Equal(focusForeground, Assert.IsType<SolidColorBrush>(checkMark.Stroke).Color);
        AssertContrastAtLeast(focusForeground, focus);

        radioButton.IsChecked = true;
        PumpDispatcher();
        window.UpdateLayout();
        var radioIndicator = Assert.IsType<System.Windows.Shapes.Path>(
            radioButton.Template.FindName("SelectionIndicator", radioButton));
        var radioMark = Assert.IsType<System.Windows.Shapes.Path>(
            radioButton.Template.FindName("SelectionMark", radioButton));
        Assert.Equal(focus, Assert.IsType<SolidColorBrush>(radioIndicator.Stroke).Color);
        Assert.Equal(focus, Assert.IsType<SolidColorBrush>(radioMark.Fill).Color);

        checkBox.IsEnabled = false;
        radioButton.IsEnabled = false;
        PumpDispatcher();
        window.UpdateLayout();
        Assert.Equal(secondary, Assert.IsType<SolidColorBrush>(checkMark.Stroke).Color);
        Assert.Equal(secondary, Assert.IsType<SolidColorBrush>(radioMark.Fill).Color);

        checkBox.IsChecked = false;
        radioButton.IsChecked = false;
        PumpDispatcher();
        window.UpdateLayout();
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(checkMark.Stroke).Color);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(radioMark.Fill).Color);
        checkBox.IsEnabled = true;
        radioButton.IsEnabled = true;
    }

    private static IReadOnlySet<Type> CollectRenderedControlTypes(
        SettingsWindow window,
        SettingsWindowViewModel viewModel)
    {
        var result = new HashSet<Type>(FindDescendants<Control>(window).Select(control => control.GetType()));
        foreach (var category in viewModel.Categories)
        {
            viewModel.SelectedCategory = category;
            PumpDispatcher();
            window.UpdateLayout();
            result.UnionWith(FindDescendants<Control>(window).Select(control => control.GetType()));
            foreach (var comboBox in FindDescendants<ComboBox>(window))
            {
                comboBox.IsDropDownOpen = true;
                PumpDispatcher();
                window.UpdateLayout();
                result.UnionWith(FindDescendants<Control>(window).Select(control => control.GetType()));
                if (comboBox.Template.FindName("PART_Popup", comboBox) is Popup { Child: { } popupChild })
                {
                    result.UnionWith(FindDescendants<Control>(popupChild).Select(control => control.GetType()));
                }

                comboBox.IsDropDownOpen = false;
            }
        }

        return result;
    }

    private static void AssertExplicitStyles(SettingsWindow window, IEnumerable<Type> renderedTypes)
    {
        var requiredEvenWhenNotCurrentlyRendered = new[]
        {
            typeof(Button),
            typeof(RepeatButton),
            typeof(ToggleButton),
            typeof(CheckBox),
            typeof(RadioButton),
            typeof(TextBox),
            typeof(PasswordBox),
            typeof(ComboBox),
            typeof(ComboBoxItem),
            typeof(ListBox),
            typeof(ListBoxItem),
            typeof(GroupBox),
        };
        var exclusions = new Dictionary<Type, string>
        {
            // Passive content host. It has no state-dependent color of its own.
            [typeof(ContentControl)] = "passive data-template host",
            // Passive viewport. Its interactive ScrollBar descendants have a dedicated app template.
            [typeof(ScrollViewer)] = "passive viewport; ScrollBar is styled in Controls.xaml",
            // Composite host with no state drawing; its TextBox and button descendants are checked separately.
            [typeof(IntegerSpinner)] = "passive composite host; interactive descendants are locally styled",
            // Passive item host. Its interactive item containers are checked as their concrete control types.
            [typeof(ItemsControl)] = "passive item host; item containers are styled separately",
            // Decorative divider with no pointer, focus, selection, or enabled-state drawing.
            [typeof(Separator)] = "passive decorative divider with no interactive state",
        };
        var explicitApplicationStyles = new Dictionary<Type, Style>
        {
            // Controls.xaml supplies the implicit ScrollBar style resolved by SettingsWindow.
            [typeof(ScrollBar)] = Assert.IsType<Style>(window.FindResource(typeof(ScrollBar))),
            // Every Thumb in that template explicitly uses this keyed, theme-aware style.
            [typeof(Thumb)] = Assert.IsType<Style>(window.FindResource("ScrollBarThumbStyle")),
        };
        Assert.All(explicitApplicationStyles, pair => Assert.Equal(pair.Key, pair.Value.TargetType));
        var candidates = renderedTypes
            .Concat(requiredEvenWhenNotCurrentlyRendered)
            .Distinct()
            .Where(type => !exclusions.ContainsKey(type) && !explicitApplicationStyles.ContainsKey(type));
        var missing = candidates
            .Where(type => !HasExplicitStyleDefinition(window, type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            missing.Length == 0,
            $"SettingsWindow controls missing explicit styles: {string.Join(", ", missing.Select(type => type.FullName))}");
    }

    private static bool HasExplicitStyleDefinition(SettingsWindow window, Type type)
    {
        var resources = TryFindResourceDictionary(window.Resources, type)
            ?? TryFindResourceDictionary(Application.Current.Resources, type);
        return resources?[type] is Style { TargetType: not null } style
            && style.TargetType == type;
    }

    private static void AssertRenderedState(
        Control control,
        Color normalBackground,
        Color expectedForeground,
        bool requireContrast)
    {
        var rendered = RenderedStateBorder(control);
        Assert.Equal(normalBackground, rendered.BackgroundColor);
        Assert.Equal(expectedForeground, RenderedForeground(control));

        if (requireContrast)
        {
            AssertContrastAtLeast(expectedForeground, rendered.BackgroundColor);
        }
    }

    private static RenderedBorderColors RenderedStateBorder(Control control)
    {
        var border = Assert.IsType<Border>(control.Template.FindName("StateBorder", control));
        Assert.True(border.IsVisible);
        Assert.True(border.ActualWidth > 0d);
        Assert.True(border.ActualHeight > 0d);
        var background = Assert.IsType<SolidColorBrush>(border.Background).Color;
        var borderBrush = Assert.IsType<SolidColorBrush>(border.BorderBrush).Color;
        return new RenderedBorderColors(background, borderBrush);
    }

    private static Color RenderedForeground(Control control)
    {
        if (control.Template.FindName("StateContent", control) is DependencyObject stateContent)
        {
            return Assert.IsType<SolidColorBrush>(TextElement.GetForeground(stateContent)).Color;
        }

        var textView = FindDescendants<DependencyObject>(control)
            .FirstOrDefault(candidate => candidate.GetType().Name is "TextBoxView" or "PasswordBoxView");
        Assert.NotNull(textView);
        return Assert.IsType<SolidColorBrush>(TextElement.GetForeground(textView)).Color;
    }

    private static Color ThemeColor(SettingsWindow window, string key) =>
        Assert.IsType<SolidColorBrush>(window.FindResource(key)).Color;

    private static void AssertContrastAtLeast(Color foreground, Color background)
    {
        var foregroundColor = new RgbColor(foreground.R, foreground.G, foreground.B);
        var backgroundColor = new RgbColor(background.R, background.G, background.B);
        var ratio = foregroundColor.GetContrastRatio(backgroundColor);
        Assert.True(ratio >= 4.5d, $"Expected contrast >= 4.5:1, but was {ratio:F2}:1.");
    }

    private static void SetMouseOver(UIElement element, bool value) =>
        _ = SetReadOnlyBooleanValue.Invoke(element, [IsMouseOverPropertyKey, value]);

    private static void SetPressed(ButtonBase button, bool value) =>
        IsPressedProperty.SetValue(button, value);

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

    private static T? FindAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static ResourceDictionary FindResourceDictionary(
        ResourceDictionary resources,
        object key) => TryFindResourceDictionary(resources, key)
        ?? throw new InvalidOperationException($"A resource dictionary for {key} was not found.");

    private static ResourceDictionary? TryFindResourceDictionary(
        ResourceDictionary resources,
        object key)
    {
        if (resources.Keys.Cast<object>().Any(candidate => Equals(candidate, key)))
        {
            return resources;
        }

        for (var index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            var candidate = TryFindResourceDictionary(resources.MergedDictionaries[index], key);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record ButtonFamilyProbes(
        Button Button,
        RepeatButton RepeatButton,
        CheckBox CheckBox,
        RadioButton RadioButton)
    {
        public IReadOnlyList<ButtonBase> Controls => [Button, RepeatButton, CheckBox, RadioButton];
    }

    private sealed record RenderedBorderColors(Color BackgroundColor, Color BorderBrushColor);

    private sealed record TextEntryProbes(TextBox TextBox, PasswordBox PasswordBox);

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

    private sealed class StaticCalendarCatalogService : ICalendarCatalogService
    {
        public Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
            AccountSettings account,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarDescriptor>>([]);

        public Task<AccountCache?> LoadCacheAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AccountCache?>(null);
    }

    private sealed class FixedLocalTimeZoneProvider : ILocalTimeZoneProvider
    {
        public TimeZoneInfo GetCurrent() => TimeZoneInfo.Utc;
    }
}
