using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase7d2Tests
{
    private readonly WpfApplicationFixture _fixture;

    public Phase7d2Tests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void ConditionSummariesUseApprovedWordingAndOperatorJoiners()
    {
        var allRule = CreateRule(
            "All",
            SettingsColorPalette.Colors[0],
            conditions:
            [
                ColorRuleCondition.ForTitle(TextMatchKind.Contains, "定例"),
                ColorRuleCondition.ForTitle(TextMatchKind.Exact, "全社会議"),
                ColorRuleCondition.ForCalendarName(TextMatchKind.Contains, "業務"),
                ColorRuleCondition.ForCalendarName(TextMatchKind.Exact, "個人"),
                ColorRuleCondition.ForProvider(ProviderKind.Microsoft),
            ]);
        var anyRule = CreateRule(
            "Any",
            SettingsColorPalette.Colors[1],
            ColorRuleOperator.Any,
            [
                ColorRuleCondition.ForTitle(TextMatchKind.Contains, "計画"),
                ColorRuleCondition.ForProvider(ProviderKind.Google),
            ]);
        var subject = CreateSubject(new AppSettings(colorRules: [allRule, anyRule]));

        Assert.Equal(
            "件名に「定例」を含む AND 件名が「全社会議」と一致 AND "
            + "カレンダー名に「業務」を含む AND カレンダー名が「個人」と一致 AND 取得元=Microsoft",
            subject.ViewModel.Rules[0].ConditionSummary);
        Assert.Equal(
            "件名に「計画」を含む OR 取得元=Google",
            subject.ViewModel.Rules[1].ConditionSummary);
    }

    [Fact]
    public async Task ReorderCommandsHandleBoundariesArbitraryMovesAndOneSavePerOperation()
    {
        var account = CreateAccountSettings();
        var windows = new WindowPreferences(
            new WindowPlacement(10, 20, 800, 600, "DISPLAY1", 96, 96),
            new WindowPlacement(30, 40, 760, 560, "DISPLAY1", 96, 96));
        var subject = CreateSubject(new AppSettings(
            windows: windows,
            accounts: [account],
            colorRules:
            [
                CreateRule("A", SettingsColorPalette.Colors[0]),
                CreateRule("B", SettingsColorPalette.Colors[1]),
                CreateRule("C", SettingsColorPalette.Colors[2]),
            ]));
        var notifications = new List<ApplicationSettingsChangedEventArgs>();
        subject.SettingsService.SettingsChanged += (_, eventArgs) => notifications.Add(eventArgs);

        Assert.False(subject.ViewModel.MoveRuleUpCommand.CanExecute(subject.ViewModel.Rules[0]));
        Assert.True(subject.ViewModel.MoveRuleDownCommand.CanExecute(subject.ViewModel.Rules[0]));
        Assert.True(subject.ViewModel.MoveRuleUpCommand.CanExecute(subject.ViewModel.Rules[^1]));
        Assert.False(subject.ViewModel.MoveRuleDownCommand.CanExecute(subject.ViewModel.Rules[^1]));

        await subject.ViewModel.MoveRuleDownCommand.ExecuteAsync(subject.ViewModel.Rules[0]);
        Assert.Equal(["B", "A", "C"], RuleNames(subject));
        Assert.Equal(1, subject.Store.SaveCount);
        Assert.Equal(1, subject.SettingsService.UpdateCount);
        AssertColorRulesOnly(Assert.Single(notifications));
        Assert.False(subject.ViewModel.IsDirty);

        await subject.ViewModel.MoveRuleUpCommand.ExecuteAsync(subject.ViewModel.Rules[1]);
        Assert.Equal(["A", "B", "C"], RuleNames(subject));
        Assert.Equal(2, subject.Store.SaveCount);
        Assert.Equal(2, subject.SettingsService.UpdateCount);
        Assert.Equal(2, notifications.Count);
        AssertColorRulesOnly(notifications[1]);

        var arbitraryMove = new ColorRuleMoveRequest(0, 2);
        Assert.True(subject.ViewModel.MoveRuleCommand.CanExecute(arbitraryMove));
        await subject.ViewModel.MoveRuleCommand.ExecuteAsync(arbitraryMove);
        Assert.Equal(["B", "C", "A"], RuleNames(subject));
        Assert.Equal(3, subject.Store.SaveCount);
        Assert.Equal(3, subject.SettingsService.UpdateCount);
        Assert.Equal(3, notifications.Count);
        AssertColorRulesOnly(notifications[2]);
        Assert.Same(windows, subject.Store.Settings.Windows);
        Assert.Same(account, Assert.Single(subject.Store.Settings.Accounts));
        Assert.False(subject.ViewModel.IsDirty);
    }

    [Fact]
    public async Task ReorderingChangesWhichMatchingRuleWinsInExistingPipeline()
    {
        var firstColor = SettingsColorPalette.Colors[4];
        var secondColor = SettingsColorPalette.Colors[6];
        var calendarEvent = CreateEvent("meeting planning");
        var subject = CreateSubject(new AppSettings(colorRules:
        [
            CreateRule("First", firstColor),
            CreateRule("Second", secondColor),
        ]));

        Assert.Equal(firstColor, ResolveColor(subject.Store.Settings.ColorRules, calendarEvent));

        await subject.ViewModel.MoveRuleCommand.ExecuteAsync(new ColorRuleMoveRequest(0, 1));

        Assert.Equal(["Second", "First"], RuleNames(subject));
        Assert.Equal(secondColor, ResolveColor(subject.Store.Settings.ColorRules, calendarEvent));
    }

    [Fact]
    public async Task TogglePreservesPositionAndDisabledRuleDoesNotApply()
    {
        var ruleColor = SettingsColorPalette.Colors[5];
        var calendarEvent = CreateEvent("meeting planning");
        var subject = CreateSubject(new AppSettings(colorRules:
        [
            CreateRule("A", SettingsColorPalette.Colors[0]),
            CreateRule("B", ruleColor),
            CreateRule("C", SettingsColorPalette.Colors[2]),
        ]));
        var notifications = new List<ApplicationSettingsChangedEventArgs>();
        subject.SettingsService.SettingsChanged += (_, eventArgs) => notifications.Add(eventArgs);

        await subject.ViewModel.ToggleRuleCommand.ExecuteAsync(subject.ViewModel.Rules[1]);

        Assert.Equal(["A", "B", "C"], RuleNames(subject));
        Assert.False(subject.ViewModel.Rules[1].IsEnabled);
        Assert.Equal(subject.ViewModel.EnableText, subject.ViewModel.Rules[1].ToggleEnabledText);
        Assert.NotEqual(ruleColor, ResolveColor([subject.Store.Settings.ColorRules[1]], calendarEvent));
        Assert.Equal(1, subject.Store.SaveCount);
        Assert.Equal(1, subject.SettingsService.UpdateCount);
        AssertColorRulesOnly(Assert.Single(notifications));
        Assert.False(subject.ViewModel.IsDirty);

        await subject.ViewModel.ToggleRuleCommand.ExecuteAsync(subject.ViewModel.Rules[1]);

        Assert.Equal(["A", "B", "C"], RuleNames(subject));
        Assert.True(subject.ViewModel.Rules[1].IsEnabled);
        Assert.Equal(subject.ViewModel.DisableText, subject.ViewModel.Rules[1].ToggleEnabledText);
        Assert.Equal(ruleColor, ResolveColor([subject.Store.Settings.ColorRules[1]], calendarEvent));
        Assert.Equal(2, subject.Store.SaveCount);
        Assert.Equal(2, subject.SettingsService.UpdateCount);
        AssertColorRulesOnly(notifications[1]);
        Assert.False(subject.ViewModel.IsDirty);
    }

    [Fact]
    public async Task DeleteRequiresConfirmationAndPreservesRemainingOrder()
    {
        var confirmation = new RecordingConfirmationService { ConfirmDeleteResult = false };
        var subject = CreateSubject(
            new AppSettings(colorRules:
            [
                CreateRule("A", SettingsColorPalette.Colors[0]),
                CreateRule("B", SettingsColorPalette.Colors[1]),
                CreateRule("C", SettingsColorPalette.Colors[2]),
            ]),
            confirmation);
        var notifications = new List<ApplicationSettingsChangedEventArgs>();
        subject.SettingsService.SettingsChanged += (_, eventArgs) => notifications.Add(eventArgs);

        await subject.ViewModel.DeleteRuleCommand.ExecuteAsync(subject.ViewModel.Rules[1]);

        Assert.Equal(["A", "B", "C"], RuleNames(subject));
        Assert.Equal("B", Assert.Single(confirmation.DeleteTargets));
        Assert.Equal(0, subject.Store.SaveCount);
        Assert.Equal(0, subject.SettingsService.UpdateCount);
        Assert.Empty(notifications);

        confirmation.ConfirmDeleteResult = true;
        await subject.ViewModel.DeleteRuleCommand.ExecuteAsync(subject.ViewModel.Rules[1]);

        Assert.Equal(["A", "C"], RuleNames(subject));
        Assert.Equal(["B", "B"], confirmation.DeleteTargets);
        Assert.Equal(1, subject.Store.SaveCount);
        Assert.Equal(1, subject.SettingsService.UpdateCount);
        AssertColorRulesOnly(Assert.Single(notifications));
        Assert.False(subject.ViewModel.IsDirty);
    }

    [Fact]
    public void EditorAndListManagementAreMutuallyExclusiveAndOnlyEditorBecomesDirty()
    {
        var subject = CreateSubject(new AppSettings(colorRules:
        [
            CreateRule("A", SettingsColorPalette.Colors[0]),
            CreateRule("B", SettingsColorPalette.Colors[1]),
        ]));
        var first = subject.ViewModel.Rules[0];

        subject.ViewModel.EditRuleCommand.Execute(first);

        Assert.True(subject.ViewModel.IsEditing);
        Assert.False(subject.ViewModel.IsDirty);
        Assert.False(subject.ViewModel.AddRuleCommand.CanExecute(null));
        Assert.False(subject.ViewModel.EditRuleCommand.CanExecute(first));
        Assert.False(subject.ViewModel.MoveRuleCommand.CanExecute(new ColorRuleMoveRequest(0, 1)));
        Assert.False(subject.ViewModel.ToggleRuleCommand.CanExecute(first));
        Assert.False(subject.ViewModel.DeleteRuleCommand.CanExecute(first));

        subject.ViewModel.RuleName = "Changed";
        Assert.True(subject.ViewModel.IsDirty);
        subject.Confirmation.ConfirmDiscardResult = true;
        subject.ViewModel.CancelEditCommand.Execute(null);

        Assert.False(subject.ViewModel.IsEditing);
        Assert.False(subject.ViewModel.IsDirty);
        Assert.True(subject.ViewModel.AddRuleCommand.CanExecute(null));
        Assert.Equal(["A", "B"], RuleNames(subject));
    }

    [Fact]
    public void ProductionCompositionSharesDeleteConfirmationService()
    {
        var services = new ServiceCollection();
        services.AddUnifiedCalendarApplication();
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<MessageBoxSettingsConfirmationService>(),
            provider.GetRequiredService<IColorRuleDeleteConfirmationService>());
        Assert.NotNull(provider.GetRequiredService<ColorRulesSettingsViewModel>());
    }

    [Fact]
    public void ListUiProvidesIconsTrimmingThemesMutualExclusionAndSingleScrollPath()
    {
        _fixture.Invoke(() =>
        {
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            var longName = new string('W', ColorRule.MaximumNameLength);
            var longValue = new string('W', 180);
            var rules = new[]
            {
                CreateRule(
                    longName,
                    SettingsColorPalette.Colors[0],
                    conditions: [ColorRuleCondition.ForTitle(TextMatchKind.Contains, longValue)]),
            }.Concat(Enumerable.Range(1, 7).Select(index => CreateRule(
                $"Rule {index}",
                SettingsColorPalette.Colors[index],
                isEnabled: index != 3))).ToArray();
            var subject = CreateSubject(new AppSettings(colorRules: rules));
            var owner = new Window { ShowInTaskbar = false };
            owner.Show();
            owner.Hide();
            using var shell = new SettingsWindowViewModel(
                subject.ViewModel,
                subject.TextService,
                subject.SettingsService,
                subject.SyncService,
                subject.ColorPicker,
                subject.BrushCache,
                resetConfirmationService: subject.Confirmation,
                colorRules: subject.ViewModel);
            var window = new SettingsWindow(
                shell,
                new SettingsWindowPlacementService(subject.SettingsService, new TestMonitorProvider()),
                subject.Confirmation)
            {
                Owner = owner,
                ShowInTaskbar = false,
                Width = LayoutMetrics.MinimumSettingsWidth,
                Height = LayoutMetrics.MinimumSettingsHeight,
            };
            window.InitializeShellAsync().GetAwaiter().GetResult();
            shell.SelectedCategory = shell.Categories.Single(category => category.Key == "color-rules");
            window.Show();
            PumpDispatcher();
            window.UpdateLayout();

            var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));
            var listPanel = FindDescendants<StackPanel>(content)
                .Single(panel => panel.Name == "ColorRuleListPanel");
            var editorPanel = FindDescendants<StackPanel>(content)
                .Single(panel => panel.Name == "ColorRuleEditorPanel");
            Assert.Equal(Visibility.Visible, listPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, editorPanel.Visibility);

            var nameText = FindDescendants<TextBlock>(content)
                .Single(text => text.Text == longName);
            var summaryText = FindDescendants<TextBlock>(content)
                .Single(text => text.Text == subject.ViewModel.Rules[0].ConditionSummary);
            Assert.Equal(TextTrimming.CharacterEllipsis, nameText.TextTrimming);
            Assert.Equal(TextTrimming.CharacterEllipsis, summaryText.TextTrimming);
            Assert.Equal(longName, nameText.ToolTip);
            Assert.Equal(subject.ViewModel.Rules[0].ConditionSummary, summaryText.ToolTip);
            Assert.True(MeasureTextWidth(nameText) > nameText.ActualWidth);
            Assert.True(MeasureTextWidth(summaryText) > summaryText.ActualWidth);

            var expectedActions = new[]
            {
                subject.ViewModel.AddText,
                subject.ViewModel.EditText,
                subject.ViewModel.DeleteText,
                subject.ViewModel.MoveUpText,
                subject.ViewModel.MoveDownText,
            };
            var buttons = FindDescendants<Button>(content).ToArray();
            foreach (var action in expectedActions)
            {
                var matching = buttons.Where(button => AutomationProperties.GetName(button) == action).ToArray();
                Assert.NotEmpty(matching);
                Assert.All(matching, button =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                    Assert.False(string.IsNullOrWhiteSpace(button.ToolTip?.ToString()));
                });
            }

            var firstRow = subject.ViewModel.Rules[0];
            var lastRow = subject.ViewModel.Rules[^1];
            var firstUp = FindActionButton(buttons, firstRow, subject.ViewModel.MoveUpText);
            var firstDown = FindActionButton(buttons, firstRow, subject.ViewModel.MoveDownText);
            var lastDown = FindActionButton(buttons, lastRow, subject.ViewModel.MoveDownText);
            Assert.False(firstUp.IsEnabled);
            Assert.True(firstDown.IsEnabled);
            Assert.False(lastDown.IsEnabled);
            Assert.True(FindActionButton(buttons, firstRow, subject.ViewModel.EditText).Focusable);

            var disabledIdentity = FindDescendants<Grid>(content).Single(grid =>
                grid.Name == "RuleIdentityPanel"
                && grid.DataContext is ColorRuleListItemViewModel { IsEnabled: false });
            Assert.Equal(ColorMetrics.DisabledRuleOpacity, disabledIdentity.Opacity);
            var ruleBorders = FindDescendants<Border>(content)
                .Where(border =>
                    border.DataContext is ColorRuleListItemViewModel
                    && border.Child is Grid { RowDefinitions.Count: 2 })
                .ToArray();
            Assert.Equal(rules.Length, ruleBorders.Length);
            Assert.All(ruleBorders, border => Assert.True(border.AllowDrop));

            VerifyRenderedListTheme(window, content);
            themeService.Apply(_fixture.Application.Resources, useDarkTheme: true);
            PumpDispatcher();
            window.UpdateLayout();
            VerifyRenderedListTheme(window, content);

            var shellScrollViewer = Assert.IsType<ScrollViewer>(window.FindName("CategoryScrollViewer"));
            var functionalViewers = new[] { shellScrollViewer }
                .Concat(FindDescendants<ScrollViewer>(content)
                    .Where(viewer => viewer.TemplatedParent is not TextBox))
                .ToArray();
            Assert.Single(functionalViewers);
            Assert.True(shellScrollViewer.ScrollableHeight > 0d);
            var lastDelete = FindActionButton(buttons, lastRow, subject.ViewModel.DeleteText);
            Assert.True(lastDelete.Focus());
            lastDelete.BringIntoView();
            PumpDispatcher();
            Assert.True(shellScrollViewer.VerticalOffset > 0d);

            subject.ViewModel.EditRuleCommand.Execute(subject.ViewModel.Rules[0]);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, listPanel.Visibility);
            Assert.Equal(Visibility.Visible, editorPanel.Visibility);
            subject.ViewModel.CancelEditCommand.Execute(null);
            PumpDispatcher();
            Assert.Equal(Visibility.Visible, listPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, editorPanel.Visibility);

            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            window.Close();
            owner.Close();
        });
    }

    private static TestSubject CreateSubject(
        AppSettings settings,
        RecordingConfirmationService? confirmation = null)
    {
        var store = new RecordingSettingsStore(settings);
        var settingsService = new RecordingApplicationSettingsService(
            new ApplicationSettingsService(store));
        var sync = new TestCalendarSyncService { CurrentSnapshot = Phase6Data.EmptySnapshot };
        var colorPicker = new RecordingColorPickerService();
        var brushCache = new BrushCache();
        var textService = new ResourceUiTextService();
        confirmation ??= new RecordingConfirmationService();
        var viewModel = new ColorRulesSettingsViewModel(
            settingsService,
            sync,
            colorPicker,
            brushCache,
            textService,
            confirmation,
            deleteConfirmationService: confirmation);
        viewModel.Initialize(settings);
        return new TestSubject(
            viewModel,
            store,
            settingsService,
            sync,
            colorPicker,
            brushCache,
            textService,
            confirmation);
    }

    private static ColorRule CreateRule(
        string name,
        RgbColor color,
        ColorRuleOperator ruleOperator = ColorRuleOperator.All,
        IReadOnlyList<ColorRuleCondition>? conditions = null,
        bool isEnabled = true) => new(
            name,
            isEnabled,
            ruleOperator,
            conditions ?? [ColorRuleCondition.ForTitle(TextMatchKind.Contains, "meeting")],
            color);

    private static string[] RuleNames(TestSubject subject) =>
        subject.ViewModel.Rules.Select(rule => rule.Name).ToArray();

    private static CalendarEvent CreateEvent(string title) => new(
        new EventKey(
            ProviderKind.Google,
            Phase6Data.GoogleAccountId,
            "calendar",
            "event"),
        title,
        new TimedEventTiming(Phase6Data.Now.AddMinutes(15), Phase6Data.Now.AddHours(1)),
        "Work",
        responseStatus: AttendeeResponse.Accepted);

    private static RgbColor ResolveColor(
        IReadOnlyList<ColorRule> rules,
        CalendarEvent calendarEvent)
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var snapshot = Phase6Data.Snapshot(
            [account],
            [new CalendarSelection(account.InternalAccountId, "calendar", true)],
            [calendarEvent]);
        return new CalendarPresentationService(
            new MutableTimeProvider(Phase6Data.Now),
            ColorMetrics.ProgressLightnessDelta).BuildSnapshot(
                snapshot,
                new DisplaySettings(defaultEventColor: RgbColor.Parse("#00696F")),
                rules,
                TimeZoneInfo.Utc).Events.Single().BackgroundColor;
    }

    private static AccountSettings CreateAccountSettings() => new(
        Phase6Data.GoogleAccountId,
        ProviderKind.Google,
        "subject-google",
        "Google",
        "google@example.invalid",
        enabled: true,
        $"google/{Phase6Data.GoogleAccountId:D}",
        [new CalendarSetting("calendar", true)]);

    private static void AssertColorRulesOnly(ApplicationSettingsChangedEventArgs notification)
    {
        Assert.Equal(SettingsSection.ColorRules, notification.ChangedSections);
        Assert.False(notification.ChangedSections.HasFlag(SettingsSection.Windows));
        Assert.False(notification.ChangedSections.HasFlag(SettingsSection.Accounts));
    }

    private static Button FindActionButton(
        IEnumerable<Button> buttons,
        ColorRuleListItemViewModel row,
        string action) => buttons.Single(button =>
            ReferenceEquals(button.DataContext, row)
            && AutomationProperties.GetName(button) == action);

    private static double MeasureTextWidth(TextBlock textBlock) => new FormattedText(
        textBlock.Text,
        CultureInfo.CurrentUICulture,
        textBlock.FlowDirection,
        new Typeface(
            textBlock.FontFamily,
            textBlock.FontStyle,
            textBlock.FontWeight,
            textBlock.FontStretch),
        textBlock.FontSize,
        Brushes.Black,
        VisualTreeHelper.GetDpi(textBlock).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private static void VerifyRenderedListTheme(SettingsWindow window, DependencyObject content)
    {
        var primary = Assert.IsType<SolidColorBrush>(window.FindResource("PrimaryTextBrush"));
        var secondary = Assert.IsType<SolidColorBrush>(window.FindResource("SecondaryTextBrush"));
        var background = Assert.IsType<SolidColorBrush>(window.FindResource("ButtonBackgroundBrush"));
        var focusForeground = Assert.IsType<SolidColorBrush>(window.FindResource("FocusForegroundBrush"));
        var actionButtons = FindDescendants<Button>(content)
            .Where(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
            .ToArray();
        Assert.NotEmpty(actionButtons);
        foreach (var button in actionButtons)
        {
            var expectedForeground = button.IsEnabled ? primary : secondary;
            Assert.Equal(expectedForeground, button.Foreground);
            Assert.Equal(background, button.Background);
            var presenter = Assert.Single(FindDescendants<ContentPresenter>(button));
            Assert.Equal(
                button.IsKeyboardFocusWithin ? focusForeground : expectedForeground,
                TextElement.GetForeground(presenter));
        }

        var focusTarget = actionButtons.First(button =>
            button.IsEnabled
            && button.DataContext is ColorRuleListItemViewModel);
        Assert.True(focusTarget.Focus());
        PumpDispatcher();
        Assert.True(focusTarget.IsKeyboardFocusWithin);
        Assert.Equal(focusForeground, TextElement.GetForeground(
            Assert.Single(FindDescendants<ContentPresenter>(focusTarget))));
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

    private sealed record TestSubject(
        ColorRulesSettingsViewModel ViewModel,
        RecordingSettingsStore Store,
        RecordingApplicationSettingsService SettingsService,
        TestCalendarSyncService SyncService,
        RecordingColorPickerService ColorPicker,
        BrushCache BrushCache,
        ResourceUiTextService TextService,
        RecordingConfirmationService Confirmation);

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public RecordingSettingsStore(AppSettings settings)
        {
            Settings = settings;
        }

        public AppSettings Settings { get; private set; }

        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Settings = settings;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingApplicationSettingsService : IApplicationSettingsService
    {
        private readonly IApplicationSettingsService _inner;

        public RecordingApplicationSettingsService(IApplicationSettingsService inner)
        {
            _inner = inner;
            _inner.SettingsChanged += OnSettingsChanged;
        }

        public event EventHandler<ApplicationSettingsChangedEventArgs>? SettingsChanged;

        public int UpdateCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(cancellationToken);

        public Task<AppSettings> UpdateAsync(
            Func<AppSettings, AppSettings> update,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            return _inner.UpdateAsync(update, cancellationToken);
        }

        private void OnSettingsChanged(
            object? sender,
            ApplicationSettingsChangedEventArgs eventArgs) =>
            SettingsChanged?.Invoke(this, eventArgs);
    }

    private sealed class RecordingColorPickerService : IColorPickerService
    {
        public RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor) => null;
    }

    private sealed class RecordingConfirmationService
        : ISettingsConfirmationService,
          ISettingsResetConfirmationService,
          IColorRuleDeleteConfirmationService
    {
        public bool ConfirmDiscardResult { get; set; } = true;

        public bool ConfirmDeleteResult { get; set; } = true;

        public List<string> DeleteTargets { get; } = [];

        public bool ConfirmDiscard() => ConfirmDiscardResult;

        public SettingsExitDecision ConfirmApplicationExit() => SettingsExitDecision.Discard;

        public bool ConfirmReset() => false;

        public bool ConfirmDelete(string ruleName)
        {
            DeleteTargets.Add(ruleName);
            return ConfirmDeleteResult;
        }
    }

    private sealed class FixedThemePreferenceReader : IThemePreferenceReader
    {
        public bool IsDarkTheme() => false;
    }
}
