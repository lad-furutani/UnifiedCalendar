namespace UnifiedCalendar.Core.Models;

public abstract record EventTiming;

public sealed record TimedEventTiming : EventTiming
{
    public TimedEventTiming(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        StartUtc = startUtc.ToUniversalTime();
        EndUtc = endUtc.ToUniversalTime();

        if (EndUtc < StartUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endUtc),
                "The end of a timed event cannot precede its start.");
        }
    }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }

    public bool IsZeroDuration => StartUtc == EndUtc;
}

public sealed record AllDayEventTiming : EventTiming
{
    public AllDayEventTiming(DateOnly startDate, DateOnly endDateExclusive)
    {
        if (endDateExclusive <= startDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endDateExclusive),
                "The exclusive end date must follow the start date.");
        }

        StartDate = startDate;
        EndDateExclusive = endDateExclusive;
    }

    public DateOnly StartDate { get; }

    public DateOnly EndDateExclusive { get; }

    public DateOnly DisplayEndDate => EndDateExclusive.AddDays(-1);

    public bool IsMultiDay => EndDateExclusive.DayNumber - StartDate.DayNumber > 1;
}
