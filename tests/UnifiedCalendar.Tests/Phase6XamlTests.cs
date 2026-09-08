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
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase6XamlTests
{
    private readonly WpfApplicationFixture _fixture;

    public Phase6XamlTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void MainWindowLoadsOnStaWithRequiredVirtualizationAndSizeContract()
    {
        _fixture.Invoke(() =>
        {
                var time = new MutableTimeProvider(Phase6Data.Now);
                var resources = _fixture.Application.Resources;
                var nonThemeDictionary = new ResourceDictionary
                {
                    ["PreservedResource"] = "preserved",
                };
                resources.MergedDictionaries.Add(nonThemeDictionary);
                var themeService = new StartupThemeService(new WindowsThemePreferenceReader());
                themeService.Apply(resources);
                themeService.Apply(resources);
                Assert.Contains(nonThemeDictionary, resources.MergedDictionaries);
                Assert.Equal("preserved", resources["PreservedResource"]);
                Assert.Single(resources.MergedDictionaries, dictionary =>
                    dictionary.Source?.OriginalString.Contains(
                        "/Resources/Themes/",
                        StringComparison.OrdinalIgnoreCase) == true);
                Assert.DoesNotContain(
                    resources.Keys.Cast<object>(),
                    key => Equals(key, "HoverOverlayBrush"));
                Assert.NotNull(resources["WindowBackgroundBrush"]);
                var appliedHover = Assert.IsType<SolidColorBrush>(resources["HoverOverlayBrush"]);
                Assert.Equal(ColorMetrics.HoverOpacity, appliedHover.Opacity);
                foreach (var themeName in new[] { "Light", "Dark" })
                {
                    var theme = new ResourceDictionary
                    {
                        Source = new Uri(
                            $"/UnifiedCalendar.App;component/Resources/Themes/{themeName}.xaml",
                            UriKind.Relative),
                    };
                    var hover = Assert.IsType<SolidColorBrush>(theme["HoverOverlayBrush"]);
                    Assert.Equal(ColorMetrics.HoverOpacity, hover.Opacity);
                }

                var google = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
                var microsoft = Phase6Data.Account(Phase6Data.MicrosoftAccountId, ProviderKind.Microsoft);
                var interactiveEvent = Phase6Data.Event(
                    "interactive",
                    title: "Interactive event",
                    location: "Room https://location.example.invalid/item",
                    meetingUri: new Uri("https://meeting.example.invalid/room"),
                    description: "Details https://description.example.invalid/item",
                    sourceDetailUri: new Uri("https://source.example.invalid/item"));
                var attentionResponses = new Dictionary<string, AttendeeResponse>
                {
                    ["tentative"] = AttendeeResponse.Tentative,
                    ["not-responded"] = AttendeeResponse.NotResponded,
                    ["declined"] = AttendeeResponse.Declined,
                    ["unknown"] = AttendeeResponse.Unknown,
                };
                var attentionEvents = attentionResponses.Select(value => Phase6Data.Event(
                    value.Key,
                    responseStatus: value.Value));
                var events = new[] { interactiveEvent }
                    .Concat(attentionEvents)
                    .Concat(Enumerable.Range(1, 80).Select(index => Phase6Data.Event(
                        $"event-{index:D3}",
                        startUtc: Phase6Data.Now.AddMinutes(60 + (index * 30)),
                        endUtc: Phase6Data.Now.AddMinutes(85 + (index * 30)))))
                    .ToArray();
                var sync = new TestCalendarSyncService
                {
                    CurrentSnapshot = Phase6Data.Snapshot(
                        [google, microsoft],
                        [
                            new CalendarSelection(google.InternalAccountId, "calendar", true),
                            new CalendarSelection(microsoft.InternalAccountId, "calendar", true),
                        ],
                        events),
                    AccountStates =
                    [
                        Phase6Data.State(
                            google.InternalAccountId,
                            SyncStatus.Failed,
                            error: SyncErrorCategory.Network),
                        Phase6Data.State(
                            microsoft.InternalAccountId,
                            SyncStatus.AuthenticationRequired,
                            error: SyncErrorCategory.AuthenticationRequired),
                    ],
                };
                var interactions = new RecordingAccountInteractionService { IsAvailable = false };
                var launcher = new RecordingUriLauncher();
                var viewModel = Phase6Data.CreateViewModel(
                    sync,
                    time,
                    dispatcher: new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
                    interactions: interactions,
                    uriLauncher: launcher);
                var initialization = viewModel.InitializeAsync(CancellationToken.None);
                PumpDispatcherUntil(() => initialization.IsCompleted);
                initialization.GetAwaiter().GetResult();
                var viewport = new TimelineViewportCoordinator();
                var window = new MainWindow(
                    viewModel,
                    viewport,
                    new TimeColumnWidthCalculator(new ResourceUiTextService()),
                    new MainWindowPlacementService(new TestSettingsStore(), new TestMonitorProvider()));
                var list = Assert.IsType<ListBox>(window.FindName("TimelineList"));
                var popup = Assert.IsType<Popup>(window.FindName("DetailsPopup"));
                var warningPopup = Assert.IsType<Popup>(window.FindName("WarningPopup"));
                var warningButton = Assert.IsType<Button>(window.FindName("WarningButton"));
                var statusArea = Assert.IsType<Border>(window.FindName("StatusArea"));
                var stickyDateOverlay = Assert.IsType<Border>(window.FindName("StickyDateOverlay"));

                Assert.Equal(LayoutMetrics.MinimumMainWidth, window.MinWidth);
                Assert.Equal(LayoutMetrics.MinimumMainHeight, window.MinHeight);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
                Assert.True(ScrollViewer.GetCanContentScroll(list));
                Assert.False(popup.StaysOpen);
                Assert.False(warningPopup.StaysOpen);

                window.ShowInTaskbar = false;
                window.Show();
                PumpDispatcherUntil(() =>
                    viewModel.TimelineItems.OfType<EventRowViewModel>().Any());
                Assert.Equal(
                    events.Length,
                    viewModel.TimelineItems.OfType<EventRowViewModel>().Count());
                window.UpdateLayout();
                PumpDispatcherUntil(() => viewModel.StickyDateText.Length > 0);
                Assert.Equal(2, viewModel.AccountWarnings.Count);
                Assert.Equal(LayoutMetrics.StatusHeight, statusArea.ActualHeight);
                Assert.NotEqual(string.Empty, viewModel.StickyDateText);
                Assert.Equal(Visibility.Visible, stickyDateOverlay.Visibility);

                var firstHeader = viewModel.TimelineItems.OfType<DayHeaderItemViewModel>().First();
                var firstHeaderContainer = Assert.IsType<ListBoxItem>(
                    list.ItemContainerGenerator.ContainerFromItem(firstHeader));
                var firstRow = viewModel.TimelineItems.OfType<EventRowViewModel>().First();
                var partialHeaderRestored = false;
                EventHandler partialHeaderHandler = (_, _) => partialHeaderRestored = true;
                viewport.Restored += partialHeaderHandler;
                viewport.Restore(new TimelineViewportRestoration(
                    firstRow.Key,
                    firstHeaderContainer.ActualHeight / 2d,
                    null,
                    false,
                    false,
                    null));
                PumpDispatcherUntil(() => partialHeaderRestored);
                viewport.Restored -= partialHeaderHandler;
                Assert.True(firstHeaderContainer.TranslatePoint(new Point(), list).Y < 0d);
                Assert.True(firstHeaderContainer.TranslatePoint(new Point(), list).Y
                    + firstHeaderContainer.ActualHeight > 0d);
                Assert.NotEqual(string.Empty, viewModel.StickyDateText);
                Assert.Equal(Visibility.Visible, stickyDateOverlay.Visibility);

                var acceptedRow = viewModel.TimelineItems
                    .OfType<EventRowViewModel>()
                    .Single(value => value.Key.SourceEventId == "interactive");
                var acceptedIcon = FindAttentionIcon(list, acceptedRow);
                Assert.Null(acceptedRow.AttentionText);
                Assert.Equal(Visibility.Collapsed, acceptedIcon.Visibility);
                foreach (var response in attentionResponses)
                {
                    var attentionRow = viewModel.TimelineItems
                        .OfType<EventRowViewModel>()
                        .Single(value => value.Key.SourceEventId == response.Key);
                    var attentionIcon = FindAttentionIcon(list, attentionRow);
                    Assert.NotNull(attentionRow.AttentionText);
                    Assert.Equal(Visibility.Visible, attentionIcon.Visibility);
                    Assert.Equal("\uE946", attentionIcon.Text);
                    Assert.Equal(attentionRow.AttentionText, attentionIcon.ToolTip);
                    Assert.Equal(attentionRow.Foreground, attentionIcon.Foreground);
                    Assert.False(attentionIcon.Focusable);
                    Assert.False(KeyboardNavigation.GetIsTabStop(attentionIcon));
                }

                var row = viewModel.TimelineItems
                    .OfType<EventRowViewModel>()
                    .Single(value => value.Key.SourceEventId == "interactive");
                list.ScrollIntoView(row);
                list.UpdateLayout();
                var rowContainer = Assert.IsType<ListBoxItem>(
                    list.ItemContainerGenerator.ContainerFromItem(row));
                Assert.True(rowContainer.Focus());
                RaisePreviewKey(list, Key.Enter);
                PumpDispatcher();

                Assert.True(popup.IsOpen);
                var details = Assert.IsType<EventDetailsPopup>(popup.Child);
                var readOnlyTexts = FindDescendants<TextBox>(details).ToArray();
                Assert.Equal(5, readOnlyTexts.Length);
                Assert.All(readOnlyTexts, textBox =>
                {
                    Assert.True(textBox.IsReadOnly);
                    Assert.True(textBox.Focusable);
                    Assert.False(textBox.IsTabStop);
                });
                var selectableTexts = FindDescendants<SelectableLinkTextBox>(details).ToArray();
                Assert.Equal(3, selectableTexts.Length);
                Assert.All(selectableTexts, textBox =>
                {
                    Assert.True(textBox.IsReadOnly);
                    Assert.True(textBox.Focusable);
                    Assert.False(textBox.IsTabStop);
                });
                Assert.All(
                    selectableTexts.SelectMany(textBox => textBox.FocusableLinks),
                    hyperlink => Assert.True(KeyboardNavigation.GetIsTabStop(hyperlink)));

                var detailTargets = details.GetTabTargets();
                Assert.Collection(
                    detailTargets,
                    target => Assert.Equal(
                        new Uri("https://location.example.invalid/item"),
                        Assert.IsType<Hyperlink>(target).CommandParameter),
                    target => Assert.Equal(
                        new Uri("https://meeting.example.invalid/room"),
                        Assert.IsType<Hyperlink>(target).CommandParameter),
                    target => Assert.Equal(
                        new Uri("https://description.example.invalid/item"),
                        Assert.IsType<Hyperlink>(target).CommandParameter),
                    target => Assert.IsType<Button>(target));
                Assert.Same(detailTargets[0], Keyboard.FocusedElement);
                RaisePreviewKey(details, Key.Tab);
                Assert.Same(detailTargets[1], Keyboard.FocusedElement);
                Focus(detailTargets[^1]);
                RaisePreviewKey(details, Key.Tab);
                Assert.Same(detailTargets[0], Keyboard.FocusedElement);
                var focusedLink = Assert.IsType<Hyperlink>(detailTargets[0]);
                Assert.Equal(resources["FocusBrush"], focusedLink.Background);

                RaisePreviewKey(details, Key.Escape);
                PumpDispatcher();
                Assert.False(popup.IsOpen);
                Assert.Same(rowContainer, Keyboard.FocusedElement);

                Assert.True(viewModel.HasAccountWarnings);
                Assert.Equal(2, viewModel.AccountWarningsText.Split(Environment.NewLine).Length);
                Assert.Equal(LayoutMetrics.StatusHeight, statusArea.ActualHeight);

                warningButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpDispatcher();
                Assert.True(warningPopup.IsOpen);
                Assert.Empty(interactions.ReauthenticatedAccounts);
                Assert.Empty(launcher.OpenedUris);
                var warningContent = Assert.IsType<AccountWarningsPopup>(warningPopup.Child);
                var reauthenticationButtons = FindDescendants<Button>(warningContent).ToArray();
                Assert.Equal(2, reauthenticationButtons.Length);
                Assert.All(reauthenticationButtons, button => Assert.False(button.IsEnabled));
                Assert.Same(warningContent, Keyboard.FocusedElement);
                RaisePreviewKey(warningContent, Key.Tab);
                Assert.Same(warningContent, Keyboard.FocusedElement);
                RaisePreviewKey(warningContent, Key.Escape);
                PumpDispatcher();
                Assert.False(warningPopup.IsOpen);
                Assert.Same(warningButton, Keyboard.FocusedElement);
                Assert.Empty(interactions.ReauthenticatedAccounts);
                Assert.Empty(launcher.OpenedUris);

                Assert.True(rowContainer.Focus());
                RaisePreviewKey(list, Key.Enter);
                PumpDispatcher();
                Assert.True(viewModel.IsDetailsOpen);
                var farRow = viewModel.TimelineItems.OfType<EventRowViewModel>().Last();
                var restored = false;
                viewport.Restored += (_, _) => restored = true;
                viewport.Restore(new TimelineViewportRestoration(
                    farRow.Key,
                    -4d,
                    null,
                    false,
                    false,
                    null));
                PumpDispatcherUntil(() => restored);
                Assert.True(viewModel.IsDetailsOpen);
                Assert.NotEqual(string.Empty, viewModel.StickyDateText);
                Assert.Equal(Visibility.Visible, stickyDateOverlay.Visibility);
                Assert.False(viewport.Capture(
                    viewModel.TimelineItems.OfType<EventRowViewModel>().Select(value => value.Key).ToArray(),
                    row.Key).UserScrolledBeforeInitialSync);
                popup.IsOpen = false;
                PumpDispatcher();
                var restoredRow = Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);
                Assert.Equal(row.Key, Assert.IsType<EventRowViewModel>(restoredRow.DataContext).Key);

                viewModel.Dispose();
                window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
                window.Close();
        });
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

    private static bool Focus(IInputElement target) => target switch
    {
        UIElement element => element.Focus(),
        ContentElement element => element.Focus(),
        _ => false,
    };

    private static TextBlock FindAttentionIcon(ListBox list, EventRowViewModel row)
    {
        var container = Assert.IsType<ListBoxItem>(
            list.ItemContainerGenerator.ContainerFromItem(row));
        return Assert.Single(
            FindDescendants<TextBlock>(container),
            textBlock => textBlock.Name == "AttentionIcon");
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
}
