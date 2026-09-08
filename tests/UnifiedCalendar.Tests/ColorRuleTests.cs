using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class ColorRuleTests
{
    [Fact]
    public void ColorRule_RequiresNameAndAtLeastOneCondition()
    {
        Assert.Throws<ArgumentException>(() => new ColorRule(
            "rule",
            true,
            ColorRuleOperator.All,
            Array.Empty<ColorRuleCondition>(),
            RgbColor.Parse("#112233")));
        Assert.Throws<ArgumentException>(() => ColorRuleCondition.ForTitle(
            TextMatchKind.Contains,
            "   "));
    }

    [Fact]
    public void ColorRule_RejectsUndefinedEnumsAtPublicEntryPoints()
    {
        Assert.Equal(
            "provider",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ColorRuleCondition.ForProvider((ProviderKind)99)).ParamName);
        Assert.Equal(
            "matchKind",
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                ColorRuleCondition.ForTitle((TextMatchKind)99, "value")).ParamName);
        Assert.Equal(
            "ruleOperator",
            Assert.Throws<ArgumentOutOfRangeException>(() => new ColorRule(
                "rule",
                true,
                (ColorRuleOperator)99,
                new[] { ColorRuleCondition.ForTitle(TextMatchKind.Exact, "value") },
                RgbColor.Parse("#112233"))).ParamName);
    }

    [Fact]
    public void RgbColor_ParsesFormatsAndComputesContrast()
    {
        var color = RgbColor.Parse("#2F6FED");

        Assert.Equal("#2F6FED", color.ToHexString());
        Assert.True(RgbColor.Parse("#FFFFFF").GetRelativeLuminance()
            > RgbColor.Parse("#000000").GetRelativeLuminance());
        Assert.True(RgbColor.Parse("#000000").GetContrastRatio(RgbColor.Parse("#FFFFFF")) > 20d);
        Assert.True(color.AdjustLightness(-0.08d).GetRelativeLuminance()
            < color.AdjustLightness(0.08d).GetRelativeLuminance());
    }
}
