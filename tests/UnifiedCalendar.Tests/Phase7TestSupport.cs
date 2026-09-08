using UnifiedCalendar.App.Shell;

namespace UnifiedCalendar.Tests;

internal sealed class TestMonitorProvider : IMonitorProvider
{
    public IReadOnlyList<DisplayMonitor> Monitors { get; set; } =
    [
        new DisplayMonitor(
            @"\\.\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            96d,
            96d,
            true),
    ];

    public IReadOnlyList<DisplayMonitor> GetMonitors() => Monitors;

    public DisplayMonitor GetMonitorForWindow(nint windowHandle) => Monitors[0];
}

internal sealed class PermissiveForegroundPermissionService : IForegroundPermissionService
{
    public bool AllowSetForegroundWindow(int processId) => processId > 0;
}
