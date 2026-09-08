using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Providers.Microsoft;

internal static class MicrosoftColorPalette
{
    // Graph defines the semantic Outlook names, while the rendered RGB varies by client/theme.
    // These fixed approximations preserve the documented color family across the provider boundary.
    private static readonly IReadOnlyDictionary<string, MicrosoftCategoryColorDefinition> CategoryColors =
        new Dictionary<string, MicrosoftCategoryColorDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["preset0"] = Definition("Red", "#E74856"),
            ["preset1"] = Definition("Orange", "#FF8C00"),
            ["preset2"] = Definition("Brown", "#8E562E"),
            ["preset3"] = Definition("Yellow", "#FFF100"),
            ["preset4"] = Definition("Green", "#47D041"),
            ["preset5"] = Definition("Teal", "#30C6CC"),
            ["preset6"] = Definition("Olive", "#73AA24"),
            ["preset7"] = Definition("Blue", "#0078D4"),
            ["preset8"] = Definition("Purple", "#8764B8"),
            ["preset9"] = Definition("Cranberry", "#A4262C"),
            ["preset10"] = Definition("Steel", "#7A7574"),
            ["preset11"] = Definition("DarkSteel", "#5D5A58"),
            ["preset12"] = Definition("Gray", "#A19F9D"),
            ["preset13"] = Definition("DarkGray", "#605E5C"),
            ["preset14"] = Definition("Black", "#201F1E"),
            ["preset15"] = Definition("DarkRed", "#8B0000"),
            ["preset16"] = Definition("DarkOrange", "#C65911"),
            ["preset17"] = Definition("DarkBrown", "#5C2D0C"),
            ["preset18"] = Definition("DarkYellow", "#806000"),
            ["preset19"] = Definition("DarkGreen", "#0B6A0B"),
            ["preset20"] = Definition("DarkTeal", "#006666"),
            ["preset21"] = Definition("DarkOlive", "#556B2F"),
            ["preset22"] = Definition("DarkBlue", "#004578"),
            ["preset23"] = Definition("DarkPurple", "#5C2D91"),
            ["preset24"] = Definition("DarkCranberry", "#6B1026"),
        };

    private static readonly IReadOnlyDictionary<string, RgbColor> CalendarColors =
        new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase)
        {
            ["lightBlue"] = RgbColor.Parse("#A6D8FF"),
            ["lightGreen"] = RgbColor.Parse("#A6E7D8"),
            ["lightOrange"] = RgbColor.Parse("#FFD3A6"),
            ["lightGray"] = RgbColor.Parse("#D0D0D0"),
            ["lightYellow"] = RgbColor.Parse("#FFF2A6"),
            ["lightTeal"] = RgbColor.Parse("#A6E4E7"),
            ["lightPink"] = RgbColor.Parse("#F5B5D2"),
            ["lightBrown"] = RgbColor.Parse("#D8C0A6"),
            ["lightRed"] = RgbColor.Parse("#F2A6A6"),
        };

    public static RgbColor? FromCategoryColor(string? name) =>
        GetCategoryColorDefinition(name)?.ApproximateRgb;

    internal static MicrosoftCategoryColorDefinition? GetCategoryColorDefinition(string? name) =>
        name is not null && CategoryColors.TryGetValue(name, out var definition) ? definition : null;

    public static RgbColor? FromCalendarColor(string? name) =>
        name is not null && CalendarColors.TryGetValue(name, out var color) ? color : null;

    private static MicrosoftCategoryColorDefinition Definition(string outlookName, string rgb) =>
        new(outlookName, RgbColor.Parse(rgb));
}

internal sealed record MicrosoftCategoryColorDefinition(
    string OutlookName,
    RgbColor ApproximateRgb);
