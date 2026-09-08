using UnifiedCalendar.Providers.Microsoft;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class MicrosoftColorPaletteTests
{
    public static TheoryData<string, string, string> OutlookPresetColors => new()
    {
        { "preset0", "Red", "#E74856" },
        { "preset1", "Orange", "#FF8C00" },
        { "preset2", "Brown", "#8E562E" },
        { "preset3", "Yellow", "#FFF100" },
        { "preset4", "Green", "#47D041" },
        { "preset5", "Teal", "#30C6CC" },
        { "preset6", "Olive", "#73AA24" },
        { "preset7", "Blue", "#0078D4" },
        { "preset8", "Purple", "#8764B8" },
        { "preset9", "Cranberry", "#A4262C" },
        { "preset10", "Steel", "#7A7574" },
        { "preset11", "DarkSteel", "#5D5A58" },
        { "preset12", "Gray", "#A19F9D" },
        { "preset13", "DarkGray", "#605E5C" },
        { "preset14", "Black", "#201F1E" },
        { "preset15", "DarkRed", "#8B0000" },
        { "preset16", "DarkOrange", "#C65911" },
        { "preset17", "DarkBrown", "#5C2D0C" },
        { "preset18", "DarkYellow", "#806000" },
        { "preset19", "DarkGreen", "#0B6A0B" },
        { "preset20", "DarkTeal", "#006666" },
        { "preset21", "DarkOlive", "#556B2F" },
        { "preset22", "DarkBlue", "#004578" },
        { "preset23", "DarkPurple", "#5C2D91" },
        { "preset24", "DarkCranberry", "#6B1026" },
    };

    [Theory]
    [MemberData(nameof(OutlookPresetColors))]
    public void CategoryPreset_UsesOfficialOutlookFamilyAndFixedApproximation(
        string preset,
        string outlookName,
        string expectedRgb)
    {
        var definition = MicrosoftColorPalette.GetCategoryColorDefinition(preset);

        Assert.NotNull(definition);
        Assert.Equal(outlookName, definition.OutlookName);
        Assert.Equal(expectedRgb, definition.ApproximateRgb.ToHexString());
        Assert.Equal(expectedRgb, MicrosoftColorPalette.FromCategoryColor(preset)?.ToHexString());
    }

    [Fact]
    public void UnknownCategoryPreset_RemainsUnresolvedForCalendarFallback()
    {
        Assert.Null(MicrosoftColorPalette.GetCategoryColorDefinition("preset99"));
        Assert.Null(MicrosoftColorPalette.FromCategoryColor("preset99"));
        Assert.Null(MicrosoftColorPalette.FromCategoryColor(null));
    }
}
