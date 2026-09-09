using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
public sealed class Phase7d1Tests
{
    private readonly WpfApplicationFixture _fixture;

    public Phase7d1Tests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void CompositionUsesColorRulesEditorAsProductionPendingChanges()
    {
        var services = new ServiceCollection();
        services.AddUnifiedCalendarApplication();
        using var provider = services.BuildServiceProvider();

        var colorRules = provider.GetRequiredService<ColorRulesSettingsViewModel>();
        var pending = provider.GetRequiredService<IPendingSettingsChanges>();

        Assert.Same(colorRules, pending);
        Assert.IsAssignableFrom<IValidatedPendingSettingsChanges>(pending);
    }

    [Fact]
    public void RuleNameValidationUsesCoreLimitAndExcludesEditedRule()
    {
        var existing = CreateRule("Weekly", RgbColor.Parse("#1F6B24"));
        var subject = CreateSubject(new AppSettings(colorRules: [existing]));
        var viewModel = subject.ViewModel;

        viewModel.AddRuleCommand.Execute(null);
        viewModel.Conditions[0].ComparisonValue = "meeting";

        viewModel.RuleName = string.Empty;
        Assert.False(viewModel.CanSave);
        Assert.NotNull(viewModel.RuleNameError);

        viewModel.RuleName = new string('A', ColorRule.MaximumNameLength + 1);
        Assert.False(viewModel.CanSave);
        Assert.NotNull(viewModel.RuleNameError);

        viewModel.RuleName = "A";
        Assert.True(viewModel.CanSave);
        Assert.Null(viewModel.RuleNameError);

        viewModel.RuleName = new string('A', ColorRule.MaximumNameLength);
        Assert.True(viewModel.CanSave);
        Assert.Null(viewModel.RuleNameError);

        viewModel.RuleName = "weekly";
        Assert.False(viewModel.CanSave);
        Assert.NotNull(viewModel.RuleNameError);

        viewModel.RuleName = " Weekly ";
        Assert.False(viewModel.CanSave);
        Assert.NotNull(viewModel.RuleNameError);

        viewModel.Discard();
        viewModel.EditRuleCommand.Execute(viewModel.Rules[0]);

        Assert.Equal(ColorRule.MaximumNameLength, viewModel.NameMaximumLength);
        Assert.Equal("Weekly", viewModel.RuleName);
        Assert.True(viewModel.CanSave);
        Assert.Null(viewModel.RuleNameError);

        viewModel.IsRuleEnabled = false;
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void ConditionsRequireOneValidValueAndAllowRepeatedFields()
    {
        var subject = CreateSubject();
        var viewModel = subject.ViewModel;
        viewModel.AddRuleCommand.Execute(null);
        viewModel.RuleName = "Rule";

        Assert.Single(viewModel.Conditions);
        Assert.False(viewModel.CanSave);
        Assert.NotNull(viewModel.Conditions[0].ComparisonValueError);

        viewModel.Conditions[0].RemoveCommand.Execute(null);
        Assert.Empty(viewModel.Conditions);
        Assert.False(viewModel.CanSave);
        Assert.NotNull(viewModel.ConditionCountError);

        viewModel.AddConditionCommand.Execute(null);
        viewModel.AddConditionCommand.Execute(null);
        Assert.Equal(2, viewModel.Conditions.Count);
        Assert.Equal(
            [ColorRuleField.Title, ColorRuleField.CalendarName, ColorRuleField.Provider],
            viewModel.FieldOptions.Select(option => option.Value));
        Assert.Equal(
            [ProviderKind.Google, ProviderKind.Microsoft],
            viewModel.ProviderOptions.Select(option => option.Value));
        Assert.All(
            viewModel.Conditions,
            condition => Assert.Equal(ColorRuleField.Title, condition.SelectedFieldOption.Value));

        viewModel.Conditions[0].ComparisonValue = "   ";
        viewModel.Conditions[1].ComparisonValue = "planning";
        Assert.False(viewModel.CanSave);
        Assert.NotNull(viewModel.Conditions[0].ComparisonValueError);

        viewModel.Conditions[0].ComparisonValue = "meeting";
        Assert.True(viewModel.CanSave);
        Assert.Null(viewModel.ConditionCountError);
        Assert.All(viewModel.Conditions, condition => Assert.Null(condition.ComparisonValueError));
    }

    [Fact]
    public async Task OperatorsAndTextMatchKindsPersistAndChangeCoreEvaluation()
    {
        var subject = CreateSubject();
        var viewModel = subject.ViewModel;
        var googleEvent = CreateEvent("Team planning", "Work", ProviderKind.Google);

        viewModel.AddRuleCommand.Execute(null);
        viewModel.RuleName = "Team rule";
        viewModel.Conditions[0].ComparisonValue = "Team";
        viewModel.AddConditionCommand.Execute(null);
        var providerCondition = viewModel.Conditions[1];
        providerCondition.SelectedFieldOption = providerCondition.FieldOptions.Single(
            option => option.Value == ColorRuleField.Provider);
        providerCondition.SelectedProviderOption = providerCondition.ProviderOptions.Single(
            option => option.Value == ProviderKind.Microsoft);
        viewModel.SelectedOperatorOption = viewModel.OperatorOptions.Single(
            option => option.Value == ColorRuleOperator.All);

        await viewModel.SaveCommand.ExecuteAsync(null);
        var allRule = Assert.Single(subject.Store.Settings.ColorRules);
        Assert.Equal(ColorRuleOperator.All, allRule.Operator);
        Assert.False(IsRuleApplied(allRule, googleEvent));

        viewModel.EditRuleCommand.Execute(viewModel.Rules[0]);
        viewModel.SelectedOperatorOption = viewModel.OperatorOptions.Single(
            option => option.Value == ColorRuleOperator.Any);
        await viewModel.SaveCommand.ExecuteAsync(null);
        var anyRule = Assert.Single(subject.Store.Settings.ColorRules);
        Assert.Equal(ColorRuleOperator.Any, anyRule.Operator);
        Assert.True(IsRuleApplied(anyRule, googleEvent));

        viewModel.EditRuleCommand.Execute(viewModel.Rules[0]);
        viewModel.Conditions[1].RemoveCommand.Execute(null);
        viewModel.Conditions[0].SelectedMatchKindOption = viewModel.MatchKindOptions.Single(
            option => option.Value == TextMatchKind.Contains);
        await viewModel.SaveCommand.ExecuteAsync(null);
        var containsRule = Assert.Single(subject.Store.Settings.ColorRules);
        Assert.Equal(TextMatchKind.Contains, containsRule.Conditions[0].MatchKind);
        Assert.True(IsRuleApplied(containsRule, googleEvent));

        viewModel.EditRuleCommand.Execute(viewModel.Rules[0]);
        viewModel.Conditions[0].SelectedMatchKindOption = viewModel.MatchKindOptions.Single(
            option => option.Value == TextMatchKind.Exact);
        await viewModel.SaveCommand.ExecuteAsync(null);
        var exactRule = Assert.Single(subject.Store.Settings.ColorRules);
        Assert.Equal(TextMatchKind.Exact, exactRule.Conditions[0].MatchKind);
        Assert.False(IsRuleApplied(exactRule, googleEvent));
    }

    [Fact]
    public async Task CalendarCandidatesComeFromCurrentSnapshotAndFreeInputPersists()
    {
        var account = CreateAccountSettings();
        var cacheStore = new RecordingCacheStore();
        cacheStore.Caches[account.InternalAccountId] = new AccountCache(
            account.InternalAccountId,
            account.Provider,
            Phase6Data.Now,
            [new CachedCalendar("empty-calendar", "No events", null, Phase6Data.Now, [])]);
        var settings = new AppSettings(accounts: [account]);
        var snapshot = CreateSnapshot(
            CreateEvent("One", "Work", ProviderKind.Google),
            CreateEvent("Two", "Personal", ProviderKind.Google),
            CreateEvent("Three", "Work", ProviderKind.Google));
        var subject = CreateSubject(settings, snapshot, cacheStore: cacheStore);
        var viewModel = subject.ViewModel;
        await viewModel.InitializeAsync(settings, TestContext.Current.CancellationToken);
        viewModel.AddRuleCommand.Execute(null);
        viewModel.RuleName = "Calendar rule";
        var condition = viewModel.Conditions[0];
        condition.SelectedFieldOption = condition.FieldOptions.Single(
            option => option.Value == ColorRuleField.CalendarName);

        Assert.Equal(["No events", "Personal", "Work"], condition.CalendarNameSuggestions);

        condition.ComparisonValue = "Unlisted calendar";
        Assert.True(viewModel.CanSave);
        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(subject.Store.Settings.ColorRules);
        var savedCondition = Assert.Single(saved.Conditions);
        Assert.Equal(ColorRuleField.CalendarName, savedCondition.Field);
        Assert.Equal("Unlisted calendar", savedCondition.ComparisonValue);
    }

    [Fact]
    public async Task PreviewUsesFixedSampleAndExistingPresentationPipeline()
    {
        var subject = CreateSubject();
        var viewModel = subject.ViewModel;
        viewModel.AddRuleCommand.Execute(null);
        var initial = Assert.IsType<EventRowViewModel>(viewModel.Preview);

        Assert.Equal(
            subject.TextService.Get(UiResourceKeys.SettingsColorRulesPreviewTitle),
            initial.Title);
        var timing = Assert.IsType<TimedEventTiming>(initial.Presented.Source.Timing);
        Assert.Equal(ColorRulesSettingsViewModel.PreviewStartUtc, timing.StartUtc);
        Assert.Equal(ColorRulesSettingsViewModel.PreviewEndUtc, timing.EndUtc);
        Assert.Equal(0.5d, initial.Presented.Progress);
        Assert.Equal(ProviderKind.Google, initial.Key.Provider);
        Assert.Equal("G", initial.ProviderMark);
        AssertPipelineColors(initial.Presented);

        var changedColor = SettingsColorPalette.Colors[4];
        viewModel.SelectedColorOption = viewModel.ColorOptions.Single(
            option => option.Color == changedColor);
        var changed = Assert.IsType<EventRowViewModel>(viewModel.Preview);

        Assert.NotSame(initial, changed);
        Assert.Equal(changedColor, changed.Presented.BackgroundColor);
        Assert.Equal(0.5d, changed.Presented.Progress);
        AssertPipelineColors(changed.Presented);

        var customColor = RgbColor.Parse("#234567");
        subject.ColorPicker.Result = customColor;
        await viewModel.PickCustomColorAsync((nint)123);
        var custom = Assert.IsType<EventRowViewModel>(viewModel.Preview);
        Assert.Equal(1, subject.ColorPicker.PickCount);
        Assert.Equal(customColor, viewModel.CurrentColor);
        Assert.Null(viewModel.SelectedColorOption);
        Assert.Equal(customColor, custom.Presented.BackgroundColor);
        AssertPipelineColors(custom.Presented);
    }

    [Fact]
    public async Task ValidationControlsEditorAndShellSaveCommands()
    {
        var subject = CreateSubject();
        using var shell = new SettingsWindowViewModel(
            subject.ViewModel,
            subject.TextService,
            subject.SettingsService,
            subject.SyncService,
            subject.ColorPicker,
            subject.BrushCache,
            colorRules: subject.ViewModel);
        await shell.InitializeAsync(TestContext.Current.CancellationToken);

        subject.ViewModel.AddRuleCommand.Execute(null);
        subject.ViewModel.RuleName = "Rule";
        Assert.True(subject.ViewModel.IsDirty);
        Assert.False(subject.ViewModel.CanSave);
        Assert.False(subject.ViewModel.SaveCommand.CanExecute(null));
        Assert.False(shell.ApplyCommand.CanExecute(null));
        Assert.False(shell.OkCommand.CanExecute(null));

        subject.ViewModel.Conditions[0].ComparisonValue = "meeting";
        Assert.True(subject.ViewModel.CanSave);
        Assert.True(subject.ViewModel.SaveCommand.CanExecute(null));
        Assert.True(shell.ApplyCommand.CanExecute(null));
        Assert.True(shell.OkCommand.CanExecute(null));

        subject.ViewModel.Discard();
        shell.Display.Days++;
        await shell.Display.WaitForPendingUpdatesAsync();
        Assert.False(subject.ViewModel.IsDirty);
        Assert.False(shell.IsDirty);
    }

    [Fact]
    public void EditorCancelPromptsOnlyForUnsavedChanges()
    {
        var confirmation = new RecordingConfirmationService();
        var subject = CreateSubject(confirmation: confirmation);
        var viewModel = subject.ViewModel;

        viewModel.AddRuleCommand.Execute(null);
        Assert.False(viewModel.IsDirty);
        viewModel.CancelEditCommand.Execute(null);
        Assert.False(viewModel.IsEditing);
        Assert.Equal(0, confirmation.DiscardPromptCount);

        viewModel.AddRuleCommand.Execute(null);
        viewModel.RuleName = "Changed";
        confirmation.ConfirmDiscardResult = false;
        viewModel.CancelEditCommand.Execute(null);
        Assert.True(viewModel.IsEditing);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(1, confirmation.DiscardPromptCount);

        confirmation.ConfirmDiscardResult = true;
        viewModel.CancelEditCommand.Execute(null);
        Assert.False(viewModel.IsEditing);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(2, confirmation.DiscardPromptCount);
    }

    [Fact]
    public async Task SaveUsesOneSerializedUpdateAndOnlyChangesColorRules()
    {
        var account = CreateAccountSettings();
        var windows = new WindowPreferences(
            new WindowPlacement(10, 20, 800, 600, "DISPLAY1", 96, 96),
            new WindowPlacement(30, 40, 760, 560, "DISPLAY1", 96, 96));
        var initial = new AppSettings(windows: windows, accounts: [account]);
        var subject = CreateSubject(initial);
        ApplicationSettingsChangedEventArgs? notification = null;
        subject.SettingsService.SettingsChanged += (_, eventArgs) => notification = eventArgs;

        subject.ViewModel.AddRuleCommand.Execute(null);
        subject.ViewModel.RuleName = "Saved rule";
        subject.ViewModel.Conditions[0].ComparisonValue = "meeting";
        subject.ViewModel.SelectedColorOption = subject.ViewModel.ColorOptions[3];
        await subject.ViewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, subject.Store.SaveCount);
        Assert.NotNull(notification);
        Assert.Equal(SettingsSection.ColorRules, notification.ChangedSections);
        Assert.False(notification.ChangedSections.HasFlag(SettingsSection.Windows));
        Assert.False(notification.ChangedSections.HasFlag(SettingsSection.Accounts));
        Assert.Same(windows, subject.Store.Settings.Windows);
        Assert.Same(account, Assert.Single(subject.Store.Settings.Accounts));
        Assert.False(subject.ViewModel.IsEditing);
        Assert.False(subject.ViewModel.IsDirty);
        var row = Assert.Single(subject.ViewModel.Rules);
        Assert.Equal("Saved rule", row.Name);
        Assert.Equal(subject.ViewModel.ColorOptions[3].Color, row.Color);
    }

