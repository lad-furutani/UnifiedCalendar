using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;

namespace UnifiedCalendar.App.ViewModels;

public sealed record ColorRuleOperatorOptionViewModel(
    ColorRuleOperator Value,
    string Label);

public sealed record ColorRuleFieldOptionViewModel(
    ColorRuleField Value,
    string Label);

public sealed record TextMatchKindOptionViewModel(
    TextMatchKind Value,
    string Label);

public sealed record ProviderOptionViewModel(
    ProviderKind Value,
    string Label);

public sealed record ColorRuleListItemViewModel(
    int Index,
    bool IsEnabled,
    string EnabledText,
    string ToggleEnabledText,
    string Name,
    string ConditionSummary,
    RgbColor Color,
    Brush ColorBrush,
    bool CanMoveUp,
    bool CanMoveDown);

public sealed record ColorRuleMoveRequest(int FromIndex, int ToIndex);

public sealed class ColorRuleConditionEditorViewModel : ObservableObject
{
    private readonly Action<ColorRuleConditionEditorViewModel> _remove;
    private ColorRuleFieldOptionViewModel _selectedFieldOption;
    private TextMatchKindOptionViewModel _selectedMatchKindOption;
    private ProviderOptionViewModel _selectedProviderOption;
    private string _comparisonValue;
    private string? _comparisonValueError;

    internal ColorRuleConditionEditorViewModel(
        IReadOnlyList<ColorRuleFieldOptionViewModel> fieldOptions,
        IReadOnlyList<TextMatchKindOptionViewModel> matchKindOptions,
        IReadOnlyList<ProviderOptionViewModel> providerOptions,
        IReadOnlyList<string> calendarNameSuggestions,
        ColorRuleField field,
        TextMatchKind matchKind,
        string comparisonValue,
        ProviderKind provider,
        string calendarCandidateText,
        string comparisonValueText,
        string removeText,
        Action<ColorRuleConditionEditorViewModel> remove)
    {
        FieldOptions = fieldOptions ?? throw new ArgumentNullException(nameof(fieldOptions));
        MatchKindOptions = matchKindOptions ?? throw new ArgumentNullException(nameof(matchKindOptions));
        ProviderOptions = providerOptions ?? throw new ArgumentNullException(nameof(providerOptions));
        CalendarNameSuggestions = calendarNameSuggestions
            ?? throw new ArgumentNullException(nameof(calendarNameSuggestions));
        _remove = remove ?? throw new ArgumentNullException(nameof(remove));
        _selectedFieldOption = FieldOptions.Single(option => option.Value == field);
        _selectedMatchKindOption = MatchKindOptions.Single(option => option.Value == matchKind);
        _selectedProviderOption = ProviderOptions.Single(option => option.Value == provider);
        _comparisonValue = comparisonValue ?? throw new ArgumentNullException(nameof(comparisonValue));
        CalendarCandidateText = calendarCandidateText
            ?? throw new ArgumentNullException(nameof(calendarCandidateText));
        ComparisonValueText = comparisonValueText
            ?? throw new ArgumentNullException(nameof(comparisonValueText));
        RemoveText = removeText ?? throw new ArgumentNullException(nameof(removeText));
        RemoveCommand = new RelayCommand(() => _remove(this));
    }

    public event EventHandler? DraftChanged;

    public IReadOnlyList<ColorRuleFieldOptionViewModel> FieldOptions { get; }

    public IReadOnlyList<TextMatchKindOptionViewModel> MatchKindOptions { get; }

    public IReadOnlyList<ProviderOptionViewModel> ProviderOptions { get; }

    public IReadOnlyList<string> CalendarNameSuggestions { get; }

    public string CalendarCandidateText { get; }

    public string ComparisonValueText { get; }

    public string RemoveText { get; }

    public IRelayCommand RemoveCommand { get; }

    public ColorRuleFieldOptionViewModel SelectedFieldOption
    {
        get => _selectedFieldOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedFieldOption, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsTextCondition));
            OnPropertyChanged(nameof(IsCalendarNameCondition));
            OnPropertyChanged(nameof(IsProviderCondition));
            DraftChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public TextMatchKindOptionViewModel SelectedMatchKindOption
    {
        get => _selectedMatchKindOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedMatchKindOption, value))
            {
                DraftChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public ProviderOptionViewModel SelectedProviderOption
    {
        get => _selectedProviderOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedProviderOption, value))
            {
                DraftChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string ComparisonValue
    {
        get => _comparisonValue;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _comparisonValue, value))
            {
                DraftChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string? ComparisonValueError
    {
        get => _comparisonValueError;
        private set => SetProperty(ref _comparisonValueError, value);
    }

    public bool IsTextCondition => SelectedFieldOption.Value is
        ColorRuleField.Title or ColorRuleField.CalendarName;

    public bool IsCalendarNameCondition =>
        SelectedFieldOption.Value == ColorRuleField.CalendarName;

    public bool IsProviderCondition => SelectedFieldOption.Value == ColorRuleField.Provider;

    internal void SetComparisonValueError(string? value) => ComparisonValueError = value;

    internal ColorRuleCondition ToDomain() => SelectedFieldOption.Value switch
    {
        ColorRuleField.Title => ColorRuleCondition.ForTitle(
            SelectedMatchKindOption.Value,
            ComparisonValue),
        ColorRuleField.CalendarName => ColorRuleCondition.ForCalendarName(
            SelectedMatchKindOption.Value,
            ComparisonValue),
        ColorRuleField.Provider => ColorRuleCondition.ForProvider(SelectedProviderOption.Value),
        _ => throw new ArgumentOutOfRangeException(nameof(SelectedFieldOption)),
    };

    internal ColorRuleConditionDraft Capture() => new(
        SelectedFieldOption.Value,
        SelectedMatchKindOption.Value,
        ComparisonValue,
        SelectedProviderOption.Value);
}

