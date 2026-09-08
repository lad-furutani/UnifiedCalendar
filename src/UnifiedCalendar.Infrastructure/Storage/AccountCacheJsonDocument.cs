using System.Text.Json.Serialization;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Infrastructure.Storage;

internal sealed class AccountCacheJsonDocument
{
    public required int SchemaVersion { get; set; }

    public required Guid InternalAccountId { get; set; }

    public required ProviderKind Provider { get; set; }

    public required DateTimeOffset GeneratedAtUtc { get; set; }

    public required List<CachedCalendarJsonDocument> Calendars { get; set; }

    public AccountCache ToDomain()
    {
        if (SchemaVersion != AccountCacheJsonStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The cache schema version is not current.");
        }

        if (Calendars is null)
        {
            throw new InvalidDataException("The cache calendars are required.");
        }

        return new AccountCache(
            InternalAccountId,
            Provider,
            GeneratedAtUtc,
            Calendars.Select(calendar => calendar?.ToDomain()
                ?? throw new InvalidDataException("A cached calendar cannot be null.")));
    }

    public static AccountCacheJsonDocument FromDomain(AccountCache value) => new()
    {
        SchemaVersion = AccountCacheJsonStore.CurrentSchemaVersion,
        InternalAccountId = value.InternalAccountId,
        Provider = value.Provider,
        GeneratedAtUtc = value.GeneratedAtUtc,
        Calendars = value.Calendars.Select(CachedCalendarJsonDocument.FromDomain).ToList(),
    };
}

internal sealed class CachedCalendarJsonDocument
{
    public required string CalendarId { get; set; }

    public required string Name { get; set; }

    public required string? SourceColor { get; set; }

    public required DateTimeOffset LastSuccessfulSyncUtc { get; set; }

    public required List<CalendarEventJsonDocument> Events { get; set; }

    public CachedCalendar ToDomain() => new(
        CalendarId,
        Name,
        SourceColor is null ? null : RgbColor.Parse(SourceColor),
        LastSuccessfulSyncUtc,
        (Events ?? throw new InvalidDataException("Cached events are required."))
            .Select(calendarEvent => calendarEvent?.ToDomain()
                ?? throw new InvalidDataException("A cached event cannot be null.")));

    public static CachedCalendarJsonDocument FromDomain(CachedCalendar value) => new()
    {
        CalendarId = value.CalendarId,
        Name = value.Name,
        SourceColor = value.SourceColor?.ToHexString(),
        LastSuccessfulSyncUtc = value.LastSuccessfulSyncUtc,
        Events = value.Events.Select(CalendarEventJsonDocument.FromDomain).ToList(),
    };
}

internal sealed class CalendarEventJsonDocument
{
    public required EventKeyJsonDocument Key { get; set; }

    public required string Title { get; set; }

    public required EventTimingJsonDocument Timing { get; set; }

    public required string CalendarName { get; set; }

    public required string? DescriptionPlainText { get; set; }

    public required string? Location { get; set; }

    public required string? SourceTimeZoneId { get; set; }

    public required AttendeeResponse ResponseStatus { get; set; }

    public required string? SourceEventColor { get; set; }

    public required string? SourceCalendarColor { get; set; }

    public required string? MeetingUri { get; set; }

    public required string? SourceDetailUri { get; set; }

    public required bool IsCancelled { get; set; }

    public CalendarEvent ToDomain()
    {
        if (Key is null || Timing is null)
        {
            throw new InvalidDataException("A cached event key and timing are required.");
        }

        return new CalendarEvent(
            Key.ToDomain(),
            Title,
            Timing.ToDomain(),
            CalendarName,
            DescriptionPlainText,
            Location,
            SourceTimeZoneId,
            ResponseStatus,
            SourceEventColor is null ? null : RgbColor.Parse(SourceEventColor),
            SourceCalendarColor is null ? null : RgbColor.Parse(SourceCalendarColor),
            ParseOptionalUri(MeetingUri),
            ParseOptionalUri(SourceDetailUri),
            IsCancelled);
    }

    public static CalendarEventJsonDocument FromDomain(CalendarEvent value) => new()
    {
        Key = EventKeyJsonDocument.FromDomain(value.Key),
        Title = value.Title,
        Timing = EventTimingJsonDocument.FromDomain(value.Timing),
        CalendarName = value.CalendarName,
        DescriptionPlainText = value.DescriptionPlainText,
        Location = value.Location,
        SourceTimeZoneId = value.SourceTimeZoneId,
        ResponseStatus = value.ResponseStatus,
        SourceEventColor = value.SourceEventColor?.ToHexString(),
        SourceCalendarColor = value.SourceCalendarColor?.ToHexString(),
        MeetingUri = value.MeetingUri?.AbsoluteUri,
        SourceDetailUri = value.SourceDetailUri?.AbsoluteUri,
        IsCancelled = value.IsCancelled,
    };

    private static Uri? ParseOptionalUri(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("A cached event URI is invalid.");
        }

        return uri;
    }
}

internal sealed class EventKeyJsonDocument
{
    public required ProviderKind Provider { get; set; }

    public required Guid InternalAccountId { get; set; }

    public required string CalendarId { get; set; }

    public required string SourceEventId { get; set; }

    public required string OccurrenceKey { get; set; }

    public EventKey ToDomain() => new(
        Provider,
        InternalAccountId,
        CalendarId,
        SourceEventId,
        OccurrenceKey);

    public static EventKeyJsonDocument FromDomain(EventKey value) => new()
    {
        Provider = value.Provider,
        InternalAccountId = value.InternalAccountId,
        CalendarId = value.CalendarId,
        SourceEventId = value.SourceEventId,
        OccurrenceKey = value.OccurrenceKey,
    };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TimedEventTimingJsonDocument), "timed")]
[JsonDerivedType(typeof(AllDayEventTimingJsonDocument), "allDay")]
internal abstract class EventTimingJsonDocument
{
    public abstract EventTiming ToDomain();

    public static EventTimingJsonDocument FromDomain(EventTiming value) => value switch
    {
        TimedEventTiming timed => new TimedEventTimingJsonDocument
        {
            StartUtc = timed.StartUtc,
            EndUtc = timed.EndUtc,
        },
        AllDayEventTiming allDay => new AllDayEventTimingJsonDocument
        {
            StartDate = allDay.StartDate,
            EndDateExclusive = allDay.EndDateExclusive,
        },
        _ => throw new ArgumentException("The event timing type is not supported.", nameof(value)),
    };
}

internal sealed class TimedEventTimingJsonDocument : EventTimingJsonDocument
{
    public required DateTimeOffset StartUtc { get; set; }

    public required DateTimeOffset EndUtc { get; set; }

    public override EventTiming ToDomain() => new TimedEventTiming(StartUtc, EndUtc);
}

internal sealed class AllDayEventTimingJsonDocument : EventTimingJsonDocument
{
    public required DateOnly StartDate { get; set; }

    public required DateOnly EndDateExclusive { get; set; }

    public override EventTiming ToDomain() => new AllDayEventTiming(StartDate, EndDateExclusive);
}
