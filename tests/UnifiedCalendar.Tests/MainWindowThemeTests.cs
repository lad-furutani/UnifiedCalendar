using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class MainWindowThemeTests
{
    private readonly WpfApplicationFixture _fixture;

    public MainWindowThemeTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void MainPopupAndSpinnerControlsMeetContrastAcrossRuntimeThemeSwitch()
    {
        _fixture.Invoke(() =>
        {
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            var registered = CreateMainWindow(registered: true);
            var unregistered = CreateMainWindow(registered: false);
            var spinner = new IntegerSpinner();
            var spinnerHost = new Window
            {
                ShowInTaskbar = false,
                Content = spinner,
            };
            spinnerHost.SetResourceReference(
                Control.BackgroundProperty,
                "PanelBackgroundBrush");
            spinnerHost.Show();
            try
            {
                SetWarningFailure(registered);
                foreach (var useDarkTheme in new[] { false, true })
                {
                    themeService.Apply(_fixture.Application.Resources, useDarkTheme);
                    PumpDispatcher();
                    registered.Window.UpdateLayout();
                    unregistered.Window.UpdateLayout();
                    spinnerHost.UpdateLayout();

                    VerifyMainButtons(registered.Window);
                    VerifyMainButtons(unregistered.Window);
                    VerifyDetailsPopup(registered);
                    VerifyWarningPopup(registered);
                    VerifySpinner(spinner);
                }
            }
            finally
            {
                themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
                spinnerHost.Close();
                registered.Dispose();
                unregistered.Dispose();
            }
        });
    }

    [Fact]
    public void SharedStyleGuardCoversMainPopupsAndSpinnerAndSelfDetectsMissingButtonStyle()
    {
        _fixture.Invoke(() =>
        {
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            var context = CreateMainWindow(registered: true);
            var spinner = new IntegerSpinner();
            var spinnerHost = new Window
            {
                ShowInTaskbar = false,
                Content = spinner,
            };
            spinnerHost.Show();
            try
            {
                var details = OpenDetails(context);
                context.ViewModel.CloseDetails();
                PumpDispatcher();
                var warnings = OpenWarnings(context);
                var controls = CollectControls(context.Window)
                    .Concat(CollectControls(details, includeRoot: true))
                    .Concat(CollectControls(warnings, includeRoot: true))
                    .Concat(CollectControls(spinner, includeRoot: true))
                    .Distinct()
                    .ToArray();
                var resources = FindResourceDictionary(
                    _fixture.Application.Resources,
                    typeof(Button));
                var sharedControlTypes = resources.Keys
                    .OfType<Type>()
                    .ToHashSet();

                AssertSharedStyleGuard(resources, sharedControlTypes, controls);

                var buttonStyle = Assert.IsType<Style>(resources[typeof(Button)]);
                resources.Remove(typeof(Button));
                try
                {
                    var failure = Record.Exception(() =>
                        AssertSharedStyleGuard(resources, sharedControlTypes, controls));
                    Assert.NotNull(failure);
                    Assert.Contains(nameof(Button), failure.Message, StringComparison.Ordinal);
                }
                finally
                {
                    resources.Add(typeof(Button), buttonStyle);
                }

                AssertSharedStyleGuard(resources, sharedControlTypes, controls);
            }
            finally
            {
                spinnerHost.Close();
                context.Dispose();
            }
        });
    }

    private static MainWindowContext CreateMainWindow(bool registered)
    {
        var sync = new TestCalendarSyncService();
        var store = new TestSettingsStore();
        var interactions = new RecordingAccountInteractionService { IsAvailable = true };
        if (registered)
        {
            var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
            var accountSettings = new AccountSettings(
                account.InternalAccountId,
                account.Provider,
                account.ProviderSubjectId,
                account.DisplayName,
                account.Email,
                true,
                account.TokenRef,
                [new CalendarSetting("calendar", true)]);
            var calendarEvent = Phase6Data.Event(
                "theme-event",
                meetingUri: new Uri("https://meeting.example.invalid/room"),
                sourceDetailUri: new Uri("https://calendar.example.invalid/event"),
                description: "Details",
                location: "Room");
            store.Settings = new AppSettings(accounts: [accountSettings]);
            sync.CurrentSnapshot = Phase6Data.Snapshot(
                [account],
                [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                [calendarEvent]);
            sync.AccountStates =
            [
                Phase6Data.State(
                    account.InternalAccountId,
                    SyncStatus.AuthenticationRequired,
                    calendarId: "calendar",
                    error: SyncErrorCategory.AuthenticationRequired),
            ];
            interactions.ReauthenticateHandler = (_, _) =>
                Task.FromException(new InvalidOperationException("fixture failure"));
        }

        var viewModel = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now),
            settings: store,
            dispatcher: new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            interactions: interactions);
        var initialization = viewModel.InitializeAsync(CancellationToken.None);
        PumpDispatcherUntil(() => initialization.IsCompleted);
        initialization.GetAwaiter().GetResult();
        var window = new MainWindow(
            viewModel,
            new TimelineViewportCoordinator(),
            new TimeColumnWidthCalculator(new ResourceUiTextService()),
            new MainWindowPlacementService(store, new TestMonitorProvider()))
        {
            ShowInTaskbar = false,
        };
        window.Show();
        PumpDispatcher();
        window.UpdateLayout();
        return new MainWindowContext(window, viewModel);
    }

    private static void SetWarningFailure(MainWindowContext context)
    {
        var warning = Assert.Single(context.ViewModel.AccountWarnings);
        var task = warning.ReauthenticateCommand.ExecuteAsync(null);
        PumpDispatcherUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
        Assert.True(warning.HasOperationMessage);
    }

    private static void VerifyMainButtons(MainWindow window)
    {
        var buttons = FindDescendants<Button>(window)
            .Where(button => button.IsVisible && button.IsEnabled)
            .ToArray();
        Assert.NotEmpty(buttons);
        foreach (var button in buttons)
        {
            VerifyButtonContrast(
                window,
                button,
                button.Name == "WarningButton" ? "ErrorBrush" : "PrimaryTextBrush");
        }
    }

    private static void VerifyDetailsPopup(MainWindowContext context)
    {
        var details = OpenDetails(context);
        var sourceButton = Assert.Single(
            FindDescendants<Button>(details),
            button => button.IsVisible && button.IsEnabled);
        VerifyButtonContrast(context.Window, sourceButton, "PrimaryTextBrush");

        var primary = ThemeColor(context.Window, "PrimaryTextBrush");
        var secondary = ThemeColor(context.Window, "SecondaryTextBrush");
        var panel = ThemeColor(context.Window, "PanelBackgroundBrush");
        var textBoxes = FindDescendants<TextBox>(details).Where(value => value.IsVisible).ToArray();
        Assert.NotEmpty(textBoxes);
        Assert.All(textBoxes, textBox =>
        {
            var actual = Assert.IsType<SolidColorBrush>(textBox.Foreground).Color;
            Assert.Equal(primary, actual);
            AssertContrastAtLeast(actual, panel);
        });
        var labels = FindDescendants<TextBlock>(details)
            .Where(block => block.IsVisible
                && block.Foreground is SolidColorBrush brush
                && brush.Color == secondary)
            .ToArray();
        Assert.NotEmpty(labels);
        AssertContrastAtLeast(secondary, panel);

        context.ViewModel.CloseDetails();
        PumpDispatcher();
    }

    private static void VerifyWarningPopup(MainWindowContext context)
    {
        var warnings = OpenWarnings(context);
        var button = Assert.Single(
            FindDescendants<Button>(warnings),
            value => value.IsVisible && value.IsEnabled);
        VerifyButtonContrast(context.Window, button, "PrimaryTextBrush");

        var error = ThemeColor(context.Window, "ErrorBrush");
        var panel = ThemeColor(context.Window, "PanelBackgroundBrush");
        var errorMessages = FindDescendants<TextBlock>(warnings)
            .Where(block => block.IsVisible
                && block.Foreground is SolidColorBrush brush
                && brush.Color == error)
            .ToArray();
        Assert.True(errorMessages.Length >= 2);
        AssertContrastAtLeast(error, panel);
        Assert.Contains(errorMessages, block =>
            block.DataContext is AccountWarningViewModel warning
            && block.Text == warning.OperationMessage);

        Assert.IsType<Popup>(context.Window.FindName("WarningPopup")).IsOpen = false;
        PumpDispatcher();
    }

    private static void VerifySpinner(IntegerSpinner spinner)
    {
        var primary = ThemeColor(spinner, "PrimaryTextBrush");
        var panel = ThemeColor(spinner, "PanelBackgroundBrush");
        var textBox = Assert.Single(FindDescendants<TextBox>(spinner));
        var actual = Assert.IsType<SolidColorBrush>(textBox.Foreground).Color;
        Assert.Equal(primary, actual);
        AssertContrastAtLeast(actual, panel);
        var buttons = FindDescendants<Button>(spinner)
            .Where(button => button.IsEnabled)
            .ToArray();
        Assert.Equal(2, buttons.Length);
        Assert.All(buttons, button => VerifyButtonContrast(spinner, button, "PrimaryTextBrush"));
    }

    private static EventDetailsPopup OpenDetails(MainWindowContext context)
    {
        var eventRow = Assert.Single(context.ViewModel.TimelineItems.OfType<EventRowViewModel>());
        context.ViewModel.OpenDetails(eventRow.Key);
        PumpDispatcher();
        context.Window.UpdateLayout();
        var popup = Assert.IsType<Popup>(context.Window.FindName("DetailsPopup"));
        Assert.True(popup.IsOpen);
        return Assert.IsType<EventDetailsPopup>(popup.Child);
    }

    private static AccountWarningsPopup OpenWarnings(MainWindowContext context)
    {
        var button = Assert.IsType<Button>(context.Window.FindName("WarningButton"));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        PumpDispatcher();
        context.Window.UpdateLayout();
        var popup = Assert.IsType<Popup>(context.Window.FindName("WarningPopup"));
        Assert.True(popup.IsOpen);
        return Assert.IsType<AccountWarningsPopup>(popup.Child);
    }

    private static void VerifyButtonContrast(
        FrameworkElement resourceOwner,
        Button button,
        string expectedForegroundKey)
    {
        _ = button.ApplyTemplate();
        var border = Assert.IsType<Border>(button.Template.FindName("StateBorder", button));
        var content = Assert.IsAssignableFrom<DependencyObject>(
            button.Template.FindName("StateContent", button));
        var background = Assert.IsType<SolidColorBrush>(border.Background).Color;
        var foreground = Assert.IsType<SolidColorBrush>(
            TextElement.GetForeground(content)).Color;
        var focusBackground = ThemeColor(resourceOwner, "FocusBrush");
        if (background == focusBackground)
        {
            Assert.Equal(ThemeColor(resourceOwner, "FocusForegroundBrush"), foreground);
        }
        else
        {
            Assert.Equal(ThemeColor(resourceOwner, "ButtonBackgroundBrush"), background);
            Assert.Equal(ThemeColor(resourceOwner, expectedForegroundKey), foreground);
        }

        AssertContrastAtLeast(foreground, background);
    }

    private static void AssertSharedStyleGuard(
        ResourceDictionary sharedResources,
        IReadOnlySet<Type> sharedControlTypes,
        IEnumerable<Control> controls)
    {
        var passiveTypes = new HashSet<Type>
        {
            typeof(ContentControl),
            typeof(ItemsControl),
            typeof(ScrollViewer),
            typeof(Separator),
        };
        var missing = controls
            .Where(control => !passiveTypes.Contains(control.GetType()))
            .Where(control => !HasExplicitOrSharedStyle(
                sharedResources,
                sharedControlTypes,
                control))
            .Select(control => control.GetType())
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            missing.Length == 0,
            $"Main or popup controls missing explicit styles: {string.Join(", ", missing.Select(type => type.FullName))}");
    }

    private static bool HasExplicitOrSharedStyle(
        ResourceDictionary sharedResources,
        IReadOnlySet<Type> sharedControlTypes,
        Control control)
    {
        if (sharedControlTypes.Contains(control.GetType()))
        {
            return sharedResources[control.GetType()] is Style sharedStyle
                && sharedStyle.TargetType == control.GetType();
        }

        var valueSource = DependencyPropertyHelper.GetValueSource(
            control,
            FrameworkElement.StyleProperty);
        if (valueSource.BaseValueSource == BaseValueSource.Local
            && control.Style is Style explicitStyle)
        {
            return explicitStyle.TargetType.IsAssignableFrom(control.GetType());
        }

        return false;
    }

    private static IReadOnlyList<Control> CollectControls(
        DependencyObject root,
        bool includeRoot = false)
    {
        var controls = FindDescendants<Control>(root).ToList();
        if (includeRoot && root is Control control)
        {
            controls.Insert(0, control);
        }

        return controls;
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

    private static Color ThemeColor(FrameworkElement owner, string key) =>
        Assert.IsType<SolidColorBrush>(owner.FindResource(key)).Color;

    private static void AssertContrastAtLeast(Color foreground, Color background)
    {
        var foregroundColor = new RgbColor(foreground.R, foreground.G, foreground.B);
        var backgroundColor = new RgbColor(background.R, background.G, background.B);
        var ratio = foregroundColor.GetContrastRatio(backgroundColor);
        Assert.True(ratio >= 4.5d, $"Expected contrast >= 4.5:1, but was {ratio:F2}:1.");
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

    private sealed class MainWindowContext(
        MainWindow window,
        MainWindowViewModel viewModel) : IDisposable
    {
        public MainWindow Window { get; } = window;

        public MainWindowViewModel ViewModel { get; } = viewModel;

        public void Dispose()
        {
            Assert.IsType<Popup>(Window.FindName("DetailsPopup")).IsOpen = false;
            Assert.IsType<Popup>(Window.FindName("WarningPopup")).IsOpen = false;
            ViewModel.Dispose();
            Window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            Window.Close();
        }
    }
}
