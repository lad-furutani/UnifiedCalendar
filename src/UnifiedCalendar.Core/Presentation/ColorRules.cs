using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Core.Presentation;

public enum ColorRuleOperator
{
    All,
    Any,
}

public enum ColorRuleField
{
    Title,
    CalendarName,
    Provider,
}

public enum TextMatchKind
{
    Contains,
    Exact,
}

public sealed record ColorRuleCondition
{
    private ColorRuleCondition(
        ColorRuleField field,
        TextMatchKind matchKind,
        string? comparisonValue,
        ProviderKind? provider)
    {
        Field = field;
        MatchKind = matchKind;
        ComparisonValue = comparisonValue;
        Provider = provider;
    }

    public ColorRuleField Field { get; }

    public TextMatchKind MatchKind { get; }

    public string? ComparisonValue { get; }

    public ProviderKind? Provider { get; }

    public static ColorRuleCondition ForTitle(TextMatchKind matchKind, string comparisonValue) =>
        ForText(ColorRuleField.Title, matchKind, comparisonValue);

    public static ColorRuleCondition ForCalendarName(TextMatchKind matchKind, string comparisonValue) =>
        ForText(ColorRuleField.CalendarName, matchKind, comparisonValue);

    public static ColorRuleCondition ForProvider(ProviderKind provider) =>
        new(
            ColorRuleField.Provider,
            TextMatchKind.Exact,
            null,
            Enum.IsDefined(provider)
                ? provider
                : throw new ArgumentOutOfRangeException(nameof(provider), provider, "The provider is not defined."));

    internal bool IsMatch(CalendarEvent calendarEvent)
    {
        return Field switch
        {
            ColorRuleField.Title => CompareText(calendarEvent.Title),
            ColorRuleField.CalendarName => CompareText(calendarEvent.CalendarName),
            ColorRuleField.Provider => calendarEvent.Key.Provider == Provider,
            _ => throw new ArgumentOutOfRangeException(nameof(Field)),
        };
    }

    private static ColorRuleCondition ForText(
        ColorRuleField field,
        TextMatchKind matchKind,
        string comparisonValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(comparisonValue);
        if (!Enum.IsDefined(matchKind))
        {
            throw new ArgumentOutOfRangeException(nameof(matchKind), matchKind, "The match kind is not defined.");
        }

        var trimmed = comparisonValue.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("A comparison value cannot be empty after trimming.", nameof(comparisonValue));
        }

        return new ColorRuleCondition(field, matchKind, trimmed, null);
    }

    private bool CompareText(string source)
    {
        var candidate = source.Trim();
        return MatchKind switch
        {
            TextMatchKind.Contains => candidate.Contains(
                ComparisonValue!,
                StringComparison.OrdinalIgnoreCase),
            TextMatchKind.Exact => candidate.Equals(
                ComparisonValue,
                StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(MatchKind)),
        };
    }
}

public sealed class ColorRule
{
    public const int MaximumNameLength = 64;

    private readonly IReadOnlyList<ColorRuleCondition> _conditions;

    public ColorRule(
        string name,
        bool isEnabled,
        ColorRuleOperator ruleOperator,
        IEnumerable<ColorRuleCondition> conditions,
        RgbColor color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(conditions);

        var trimmedName = name.Trim();
        if (trimmedName.Length > MaximumNameLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                $"A rule name cannot exceed {MaximumNameLength} characters.");
        }

        var conditionArray = conditions.ToArray();
        if (conditionArray.Length == 0)
        {
            throw new ArgumentException("A color rule must contain at least one condition.", nameof(conditions));
        }

        if (conditionArray.Any(condition => condition is null))
        {
            throw new ArgumentException("Color rule conditions cannot contain null elements.", nameof(conditions));
        }

        if (!Enum.IsDefined(ruleOperator))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ruleOperator),
                ruleOperator,
                "The color rule operator is not defined.");
        }

        Name = trimmedName;
        IsEnabled = isEnabled;
        Operator = ruleOperator;
        _conditions = Array.AsReadOnly(conditionArray);
        Color = color;
    }

    public string Name { get; }

    public bool IsEnabled { get; }

    public ColorRuleOperator Operator { get; }

    public IReadOnlyList<ColorRuleCondition> Conditions => _conditions;

    public RgbColor Color { get; }

    internal bool IsMatch(CalendarEvent calendarEvent) => Operator switch
    {
        ColorRuleOperator.All => Conditions.All(condition => condition.IsMatch(calendarEvent)),
        ColorRuleOperator.Any => Conditions.Any(condition => condition.IsMatch(calendarEvent)),
        _ => throw new ArgumentOutOfRangeException(nameof(Operator)),
    };
}
