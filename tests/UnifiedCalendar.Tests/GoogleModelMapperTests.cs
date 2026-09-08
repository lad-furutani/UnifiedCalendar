using Google.Apis.Calendar.v3.Data;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Providers.Google;
using Xunit;
using GoogleEvent = Google.Apis.Calendar.v3.Data.Event;

namespace UnifiedCalendar.Tests;

public sealed class GoogleModelMapperTests
{
    [Fact]
    public void TimedEvent_UtcIsPreserved()
    {
        var source = CreateTimed(
            "2026-08-28T10:00:00Z",
            "2026-08-28T11:00:00Z");

        var result = Map(source);

        Assert.NotNull(result);
        var timing = Assert.IsType<TimedEventTiming>(result.Timing);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T10:00:00Z"), timing.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T11:00:00Z"), timing.EndUtc);
    }

    [Fact]
    public void TimedEvent_OffsetIsNormalizedToUtc()
    {
        var source = CreateTimed(
            "2026-08-28T10:00:00+09:00",
            "2026-08-28T11:30:00+09:00");

        var timing = Assert.IsType<TimedEventTiming>(MapRequired(source).Timing);

        Assert.Equal(DateTimeOffset.Parse("2026-08-28T01:00:00Z"), timing.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T02:30:00Z"), timing.EndUtc);
    }

    [Fact]
    public void TimedEvent_ZeroDurationIsAllowed()
    {
        var source = CreateTimed(
            "2026-08-28T10:00:00Z",
            "2026-08-28T10:00:00Z");

        var timing = Assert.IsType<TimedEventTiming>(MapRequired(source).Timing);

        Assert.True(timing.IsZeroDuration);
    }

    [Fact]
    public void TimedEvent_EndBeforeStartIsExcluded()
    {
        var source = CreateTimed(
            "2026-08-28T11:00:00Z",
            "2026-08-28T10:00:00Z");

        var result = MapResult(source);
        Assert.Null(result.Event);
        Assert.Equal(GoogleEventExclusionReason.InvalidTiming, result.ExclusionReason);
    }

    [Theory]
    [InlineData("2026-08-28", "2026-08-29", false)]
    [InlineData("2026-08-28", "2026-08-31", true)]
    public void AllDayEvent_PreservesDateOnlyExclusiveEnd(
        string start,
        string end,
        bool isMultiDay)
    {
        var source = new GoogleEvent
        {
            Id = "fixture-all-day",
            Status = "confirmed",
            Start = new EventDateTime { Date = start },
            End = new EventDateTime { Date = end },
        };

        var timing = Assert.IsType<AllDayEventTiming>(MapRequired(source).Timing);

        Assert.Equal(DateOnly.Parse(start), timing.StartDate);
        Assert.Equal(DateOnly.Parse(end), timing.EndDateExclusive);
        Assert.Equal(isMultiDay, timing.IsMultiDay);
    }

    [Fact]
    public void RecurringModifiedOccurrence_UsesOriginalIdentityAndCurrentContent()
    {
        var first = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        first.Id = "fixture-occurrence";
        first.RecurringEventId = "fixture-series";
        first.OriginalStartTime = new EventDateTime { DateTimeRaw = "2026-08-28T09:00:00+09:00" };
        first.Summary = "Fixture original";
        var modified = CreateTimed("2026-08-28T12:00:00Z", "2026-08-28T13:00:00Z");
        modified.Id = first.Id;
        modified.RecurringEventId = first.RecurringEventId;
        modified.OriginalStartTime = new EventDateTime { DateTimeRaw = "2026-08-28T00:00:00Z" };
        modified.Summary = "Fixture modified";

        var before = MapRequired(first);
        var after = MapRequired(modified);

        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.Key.ToStableId(), after.Key.ToStableId());
        Assert.Equal("Fixture modified", after.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T12:00:00Z"), Assert.IsType<TimedEventTiming>(after.Timing).StartUtc);
    }

    [Fact]
    public void CancelledOccurrence_IsExcluded()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.Status = "cancelled";
        source.RecurringEventId = "fixture-series";
        source.OriginalStartTime = new EventDateTime { DateTimeRaw = "2026-08-28T10:00:00Z" };

        var result = MapResult(source);
        Assert.Null(result.Event);
        Assert.Equal(GoogleEventExclusionReason.Cancelled, result.ExclusionReason);
    }

    [Fact]
    public void RecurringOccurrenceWithoutOriginalStart_HasSafeExclusionReason()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.RecurringEventId = "fixture-series";

        var result = MapResult(source);

        Assert.Null(result.Event);
        Assert.Equal(GoogleEventExclusionReason.InvalidOccurrenceKey, result.ExclusionReason);
    }

    [Fact]
    public void MissingEventId_HasSafeExclusionReason()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.Id = null;

        var result = MapResult(source);

        Assert.Null(result.Event);
        Assert.Equal(GoogleEventExclusionReason.MissingIdentifier, result.ExclusionReason);
    }

    [Theory]
    [InlineData("accepted", AttendeeResponse.Accepted)]
    [InlineData("tentative", AttendeeResponse.Tentative)]
    [InlineData("needsAction", AttendeeResponse.NotResponded)]
    [InlineData("declined", AttendeeResponse.Declined)]
    [InlineData("fixtureUnknown", AttendeeResponse.Unknown)]
    public void Participation_UsesSelfAttendee(string response, AttendeeResponse expected)
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.Attendees =
        [
            new EventAttendee { ResponseStatus = "declined" },
            new EventAttendee { Self = true, ResponseStatus = response },
        ];

        Assert.Equal(expected, MapRequired(source).ResponseStatus);
    }

    [Fact]
    public void Participation_WithoutSelfDoesNotUseAnotherAttendee()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.Attendees = [new EventAttendee { ResponseStatus = "accepted" }];

        Assert.Equal(AttendeeResponse.Unknown, MapRequired(source).ResponseStatus);
    }

    [Fact]
    public void Urls_PreferVideoConferenceThenUseHangoutFallback()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.HtmlLink = "https://calendar.example.test/fixture";
        source.HangoutLink = "https://meet.example.test/fallback";
        source.ConferenceData = new ConferenceData
        {
            EntryPoints =
            [
                new EntryPoint { EntryPointType = "phone", Uri = "tel:+10000000000" },
                new EntryPoint { EntryPointType = "video", Uri = "https://meet.example.test/preferred" },
            ],
        };

        var mapped = MapRequired(source);

        Assert.Equal("https://calendar.example.test/fixture", mapped.SourceDetailUri?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("https://meet.example.test/preferred", mapped.MeetingUri?.AbsoluteUri.TrimEnd('/'));

        source.ConferenceData = new ConferenceData
        {
            EntryPoints = [new EntryPoint { EntryPointType = "video", Uri = "javascript:fixture" }],
        };
        Assert.Equal(
            "https://meet.example.test/fallback",
            MapRequired(source).MeetingUri?.AbsoluteUri.TrimEnd('/'));
    }

    [Fact]
    public void Urls_RejectNonHttpAndRelativeValues()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.HtmlLink = "file:///fixture";
        source.HangoutLink = "/relative/fixture";

        var mapped = MapRequired(source);

        Assert.Null(mapped.SourceDetailUri);
        Assert.Null(mapped.MeetingUri);
    }

    [Fact]
    public void Colors_EventColorAndCalendarFallbackRemainSeparate()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.ColorId = "known";
        var colors = new Dictionary<string, RgbColor> { ["known"] = RgbColor.Parse("#ABCDEF") };

        var known = MapRequired(source, colors);
        Assert.Equal("#ABCDEF", known.SourceEventColor?.ToHexString());
        Assert.Equal("#445566", known.SourceCalendarColor?.ToHexString());

        source.ColorId = "unknown";
        var unknown = MapRequired(source, colors);
        Assert.Null(unknown.SourceEventColor);
        Assert.Equal("#445566", unknown.SourceCalendarColor?.ToHexString());
    }

    [Fact]
    public void Description_StripsHtmlDecodesEntitiesAndPreservesLocationAsText()
    {
        var source = CreateTimed("2026-08-28T10:00:00Z", "2026-08-28T11:00:00Z");
        source.Description = "<p>Fixture &amp; safe</p><div>Second</div><script>text only</script>";
        source.Location = "Fixture location";

        var mapped = MapRequired(source);

        Assert.Equal("Fixture & safe\nSecond\ntext only", mapped.DescriptionPlainText);
        Assert.Equal("Fixture location", mapped.Location);
    }

    private static GoogleEvent CreateTimed(string start, string end) => new()
    {
        Id = "fixture-event",
        Summary = "Fixture title",
        Status = "confirmed",
        Start = new EventDateTime { DateTimeRaw = start },
        End = new EventDateTime { DateTimeRaw = end },
    };

    private static CalendarEvent? Map(
        GoogleEvent source,
        IReadOnlyDictionary<string, RgbColor>? colors = null) =>
        MapResult(source, colors).Event;

    private static GoogleEventMappingResult MapResult(
        GoogleEvent source,
        IReadOnlyDictionary<string, RgbColor>? colors = null) =>
        GoogleModelMapper.ToCalendarEvent(
            source,
            GoogleProviderSamples.Account,
            GoogleProviderSamples.Calendar,
            colors ?? new Dictionary<string, RgbColor>());

    private static CalendarEvent MapRequired(
        GoogleEvent source,
        IReadOnlyDictionary<string, RgbColor>? colors = null)
    {
        var result = Map(source, colors);
        Assert.NotNull(result);
        return result;
    }
}
