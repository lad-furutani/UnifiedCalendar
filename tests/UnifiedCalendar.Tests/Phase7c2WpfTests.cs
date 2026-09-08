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
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase7c2WpfTests
{
    private readonly WpfApplicationFixture _fixture;

    public Phase7c2WpfTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CalendarRowsAreNotSelectableAndTogglesRemainKeyboardReachable()
    {
        _fixture.Invoke(() =>
        {
            var context = CreateWindow();
            try
            {
                ShowCalendarCategory(context);
                var content = Assert.IsType<ContentControl>(
                    context.Window.FindName("CategoryContent"));
                var calendarItems = Assert.Single(
                    FindDescendants<ItemsControl>(content),
                    control => control.DataContext is CalendarSelectionSettingsViewModel);

                Assert.Empty(FindDescendants<ListBoxItem>(calendarItems));
                var toggles = FindDescendants<CheckBox>(calendarItems)
                    .Where(toggle => toggle.DataContext is CalendarListItemViewModel)
                    .ToArray();
                Assert.Equal(2, toggles.Length);
                Assert.All(toggles, toggle =>
                {
                    Assert.True(toggle.Focusable);
                    Assert.True(toggle.IsTabStop);
                    Assert.Null(FindAncestor<ListBoxItem>(toggle));
                });
                Assert.True(toggles[0].Focus());

                var readableRow = context.CalendarSelection.AccountGroups[0].Calendars[0];
                var name = Assert.Single(FindDescendants<TextBlock>(calendarItems), block =>
                    ReferenceEquals(block.DataContext, readableRow)
                    && block.Text == readableRow.Name);
                Assert.Equal(TextWrapping.NoWrap, name.TextWrapping);
                Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
                Assert.Equal(readableRow.Name, name.ToolTip);

                var categoryScroller = Assert.IsType<ScrollViewer>(
                    context.Window.FindName("CategoryScrollViewer"));
                Assert.Equal(ScrollBarVisibility.Disabled, categoryScroller.HorizontalScrollBarVisibility);
                Assert.Equal(0d, categoryScroller.ScrollableWidth);
            }
            finally
            {
                context.Window.Close();
                context.Owner.Close();
            }
        });
    }

    [Fact]
    public void CalendarCategoryTextPaletteMeetsContrastAcrossRuntimeSwitch()
    {
        _fixture.Invoke(() =>
        {
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            var context = CreateWindow();
            try
            {
                ShowCalendarCategory(context);
                foreach (var useDarkTheme in new[] { false, true })
                {
                    themeService.Apply(_fixture.Application.Resources, useDarkTheme);
                    PumpDispatcher();
                    context.Window.UpdateLayout();

                    var visibleText = FindDescendants<TextBlock>(context.Window)
                        .Where(block => block.IsVisible)
                        .ToArray();
                    Assert.Contains(visibleText, block =>
                        block.DataContext is CalendarListItemViewModel);
                    foreach (var foregroundKey in new[]
                    {
                        "PrimaryTextBrush",
                        "SecondaryTextBrush",
                        "ErrorBrush",
                    })
                    {
                        AssertContrastAtLeast(
                            ThemeColor(context.Window, foregroundKey),
                            ThemeColor(context.Window, "PanelBackgroundBrush"));
                        AssertContrastAtLeast(
                            ThemeColor(context.Window, foregroundKey),
                            ThemeColor(context.Window, "ButtonBackgroundBrush"));
                    }
                }
            }
            finally
            {
                themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
                context.Window.Close();
                context.Owner.Close();
            }
        });
    }

    [Fact]
    public void FailedVisibilitySaveRestoresCheckBoxToActualValue()
    {
        _fixture.Invoke(() =>
        {
            var context = CreateWindow(new IOException("fixture save failure"));
            try
            {
                ShowCalendarCategory(context);
                var row = context.CalendarSelection.AccountGroups[0].Calendars[0];
                var content = Assert.IsType<ContentControl>(
                    context.Window.FindName("CategoryContent"));
                var toggle = Assert.Single(
                    FindDescendants<CheckBox>(content),
                    value => ReferenceEquals(value.DataContext, row));
                Assert.True(row.IsVisible);
                Assert.True(toggle.IsChecked is true);

                toggle.SetCurrentValue(CheckBox.IsCheckedProperty, false);
                var update = row.ToggleVisibilityCommand.ExecuteAsync(null);
                PumpDispatcherUntil(() => update.IsCompleted);
                update.GetAwaiter().GetResult();
                PumpDispatcher();

                Assert.True(row.IsVisible);
                Assert.True(toggle.IsChecked is true);
                Assert.NotEmpty(context.CalendarSelection.OperationMessage);
            }
            finally
            {
                context.Window.Close();
                context.Owner.Close();
            }
        });
    }

    private static CalendarWindowContext CreateWindow(Exception? saveException = null)
    {
        var owner = new Window { ShowInTaskbar = false };
        owner.Show();
        owner.Hide();
        var accountId = Guid.NewGuid();
        var account = new AccountSettings(
            accountId,
            ProviderKind.Microsoft,
            "calendar-ui-subject",
            "Calendar UI account",
            "calendar-ui@example.invalid",
            true,
            $"microsoft/{accountId:D}",
            [
                new CalendarSetting("readable", true),
                new CalendarSetting("unreadable", true),
            ]);
        var store = new TestSettingsStore
        {
            Settings = new AppSettings(accounts: [account]),
            SaveException = saveException,
        };
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService
        {
            AccountStates =
            [
                Phase6Data.State(
                    accountId,
                    SyncStatus.Failed,
                    calendarId: "readable",
                    error: SyncErrorCategory.Network),
            ],
        };
        var text = new ResourceUiTextService();
        var catalog = new StaticCalendarCatalogService(
        [
            new CalendarDescriptor(
                "readable",
                "readable",
                "Very long readable calendar name used to verify single-line trimming",
                true,
                true,
                new RgbColor(66, 133, 244)),
            new CalendarDescriptor(
                "unreadable",
                "unreadable",
                "Unreadable calendar",
                false,
                false,
                null),
        ]);
        var calendarSelection = new CalendarSelectionSettingsViewModel(
            settings,
            sync,
            catalog,
            text,
            new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
            new BrushCache());
        var viewModel = new SettingsWindowViewModel(
            new NoPendingSettingsChanges(),
            text,
            settings,
            sync,
            new NoOpColorPickerService(),
            new BrushCache(),
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
        return new CalendarWindowContext(owner, window, viewModel, calendarSelection);
    }

    private static void ShowCalendarCategory(CalendarWindowContext context)
    {
        context.Window.Show();
        context.ViewModel.SelectedCategory = context.ViewModel.Categories.Single(category =>
            category.Key == "calendar-selection");
        PumpDispatcherUntil(() => context.CalendarSelection.AccountGroups.Count == 1);
        context.Window.UpdateLayout();
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

    private sealed record CalendarWindowContext(
        Window Owner,
        SettingsWindow Window,
        SettingsWindowViewModel ViewModel,
        CalendarSelectionSettingsViewModel CalendarSelection);

    private sealed class StaticCalendarCatalogService(
        IReadOnlyList<CalendarDescriptor> calendars) : ICalendarCatalogService
    {
        public Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
            AccountSettings account,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(calendars);
        }

        public Task<AccountCache?> LoadCacheAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<AccountCache?>(null);
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
}
