using System.Windows;
using System.Windows.Interop;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.App.Shell;

public sealed class MainWindowPlacementService
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly IMonitorProvider _monitorProvider;

    /// <summary>
    /// Creates a test-only placement service from a settings store.
    /// </summary>
    /// <remarks>
    /// This bypasses application setting update serialization and must not be used in production.
    /// </remarks>
    public MainWindowPlacementService(
        ISettingsStore settingsStore,
        IMonitorProvider monitorProvider)
        : this(new ApplicationSettingsService(settingsStore), monitorProvider)
    {
    }

    public MainWindowPlacementService(
        IApplicationSettingsService settingsService,
        IMonitorProvider monitorProvider)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _monitorProvider = monitorProvider ?? throw new ArgumentNullException(nameof(monitorProvider));
    }

    public async Task<WindowPlacement?> LoadSavedPlacementAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        return settings.Windows.Main;
    }

    public DipRect CalculateRestoreBounds(WindowPlacement? placement) =>
        WindowPlacementCalculator.RestoreMainWindow(placement, _monitorProvider.GetMonitors());

    public DipRect CalculateFinalRestoreBounds(WindowPlacement? placement, nint windowHandle)
    {
        var current = _monitorProvider.GetMonitorForWindow(windowHandle);
        var monitors = _monitorProvider.GetMonitors()
            .Select(monitor => monitor.DeviceName.Equals(
                current.DeviceName,
                StringComparison.OrdinalIgnoreCase)
                ? current
                : monitor)
            .ToArray();
        return WindowPlacementCalculator.RestoreMainWindow(placement, monitors);
    }

    public WindowPlacement Capture(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            throw new InvalidOperationException("The window source has not been initialized.");
        }

        var monitor = _monitorProvider.GetMonitorForWindow(handle);
        var bounds = window.RestoreBounds;
        return new WindowPlacement(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            monitor.DeviceName,
            monitor.DpiX,
            monitor.DpiY);
    }

    public async Task SaveAsync(
        WindowPlacement placement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _ = await _settingsService.UpdateAsync(
            current => new AppSettings(
                current.Display,
                current.Sync,
                current.General,
                new WindowPreferences(placement, current.Windows.Settings),
                current.Accounts,
                current.ColorRules),
            cancellationToken).ConfigureAwait(false);
    }
}
