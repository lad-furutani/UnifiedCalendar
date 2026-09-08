using Microsoft.Graph.Models;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Providers.Microsoft;
using Xunit;
using GraphCalendar = Microsoft.Graph.Models.Calendar;
using GraphEvent = Microsoft.Graph.Models.Event;
using RgbColor = UnifiedCalendar.Core.Models.RgbColor;

namespace UnifiedCalendar.Tests;

public sealed class MicrosoftModelMapperTests
{
    [Fact]
    public void CalendarMapping_PreservesPrimaryReadabilityLocatorAndColor()
    {
        var source = new GraphCalendar
        {
            Id = "fixture-calendar",
            Name = "Fixture Calendar",
            IsDefaultCalendar = true,
            HexColor = "#12ab34",
        };

        var mapped = MicrosoftModelMapper.ToCalendarDescriptor(source);

        Assert.NotNull(mapped);
        Assert.Equal(source.Id, mapped.CalendarId);
        Assert.Equal(source.Id, mapped.ProviderLocator);
        Assert.True(mapped.IsPrimary);
        Assert.True(mapped.CanReadEvents);
        Assert.Equal("#12AB34", mapped.SourceColor?.ToHexString());

        source.AdditionalData["canReadEvents"] = false;
        Assert.False(MicrosoftModelMapper.ToCalendarDescriptor(source)!.CanReadEvents);
    }

    [Fact]
    public void TimedEvent_OffsetIsNormalizedAndZeroDurationIsAllowed()
    {
        var source = CreateTimed("2026-08-28T10:00:00+09:00", "2026-08-28T10:00:00+09:00");

        var timing = Assert.IsType<TimedEventTiming>(MapRequired(source).Timing);

        Assert.Equal(DateTimeOffset.Parse("2026-08-28T01:00:00Z"), timing.StartUtc);
        Assert.Equal(timing.StartUtc, timing.EndUtc);
        Assert.True(timing.IsZeroDuration);
    }

    [Fact]
    public void TimedEvent_EndBeforeStartIsSafelyExcluded()
    {
        var result = Map(CreateTimed("2026-08-28T11:00:00Z", "2026-08-28T10:00:00Z"));

        Assert.Null(result.Event);
        Assert.Equal(MicrosoftEventExclusionReason.InvalidTiming, result.ExclusionReason);
    }

    [Theory]
    [InlineData("2026-08-28T00:00:00.0000000", "2026-08-29T00:00:00.0000000", false)]
    [InlineData("2026-08-28T00:00:00.0000000", "2026-08-31T00:00:00.0000000", true)]
    public void AllDayEvent_PreservesDateOnlyAndExclusiveEnd(string start, string end, bool multiDay)
    {
        var source = CreateTimed(start, end);
        source.IsAllDay = true;

        var timing = Assert.IsType<AllDayEventTiming>(MapRequired(source).Timing);

        Assert.Equal(new DateOnly(2026, 8, 28), timing.StartDate);
        Assert.Equal(DateOnly.Parse(end.AsSpan(0, 10)), timing.EndDateExclusive);
        Assert.Equal(multiDay, timing.IsMultiDay);
    }

