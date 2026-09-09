using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;

namespace UnifiedCalendar.App.ViewModels;

public sealed record SettingsColorOptionViewModel(
    string Name,
    RgbColor Color,
    Brush Brush);

public sealed record SyncIntervalOptionViewModel(int Minutes, string Label);

public sealed class DisplaySettingsViewModel : ObservableObject
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly ICalendarSyncService _syncService;
    private readonly IColorPickerService _colorPicker;
    private readonly BrushCache _brushCache;
    private readonly object _updateGate = new();
    private Task _pendingUpdate = Task.CompletedTask;
    private bool _initialized;
    private int _days = 7;
    private int _fontSizeDip = 14;
    private DisplayDensity _density = DisplayDensity.Standard;
    private RgbColor _currentColor = DisplaySettings.InitialDefaultEventColor;
    private SettingsColorOptionViewModel? _selectedColorOption;

    public DisplaySettingsViewModel(
        IApplicationSettingsService settingsService,
        ICalendarSyncService syncService,
        IColorPickerService colorPicker,
        BrushCache brushCache,
        IUiTextService textService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _colorPicker = colorPicker ?? throw new ArgumentNullException(nameof(colorPicker));
        _brushCache = brushCache ?? throw new ArgumentNullException(nameof(brushCache));
        ArgumentNullException.ThrowIfNull(textService);

        DaysText = textService.Get(UiResourceKeys.SettingsDisplayDays);
        FontSizeText = textService.Get(UiResourceKeys.SettingsDisplayFontSize);
        DensityText = textService.Get(UiResourceKeys.SettingsDisplayDensity);
        CompactText = textService.Get(UiResourceKeys.SettingsDisplayDensityCompact);
        StandardText = textService.Get(UiResourceKeys.SettingsDisplayDensityStandard);
        ComfortableText = textService.Get(UiResourceKeys.SettingsDisplayDensityComfortable);
        DefaultColorText = textService.Get(UiResourceKeys.SettingsDisplayDefaultColor);
        CustomColorText = textService.Get(UiResourceKeys.SettingsDisplayCustomColor);
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
        CurrentColorBrush = brushCache.GetSolid(_currentColor);
        CurrentColorText = _currentColor.ToString();
    }

    public string DaysText { get; }

    public string FontSizeText { get; }

    public string DensityText { get; }

    public string CompactText { get; }

    public string StandardText { get; }

    public string ComfortableText { get; }

    public string DefaultColorText { get; }

    public string CustomColorText { get; }

    public IReadOnlyList<SettingsColorOptionViewModel> ColorOptions { get; }

    public int Days
    {
        get => _days;
        set
        {
            if (value is < DisplayPreferences.MinimumDays or > DisplayPreferences.MaximumDays)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _days, value) && _initialized)
            {
                Observe(EnqueueUpdate(() => SaveDisplayAsync(
                    display => new DisplayPreferences(
                        value,
                        display.FontSizeDip,
                        display.Density,
                        display.DefaultEventColor),
                    requestSyncForExpansion: true)), "DisplayDays");
            }
        }
    }

    public int FontSizeDip
    {
        get => _fontSizeDip;
        set
        {
            if (value is < DisplayPreferences.MinimumFontSizeDip or > DisplayPreferences.MaximumFontSizeDip)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _fontSizeDip, value) && _initialized)
            {
                Observe(EnqueueUpdate(() => SaveDisplayAsync(
                    display => new DisplayPreferences(
                        display.Days,
                        value,
                        display.Density,
                        display.DefaultEventColor))), "DisplayFontSize");
            }
        }
    }

    public DisplayDensity Density
    {
        get => _density;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (!SetProperty(ref _density, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsCompact));
            OnPropertyChanged(nameof(IsStandard));
            OnPropertyChanged(nameof(IsComfortable));
            if (_initialized)
            {
                Observe(EnqueueUpdate(() => SaveDisplayAsync(
                    display => new DisplayPreferences(
                        display.Days,
                        display.FontSizeDip,
                        value,
                        display.DefaultEventColor))), "DisplayDensity");
            }
        }
    }

    public bool IsCompact
    {
        get => Density == DisplayDensity.Compact;
        set
        {
            if (value)
            {
                Density = DisplayDensity.Compact;
            }
        }
    }

    public bool IsStandard
    {
        get => Density == DisplayDensity.Standard;
        set
        {
            if (value)
            {
                Density = DisplayDensity.Standard;
            }
        }
    }

    public bool IsComfortable
    {
        get => Density == DisplayDensity.Comfortable;
        set
        {
            if (value)
            {
                Density = DisplayDensity.Comfortable;
            }
        }
    }

    public SettingsColorOptionViewModel? SelectedColorOption
    {
        get => _selectedColorOption;
        set
        {
            if (!SetProperty(ref _selectedColorOption, value)
                || value is null
                || !_initialized)
            {
                return;
            }

            SetCurrentColor(value.Color);
            Observe(EnqueueUpdate(() => SaveDisplayAsync(
                display => new DisplayPreferences(
                    display.Days,
                    display.FontSizeDip,
                    display.Density,
                    value.Color))), "DisplayDefaultColor");
        }
    }

    public RgbColor CurrentColor
    {
        get => _currentColor;
        private set => SetProperty(ref _currentColor, value);
    }

    public Brush CurrentColorBrush { get; private set; }

    public string CurrentColorText { get; private set; }

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _initialized = false;
        Days = settings.Display.Days;
        FontSizeDip = settings.Display.FontSizeDip;
        Density = settings.Display.Density;
        SetCurrentColor(settings.Display.DefaultEventColor);
        SelectedColorOption = ColorOptions.FirstOrDefault(option => option.Color == CurrentColor);
        _initialized = true;
    }

    public async Task PickCustomColorAsync(nint ownerWindowHandle)
    {
        var selected = _colorPicker.PickColor(ownerWindowHandle, CurrentColor);
        if (!selected.HasValue || selected.Value == CurrentColor)
        {
            return;
        }

        SetCurrentColor(selected.Value);
        _initialized = false;
        SelectedColorOption = ColorOptions.FirstOrDefault(option => option.Color == selected.Value);
        _initialized = true;
        await EnqueueUpdate(() => SaveDisplayAsync(
            display => new DisplayPreferences(
                display.Days,
                display.FontSizeDip,
                display.Density,
                selected.Value))).ConfigureAwait(false);
    }

    public Task WaitForPendingUpdatesAsync()
    {
        lock (_updateGate)
        {
            return _pendingUpdate;
        }
    }

    private async Task SaveDisplayAsync(
        Func<DisplayPreferences, DisplayPreferences> update,
        bool requestSyncForExpansion = false)
    {
        var expanded = false;
        _ = await _settingsService.UpdateAsync(current =>
        {
            var display = update(current.Display);
            expanded = requestSyncForExpansion && display.Days > current.Display.Days;
            return new AppSettings(
                display,
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules,
                current.Notifications);
        }).ConfigureAwait(false);
        if (expanded)
        {
            _ = await _syncService.RequestSyncAsync(SyncTriggerReason.Manual).ConfigureAwait(false);
        }
    }

    private Task EnqueueUpdate(Func<Task> update)
    {
        lock (_updateGate)
        {
            _pendingUpdate = _pendingUpdate
                .ContinueWith(
                    _ => update(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            return _pendingUpdate;
        }
    }

    private void SetCurrentColor(RgbColor color)
    {
        CurrentColor = color;
        CurrentColorBrush = _brushCache.GetSolid(color);
        CurrentColorText = color.ToString();
        OnPropertyChanged(nameof(CurrentColorBrush));
        OnPropertyChanged(nameof(CurrentColorText));
    }

    private static async void Observe(Task task, string stage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsUpdateFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }
}

public sealed class UpdateSettingsViewModel : ObservableObject
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly object _updateGate = new();
    private Task _pendingUpdate = Task.CompletedTask;
    private bool _initialized;
    private SyncIntervalOptionViewModel? _selectedInterval;

    public UpdateSettingsViewModel(
        IApplicationSettingsService settingsService,
        IUiTextService textService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        ArgumentNullException.ThrowIfNull(textService);
        IntervalText = textService.Get(UiResourceKeys.SettingsUpdateInterval);
        IntervalOptions = new[] { 1, 5, 10, 15, 30, 60 }
            .Select(minutes => new SyncIntervalOptionViewModel(
                minutes,
                textService.Get(UiResourceKeys.SettingsUpdateIntervalOption, minutes)))
            .ToArray();
    }

    public string IntervalText { get; }

    public IReadOnlyList<SyncIntervalOptionViewModel> IntervalOptions { get; }

    public SyncIntervalOptionViewModel? SelectedInterval
    {
        get => _selectedInterval;
        set
        {
            if (!SetProperty(ref _selectedInterval, value)
                || value is null
                || !_initialized)
            {
                return;
            }

            Observe(EnqueueUpdate(() => SaveIntervalAsync(value.Minutes)), "SyncInterval");
        }
    }

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _initialized = false;
        SelectedInterval = IntervalOptions.Single(option =>
            option.Minutes == settings.Sync.IntervalMinutes);
        _initialized = true;
    }

    public Task WaitForPendingUpdatesAsync()
    {
        lock (_updateGate)
        {
            return _pendingUpdate;
        }
    }

    private Task SaveIntervalAsync(int intervalMinutes) => _settingsService.UpdateAsync(
        current => new AppSettings(
            current.Display,
            new SyncPreferences(intervalMinutes),
            current.General,
            current.Windows,
            current.Accounts,
            current.ColorRules,
            current.Notifications));

    private Task EnqueueUpdate(Func<Task> update)
    {
        lock (_updateGate)
        {
            _pendingUpdate = _pendingUpdate
                .ContinueWith(
                    _ => update(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            return _pendingUpdate;
        }
    }

    private static async void Observe(Task task, string stage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsUpdateFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }
}

public sealed class NotificationSettingsViewModel : ObservableObject
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly object _updateGate = new();
    private Task _pendingUpdate = Task.CompletedTask;
    private bool _initialized;
    private bool _enabled = true;
    private int _leadMinutes = 5;

    public NotificationSettingsViewModel(
        IApplicationSettingsService settingsService,
        IUiTextService textService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        ArgumentNullException.ThrowIfNull(textService);
        EnabledText = textService.Get(UiResourceKeys.SettingsNotificationsEnabled);
        LeadMinutesText = textService.Get(UiResourceKeys.SettingsNotificationsLeadMinutes);
        LeadMinutesUnitText = textService.Get(UiResourceKeys.SettingsNotificationsLeadMinutesUnit);
    }

    public string EnabledText { get; }

    public string LeadMinutesText { get; }

    public string LeadMinutesUnitText { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsLeadMinutesEnabled));
            if (_initialized)
            {
                Observe(
                    EnqueueUpdate(() => SaveAsync(new NotificationPreferences(value, LeadMinutes))),
                    "NotificationEnabled");
            }
        }
    }

    public int LeadMinutes
    {
        get => _leadMinutes;
        set
        {
            if (value is < NotificationPreferences.MinimumLeadMinutes
                or > NotificationPreferences.MaximumLeadMinutes)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (SetProperty(ref _leadMinutes, value) && _initialized)
            {
                Observe(
                    EnqueueUpdate(() => SaveAsync(new NotificationPreferences(Enabled, value))),
                    "NotificationLeadMinutes");
            }
        }
    }

    public bool IsLeadMinutesEnabled => Enabled;

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _initialized = false;
        Enabled = settings.Notifications.Enabled;
        LeadMinutes = settings.Notifications.LeadMinutes;
        _initialized = true;
    }

    public Task WaitForPendingUpdatesAsync()
    {
        lock (_updateGate)
        {
            return _pendingUpdate;
        }
    }

    private Task SaveAsync(NotificationPreferences notifications) =>
        _settingsService.UpdateAsync(current => new AppSettings(
            current.Display,
            current.Sync,
            current.General,
            current.Windows,
            current.Accounts,
            current.ColorRules,
            notifications));

    private Task EnqueueUpdate(Func<Task> update)
    {
        lock (_updateGate)
        {
            _pendingUpdate = _pendingUpdate
                .ContinueWith(
                    _ => update(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            return _pendingUpdate;
        }
    }

    private static async void Observe(Task task, string stage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsUpdateFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }
}

public sealed class GeneralSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly IStartupRegistrationService _startupRegistration;
    private readonly IAppLocalPathLauncher _pathLauncher;
    private readonly IApplicationInfoProvider _applicationInfoProvider;
    private readonly IUiTextService _textService;
    private readonly ILocalTimeZoneProvider _localTimeZoneProvider;
    private readonly InternalRefreshSignal? _refreshSignal;
    private readonly object _updateGate = new();
    private Task _pendingUpdate = Task.CompletedTask;
    private bool _initialized;
    private bool _startWithWindows = true;
    private string _buildTimeText = string.Empty;
    private bool _disposed;

    public GeneralSettingsViewModel(
        IApplicationSettingsService settingsService,
        IStartupRegistrationService startupRegistration,
        IApplicationInfoProvider applicationInfoProvider,
        IAppLocalPathLauncher pathLauncher,
        IUiTextService textService,
        Func<CancellationToken, Task> resetToDefaultsAsync,
        ILocalTimeZoneProvider? localTimeZoneProvider = null,
        InternalRefreshSignal? refreshSignal = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _startupRegistration = startupRegistration
            ?? throw new ArgumentNullException(nameof(startupRegistration));
        _applicationInfoProvider = applicationInfoProvider
            ?? throw new ArgumentNullException(nameof(applicationInfoProvider));
        _pathLauncher = pathLauncher ?? throw new ArgumentNullException(nameof(pathLauncher));
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        ArgumentNullException.ThrowIfNull(resetToDefaultsAsync);
        _localTimeZoneProvider = localTimeZoneProvider ?? new SystemLocalTimeZoneProvider();
        _refreshSignal = refreshSignal;

        var applicationInfo = _applicationInfoProvider.Get();
        StartWithWindowsText = textService.Get(UiResourceKeys.SettingsGeneralStartWithWindows);
        ApplicationInfoHeadingText = textService.Get(UiResourceKeys.SettingsGeneralApplicationInfo);
        ApplicationNameText = textService.Get(
            UiResourceKeys.SettingsGeneralApplicationName,
            applicationInfo.ProductName);
        VersionText = textService.Get(
            UiResourceKeys.SettingsGeneralVersion,
            applicationInfo.Version);
        UpdateBuildTimeText(applicationInfo);
        LocalFilesHeadingText = textService.Get(UiResourceKeys.SettingsGeneralLocalFiles);
        OpenRootDirectoryText = textService.Get(UiResourceKeys.SettingsGeneralOpenRootDirectory);
        OpenSettingsFileText = textService.Get(UiResourceKeys.SettingsGeneralOpenSettingsFile);
        OpenLogsDirectoryText = textService.Get(UiResourceKeys.SettingsGeneralOpenLogsDirectory);
        OpenLatestLogFileText = textService.Get(UiResourceKeys.SettingsGeneralOpenLatestLogFile);
        ResetToDefaultsText = textService.Get(UiResourceKeys.SettingsGeneralResetToDefaults);

        OpenRootDirectoryCommand = CreateOpenCommand(AppLocalPathTarget.RootDirectory);
        OpenSettingsFileCommand = CreateOpenCommand(AppLocalPathTarget.SettingsFile);
        OpenLogsDirectoryCommand = CreateOpenCommand(AppLocalPathTarget.LogsDirectory);
        OpenLatestLogFileCommand = CreateOpenCommand(AppLocalPathTarget.LatestLogFile);
        ResetToDefaultsCommand = new AsyncRelayCommand(resetToDefaultsAsync);
        if (_refreshSignal is not null)
        {
            _refreshSignal.RefreshRequested += OnInternalRefreshRequested;
        }
    }

    public string StartWithWindowsText { get; }

    public string ApplicationInfoHeadingText { get; }

    public string ApplicationNameText { get; }

    public string VersionText { get; }

    public string BuildTimeText
    {
        get => _buildTimeText;
        private set => SetProperty(ref _buildTimeText, value);
    }

    public string LocalFilesHeadingText { get; }

    public string OpenRootDirectoryText { get; }

    public string OpenSettingsFileText { get; }

    public string OpenLogsDirectoryText { get; }

    public string OpenLatestLogFileText { get; }

    public string ResetToDefaultsText { get; }

    public IAsyncRelayCommand OpenRootDirectoryCommand { get; }

    public IAsyncRelayCommand OpenSettingsFileCommand { get; }

    public IAsyncRelayCommand OpenLogsDirectoryCommand { get; }

    public IAsyncRelayCommand OpenLatestLogFileCommand { get; }

    public IAsyncRelayCommand ResetToDefaultsCommand { get; }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value) && _initialized)
            {
                Observe(EnqueueUpdate(() => SaveStartWithWindowsAsync(value)), "StartWithWindows");
            }
        }
    }

    public void Initialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _initialized = false;
        StartWithWindows = settings.General.StartWithWindows;
        _initialized = true;
    }

    public Task WaitForPendingUpdatesAsync()
    {
        lock (_updateGate)
        {
            return _pendingUpdate;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_refreshSignal is not null)
        {
            _refreshSignal.RefreshRequested -= OnInternalRefreshRequested;
        }
    }

    private IAsyncRelayCommand CreateOpenCommand(AppLocalPathTarget target) =>
        new AsyncRelayCommand(cancellationToken => _pathLauncher.OpenAsync(target, cancellationToken));

    private async Task SaveStartWithWindowsAsync(bool startWithWindows)
    {
        _ = await _settingsService.UpdateAsync(current => new AppSettings(
            current.Display,
            current.Sync,
            new GeneralPreferences(startWithWindows),
            current.Windows,
            current.Accounts,
            current.ColorRules,
            current.Notifications)).ConfigureAwait(false);
        _startupRegistration.SetEnabled(startWithWindows);
    }

    private void OnInternalRefreshRequested(
        object? sender,
        InternalRefreshRequestedEventArgs eventArgs)
    {
        if (eventArgs.Reason == InternalRefreshReason.TimeZoneChanged)
        {
            UpdateBuildTimeText(_applicationInfoProvider.Get());
        }
    }

    private void UpdateBuildTimeText(ApplicationInfo applicationInfo)
    {
        var localBuildTime = TimeZoneInfo.ConvertTime(
            applicationInfo.BuildTimeUtc,
            _localTimeZoneProvider.GetCurrent());
        BuildTimeText = _textService.Get(
            UiResourceKeys.SettingsGeneralBuildTime,
            localBuildTime);
    }

    private Task EnqueueUpdate(Func<Task> update)
    {
        lock (_updateGate)
        {
            _pendingUpdate = _pendingUpdate
                .ContinueWith(
                    _ => update(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
            return _pendingUpdate;
        }
    }

    private static async void Observe(Task task, string stage)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsUpdateFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }

}
