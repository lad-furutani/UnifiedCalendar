using System.Collections.Concurrent;
using System.Windows.Media;
using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.App.Presentation;

public sealed class BrushCache
{
    private readonly ConcurrentDictionary<int, SolidColorBrush> _solidBrushes = new();

    public Brush GetEventBackground(
        RgbColor background,
        RgbColor elapsed,
        RgbColor remaining,
        double? progress)
    {
        if (!progress.HasValue)
        {
            return GetSolid(background);
        }

        var offset = Math.Clamp(progress.Value, 0d, 1d);
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0d, 0.5d),
            EndPoint = new System.Windows.Point(1d, 0.5d),
        };
        brush.GradientStops.Add(new GradientStop(ToColor(elapsed), offset));
        brush.GradientStops.Add(new GradientStop(ToColor(remaining), offset));
        brush.Freeze();
        return brush;
    }

    public SolidColorBrush GetSolid(RgbColor color)
    {
        var key = (color.Red << 16) | (color.Green << 8) | color.Blue;
        return _solidBrushes.GetOrAdd(key, _ =>
        {
            var brush = new SolidColorBrush(ToColor(color));
            brush.Freeze();
            return brush;
        });
    }

    private static Color ToColor(RgbColor color) => Color.FromRgb(color.Red, color.Green, color.Blue);
}
