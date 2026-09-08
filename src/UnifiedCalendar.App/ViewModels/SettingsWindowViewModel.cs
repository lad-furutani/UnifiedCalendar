using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;

namespace UnifiedCalendar.App.ViewModels;

public sealed record SettingsCategoryViewModel(
    string Key,
    string Title,
    object Content);

public sealed record SettingsPlaceholderViewModel(string Text);

public sealed partial class SettingsWindowViewModel : ObservableObject, IDisposable
{
    private readonly IPendingSettingsChanges _pendingChanges;
    private readonly IApplicationSettingsService _settingsService;
    private readonly IStartupRegistrationService _startupRegistration;
    private readonly ISettingsResetConfirmationService _resetConfirmationService;
    private bool _disposed;

    public SettingsWindowViewModel(
        IPendingSettingsChanges pendingChanges,
        IUiTextService textService,
        IApplicationSettingsService settingsService,
        ICalendarSyncService syncService,
        IColorPickerService colorPicker,
        BrushCache brushCache,
        IStartupRegistrationService? startupRegistration = null,
        IApplicationInfoProvider? applicationInfoProvider = null,
        IAppLocalPathLauncher? pathLauncher = null,
        ISettingsResetConfirmationService? resetConfirmationService = null,
        ILocalTimeZoneProvider? localTimeZoneProvider = null,
        InternalRefreshSignal? refreshSignal = null,
        ColorRulesSettingsViewModel? colorRules = null,
        AccountSettingsViewModel? accounts = null,
        CalendarSelectionSettingsViewModel? calendarSelection = null)
    {
        _pendingChanges = pendingChanges ?? throw new ArgumentNullException(nameof(pendingChanges));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        ArgumentNullException.ThrowIfNull(syncService);
        _startupRegistration = startupRegistration ?? new NoOpStartupRegistrationService();
        _resetConfirmationService = resetConfirmationService ?? new DeclineResetConfirmationService();
        ArgumentNullException.ThrowIfNull(textService);
        Display = new DisplaySettingsViewModel(
            settingsService,
            syncService,
            colorPicker,
            brushCache,
            textService);
        Update = new UpdateSettingsViewModel(settingsService, textService);
        General = new GeneralSettingsViewModel(
            settingsService,
            _startupRegistration,
            applicationInfoProvider ?? new AssemblyApplicationInfoProvider(),
            pathLauncher ?? new NoOpAppLocalPathLauncher(),
            textService,
            ResetToDefaultsAsync,
            localTimeZoneProvider,
            refreshSignal);
        ColorRules = colorRules
            ?? pendingChanges as ColorRulesSettingsViewModel
            ?? new ColorRulesSettingsViewModel(
                settingsService,
                syncService,
                colorPicker,
                brushCache,
                textService,
                AllowDiscardConfirmationService.Instance);
        Accounts = accounts;
        CalendarSelection = calendarSelection;

        Title = textService.Get(UiResourceKeys.SettingsTitle);
        OkText = textService.Get(UiResourceKeys.SettingsOk);
        ApplyText = textService.Get(UiResourceKeys.SettingsApply);
        CancelText = textService.Get(UiResourceKeys.SettingsCancel);
        var placeholder = textService.Get(UiResourceKeys.SettingsPlaceholder);
        Categories = new ReadOnlyObservableCollection<SettingsCategoryViewModel>(
            new ObservableCollection<SettingsCategoryViewModel>(
            [
                new(
                    "account",
                    textService.Get(UiResourceKeys.SettingsAccount),
                    accounts is null ? new SettingsPlaceholderViewModel(placeholder) : accounts),
                new(
                    "calendar-selection",
                    textService.Get(UiResourceKeys.SettingsCalendarSelection),
                    calendarSelection is null
                        ? new SettingsPlaceholderViewModel(placeholder)
                        : calendarSelection),
                new("display", textService.Get(UiResourceKeys.SettingsDisplay), Display),
                new("color-rules", textService.Get(UiResourceKeys.SettingsColorRules), ColorRules),
                new("update", textService.Get(UiResourceKeys.SettingsUpdate), Update),
                new("general", textService.Get(UiResourceKeys.SettingsGeneral), General),
            ]));
        SelectedCategory = Categories[0];

        ApplyCommand = new AsyncRelayCommand(ApplyAsync, CanApplyPendingChanges);
        OkCommand = new AsyncRelayCommand(OkAsync, CanCloseWithApply);
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke(this, EventArgs.Empty));
        _pendingChanges.StateChanged += OnPendingChangesStateChanged;
        if (Accounts is not null && CalendarSelection is not null)
        {
            Accounts.AccountRegistered += OnAccountRegistered;
            CalendarSelection.BackToAccountsRequested += OnBackToAccountsRequested;
        }
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? CancelRequested;

    public ReadOnlyObservableCollection<SettingsCategoryViewModel> Categories { get; }

    public string Title { get; }

    public string OkText { get; }

    public string ApplyText { get; }

    public string CancelText { get; }

    public DisplaySettingsViewModel Display { get; }

    public UpdateSettingsViewModel Update { get; }

    public GeneralSettingsViewModel General { get; }

    public ColorRulesSettingsViewModel ColorRules { get; }

    public AccountSettingsViewModel? Accounts { get; }

    public CalendarSelectionSettingsViewModel? CalendarSelection { get; }

    public bool IsDirty => _pendingChanges.IsDirty;

