using System.Windows;
using System.Windows.Interop;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.App.Shell;

public sealed class SettingsWindowPlacementService
{
    private readonly IApplicationSettingsService _settingsService;
    private readonly IMonitorProvider _monitorProvider;

    public SettingsWindowPlacementService(
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
        return settings.Windows.Settings;
    }

    public DipRect CalculateRestoreBounds(WindowPlacement? placement, Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var monitors = _monitorProvider.GetMonitors();
        var fallbackMonitor = GetFallbackMonitor(monitors, owner);
        var anchor = owner.IsVisible ? GetWindowBounds(owner) : fallbackMonitor.WorkAreaDip;
        var fallback = CenterOn(anchor);
        return WindowPlacementCalculator.RestoreWindow(
            placement,
            monitors,
            fallbackMonitor.DeviceName,
            fallback,
            LayoutMetrics.MinimumSettingsWidth,
            LayoutMetrics.MinimumSettingsHeight);
    }

    public DipRect CalculateFinalRestoreBounds(
        WindowPlacement? placement,
        nint settingsWindowHandle,
        Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var current = _monitorProvider.GetMonitorForWindow(settingsWindowHandle);
        var monitors = _monitorProvider.GetMonitors()
            .Select(monitor => monitor.DeviceName.Equals(
                current.DeviceName,
                StringComparison.OrdinalIgnoreCase)
                ? current
                : monitor)
            .ToArray();
        var fallbackMonitor = owner.IsVisible
            ? GetFallbackMonitor(monitors, owner)
            : current;
        var anchor = owner.IsVisible ? GetWindowBounds(owner) : fallbackMonitor.WorkAreaDip;
        return WindowPlacementCalculator.RestoreWindow(
            placement,
            monitors,
            fallbackMonitor.DeviceName,
            CenterOn(anchor),
            LayoutMetrics.MinimumSettingsWidth,
            LayoutMetrics.MinimumSettingsHeight);
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
                new WindowPreferences(current.Windows.Main, placement),
                current.Accounts,
                current.ColorRules),
            cancellationToken).ConfigureAwait(false);
    }

    private DisplayMonitor GetFallbackMonitor(
        IReadOnlyCollection<DisplayMonitor> monitors,
        Window owner)
    {
        if (owner.IsVisible)
        {
            var handle = new WindowInteropHelper(owner).Handle;
            if (handle != 0)
            {
                return _monitorProvider.GetMonitorForWindow(handle);
            }
        }

        return monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors.First();
    }

    private static DipRect GetWindowBounds(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new DipRect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : new DipRect(
                window.RestoreBounds.Left,
                window.RestoreBounds.Top,
                window.RestoreBounds.Width,
                window.RestoreBounds.Height);
        if (bounds.Width > 0d && bounds.Height > 0d
            && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top))
        {
            return bounds;
        }

        return new DipRect(window.Left, window.Top, window.Width, window.Height);
    }

    private static DipRect CenterOn(DipRect anchor) => new(
        anchor.Left + ((anchor.Width - LayoutMetrics.InitialSettingsWidth) / 2d),
        anchor.Top + ((anchor.Height - LayoutMetrics.InitialSettingsHeight) / 2d),
        LayoutMetrics.InitialSettingsWidth,
        LayoutMetrics.InitialSettingsHeight);
}
