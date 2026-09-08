namespace UnifiedCalendar.Core.Presentation;

public static class UiTextResourceKeys
{
    public const string EventUntitled = "Event.Untitled";
    public const string DayHeaderToday = "DayHeader.Today";
    public const string DayHeaderTomorrow = "DayHeader.Tomorrow";
    public const string DayHeaderDate = "DayHeader.Date";
    public const string EventListInstant = "Event.List.Instant";
    public const string EventListTimedRange = "Event.List.TimedRange";
    public const string EventListAllDay = "Event.List.AllDay";
    public const string EventListAllDayRange = "Event.List.AllDayRange";
    public const string EventDetailTimedSingleDay = "Event.Detail.TimedSingleDay";
    public const string EventDetailTimedRange = "Event.Detail.TimedRange";
    public const string EventDetailAllDay = "Event.Detail.AllDay";
    public const string EventDetailAllDayRange = "Event.Detail.AllDayRange";
    public const string AttendeeAccepted = "Attendee.Accepted";
    public const string AttendeeTentative = "Attendee.Tentative";
    public const string AttendeeNotResponded = "Attendee.NotResponded";
    public const string AttendeeDeclined = "Attendee.Declined";
    public const string AttendeeUnknown = "Attendee.Unknown";
    public const string AttendeeStatus = "Attendee.Status";
    public const string EventTooltip = "Event.Tooltip";
    public const string EventTooltipWithAttention = "Event.TooltipWithAttention";
}

/// <summary>
/// Language-independent text input for the later UI text service.
/// Arguments are copied and may contain primitives, DateOnly, TimeOnly, or nested UiText values.
/// </summary>
public sealed class UiText : IEquatable<UiText>
{
    private readonly IReadOnlyList<object?> _arguments;

    private UiText(string? literal, string? resourceKey, IEnumerable<object?> arguments)
    {
        Literal = literal;
        ResourceKey = resourceKey;
        _arguments = Array.AsReadOnly(arguments.ToArray());
    }

    public string? Literal { get; }

    public string? ResourceKey { get; }

    public IReadOnlyList<object?> Arguments => _arguments;

    public bool IsLiteral => Literal is not null;

    public bool Equals(UiText? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || IsLiteral != other.IsLiteral)
        {
            return false;
        }

        if (IsLiteral)
        {
            return string.Equals(Literal, other.Literal, StringComparison.Ordinal);
        }

        if (!string.Equals(ResourceKey, other.ResourceKey, StringComparison.Ordinal)
            || Arguments.Count != other.Arguments.Count)
        {
            return false;
        }

        for (var index = 0; index < Arguments.Count; index++)
        {
            if (!object.Equals(Arguments[index], other.Arguments[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is UiText other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IsLiteral);
        if (IsLiteral)
        {
            hash.Add(Literal, StringComparer.Ordinal);
        }
        else
        {
            hash.Add(ResourceKey, StringComparer.Ordinal);
            hash.Add(Arguments.Count);
            foreach (var argument in Arguments)
            {
                hash.Add(argument);
            }
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(UiText? left, UiText? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(UiText? left, UiText? right) => !(left == right);

    public static UiText FromLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new UiText(value, null, Array.Empty<object?>());
    }

    public static UiText FromResource(string resourceKey, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        ArgumentNullException.ThrowIfNull(arguments);
        return new UiText(null, resourceKey, arguments);
    }
}