    [Fact]
    public void ModifiedOccurrence_KeepsStableKeyAndUsesCurrentContent()
    {
        var first = CreateOccurrence("Fixture original", "2026-08-28T10:00:00Z");
        var modified = CreateOccurrence("Fixture modified", "2026-08-28T12:00:00Z");
        modified.Id = "fixture-modified-occurrence-instance";

        var before = MapRequired(first);
        var after = MapRequired(modified);

        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.Key.ToStableId(), after.Key.ToStableId());
        Assert.Contains("fixture-series", after.Key.OccurrenceKey, StringComparison.Ordinal);
        Assert.Equal("fixture-series", after.Key.SourceEventId);
        Assert.Equal("Fixture modified", after.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T12:00:00Z"),
            Assert.IsType<TimedEventTiming>(after.Timing).StartUtc);
    }

    [Fact]
    public void Occurrence_UsesICalUidWhenSeriesMasterIdIsUnavailable()
    {
        var source = CreateOccurrence("Fixture", "2026-08-28T10:00:00Z");
        source.SeriesMasterId = null;

        var mapped = MapRequired(source);

        Assert.Equal("fixture-ical", mapped.Key.SourceEventId);
        Assert.Contains("fixture-ical", mapped.Key.OccurrenceKey, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledAndInvalidOccurrenceAreExcludedWithDifferentReasons()
    {
        var cancelled = CreateOccurrence("Fixture", "2026-08-28T10:00:00Z");
        cancelled.IsCancelled = true;
        var invalid = CreateOccurrence("Fixture", "2026-08-28T10:00:00Z");
        invalid.OriginalStart = null;

        Assert.Equal(MicrosoftEventExclusionReason.Cancelled, Map(cancelled).ExclusionReason);
        Assert.Equal(MicrosoftEventExclusionReason.InvalidOccurrenceKey, Map(invalid).ExclusionReason);
    }

    [Theory]
    [InlineData("accepted", AttendeeResponse.Accepted)]
    [InlineData("tentativelyAccepted", AttendeeResponse.Tentative)]
    [InlineData("notResponded", AttendeeResponse.NotResponded)]
    [InlineData("declined", AttendeeResponse.Declined)]
    [InlineData("organizer", AttendeeResponse.Unknown)]
    public void Participation_IsMapped(string response, AttendeeResponse expected)
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.ResponseStatus = new ResponseStatus
        {
            Response = Enum.Parse<ResponseType>(response, ignoreCase: true),
        };

        Assert.Equal(expected, MapRequired(source).ResponseStatus);
    }

    [Fact]
    public void CategoryColor_UsesFirstResolvableCategoryAndKeepsCalendarFallbackSeparate()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.Categories = ["Unknown", "Known"];
        var mapped = MapRequired(source, new Dictionary<string, RgbColor>
        {
            ["Known"] = RgbColor.Parse("#112233"),
        });

        Assert.Equal("#112233", mapped.SourceEventColor?.ToHexString());
        Assert.Equal("#445566", mapped.SourceCalendarColor?.ToHexString());
    }

    [Fact]
    public void UrlAndBodyMapping_AcceptsOnlyWebUrisAndConvertsHtmlToText()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.Body = new ItemBody
        {
            ContentType = Enum.Parse<BodyType>("html", true),
            Content = "<p>Fixture &amp; safe</p><div>Second</div>",
        };
        source.WebLink = "file:///fixture";
        source.OnlineMeetingUrl = "javascript:fixture";
        Assert.Equal("Fixture & safe\nSecond", MapRequired(source).DescriptionPlainText);
        Assert.Null(MapRequired(source).SourceDetailUri);
        Assert.Null(MapRequired(source).MeetingUri);

        source.WebLink = "https://calendar.example.test/fixture";
        source.OnlineMeeting = new OnlineMeetingInfo { JoinUrl = "https://meeting.example.test/fixture" };
        Assert.NotNull(MapRequired(source).SourceDetailUri);
        Assert.NotNull(MapRequired(source).MeetingUri);
    }

    private static GraphEvent CreateTimed(string start, string end) => new()
    {
        Id = "fixture-event",
        Subject = "Fixture title",
        Start = new DateTimeTimeZone { DateTime = start, TimeZone = "UTC" },
        End = new DateTimeTimeZone { DateTime = end, TimeZone = "UTC" },
        IsAllDay = false,
        IsCancelled = false,
    };

    private static GraphEvent CreateOccurrence(string title, string start)
    {
        var source = CreateTimed(start, DateTimeOffset.Parse(start).AddHours(1).ToString("O"));
        source.Subject = title;
        source.Type = Enum.Parse<EventType>("occurrence", true);
        source.SeriesMasterId = "fixture-series";
        source.ICalUId = "fixture-ical";
        source.OriginalStart = DateTimeOffset.Parse("2026-08-28T10:00:00Z");
        source.OriginalStartTimeZone = "UTC";
        return source;
    }

    private static MicrosoftEventMappingResult Map(
        GraphEvent source,
        IReadOnlyDictionary<string, RgbColor>? colors = null) =>
        MicrosoftModelMapper.ToCalendarEvent(
            source,
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            colors ?? new Dictionary<string, RgbColor>());

    private static CalendarEvent MapRequired(
        GraphEvent source,
        IReadOnlyDictionary<string, RgbColor>? colors = null) =>
        Map(source, colors).Event ?? throw new InvalidOperationException("Fixture event was excluded.");
}