    public IAsyncRelayCommand OkCommand { get; }

    public IAsyncRelayCommand ApplyCommand { get; }

    public IRelayCommand CancelCommand { get; }

    [ObservableProperty]
    private SettingsCategoryViewModel? _selectedCategory;

    public void Discard() => _pendingChanges.Discard();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(true);
        Display.Initialize(settings);
        Update.Initialize(settings);
        General.Initialize(settings);
        Accounts?.Initialize(settings);
        CalendarSelection?.Initialize(settings);
        await ColorRules.InitializeAsync(settings, cancellationToken).ConfigureAwait(true);
    }

    public async Task ApplyPendingChangesAsync(CancellationToken cancellationToken = default)
    {
        await WaitForImmediateSettingsAsync().ConfigureAwait(true);
        await _pendingChanges.ApplyAsync(cancellationToken).ConfigureAwait(true);
    }

    public Task FlushImmediateSettingsAsync() => WaitForImmediateSettingsAsync();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingChanges.StateChanged -= OnPendingChangesStateChanged;
        if (Accounts is not null && CalendarSelection is not null)
        {
            Accounts.AccountRegistered -= OnAccountRegistered;
            CalendarSelection.BackToAccountsRequested -= OnBackToAccountsRequested;
        }

        General.Dispose();
        Accounts?.Dispose();
        CalendarSelection?.Dispose();
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        await ApplyPendingChangesAsync(cancellationToken).ConfigureAwait(true);
        NotifyPendingStateChanged();
    }

    private async Task OkAsync(CancellationToken cancellationToken)
    {
        await WaitForImmediateSettingsAsync().ConfigureAwait(true);
        if (IsDirty)
        {
            await _pendingChanges.ApplyAsync(cancellationToken).ConfigureAwait(true);
            NotifyPendingStateChanged();
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPendingChangesStateChanged(object? sender, EventArgs eventArgs) =>
        NotifyPendingStateChanged();

    private void NotifyPendingStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        ApplyCommand.NotifyCanExecuteChanged();
        OkCommand.NotifyCanExecuteChanged();
    }

    private async Task ResetToDefaultsAsync(CancellationToken cancellationToken)
    {
        if (!_resetConfirmationService.ConfirmReset())
        {
            return;
        }

        await WaitForImmediateSettingsAsync().ConfigureAwait(true);
        var defaults = AppSettings.CreateDefault();
        var settings = await _settingsService.UpdateAsync(current => new AppSettings(
            defaults.Display,
            defaults.Sync,
            defaults.General,
            current.Windows,
            current.Accounts,
            []), cancellationToken).ConfigureAwait(true);
        try
        {
            _startupRegistration.SetEnabled(settings.General.StartWithWindows);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "StartupRegistrationFailed {Stage} {ErrorCategory}",
                "SettingsReset",
                exception.GetType().Name);
        }
        Display.Initialize(settings);
        Update.Initialize(settings);
        General.Initialize(settings);
        CalendarSelection?.Initialize(settings);
        await ColorRules.InitializeAsync(settings, cancellationToken).ConfigureAwait(true);
    }

    private bool CanApplyPendingChanges() =>
        IsDirty
        && (_pendingChanges is not IValidatedPendingSettingsChanges validated || validated.CanApply);

    private bool CanCloseWithApply() =>
        !IsDirty
        || _pendingChanges is not IValidatedPendingSettingsChanges validated
        || validated.CanApply;

    private Task WaitForImmediateSettingsAsync() => Task.WhenAll(
        Display.WaitForPendingUpdatesAsync(),
        Update.WaitForPendingUpdatesAsync(),
        General.WaitForPendingUpdatesAsync());

    partial void OnSelectedCategoryChanged(SettingsCategoryViewModel? value)
    {
        if (value?.Content is CalendarSelectionSettingsViewModel calendarSelection)
        {
            Observe(calendarSelection.ActivateAsync(), "CalendarSelectionActivation");
            return;
        }

        CalendarSelection?.LeavePostRegistrationFlow();
    }

    private void OnAccountRegistered(object? sender, AccountRegisteredEventArgs eventArgs)
    {
        if (_disposed || CalendarSelection is null)
        {
            return;
        }

        CalendarSelection.ShowRegisteredAccount(eventArgs.Result);
        SelectedCategory = Categories.Single(category => category.Key == "calendar-selection");
    }

    private void OnBackToAccountsRequested(object? sender, EventArgs eventArgs)
    {
        if (!_disposed)
        {
            SelectedCategory = Categories.Single(category => category.Key == "account");
        }
    }

    private static async void Observe(Task task, string stage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsProjectionFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }

    private sealed class NoOpStartupRegistrationService : IStartupRegistrationService
    {
        public bool IsEnabled => false;

        public void SetEnabled(bool enabled)
        {
        }
    }

    private sealed class NoOpAppLocalPathLauncher : IAppLocalPathLauncher
    {
        public Task OpenAsync(
            AppLocalPathTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class DeclineResetConfirmationService : ISettingsResetConfirmationService
    {
        public bool ConfirmReset() => false;
    }

    private sealed class AllowDiscardConfirmationService : ISettingsConfirmationService
    {
        public static AllowDiscardConfirmationService Instance { get; } = new();

        public bool ConfirmDiscard() => true;

        public SettingsExitDecision ConfirmApplicationExit() => SettingsExitDecision.Discard;
    }
}