internal sealed record ColorRuleConditionDraft(
    ColorRuleField Field,
    TextMatchKind MatchKind,
    string ComparisonValue,
    ProviderKind Provider);

public sealed class ColorRulesSettingsViewModel
    : ObservableObject, IPendingSettingsChanges, IValidatedPendingSettingsChanges
{
    internal static readonly DateTimeOffset PreviewStartUtc =
        new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset PreviewEndUtc =
        new(2026, 1, 5, 11, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset PreviewNowUtc =
        new(2026, 1, 5, 10, 30, 0, TimeSpan.Zero);

    private static readonly Guid PreviewAccountId =
        Guid.Parse("8fef4dbb-5f6b-40f8-8480-498897a91b67");
    private const string PreviewCalendarId = "preview-calendar";
    private const string PreviewEventId = "preview-event";

    private readonly IApplicationSettingsService _settingsService;
    private readonly ICalendarSyncService _syncService;
    private readonly IColorPickerService _colorPicker;
    private readonly BrushCache _brushCache;
    private readonly IUiTextService _textService;
    private readonly ISettingsConfirmationService _confirmationService;
    private readonly IColorRuleDeleteConfirmationService _deleteConfirmationService;
    private readonly ICacheStore? _cacheStore;
    private readonly CalendarPresentationService _previewPresentationService;
    private readonly ObservableCollection<ColorRuleListItemViewModel> _rules = [];
    private readonly ObservableCollection<ColorRuleConditionEditorViewModel> _conditions = [];
    private IReadOnlyList<ColorRule> _domainRules = [];
    private IReadOnlyList<string> _calendarNameSuggestions = [];
    private RuleDraftSnapshot? _baseline;
    private int? _editingIndex;
    private bool _suppressDraftNotifications;
    private bool _isEditing;
    private string _editorTitle = string.Empty;
    private string _ruleName = string.Empty;
    private string? _ruleNameError;
    private string? _conditionCountError;
    private bool _isRuleEnabled = true;
    private ColorRuleOperatorOptionViewModel _selectedOperatorOption;
    private RgbColor _currentColor = DisplaySettings.InitialDefaultEventColor;
    private Brush _currentColorBrush;
    private string _currentColorText = string.Empty;
    private SettingsColorOptionViewModel? _selectedColorOption;
    private EventRowViewModel? _preview;
    private bool _isListOperationRunning;

    public ColorRulesSettingsViewModel(
        IApplicationSettingsService settingsService,
        ICalendarSyncService syncService,
        IColorPickerService colorPicker,
        BrushCache brushCache,
        IUiTextService textService,
        ISettingsConfirmationService confirmationService,
        ICacheStore? cacheStore = null,
        IColorRuleDeleteConfirmationService? deleteConfirmationService = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _colorPicker = colorPicker ?? throw new ArgumentNullException(nameof(colorPicker));
        _brushCache = brushCache ?? throw new ArgumentNullException(nameof(brushCache));
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        _confirmationService = confirmationService
            ?? throw new ArgumentNullException(nameof(confirmationService));
        _deleteConfirmationService = deleteConfirmationService
            ?? DeclineColorRuleDeleteConfirmationService.Instance;
        _cacheStore = cacheStore;
        _previewPresentationService = new CalendarPresentationService(
            new FixedPreviewTimeProvider(),
            ColorMetrics.ProgressLightnessDelta);

        HeadingText = textService.Get(UiResourceKeys.SettingsColorRulesHeading);
        EmptyText = textService.Get(UiResourceKeys.SettingsColorRulesEmpty);
        AddText = textService.Get(UiResourceKeys.SettingsColorRulesAdd);
        NewEditorTitle = textService.Get(UiResourceKeys.SettingsColorRulesNewTitle);
        EditEditorTitle = textService.Get(UiResourceKeys.SettingsColorRulesEditTitle);
        NameText = textService.Get(UiResourceKeys.SettingsColorRulesName);
        EnabledText = textService.Get(UiResourceKeys.SettingsColorRulesEnabled);
        EnabledStateText = textService.Get(UiResourceKeys.SettingsColorRulesStateEnabled);
        DisabledStateText = textService.Get(UiResourceKeys.SettingsColorRulesStateDisabled);
        OperatorText = textService.Get(UiResourceKeys.SettingsColorRulesOperator);
        ConditionsText = textService.Get(UiResourceKeys.SettingsColorRulesConditions);
        AddConditionText = textService.Get(UiResourceKeys.SettingsColorRulesAddCondition);
        ColorText = textService.Get(UiResourceKeys.SettingsColorRulesColor);
        CustomColorText = textService.Get(UiResourceKeys.SettingsDisplayCustomColor);
        PreviewText = textService.Get(UiResourceKeys.SettingsColorRulesPreview);
        SaveText = textService.Get(UiResourceKeys.SettingsColorRulesSave);
        CancelText = textService.Get(UiResourceKeys.SettingsCancel);
        EditText = textService.Get(UiResourceKeys.SettingsColorRulesEdit);
        DeleteText = textService.Get(UiResourceKeys.SettingsColorRulesDelete);
        MoveUpText = textService.Get(UiResourceKeys.SettingsColorRulesMoveUp);
        MoveDownText = textService.Get(UiResourceKeys.SettingsColorRulesMoveDown);
        EnableText = textService.Get(UiResourceKeys.SettingsColorRulesEnable);
        DisableText = textService.Get(UiResourceKeys.SettingsColorRulesDisable);
        _summaryJoinAll = textService.Get(UiResourceKeys.SettingsColorRulesSummaryJoinAll);
        _summaryJoinAny = textService.Get(UiResourceKeys.SettingsColorRulesSummaryJoinAny);
        _nameRequiredError = textService.Get(UiResourceKeys.SettingsColorRulesNameRequired);
        _nameTooLongError = textService.Get(
            UiResourceKeys.SettingsColorRulesNameTooLong,
            ColorRule.MaximumNameLength);
        _nameDuplicateError = textService.Get(UiResourceKeys.SettingsColorRulesNameDuplicate);
        _conditionRequiredError = textService.Get(UiResourceKeys.SettingsColorRulesConditionRequired);
        _comparisonRequiredError = textService.Get(UiResourceKeys.SettingsColorRulesComparisonRequired);
        _calendarCandidateText = textService.Get(UiResourceKeys.SettingsColorRulesCalendarCandidate);
        _comparisonValueText = textService.Get(UiResourceKeys.SettingsColorRulesComparisonValue);
        _removeConditionText = textService.Get(UiResourceKeys.SettingsColorRulesRemoveCondition);
        _previewTitle = textService.Get(UiResourceKeys.SettingsColorRulesPreviewTitle);
        _previewCalendarName = textService.Get(UiResourceKeys.SettingsColorRulesPreviewCalendar);

        OperatorOptions =
        [
            new(ColorRuleOperator.All, textService.Get(UiResourceKeys.SettingsColorRulesOperatorAll)),
            new(ColorRuleOperator.Any, textService.Get(UiResourceKeys.SettingsColorRulesOperatorAny)),
        ];
        FieldOptions =
        [
            new(ColorRuleField.Title, textService.Get(UiResourceKeys.SettingsColorRulesFieldTitle)),
            new(ColorRuleField.CalendarName, textService.Get(UiResourceKeys.SettingsColorRulesFieldCalendar)),
            new(ColorRuleField.Provider, textService.Get(UiResourceKeys.SettingsColorRulesFieldProvider)),
        ];
        MatchKindOptions =
        [
            new(TextMatchKind.Contains, textService.Get(UiResourceKeys.SettingsColorRulesMatchContains)),
            new(TextMatchKind.Exact, textService.Get(UiResourceKeys.SettingsColorRulesMatchExact)),
        ];
        ProviderOptions =
        [
            new(ProviderKind.Google, textService.Get(UiResourceKeys.ProviderGoogle)),
            new(ProviderKind.Microsoft, textService.Get(UiResourceKeys.ProviderMicrosoft)),
        ];
        var colorNameKeys = new[]
        {
            UiResourceKeys.SettingsColorBlue,
            UiResourceKeys.SettingsColorTeal,
            UiResourceKeys.SettingsColorGreen,
            UiResourceKeys.SettingsColorAmber,
            UiResourceKeys.SettingsColorRed,
            UiResourceKeys.SettingsColorPink,
            UiResourceKeys.SettingsColorPurple,
            UiResourceKeys.SettingsColorBlueGray,
        };
        ColorOptions = SettingsColorPalette.Colors
            .Select((color, index) => new SettingsColorOptionViewModel(
                textService.Get(colorNameKeys[index]),
                color,
                brushCache.GetSolid(color)))
            .ToArray();

        _selectedOperatorOption = OperatorOptions[0];
        _currentColorBrush = brushCache.GetSolid(_currentColor);
        _currentColorText = _currentColor.ToString();
        Rules = new ReadOnlyObservableCollection<ColorRuleListItemViewModel>(_rules);
        Conditions = new ReadOnlyObservableCollection<ColorRuleConditionEditorViewModel>(_conditions);
        AddRuleCommand = new RelayCommand(BeginAdd, CanBeginListEdit);
        EditRuleCommand = new RelayCommand<ColorRuleListItemViewModel>(BeginEdit, CanBeginListEdit);
        MoveRuleUpCommand = new AsyncRelayCommand<ColorRuleListItemViewModel>(
            MoveRuleUpAsync,
            CanMoveRuleUp);
        MoveRuleDownCommand = new AsyncRelayCommand<ColorRuleListItemViewModel>(
            MoveRuleDownAsync,
            CanMoveRuleDown);
        MoveRuleCommand = new AsyncRelayCommand<ColorRuleMoveRequest>(
            MoveRuleAsync,
            CanMoveRule);
        ToggleRuleCommand = new AsyncRelayCommand<ColorRuleListItemViewModel>(
            ToggleRuleAsync,
            CanManageRule);
        DeleteRuleCommand = new AsyncRelayCommand<ColorRuleListItemViewModel>(
            DeleteRuleAsync,
            CanManageRule);
        AddConditionCommand = new RelayCommand(AddCondition);
        SaveCommand = new AsyncRelayCommand(SaveEditorAsync, () => CanSave);
        CancelEditCommand = new RelayCommand(CancelEditor);
    }

    private readonly string _nameRequiredError;
    private readonly string _nameTooLongError;
    private readonly string _nameDuplicateError;
    private readonly string _conditionRequiredError;
    private readonly string _comparisonRequiredError;
    private readonly string _calendarCandidateText;
    private readonly string _comparisonValueText;
    private readonly string _removeConditionText;
    private readonly string _previewTitle;
    private readonly string _previewCalendarName;
    private readonly string _summaryJoinAll;
    private readonly string _summaryJoinAny;

    public event EventHandler? StateChanged;

    public string HeadingText { get; }

    public string EmptyText { get; }

    public string AddText { get; }

    public string NewEditorTitle { get; }

    public string EditEditorTitle { get; }

    public string NameText { get; }

    public string EnabledText { get; }

    public string EnabledStateText { get; }

    public string DisabledStateText { get; }

    public string OperatorText { get; }

    public string ConditionsText { get; }

    public string AddConditionText { get; }

    public string ColorText { get; }

    public string CustomColorText { get; }

    public string PreviewText { get; }

    public string SaveText { get; }

    public string CancelText { get; }

    public string EditText { get; }

    public string DeleteText { get; }

    public string MoveUpText { get; }

    public string MoveDownText { get; }

    public string EnableText { get; }

    public string DisableText { get; }

    public int NameMaximumLength => ColorRule.MaximumNameLength;

    public ReadOnlyObservableCollection<ColorRuleListItemViewModel> Rules { get; }

    public ReadOnlyObservableCollection<ColorRuleConditionEditorViewModel> Conditions { get; }

    public IReadOnlyList<ColorRuleOperatorOptionViewModel> OperatorOptions { get; }

    public IReadOnlyList<ColorRuleFieldOptionViewModel> FieldOptions { get; }

    public IReadOnlyList<TextMatchKindOptionViewModel> MatchKindOptions { get; }

    public IReadOnlyList<ProviderOptionViewModel> ProviderOptions { get; }

    public IReadOnlyList<SettingsColorOptionViewModel> ColorOptions { get; }

    public bool IsEditing
    {
        get => _isEditing;
        private set => SetProperty(ref _isEditing, value);
    }

    public bool IsEmpty => Rules.Count == 0;

    public string EditorTitle
    {
        get => _editorTitle;
        private set => SetProperty(ref _editorTitle, value);
    }

    public string RuleName
    {
        get => _ruleName;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _ruleName, value))
            {
                OnDraftChanged();
            }
        }
    }

    public string? RuleNameError
    {
        get => _ruleNameError;
        private set => SetProperty(ref _ruleNameError, value);
    }

    public string? ConditionCountError
    {
        get => _conditionCountError;
        private set => SetProperty(ref _conditionCountError, value);
    }

    public bool IsRuleEnabled
    {
        get => _isRuleEnabled;
        set
        {
            if (SetProperty(ref _isRuleEnabled, value))
            {
                OnDraftChanged();
            }
        }
    }

    public ColorRuleOperatorOptionViewModel SelectedOperatorOption
    {
        get => _selectedOperatorOption;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedOperatorOption, value))
            {
                OnDraftChanged();
            }
        }
    }

    public SettingsColorOptionViewModel? SelectedColorOption
    {
        get => _selectedColorOption;
        set
        {
            if (!SetProperty(ref _selectedColorOption, value) || value is null)
            {
                return;
            }

            SetCurrentColor(value.Color);
        }
    }

    public RgbColor CurrentColor
    {
        get => _currentColor;
        private set => SetProperty(ref _currentColor, value);
    }

    public Brush CurrentColorBrush
    {
        get => _currentColorBrush;
        private set => SetProperty(ref _currentColorBrush, value);
    }

    public string CurrentColorText
    {
        get => _currentColorText;
        private set => SetProperty(ref _currentColorText, value);
    }

    public EventRowViewModel? Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public bool IsDirty => IsEditing && _baseline is not null && !_baseline.Matches(this);

    public bool CanSave =>
        IsEditing
        && RuleNameError is null
        && ConditionCountError is null
        && Conditions.All(condition => condition.ComparisonValueError is null);

    public bool CanApply => CanSave;

    public IRelayCommand AddRuleCommand { get; }

    public IRelayCommand<ColorRuleListItemViewModel> EditRuleCommand { get; }

    public IAsyncRelayCommand<ColorRuleListItemViewModel> MoveRuleUpCommand { get; }

    public IAsyncRelayCommand<ColorRuleListItemViewModel> MoveRuleDownCommand { get; }

    public IAsyncRelayCommand<ColorRuleMoveRequest> MoveRuleCommand { get; }

    public IAsyncRelayCommand<ColorRuleListItemViewModel> ToggleRuleCommand { get; }

    public IAsyncRelayCommand<ColorRuleListItemViewModel> DeleteRuleCommand { get; }

    public IRelayCommand AddConditionCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand CancelEditCommand { get; }

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeCore(settings, []);
    }

    public async Task InitializeAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_cacheStore is null || settings.Accounts.Count == 0)
        {
            InitializeCore(settings, []);
            return;
        }

        var caches = await Task.WhenAll(settings.Accounts.Select(account =>
            _cacheStore.LoadAccountAsync(account.InternalAccountId, cancellationToken)))
            .ConfigureAwait(true);
        InitializeCore(
            settings,
            caches
                .Where(cache => cache is not null)
                .SelectMany(cache => cache!.Calendars)
                .Select(calendar => calendar.Name));
    }

    private void InitializeCore(AppSettings settings, IEnumerable<string> cachedCalendarNames)
    {
        _domainRules = settings.ColorRules.ToArray();
        _calendarNameSuggestions = _syncService.CurrentSnapshot.Events
            .Select(calendarEvent => calendarEvent.CalendarName)
            .Concat(cachedCalendarNames)
            .Select(name => name.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        RebuildRuleList();
        EndEditor();
    }

    public Task PickCustomColorAsync(nint ownerWindowHandle)
    {
        var selected = _colorPicker.PickColor(ownerWindowHandle, CurrentColor);
        if (!selected.HasValue || selected.Value == CurrentColor)
        {
            return Task.CompletedTask;
        }

        _selectedColorOption = ColorOptions.FirstOrDefault(option => option.Color == selected.Value);
        OnPropertyChanged(nameof(SelectedColorOption));
        SetCurrentColor(selected.Value);
        return Task.CompletedTask;
    }

    public Task ApplyAsync(CancellationToken cancellationToken = default) =>
        !IsDirty
            ? Task.CompletedTask
            : SaveCoreAsync(cancellationToken);

    public void Discard() => EndEditor();

    private bool CanBeginListEdit() => !IsEditing && !_isListOperationRunning;

    private bool CanBeginListEdit(ColorRuleListItemViewModel? item) =>
        CanManageRule(item);

    private bool CanManageRule(ColorRuleListItemViewModel? item) =>
        CanBeginListEdit()
        && item is not null
        && item.Index >= 0
        && item.Index < _rules.Count
        && ReferenceEquals(_rules[item.Index], item);

    private bool CanMoveRuleUp(ColorRuleListItemViewModel? item) =>
        CanManageRule(item) && item!.CanMoveUp;

    private bool CanMoveRuleDown(ColorRuleListItemViewModel? item) =>
        CanManageRule(item) && item!.CanMoveDown;

    private bool CanMoveRule(ColorRuleMoveRequest? request) =>
        CanBeginListEdit()
        && request is not null
        && request.FromIndex >= 0
        && request.FromIndex < _domainRules.Count
        && request.ToIndex >= 0
        && request.ToIndex < _domainRules.Count
        && request.FromIndex != request.ToIndex;

    private Task MoveRuleUpAsync(
        ColorRuleListItemViewModel? item,
        CancellationToken cancellationToken) =>
        item is null
            ? Task.CompletedTask
            : MoveRuleAsync(
                new ColorRuleMoveRequest(item.Index, item.Index - 1),
                cancellationToken);

    private Task MoveRuleDownAsync(
        ColorRuleListItemViewModel? item,
        CancellationToken cancellationToken) =>
        item is null
            ? Task.CompletedTask
            : MoveRuleAsync(
                new ColorRuleMoveRequest(item.Index, item.Index + 1),
                cancellationToken);

    private Task MoveRuleAsync(
        ColorRuleMoveRequest? request,
        CancellationToken cancellationToken)
    {
        if (!CanMoveRule(request))
        {
            return Task.CompletedTask;
        }

        return RunListOperationAsync(
            token => UpdateRuleListAsync(rules =>
            {
                var moved = rules[request!.FromIndex];
                rules.RemoveAt(request.FromIndex);
                rules.Insert(request.ToIndex, moved);
            }, token),
            cancellationToken);
    }

    private Task ToggleRuleAsync(
        ColorRuleListItemViewModel? item,
        CancellationToken cancellationToken)
    {
        if (!CanManageRule(item))
        {
            return Task.CompletedTask;
        }

        return RunListOperationAsync(
            token => UpdateRuleListAsync(rules =>
            {
                var current = rules[item!.Index];
                rules[item.Index] = new ColorRule(
                    current.Name,
                    !current.IsEnabled,
                    current.Operator,
                    current.Conditions,
                    current.Color);
            }, token),
            cancellationToken);
    }

    private Task DeleteRuleAsync(
        ColorRuleListItemViewModel? item,
        CancellationToken cancellationToken)
    {
        if (!CanManageRule(item)
            || !_deleteConfirmationService.ConfirmDelete(_domainRules[item!.Index].Name))
        {
            return Task.CompletedTask;
        }

        return RunListOperationAsync(
            token => UpdateRuleListAsync(
                rules => rules.RemoveAt(item.Index),
                token),
            cancellationToken);
    }

    private async Task RunListOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        _isListOperationRunning = true;
        NotifyListCommandsCanExecuteChanged();
        try
        {
            await operation(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _isListOperationRunning = false;
            NotifyListCommandsCanExecuteChanged();
        }
    }

    private async Task UpdateRuleListAsync(
        Action<List<ColorRule>> update,
        CancellationToken cancellationToken)
    {
        var settings = await _settingsService.UpdateAsync(current =>
        {
            var rules = current.ColorRules.ToList();
            update(rules);
            return new AppSettings(
                current.Display,
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                rules,
                current.Notifications);
        }, cancellationToken).ConfigureAwait(true);

        _domainRules = settings.ColorRules.ToArray();
        RebuildRuleList();
    }

    private void BeginAdd()
    {
        if (!CanBeginListEdit())
        {
            return;
        }

        BeginEditor(
            editingIndex: null,
            name: string.Empty,
            isEnabled: true,
            ruleOperator: ColorRuleOperator.All,
            conditions:
            [
                ColorRuleCondition.ForTitle(TextMatchKind.Contains, "placeholder"),
            ],
            color: SettingsColorPalette.Colors[0],
            clearInitialComparisonValue: true);
    }

    private void BeginEdit(ColorRuleListItemViewModel? item)
    {
        if (!CanManageRule(item))
        {
            return;
        }

        var rule = _domainRules[item!.Index];
        BeginEditor(
            item.Index,
            rule.Name,
            rule.IsEnabled,
            rule.Operator,
            rule.Conditions,
            rule.Color,
            clearInitialComparisonValue: false);
    }

    private void BeginEditor(
        int? editingIndex,
        string name,
        bool isEnabled,
        ColorRuleOperator ruleOperator,
        IReadOnlyList<ColorRuleCondition> conditions,
        RgbColor color,
        bool clearInitialComparisonValue)
    {
        _suppressDraftNotifications = true;
        try
        {
            _editingIndex = editingIndex;
            IsEditing = true;
            EditorTitle = editingIndex.HasValue ? EditEditorTitle : NewEditorTitle;
            RuleName = name;
            IsRuleEnabled = isEnabled;
            SelectedOperatorOption = OperatorOptions.Single(option => option.Value == ruleOperator);
            ClearConditions();
            foreach (var condition in conditions)
            {
                var comparisonValue = clearInitialComparisonValue
                    ? string.Empty
                    : condition.ComparisonValue ?? string.Empty;
                AddConditionCore(
                    condition.Field,
                    condition.MatchKind,
                    comparisonValue,
                    condition.Provider ?? ProviderKind.Google);
            }

            SetCurrentColorCore(color);
            _baseline = RuleDraftSnapshot.Capture(this);
        }
        finally
        {
            _suppressDraftNotifications = false;
        }

        RefreshValidationAndState();
    }

    private void AddCondition() => AddConditionCore(
        ColorRuleField.Title,
        TextMatchKind.Contains,
        string.Empty,
        ProviderKind.Google);

    private void AddConditionCore(
        ColorRuleField field,
        TextMatchKind matchKind,
        string comparisonValue,
        ProviderKind provider)
    {
        var condition = new ColorRuleConditionEditorViewModel(
            FieldOptions,
            MatchKindOptions,
            ProviderOptions,
            _calendarNameSuggestions,
            field,
            matchKind,
            comparisonValue,
            provider,
            _calendarCandidateText,
            _comparisonValueText,
            _removeConditionText,
            RemoveCondition);
        condition.DraftChanged += OnConditionDraftChanged;
        _conditions.Add(condition);
        OnDraftChanged();
    }

    private void RemoveCondition(ColorRuleConditionEditorViewModel condition)
    {
        condition.DraftChanged -= OnConditionDraftChanged;
        _ = _conditions.Remove(condition);
        OnDraftChanged();
    }

    private void OnConditionDraftChanged(object? sender, EventArgs eventArgs) => OnDraftChanged();

    private async Task SaveEditorAsync(CancellationToken cancellationToken)
    {
        if (!CanSave)
        {
            return;
        }

        await SaveCoreAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        RefreshValidationAndState();
        if (!CanSave)
        {
            throw new InvalidOperationException("The color rule draft is not valid.");
        }

        var savedRule = new ColorRule(
            RuleName,
            IsRuleEnabled,
            SelectedOperatorOption.Value,
            Conditions.Select(condition => condition.ToDomain()),
            CurrentColor);
        var editingIndex = _editingIndex;
        var settings = await _settingsService.UpdateAsync(current =>
        {
            var rules = current.ColorRules.ToList();
            if (editingIndex.HasValue)
            {
                if (editingIndex.Value < 0 || editingIndex.Value >= rules.Count)
                {
                    throw new InvalidOperationException("The edited color rule no longer exists.");
                }

                rules[editingIndex.Value] = savedRule;
            }
            else
            {
                rules.Add(savedRule);
            }

            return new AppSettings(
                current.Display,
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                rules,
                current.Notifications);
        }, cancellationToken).ConfigureAwait(true);

        _domainRules = settings.ColorRules.ToArray();
        RebuildRuleList();
        EndEditor();
    }

    private void CancelEditor()
    {
        if (IsDirty && !_confirmationService.ConfirmDiscard())
        {
            return;
        }

        EndEditor();
    }

    private void EndEditor()
    {
        _suppressDraftNotifications = true;
        try
        {
            ClearConditions();
            _baseline = null;
            _editingIndex = null;
            IsEditing = false;
            EditorTitle = string.Empty;
            RuleNameError = null;
            ConditionCountError = null;
            Preview = null;
        }
        finally
        {
            _suppressDraftNotifications = false;
        }

        NotifyStateChanged();
    }

    private void ClearConditions()
    {
        foreach (var condition in _conditions)
        {
            condition.DraftChanged -= OnConditionDraftChanged;
        }

        _conditions.Clear();
    }

    private void RebuildRuleList()
    {
        _rules.Clear();
        for (var index = 0; index < _domainRules.Count; index++)
        {
            var rule = _domainRules[index];
            _rules.Add(new ColorRuleListItemViewModel(
                index,
                rule.IsEnabled,
                rule.IsEnabled ? EnabledStateText : DisabledStateText,
                rule.IsEnabled ? DisableText : EnableText,
                rule.Name,
                BuildConditionSummary(rule),
                rule.Color,
                _brushCache.GetSolid(rule.Color),
                CanMoveUp: index > 0,
                CanMoveDown: index < _domainRules.Count - 1));
        }

        OnPropertyChanged(nameof(IsEmpty));
        NotifyListCommandsCanExecuteChanged();
    }

    private string BuildConditionSummary(ColorRule rule)
    {
        var separator = rule.Operator == ColorRuleOperator.All
            ? _summaryJoinAll
            : _summaryJoinAny;
        return string.Join(separator, rule.Conditions.Select(BuildConditionSummary));
    }

    private string BuildConditionSummary(ColorRuleCondition condition) =>
        (condition.Field, condition.MatchKind) switch
        {
            (ColorRuleField.Title, TextMatchKind.Contains) => _textService.Get(
                UiResourceKeys.SettingsColorRulesSummaryTitleContains,
                condition.ComparisonValue!),
            (ColorRuleField.Title, TextMatchKind.Exact) => _textService.Get(
                UiResourceKeys.SettingsColorRulesSummaryTitleExact,
                condition.ComparisonValue!),
            (ColorRuleField.CalendarName, TextMatchKind.Contains) => _textService.Get(
                UiResourceKeys.SettingsColorRulesSummaryCalendarContains,
                condition.ComparisonValue!),
            (ColorRuleField.CalendarName, TextMatchKind.Exact) => _textService.Get(
                UiResourceKeys.SettingsColorRulesSummaryCalendarExact,
                condition.ComparisonValue!),
            (ColorRuleField.Provider, _) => _textService.Get(
                UiResourceKeys.SettingsColorRulesSummaryProvider,
                _textService.Get(condition.Provider == ProviderKind.Google
                    ? UiResourceKeys.SettingsColorRulesSummaryProviderGoogle
                    : UiResourceKeys.SettingsColorRulesSummaryProviderMicrosoft)),
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

    private void SetCurrentColor(RgbColor color)
    {
        if (color == CurrentColor)
        {
            return;
        }

        SetCurrentColorCore(color);
        OnDraftChanged();
    }

    private void SetCurrentColorCore(RgbColor color)
    {
        CurrentColor = color;
        CurrentColorBrush = _brushCache.GetSolid(color);
        CurrentColorText = color.ToString();
        _selectedColorOption = ColorOptions.FirstOrDefault(option => option.Color == color);
        OnPropertyChanged(nameof(SelectedColorOption));
        RebuildPreview();
    }

    private void OnDraftChanged()
    {
        if (_suppressDraftNotifications)
        {
            return;
        }

        RefreshValidationAndState();
    }

    private void RefreshValidationAndState()
    {
        if (!IsEditing)
        {
            NotifyStateChanged();
            return;
        }

        var trimmedName = RuleName.Trim();
        RuleNameError = trimmedName.Length == 0
            ? _nameRequiredError
            : trimmedName.Length > ColorRule.MaximumNameLength
                ? _nameTooLongError
                : _domainRules
                    .Where((_, index) => index != _editingIndex)
                    .Any(rule => rule.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase))
                    ? _nameDuplicateError
                    : null;
        ConditionCountError = Conditions.Count == 0 ? _conditionRequiredError : null;
        foreach (var condition in Conditions)
        {
            condition.SetComparisonValueError(
                condition.IsTextCondition && string.IsNullOrWhiteSpace(condition.ComparisonValue)
                    ? _comparisonRequiredError
                    : null);
        }

        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSave));
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanApply));
        NotifyListCommandsCanExecuteChanged();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyListCommandsCanExecuteChanged()
    {
        AddRuleCommand.NotifyCanExecuteChanged();
        EditRuleCommand.NotifyCanExecuteChanged();
        MoveRuleUpCommand.NotifyCanExecuteChanged();
        MoveRuleDownCommand.NotifyCanExecuteChanged();
        MoveRuleCommand.NotifyCanExecuteChanged();
        ToggleRuleCommand.NotifyCanExecuteChanged();
        DeleteRuleCommand.NotifyCanExecuteChanged();
    }

    private void RebuildPreview()
    {
        if (!IsEditing)
        {
            return;
        }

        var account = new CalendarAccount(
            PreviewAccountId,
            ProviderKind.Google,
            "preview-subject",
            _previewCalendarName,
            "preview@example.invalid",
            enabled: true,
            $"google/{PreviewAccountId:D}");
        var calendarEvent = new CalendarEvent(
            new EventKey(
                ProviderKind.Google,
                PreviewAccountId,
                PreviewCalendarId,
                PreviewEventId),
            _previewTitle,
            new TimedEventTiming(PreviewStartUtc, PreviewEndUtc),
            _previewCalendarName,
            responseStatus: AttendeeResponse.Accepted,
            sourceEventColor: CurrentColor);
        var snapshot = new SyncSnapshot(
            [account],
            [new CalendarSelection(PreviewAccountId, PreviewCalendarId, isVisible: true)],
            [calendarEvent],
            PreviewNowUtc);
        var presentation = _previewPresentationService.BuildSnapshot(
            snapshot,
            new DisplaySettings(displayDays: 1),
            [],
            TimeZoneInfo.Utc);
        var presented = presentation.Events.Single();
        Preview = new EventRowViewModel(
            presented,
            _textService,
            _brushCache,
            NoOpExternalUriLauncher.Instance,
            _ => { });
    }

    private sealed class FixedPreviewTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => PreviewNowUtc;
    }

    private sealed class NoOpExternalUriLauncher : IExternalUriLauncher
    {
        public static NoOpExternalUriLauncher Instance { get; } = new();

        public Task<bool> OpenAsync(
            Uri uri,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    private sealed class DeclineColorRuleDeleteConfirmationService
        : IColorRuleDeleteConfirmationService
    {
        public static DeclineColorRuleDeleteConfirmationService Instance { get; } = new();

        public bool ConfirmDelete(string ruleName) => false;
    }

    private sealed class RuleDraftSnapshot
    {
        private RuleDraftSnapshot(
            string name,
            bool isEnabled,
            ColorRuleOperator ruleOperator,
            IReadOnlyList<ColorRuleConditionDraft> conditions,
            RgbColor color)
        {
            Name = name;
            IsEnabled = isEnabled;
            Operator = ruleOperator;
            Conditions = conditions;
            Color = color;
        }

        private string Name { get; }

        private bool IsEnabled { get; }

        private ColorRuleOperator Operator { get; }

        private IReadOnlyList<ColorRuleConditionDraft> Conditions { get; }

        private RgbColor Color { get; }

        public static RuleDraftSnapshot Capture(ColorRulesSettingsViewModel source) => new(
            source.RuleName,
            source.IsRuleEnabled,
            source.SelectedOperatorOption.Value,
            source.Conditions.Select(condition => condition.Capture()).ToArray(),
            source.CurrentColor);

        public bool Matches(ColorRulesSettingsViewModel source) =>
            Name == source.RuleName
            && IsEnabled == source.IsRuleEnabled
            && Operator == source.SelectedOperatorOption.Value
            && Color == source.CurrentColor
            && Conditions.SequenceEqual(source.Conditions.Select(condition => condition.Capture()));
    }
}