    [Fact]
    public async Task SavedRulesImmediatelyReprojectAndKeepCorePriorityFallbackAndDisabledBehavior()
    {
        var matched = CreateEvent("match planning", "Work", ProviderKind.Google, "matched");
        var unmatched = CreateEvent("ordinary", "Work", ProviderKind.Google, "unmatched");
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = CreateSnapshot(matched, unmatched),
        };
        var store = new RecordingSettingsStore(AppSettings.CreateDefault());
        var settingsService = new ApplicationSettingsService(store);
        var viewport = new RecordingTimelineViewport();
        using var main = Phase6Data.CreateViewModel(
            sync,
            new MutableTimeProvider(Phase6Data.Now),
            settings: new TestSettingsStore(),
            viewport: viewport,
            settingsService: settingsService);
        await main.InitializeAsync(CancellationToken.None);
        var colorRules = new ColorRulesSettingsViewModel(
            settingsService,
            sync,
            new RecordingColorPickerService(),
            new BrushCache(),
            new ResourceUiTextService(),
            new RecordingConfirmationService());
        colorRules.Initialize(store.Settings);
        var defaultColor = store.Settings.Display.DefaultEventColor;

        Assert.Equal(defaultColor, FindPresented(main, matched.Key).BackgroundColor);
        Assert.Equal(defaultColor, FindPresented(main, unmatched.Key).BackgroundColor);

