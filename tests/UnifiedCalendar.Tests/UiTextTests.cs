using UnifiedCalendar.Core.Presentation;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class UiTextTests
{
    [Fact]
    public void SameLiteralsAreEqual()
    {
        var left = UiText.FromLiteral("title");
        var right = UiText.FromLiteral("title");

        Assert.Equal(left, right);
        Assert.True(left.Equals(right));
    }

    [Fact]
    public void DifferentLiteralsAreNotEqualUsingOrdinalComparison()
    {
        var lower = UiText.FromLiteral("title");
        var upper = UiText.FromLiteral("Title");

        Assert.NotEqual(lower, upper);
        Assert.False(lower.Equals(upper));
    }

    [Fact]
    public void SameResourceKeyAndArgumentsAreEqual()
    {
        var left = UiText.FromResource("Event.Range", "value", 42);
        var right = UiText.FromResource("Event.Range", "value", 42);

        Assert.Equal(left, right);
    }

    [Fact]
    public void DifferentResourceKeysAreNotEqualUsingOrdinalComparison()
    {
        var left = UiText.FromResource("Event.Range", "value");
        var right = UiText.FromResource("event.Range", "value");

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void DifferentArgumentValuesAreNotEqual()
    {
        var left = UiText.FromResource("Event.Range", "first", 42);
        var right = UiText.FromResource("Event.Range", "second", 42);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void DifferentArgumentCountsAreNotEqual()
    {
        var left = UiText.FromResource("Event.Range", "value");
        var right = UiText.FromResource("Event.Range", "value", 42);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void DifferentArgumentOrderIsNotEqual()
    {
        var left = UiText.FromResource("Event.Range", "first", "second");
        var right = UiText.FromResource("Event.Range", "second", "first");

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void EqualNullArgumentsAreHandled()
    {
        var left = UiText.FromResource("Event.Optional", "value", null, 42);
        var right = UiText.FromResource("Event.Optional", "value", null, 42);

        Assert.Equal(left, right);
    }

    [Fact]
    public void EqualNestedUiTextArgumentsUseValueEquality()
    {
        var left = UiText.FromResource(
            "Event.Tooltip",
            UiText.FromResource("Event.Untitled"),
            UiText.FromLiteral("detail"));
        var right = UiText.FromResource(
            "Event.Tooltip",
            UiText.FromResource("Event.Untitled"),
            UiText.FromLiteral("detail"));

        Assert.Equal(left, right);
    }

    [Fact]
    public void LiteralAndResourceFormsAreNotEqualEvenWhenStringsMatch()
    {
        var literal = UiText.FromLiteral("Event.Untitled");
        var resource = UiText.FromResource("Event.Untitled");

        Assert.NotEqual(literal, resource);
    }

    [Fact]
    public void EqualValuesHaveEqualHashCodes()
    {
        var left = UiText.FromResource(
            "Event.Tooltip",
            null,
            UiText.FromLiteral("detail"));
        var right = UiText.FromResource(
            "Event.Tooltip",
            null,
            UiText.FromLiteral("detail"));

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void EqualityOperatorsMatchEquals()
    {
        var left = UiText.FromResource("Event.Range", 1, 2);
        var equal = UiText.FromResource("Event.Range", 1, 2);
        var different = UiText.FromResource("Event.Range", 2, 1);

        Assert.True(left == equal);
        Assert.False(left != equal);
        Assert.False(left == different);
        Assert.True(left != different);
    }

    [Fact]
    public void NullComparisonsAreCorrect()
    {
        var value = UiText.FromLiteral("title");
        UiText? none = null;

        Assert.False(value.Equals(none));
        Assert.False(value == none);
        Assert.False(none == value);
        Assert.True(value != none);
        Assert.True(none == null);
        Assert.False(none != null);
    }
}
