using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Core.Presentation;

public sealed record DisplaySettings
{
    public const int MinimumDisplayDays = 1;
    public const int MaximumDisplayDays = 90;

    public static readonly RgbColor InitialDefaultEventColor = RgbColor.Parse("#2F6FED");

    public DisplaySettings(int displayDays = 7, RgbColor? defaultEventColor = null)
    {
        if (displayDays is < MinimumDisplayDays or > MaximumDisplayDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(displayDays),
                $"Display days must be between {MinimumDisplayDays} and {MaximumDisplayDays}.");
        }

        DisplayDays = displayDays;
        DefaultEventColor = defaultEventColor ?? InitialDefaultEventColor;
    }

    public int DisplayDays { get; }

    public RgbColor DefaultEventColor { get; }
}

public sealed record PresentedEvent(
    EventKey Key,
    string StableId,
    CalendarEvent Source,
    DateOnly GroupDate,
    UiText Title,
    UiText ListTime,
    UiText Tooltip,
    UiText DetailDateTime,
    DateTimeOffset? LocalStart,
    bool IsAllDay,
    bool IsMultiDay,
    double? Progress,
    AttendeeResponse? AttentionResponse,
    UiText? AttentionResponseText,
    UiText? DetailResponseText,
    RgbColor BackgroundColor,
    RgbColor ForegroundColor,
    RgbColor ElapsedColor,
    RgbColor RemainingColor);

public sealed class PresentedDay
{
    private readonly IReadOnlyList<PresentedEvent> _events;

    public PresentedDay(DateOnly date, UiText header, IEnumerable<PresentedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(events);

        Date = date;
        Header = header;
        _events = Array.AsReadOnly(events.ToArray());
    }

    public DateOnly Date { get; }

    public UiText Header { get; }

    public IReadOnlyList<PresentedEvent> Events => _events;
}

public sealed class PresentationSnapshot
{
    private readonly IReadOnlyList<PresentedDay> _days;
    private readonly IReadOnlyList<PresentedEvent> _events;

    public PresentationSnapshot(
        DateTimeOffset generatedAtUtc,
        DateOnly today,
        IEnumerable<PresentedDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);

        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();
        Today = today;
        _days = Array.AsReadOnly(days.ToArray());
        _events = Array.AsReadOnly(_days.SelectMany(day => day.Events).ToArray());
    }

    public DateTimeOffset GeneratedAtUtc { get; }

    public DateOnly Today { get; }

    public IReadOnlyList<PresentedDay> Days => _days;

    public IReadOnlyList<PresentedEvent> Events => _events;
}
