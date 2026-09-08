using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.App.Presentation;

namespace UnifiedCalendar.App.Shell;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

public readonly record struct DipRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

public sealed record DisplayMonitor(
    string DeviceName,
    PixelRect WorkAreaPixels,
    double DpiX,
    double DpiY,
    bool IsPrimary)
{
    public DipRect WorkAreaDip => new(
        WorkAreaPixels.Left * LayoutMetrics.DipsPerInch / DpiX,
        WorkAreaPixels.Top * LayoutMetrics.DipsPerInch / DpiY,
        WorkAreaPixels.Width * LayoutMetrics.DipsPerInch / DpiX,
        WorkAreaPixels.Height * LayoutMetrics.DipsPerInch / DpiY);
}

public static class WindowPlacementCalculator
{
    public static DipRect RestoreMainWindow(
        WindowPlacement? saved,
        IReadOnlyCollection<DisplayMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }

        var fallback = FindPrimaryMonitor(monitors);
        var workArea = fallback.WorkAreaDip;
        return RestoreWindow(
            saved,
            monitors,
            fallback.DeviceName,
            new DipRect(
                workArea.Right - LayoutMetrics.InitialMainWidth,
                workArea.Top,
                LayoutMetrics.InitialMainWidth,
                LayoutMetrics.InitialMainHeight),
            LayoutMetrics.MinimumMainWidth,
            LayoutMetrics.MinimumMainHeight);
    }

    public static DipRect RestoreWindow(
        WindowPlacement? saved,
        IReadOnlyCollection<DisplayMonitor> monitors,
        string fallbackMonitorDeviceName,
        DipRect fallbackBounds,
        double minimumWidth,
        double minimumHeight)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackMonitorDeviceName);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }

        ValidateMonitors(monitors);
        ValidateBounds(fallbackBounds, nameof(fallbackBounds));
        if (!double.IsFinite(minimumWidth) || minimumWidth <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumWidth));
        }

        if (!double.IsFinite(minimumHeight) || minimumHeight <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumHeight));
        }

        var savedMonitor = saved is null
            ? null
            : monitors.FirstOrDefault(monitor => monitor.DeviceName.Equals(
                saved.MonitorDeviceName,
                StringComparison.OrdinalIgnoreCase));
        var fallbackMonitor = monitors.FirstOrDefault(monitor => monitor.DeviceName.Equals(
                fallbackMonitorDeviceName,
                StringComparison.OrdinalIgnoreCase))
            ?? FindPrimaryMonitor(monitors);
        var target = savedMonitor ?? fallbackMonitor;
        var candidate = fallbackBounds;
        if (saved is not null && savedMonitor is not null)
        {
            var savedDpiX = saved.SavedDpiX ?? target.DpiX;
            var savedDpiY = saved.SavedDpiY ?? target.DpiY;
            candidate = new DipRect(
                saved.LeftDip * savedDpiX / target.DpiX,
                saved.TopDip * savedDpiY / target.DpiY,
                saved.WidthDip,
                saved.HeightDip);
        }

        return Clamp(candidate, target.WorkAreaDip, minimumWidth, minimumHeight);
    }

    private static DipRect Clamp(
        DipRect candidate,
        DipRect workArea,
        double minimumWidth,
        double minimumHeight)
    {
        var width = Math.Max(candidate.Width, minimumWidth);
        var height = Math.Max(candidate.Height, minimumHeight);

        double left;
        if (width <= workArea.Width)
        {
            left = Math.Clamp(candidate.Left, workArea.Left, workArea.Right - width);
        }
        else
        {
            var minimumLeft = workArea.Left + LayoutMetrics.MinimumVisibleTitleBarWidth - width;
            var maximumLeft = workArea.Right - LayoutMetrics.MinimumVisibleTitleBarWidth;
            left = Math.Clamp(candidate.Left, minimumLeft, maximumLeft);
        }

        double top;
        if (height <= workArea.Height)
        {
            top = Math.Clamp(candidate.Top, workArea.Top, workArea.Bottom - height);
        }
        else
        {
            top = Math.Clamp(
                candidate.Top,
                workArea.Top,
                workArea.Bottom - LayoutMetrics.MinimumVisibleTitleBarHeight);
        }

        return new DipRect(left, top, width, height);
    }

    private static DisplayMonitor FindPrimaryMonitor(IEnumerable<DisplayMonitor> monitors) =>
        monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors.First();

    private static void ValidateBounds(DipRect bounds, string parameterName)
    {
        if (!double.IsFinite(bounds.Left)
            || !double.IsFinite(bounds.Top)
            || !double.IsFinite(bounds.Width)
            || bounds.Width <= 0d
            || !double.IsFinite(bounds.Height)
            || bounds.Height <= 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateMonitors(IEnumerable<DisplayMonitor> monitors)
    {
        foreach (var monitor in monitors)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(monitor.DeviceName);
            if (monitor.WorkAreaPixels.Width <= 0 || monitor.WorkAreaPixels.Height <= 0
                || !double.IsFinite(monitor.DpiX) || monitor.DpiX <= 0d
                || !double.IsFinite(monitor.DpiY) || monitor.DpiY <= 0d)
            {
                throw new ArgumentException("Monitor work areas and DPI values must be positive.", nameof(monitors));
            }
        }
    }
}
