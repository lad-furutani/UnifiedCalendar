using System.Runtime.InteropServices;
using UnifiedCalendar.App.Presentation;

namespace UnifiedCalendar.App.Shell;

public interface IMonitorProvider
{
    IReadOnlyList<DisplayMonitor> GetMonitors();

    DisplayMonitor GetMonitorForWindow(nint windowHandle);
}

public sealed class WindowsMonitorProvider : IMonitorProvider
{
    private const uint MonitorInfoPrimary = 0x00000001;

    public IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        bool AddMonitor(
            nint monitor,
            nint deviceContext,
            ref NativeMethods.Rect monitorRectangle,
            nint data)
        {
            monitors.Add(ReadMonitor(monitor));
            return true;
        }

        if (!NativeMethods.EnumDisplayMonitors(0, 0, AddMonitor, 0))
        {
            throw new InvalidOperationException("Windows monitor enumeration failed.");
        }

        return monitors;
    }

    public DisplayMonitor GetMonitorForWindow(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        }

        var monitor = NativeMethods.MonitorFromWindow(
            windowHandle,
            NativeMethods.MonitorDefaultToNearest);
        if (monitor == 0)
        {
            throw new InvalidOperationException("The monitor for the window is unavailable.");
        }

        var result = ReadMonitor(monitor);
        var windowDpi = NativeMethods.GetDpiForWindow(windowHandle);
        return windowDpi > 0
            ? result with { DpiX = windowDpi, DpiY = windowDpi }
            : result;
    }

    private static DisplayMonitor ReadMonitor(nint monitor)
    {
        var info = new NativeMethods.MonitorInfoEx
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            DeviceName = string.Empty,
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException("Windows monitor information is unavailable.");
        }

        var dpiX = (uint)LayoutMetrics.DipsPerInch;
        var dpiY = (uint)LayoutMetrics.DipsPerInch;
        _ = NativeMethods.GetDpiForMonitor(
            monitor,
            NativeMethods.MonitorDpiType.Effective,
            out dpiX,
            out dpiY);
        if (dpiX == 0 || dpiY == 0)
        {
            dpiX = (uint)LayoutMetrics.DipsPerInch;
            dpiY = (uint)LayoutMetrics.DipsPerInch;
        }

        return new DisplayMonitor(
            info.DeviceName,
            new PixelRect(
                info.WorkArea.Left,
                info.WorkArea.Top,
                info.WorkArea.Right,
                info.WorkArea.Bottom),
            dpiX,
            dpiY,
            (info.Flags & MonitorInfoPrimary) != 0);
    }
}
