using System.Runtime.InteropServices;

namespace UnifiedCalendar.App.Shell;

internal static class NativeMethods
{
    internal const int GwlStyle = -16;
    internal const long WsMaximizeBox = 0x00010000L;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal delegate bool MonitorEnumProcedure(
        nint monitor,
        nint deviceContext,
        ref Rect monitorRectangle,
        nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        nint deviceContext,
        nint clipRectangle,
        MonitorEnumProcedure callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx monitorInfo);

    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllowSetForegroundWindow(uint processId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string DeviceName;
    }

    internal enum MonitorDpiType
    {
        Effective = 0,
    }
}
