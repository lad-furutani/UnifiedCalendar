using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.App.Presentation;

public static class SettingsColorPalette
{
    public static IReadOnlyList<RgbColor> Colors { get; } = Array.AsReadOnly<RgbColor>(
    [
        DisplaySettings.InitialDefaultEventColor,
        RgbColor.Parse("#00696F"),
        RgbColor.Parse("#1F6B24"),
        RgbColor.Parse("#8A5200"),
        RgbColor.Parse("#B01F1F"),
        RgbColor.Parse("#96114B"),
        RgbColor.Parse("#5A2E9B"),
        RgbColor.Parse("#3A4B54"),
    ]);
}