        var disabledColor = SettingsColorPalette.Colors[6];
        await SaveNewTitleRuleAsync(colorRules, "Disabled", "match", disabledColor, false);
        await Phase6Data.WaitUntilAsync(() =>
            FindPresented(main, matched.Key).BackgroundColor == defaultColor);

        var firstEnabledColor = SettingsColorPalette.Colors[4];
        await SaveNewTitleRuleAsync(colorRules, "First enabled", "match", firstEnabledColor, true);
        await Phase6Data.WaitUntilAsync(() =>
            FindPresented(main, matched.Key).BackgroundColor == firstEnabledColor);

        var lowerColor = SettingsColorPalette.Colors[2];
        var restorationCountBeforeLowerRule = viewport.Restorations.Count;
        await SaveNewTitleRuleAsync(colorRules, "Lower enabled", "match", lowerColor, true);
        await Phase6Data.WaitUntilAsync(() =>
            viewport.Restorations.Count >= restorationCountBeforeLowerRule + 1);

        Assert.Equal(restorationCountBeforeLowerRule + 1, viewport.Restorations.Count);
        Assert.Equal(firstEnabledColor, FindPresented(main, matched.Key).BackgroundColor);
        Assert.Equal(defaultColor, FindPresented(main, unmatched.Key).BackgroundColor);
        Assert.Equal(["Disabled", "First enabled", "Lower enabled"], colorRules.Rules.Select(row => row.Name));
        Assert.False(colorRules.Rules[0].IsEnabled);
        Assert.All(colorRules.Rules.Skip(1), row => Assert.True(row.IsEnabled));
    }

    [Fact]
    public void SettingsCategoryRendersListEditorPaletteAndSingleScrollPathInBothThemes()
    {
        _fixture.Invoke(() =>
        {
            var themeService = new StartupThemeService(new FixedThemePreferenceReader());
            themeService.Apply(_fixture.Application.Resources, useDarkTheme: false);
            var existing = CreateRule("Existing", SettingsColorPalette.Colors[1]);
            var subject = CreateSubject(new AppSettings(colorRules: [existing]));
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
            };
            window.InitializeShellAsync().GetAwaiter().GetResult();
            shell.SelectedCategory = shell.Categories.Single(category => category.Key == "color-rules");
            window.Show();
            PumpDispatcher();
            window.UpdateLayout();
            var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));
            Assert.Contains(
                FindDescendants<TextBlock>(content),
                text => text.Text == "Existing");

            subject.ViewModel.AddRuleCommand.Execute(null);
            PumpDispatcher();
            window.UpdateLayout();
            Assert.True(subject.ViewModel.IsEditing);
            Assert.NotEmpty(FindDescendants<TextBox>(content));
            Assert.NotEmpty(FindDescendants<ComboBox>(content));
            Assert.Contains(
                FindDescendants<TextBlock>(content),
                text => text.Text == subject.ViewModel.RuleNameError && text.Visibility == Visibility.Visible);
            Assert.Contains(
                FindDescendants<TextBlock>(content),
                text => text.Text == subject.ViewModel.Conditions[0].ComparisonValueError
                    && text.Visibility == Visibility.Visible);
            var editorSave = FindDescendants<Button>(content).Single(
                button => Equals(button.Content, subject.ViewModel.SaveText));
            Assert.False(editorSave.IsEnabled);
            subject.ViewModel.RuleName = "Valid";
            subject.ViewModel.Conditions[0].ComparisonValue = "meeting";
            PumpDispatcher();
            window.UpdateLayout();
            Assert.True(editorSave.IsEnabled);
            var palette = Assert.Single(FindDescendants<ListBox>(content));
            Assert.Equal(SettingsColorPalette.Colors, subject.ViewModel.ColorOptions.Select(option => option.Color));
            Assert.Equal(8, palette.Items.Count);
            for (var index = 0; index < palette.Items.Count; index++)
            {
                var option = subject.ViewModel.ColorOptions[index];
                var container = Assert.IsType<ListBoxItem>(
                    palette.ItemContainerGenerator.ContainerFromIndex(index));
                var swatch = FindDescendants<Border>(container).Single(border =>
                    AutomationProperties.GetName(border) == option.Name);
                Assert.Equal(option.Name, swatch.ToolTip);
            }

            var shellScrollViewer = Assert.IsType<ScrollViewer>(window.FindName("CategoryScrollViewer"));
            var functionalViewers = new[] { shellScrollViewer }
                .Concat(FindDescendants<ScrollViewer>(content)
                    .Where(viewer => viewer.TemplatedParent is not TextBox))
                .ToArray();
            Assert.Single(functionalViewers);

            VerifyEditorThemeForegrounds(window, content);
            themeService.Apply(_fixture.Application.Resources, useDarkTheme: true);
            PumpDispatcher();
            window.UpdateLayout();
            VerifyEditorThemeForegrounds(window, content);

            subject.Confirmation.ConfirmDiscardResult = true;
            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void ProductionDirtyUsesExistingWindowCloseConfirmation()
    {
        _fixture.Invoke(() =>
        {
            var subject = CreateSubject();
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
            };
            window.InitializeShellAsync().GetAwaiter().GetResult();
            window.Show();
            subject.ViewModel.AddRuleCommand.Execute(null);
            subject.ViewModel.RuleName = "Dirty";
            Assert.True(shell.IsDirty);

            subject.Confirmation.ConfirmDiscardResult = false;
            window.Close();
            Assert.True(window.IsVisible);
            Assert.Equal(1, subject.Confirmation.DiscardPromptCount);

            subject.Confirmation.ConfirmDiscardResult = true;
            window.Close();
            Assert.False(window.IsVisible);
            Assert.Equal(2, subject.Confirmation.DiscardPromptCount);
            Assert.False(subject.ViewModel.IsDirty);
            owner.Close();
        });
    }

    [Fact]
    public void ProductionDirtyUsesExistingApplicationExitApplyPath()
    {
        _fixture.Invoke(() =>
        {
            var subject = CreateSubject();
            subject.Confirmation.ExitDecision = SettingsExitDecision.Apply;
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
            };
            window.InitializeShellAsync().GetAwaiter().GetResult();
            window.Show();
            subject.ViewModel.AddRuleCommand.Execute(null);
            subject.ViewModel.RuleName = "Exit apply";
            subject.ViewModel.Conditions[0].ComparisonValue = "meeting";
            Assert.True(shell.IsDirty);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();

            Assert.Equal(1, subject.Confirmation.ExitPromptCount);
            Assert.Equal("Exit apply", Assert.Single(subject.Store.Settings.ColorRules).Name);
            Assert.False(window.IsVisible);
            owner.Close();
        });
    }

    private static TestSubject CreateSubject(
        AppSettings? settings = null,
        SyncSnapshot? snapshot = null,
        RecordingConfirmationService? confirmation = null,
        RecordingColorPickerService? colorPicker = null,
        ICacheStore? cacheStore = null)
    {
        var store = new RecordingSettingsStore(settings ?? AppSettings.CreateDefault());
        var settingsService = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService
        {
            CurrentSnapshot = snapshot ?? Phase6Data.EmptySnapshot,
        };
        var textService = new ResourceUiTextService();
        var brushCache = new BrushCache();
        confirmation ??= new RecordingConfirmationService();
        colorPicker ??= new RecordingColorPickerService();
        var viewModel = new ColorRulesSettingsViewModel(
            settingsService,
            sync,
            colorPicker,
            brushCache,
            textService,
            confirmation,
            cacheStore);
        viewModel.Initialize(store.Settings);
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

    private static ColorRule CreateRule(string name, RgbColor color) => new(
        name,
        isEnabled: true,
        ColorRuleOperator.All,
        [ColorRuleCondition.ForTitle(TextMatchKind.Contains, "meeting")],
        color);

    private static CalendarEvent CreateEvent(
        string title,
        string calendarName,
        ProviderKind provider,
        string? eventId = null)
    {
        var accountId = provider == ProviderKind.Google
            ? Phase6Data.GoogleAccountId
            : Phase6Data.MicrosoftAccountId;
        return new CalendarEvent(
            new EventKey(provider, accountId, "calendar", eventId ?? title),
            title,
            new TimedEventTiming(Phase6Data.Now.AddMinutes(15), Phase6Data.Now.AddHours(1)),
            calendarName,
            responseStatus: AttendeeResponse.Accepted);
    }

    private static SyncSnapshot CreateSnapshot(params CalendarEvent[] events)
    {
        var accounts = events
            .Select(calendarEvent => calendarEvent.Key.Provider)
            .Distinct()
            .Select(provider => Phase6Data.Account(
                provider == ProviderKind.Google
                    ? Phase6Data.GoogleAccountId
                    : Phase6Data.MicrosoftAccountId,
                provider))
            .ToArray();
        var selections = accounts
            .Select(account => new CalendarSelection(account.InternalAccountId, "calendar", true))
            .ToArray();
        return Phase6Data.Snapshot(accounts, selections, events);
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

    private static void AssertPipelineColors(PresentedEvent actual)
    {
        var source = actual.Source;
        var account = Phase6Data.Account(source.Key.InternalAccountId, source.Key.Provider);
        var snapshot = Phase6Data.Snapshot(
            [account],
            [new CalendarSelection(source.Key.InternalAccountId, source.Key.CalendarId, true)],
            [source]);
        var expected = new CalendarPresentationService(
            new MutableTimeProvider(ColorRulesSettingsViewModel.PreviewNowUtc),
            ColorMetrics.ProgressLightnessDelta).BuildSnapshot(
                snapshot,
                new DisplaySettings(displayDays: 1),
                [],
                TimeZoneInfo.Utc).Events.Single();

        Assert.Equal(expected.BackgroundColor, actual.BackgroundColor);
        Assert.Equal(expected.ForegroundColor, actual.ForegroundColor);
        Assert.Equal(expected.ElapsedColor, actual.ElapsedColor);
        Assert.Equal(expected.RemainingColor, actual.RemainingColor);
        Assert.Equal(expected.Progress, actual.Progress);
    }

    private static bool IsRuleApplied(ColorRule rule, CalendarEvent calendarEvent)
    {
        var account = Phase6Data.Account(
            calendarEvent.Key.InternalAccountId,
            calendarEvent.Key.Provider);
        var snapshot = Phase6Data.Snapshot(
            [account],
            [new CalendarSelection(
                calendarEvent.Key.InternalAccountId,
                calendarEvent.Key.CalendarId,
                true)],
            [calendarEvent]);
        var fallback = RgbColor.Parse("#00696F");
        var presented = new CalendarPresentationService(
            new MutableTimeProvider(Phase6Data.Now),
            ColorMetrics.ProgressLightnessDelta).BuildSnapshot(
                snapshot,
                new DisplaySettings(defaultEventColor: fallback),
                [rule],
                TimeZoneInfo.Utc).Events.Single();
        return presented.BackgroundColor == rule.Color;
    }

    private static async Task SaveNewTitleRuleAsync(
        ColorRulesSettingsViewModel viewModel,
        string name,
        string comparisonValue,
        RgbColor color,
        bool isEnabled)
    {
        viewModel.AddRuleCommand.Execute(null);
        viewModel.RuleName = name;
        viewModel.IsRuleEnabled = isEnabled;
        viewModel.Conditions[0].ComparisonValue = comparisonValue;
        viewModel.SelectedColorOption = viewModel.ColorOptions.Single(option => option.Color == color);
        Assert.True(viewModel.CanSave);
        await viewModel.SaveCommand.ExecuteAsync(null);
    }

    private static PresentedEvent FindPresented(MainWindowViewModel viewModel, EventKey key) =>
        viewModel.TimelineItems
            .OfType<EventRowViewModel>()
            .Single(row => row.Key == key)
            .Presented;

    private static void VerifyEditorThemeForegrounds(SettingsWindow window, DependencyObject content)
    {
        var primary = window.FindResource("PrimaryTextBrush");
        Assert.All(FindDescendants<TextBox>(content), control => Assert.Equal(primary, control.Foreground));
        Assert.All(FindDescendants<ComboBox>(content), control => Assert.Equal(primary, control.Foreground));
        Assert.All(FindDescendants<Button>(content).Where(button => button.IsEnabled), control =>
            Assert.Equal(primary, control.Foreground));
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
        ApplicationSettingsService SettingsService,
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

    private sealed class RecordingColorPickerService : IColorPickerService
    {
        public RgbColor? Result { get; set; }

        public int PickCount { get; private set; }

        public RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor)
        {
            PickCount++;
            return Result;
        }
    }

    private sealed class RecordingCacheStore : ICacheStore
    {
        public Dictionary<Guid, AccountCache> Caches { get; } = [];

        public Task<AccountCache?> LoadAccountAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Caches.GetValueOrDefault(internalAccountId));
        }

        public Task SaveAccountAsync(
            AccountCache accountCache,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Caches[accountCache.InternalAccountId] = accountCache;
            return Task.CompletedTask;
        }

        public Task RemoveAccountAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Caches.Remove(internalAccountId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConfirmationService
        : ISettingsConfirmationService, ISettingsResetConfirmationService
    {
        public bool ConfirmDiscardResult { get; set; } = true;

        public SettingsExitDecision ExitDecision { get; set; } = SettingsExitDecision.Apply;

        public int DiscardPromptCount { get; private set; }

        public int ExitPromptCount { get; private set; }

        public bool ConfirmDiscard()
        {
            DiscardPromptCount++;
            return ConfirmDiscardResult;
        }

        public SettingsExitDecision ConfirmApplicationExit()
        {
            ExitPromptCount++;
            return ExitDecision;
        }

        public bool ConfirmReset() => false;
    }

    private sealed class FixedThemePreferenceReader : IThemePreferenceReader
    {
        public bool IsDarkTheme() => false;
    }
}
