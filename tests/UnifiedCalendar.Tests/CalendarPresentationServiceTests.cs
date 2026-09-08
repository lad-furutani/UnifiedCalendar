using System.Globalization;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(CultureSensitiveCollection.Name)]
public sealed class CalendarPresentationServiceTests
{
    private static readonly Guid GoogleAccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MicrosoftAccountId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void PresentationText_UsesResourceKeysAndStructuredArguments()
    {
        var untitled = CreateTimed("untitled", " ", 11, 0, 12, 0);
        var allDay = CreateAllDay(
            "all-day",
            "All day",
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 28));
        var tomorrow = CreateTimed(
            "tomorrow",
            "Tomorrow",
            new DateTimeOffset(2026, 8, 28, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero));

        var result = Build(new[] { untitled, allDay, tomorrow }, Now);

        Assert.Equal(UiTextResourceKeys.DayHeaderToday, result.Days[0].Header.ResourceKey);
        Assert.Equal(new DateOnly(2026, 8, 27), Assert.IsType<DateOnly>(result.Days[0].Header.Arguments[0]));
        Assert.Equal(UiTextResourceKeys.DayHeaderTomorrow, result.Days[1].Header.ResourceKey);

        var presented = Find(result, "untitled");
        Assert.Equal(UiTextResourceKeys.EventUntitled, presented.Title.ResourceKey);
        Assert.Null(presented.Title.Literal);
        Assert.Equal(UiTextResourceKeys.EventListTimedRange, presented.ListTime.ResourceKey);
        Assert.Equal(new TimeOnly(11, 0), Assert.IsType<TimeOnly>(presented.ListTime.Arguments[0]));
        Assert.Equal(new TimeOnly(12, 0), Assert.IsType<TimeOnly>(presented.ListTime.Arguments[1]));
        Assert.Equal(UiTextResourceKeys.EventDetailTimedSingleDay, presented.DetailDateTime.ResourceKey);
        Assert.Equal(new DateOnly(2026, 8, 27), Assert.IsType<DateOnly>(presented.DetailDateTime.Arguments[0]));
        Assert.Equal(UiTextResourceKeys.EventTooltip, presented.Tooltip.ResourceKey);
        Assert.Same(presented.Title, presented.Tooltip.Arguments[0]);
        Assert.Same(presented.DetailDateTime, presented.Tooltip.Arguments[1]);

        var allDayPresented = Find(result, "all-day");
        Assert.Equal(UiTextResourceKeys.EventListAllDay, allDayPresented.ListTime.ResourceKey);
        Assert.Equal(UiTextResourceKeys.EventDetailAllDay, allDayPresented.DetailDateTime.ResourceKey);
    }

    [Fact]
    public void BuildSnapshot_CopiesColorRulesOnceBeforeReadingTimeOrProjectingEvents()
    {
        var initialColor = RgbColor.Parse("#CC0000");
        var laterColor = RgbColor.Parse("#0000CC");
        var initialRule = CreateProviderRule("initial", initialColor);
        var laterRule = CreateProviderRule("later", laterColor);
        var rules = new CountingReadOnlyList<ColorRule>(new[] { initialRule });
        var provider = new CallbackTimeProvider(Now, () =>
        {
            rules.Items.Clear();
            rules.Items.Add(laterRule);
        });
        var service = new CalendarPresentationService(provider);
        var events = new[]
        {
            CreateTimed("first", "First", 11, 0, 12, 0),
            CreateTimed("second", "Second", 12, 0, 13, 0),
        };

        var result = service.BuildSnapshot(
            CreateSnapshot(events),
            new DisplaySettings(),
            rules,
            TimeZoneInfo.Utc);

        Assert.Equal(1, rules.EnumerationCount);
        Assert.All(result.Events, item => Assert.Equal(initialColor, item.BackgroundColor));
        Assert.Same(laterRule, Assert.Single(rules.Items));
    }

    [Fact]
    public void Acc002_SortsDateGroupsChronologically()
    {
        using var culture = new CultureScope("en-US");
        var events = new[]
        {
            CreateAllDay("later", "Later", new DateOnly(2026, 8, 29), new DateOnly(2026, 8, 30)),
            CreateAllDay("today", "Today", new DateOnly(2026, 8, 27), new DateOnly(2026, 8, 28)),
            CreateAllDay("tomorrow", "Tomorrow", new DateOnly(2026, 8, 28), new DateOnly(2026, 8, 29)),
        };

        var result = Build(events, Now);

        Assert.Equal(
            new[]
            {
                new DateOnly(2026, 8, 27),
                new DateOnly(2026, 8, 28),
                new DateOnly(2026, 8, 29),
            },
            result.Days.Select(day => day.Date));
    }

    [Fact]
    public void Acc002_SortsAllDayBeforeTimedEvents()
    {
        using var culture = new CultureScope("en-US");
        var timed = CreateTimed("timed", "A timed event", 11, 0, 12, 0);
        var allDay = CreateAllDay(
            "all-day",
            "Z all day",
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 28));

        var result = Build(new[] { timed, allDay }, Now);

        Assert.Equal(new[] { "all-day", "timed" }, EventIds(result));
    }

    [Fact]
    public void Acc002_SortsOngoingAndFutureEventsByLocalStartNotProgress()
    {
        using var culture = new CultureScope("en-US");
        var utcPlusNine = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+09-test",
            TimeSpan.FromHours(9),
            "UTC+09-test",
            "UTC+09-test");
        var nowUtc = new DateTimeOffset(2026, 8, 27, 1, 30, 0, TimeSpan.Zero);
        var events = new[]
        {
            CreateTimed(
                "future",
                "A future",
                new DateTimeOffset(2026, 8, 27, 2, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 27, 4, 0, 0, TimeSpan.Zero)),
            CreateTimed(
                "ongoing-later",
                "B ongoing later",
                new DateTimeOffset(2026, 8, 27, 1, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 27, 3, 0, 0, TimeSpan.Zero)),
            CreateTimed(
                "ongoing-earlier",
                "Z ongoing earlier",
                new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 27, 3, 0, 0, TimeSpan.Zero)),
        };

        var result = Build(events, nowUtc, localTimeZone: utcPlusNine);

        Assert.Equal(
            new[] { "ongoing-earlier", "ongoing-later", "future" },
            EventIds(result));
        Assert.NotNull(Find(result, "ongoing-earlier").Progress);
        Assert.NotNull(Find(result, "ongoing-later").Progress);
        Assert.Null(Find(result, "future").Progress);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(Find(result, "ongoing-earlier").LocalStart!.Value.DateTime));
    }

    [Fact]
    public void Acc002_SameStartUsesCurrentCultureIgnoreCaseTitleWithoutProviderPriority()
    {
        using var culture = new CultureScope("en-US");
        var google = CreateTimed("google", "Zulu", 11, 0, 12, 0, ProviderKind.Google);
        var microsoft = CreateTimed("microsoft", "alpha", 11, 0, 12, 0, ProviderKind.Microsoft);
        var beta = CreateTimed("beta", "Beta", 11, 0, 12, 0, ProviderKind.Google);

        var result = Build(new[] { google, beta, microsoft }, Now);

        Assert.Equal(new[] { "microsoft", "beta", "google" }, EventIds(result));
        Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
    }

    [Fact]
    public void Acc002_AllDayCompleteTieUsesStableId()
    {
        using var culture = new CultureScope("en-US");
        var events = new[]
        {
            CreateAllDay("event-2", "Same", new DateOnly(2026, 8, 27), new DateOnly(2026, 8, 28)),
            CreateAllDay("event-1", "same", new DateOnly(2026, 8, 27), new DateOnly(2026, 8, 28)),
        };

        AssertStableIdOrder(Build(events, Now));
    }

    [Fact]
    public void Acc002_TimedCompleteTieUsesStableId()
    {
        using var culture = new CultureScope("en-US");
        var events = new[]
        {
            CreateTimed("event-2", "Same", 11, 0, 12, 0),
            CreateTimed("event-1", "same", 11, 0, 12, 0),
        };

        AssertStableIdOrder(Build(events, Now));
    }

    [Fact]
    public void Acc003_MutableTimeProviderAdvancesProgressAndRemovesAtEnd()
    {
        var timeProvider = new MutableTimeProvider(
            new DateTimeOffset(2026, 8, 27, 9, 59, 0, TimeSpan.Zero));
        var service = new CalendarPresentationService(timeProvider);
        var timed = CreateTimed("timed", "Timed", 10, 0, 11, 0);
        var allDay = CreateAllDay(
            "all-day",
            "All day",
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 28));
        var source = CreateSnapshot(new[] { timed, allDay });

        var beforeStart = Build(service, source);
        Assert.Null(Find(beforeStart, "timed").Progress);
        Assert.Null(Find(beforeStart, "all-day").Progress);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 27, 10, 1, 0, TimeSpan.Zero));
        var afterStart = Build(service, source);
        var firstProgress = Find(afterStart, "timed").Progress!.Value;
        Assert.InRange(firstProgress, 0d, 1d);
        Assert.Null(Find(afterStart, "all-day").Progress);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 27, 10, 31, 0, TimeSpan.Zero));
        var later = Build(service, source);
        var laterProgress = Find(later, "timed").Progress!.Value;
        Assert.True(laterProgress > firstProgress);
        Assert.InRange(laterProgress, 0d, 1d);
        Assert.Null(Find(later, "all-day").Progress);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 27, 11, 0, 0, TimeSpan.Zero));
        var atEnd = Build(service, source);
        Assert.DoesNotContain(atEnd.Events, item => item.Key == timed.Key);
        Assert.Null(Find(atEnd, "all-day").Progress);
    }

    [Fact]
    public void Acc004_MultiDayEventsAppearOnceInTheirRequiredGroupAndDisappearAtBoundaries()
    {
        var timeProvider = new MutableTimeProvider(Now);
        var service = new CalendarPresentationService(timeProvider);
        var allDay = CreateAllDay(
            "all-day",
            "Conference",
            new DateOnly(2026, 8, 26),
            new DateOnly(2026, 8, 30));
        var pastTimed = CreateTimed(
            "past-timed",
            "Past ongoing",
            new DateTimeOffset(2026, 8, 26, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 2, 0, 0, TimeSpan.Zero));
        var futureTimed = CreateTimed(
            "future-timed",
            "Future multi-day",
            new DateTimeOffset(2026, 8, 28, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 29, 1, 0, 0, TimeSpan.Zero));
        var source = CreateSnapshot(new[] { allDay, pastTimed, futureTimed });

        var during = Build(service, source);

        Assert.Equal(new DateOnly(2026, 8, 27), Find(during, "all-day").GroupDate);
        Assert.Equal(new DateOnly(2026, 8, 27), Find(during, "past-timed").GroupDate);
        Assert.Equal(new DateOnly(2026, 8, 28), Find(during, "future-timed").GroupDate);
        Assert.Equal(UiTextResourceKeys.EventListAllDayRange, Find(during, "all-day").ListTime.ResourceKey);
        Assert.All(
            new[] { "all-day", "past-timed", "future-timed" },
            id => Assert.Single(during.Days.SelectMany(day => day.Events), item => item.Key.SourceEventId == id));
        Assert.Equal(3, during.Days.Sum(day => day.Events.Count));

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 29, 1, 0, 0, TimeSpan.Zero));
        var timedEnd = Build(service, source);
        Assert.DoesNotContain(timedEnd.Events, item => item.Key == pastTimed.Key);
        Assert.DoesNotContain(timedEnd.Events, item => item.Key == futureTimed.Key);
        Assert.Single(timedEnd.Events, item => item.Key == allDay.Key);

        timeProvider.SetUtcNow(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        var allDayEnd = Build(service, source);
        Assert.Empty(allDayEnd.Events);
        Assert.Empty(allDayEnd.Days);
    }

    [Fact]
    public void ZeroDuration_IsStructuredAsInstantBeforeStartAndRemovedAtStart()
    {
        var instant = CreateTimed("instant", "Instant", 10, 31, 10, 31);

        var before = Build(new[] { instant }, Now);
        var atStart = Build(
            new[] { instant },
            new DateTimeOffset(2026, 8, 27, 10, 31, 0, TimeSpan.Zero));

        var presented = Assert.Single(before.Events);
        Assert.Equal(UiTextResourceKeys.EventListInstant, presented.ListTime.ResourceKey);
        Assert.Equal(new TimeOnly(10, 31), Assert.IsType<TimeOnly>(presented.ListTime.Arguments[0]));
        Assert.Null(presented.Progress);
        Assert.Empty(atStart.Events);
    }

    [Fact]
    public void ParticipationStatus_UsesResourceKeysAndHidesAcceptedAttention()
    {
        var events = new[]
        {
            CreateTimed("accepted", "Accepted", 11, 0, 12, 0, response: AttendeeResponse.Accepted),
            CreateTimed("tentative", "Tentative", 12, 0, 13, 0, response: AttendeeResponse.Tentative),
            CreateTimed("not-responded", "Not responded", 13, 0, 14, 0, response: AttendeeResponse.NotResponded),
            CreateTimed("declined", "Declined", 14, 0, 15, 0, response: AttendeeResponse.Declined),
            CreateTimed("unknown", "Unknown", 15, 0, 16, 0, response: AttendeeResponse.Unknown),
        };

        var result = Build(events, Now);

        var accepted = Find(result, "accepted");
        Assert.Null(accepted.AttentionResponse);
        Assert.Equal(UiTextResourceKeys.AttendeeAccepted, accepted.DetailResponseText!.ResourceKey);
        AssertAttentionResponse(result, "tentative", UiTextResourceKeys.AttendeeTentative);
        AssertAttentionResponse(result, "not-responded", UiTextResourceKeys.AttendeeNotResponded);
        AssertAttentionResponse(result, "declined", UiTextResourceKeys.AttendeeDeclined);
        var unknown = Find(result, "unknown");
        AssertAttentionResponse(result, "unknown", UiTextResourceKeys.AttendeeUnknown);
        Assert.Null(unknown.DetailResponseText);
        Assert.Equal(UiTextResourceKeys.EventTooltipWithAttention, unknown.Tooltip.ResourceKey);
    }

    [Fact]
    public void ColorPriority_IsFirstEnabledRuleThenEventThenCalendarThenDefault()
    {
        var ruleColor = RgbColor.Parse("#CC0000");
        var eventColor = RgbColor.Parse("#006600");
        var calendarColor = RgbColor.Parse("#000066");
        var defaultColor = RgbColor.Parse("#222222");
        var custom = CreateTimed(
            "custom",
            "  DAILY StandUp  ",
            11,
            0,
            12,
            0,
            ProviderKind.Microsoft,
            sourceEventColor: eventColor,
            sourceCalendarColor: calendarColor);
        var sourceEvent = CreateTimed(
            "source-event",
            "Source event",
            12,
            0,
            13,
            0,
            sourceEventColor: eventColor,
            sourceCalendarColor: calendarColor);
        var sourceCalendar = CreateTimed(
            "source-calendar",
            "Source calendar",
            13,
            0,
            14,
            0,
            sourceCalendarColor: calendarColor);
        var fallback = CreateTimed("fallback", "Fallback", 14, 0, 15, 0);
        var rules = new[]
        {
            new ColorRule(
                "disabled",
                false,
                ColorRuleOperator.Any,
                new[] { ColorRuleCondition.ForTitle(TextMatchKind.Contains, "standup") },
                RgbColor.Parse("#FF00FF")),
            new ColorRule(
                "first enabled match",
                true,
                ColorRuleOperator.All,
                new[]
                {
                    ColorRuleCondition.ForTitle(TextMatchKind.Contains, " standup "),
                    ColorRuleCondition.ForProvider(ProviderKind.Microsoft),
                },
                ruleColor),
            new ColorRule(
                "later match",
                true,
                ColorRuleOperator.Any,
                new[] { ColorRuleCondition.ForProvider(ProviderKind.Microsoft) },
                RgbColor.Parse("#00FFFF")),
        };

        var result = Build(
            new[] { custom, sourceEvent, sourceCalendar, fallback },
            Now,
            rules,
            new DisplaySettings(defaultEventColor: defaultColor));

        Assert.Equal(ruleColor, Find(result, "custom").BackgroundColor);
        Assert.Equal(eventColor, Find(result, "source-event").BackgroundColor);
        Assert.Equal(calendarColor, Find(result, "source-calendar").BackgroundColor);
        Assert.Equal(defaultColor, Find(result, "fallback").BackgroundColor);
        Assert.Equal(RgbColor.Parse("#FFFFFF"), Find(result, "fallback").ForegroundColor);
        Assert.True(Find(result, "fallback").ElapsedColor.GetRelativeLuminance()
            < Find(result, "fallback").RemainingColor.GetRelativeLuminance());
    }

    [Fact]
    public void ColorRuleComparison_DoesNotNormalizeFullWidthCharacters()
    {
        var matchedColor = RgbColor.Parse("#FF0000");
        var defaultColor = RgbColor.Parse("#0000FF");
        var calendarEvent = CreateTimed("full-width", "ＡＢＣ", 11, 0, 12, 0);
        var rules = new[]
        {
            new ColorRule(
                "ascii",
                true,
                ColorRuleOperator.All,
                new[] { ColorRuleCondition.ForTitle(TextMatchKind.Exact, "ABC") },
                matchedColor),
        };

        var result = Build(
            new[] { calendarEvent },
            Now,
            rules,
            new DisplaySettings(defaultEventColor: defaultColor));

        Assert.Equal(defaultColor, Assert.Single(result.Events).BackgroundColor);
    }

    [Fact]
    public void FiltersCancelledDisabledOffEndedAndOutOfRangeEvents()
    {
        var disabledAccountEvent = CreateTimed(
            "disabled-account",
            "Disabled",
            11,
            0,
            12,
            0,
            ProviderKind.Microsoft);
        var offCalendarEvent = CreateTimed("off-calendar", "Off", 11, 0, 12, 0, calendarId: "off");
        var cancelled = CreateTimed("cancelled", "Cancelled", 11, 0, 12, 0, isCancelled: true);
        var ended = CreateTimed("ended", "Ended", 9, 0, 10, 0);
        var outOfRange = CreateTimed(
            "out-of-range",
            "Out",
            new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
        var visible = CreateTimed("visible", "Visible", 11, 0, 12, 0);
        var events = new[] { disabledAccountEvent, offCalendarEvent, cancelled, ended, outOfRange, visible };
        var snapshot = CreateSnapshot(
            events,
            key => key.CalendarId != "off",
            accountId => accountId != MicrosoftAccountId);
        var service = new CalendarPresentationService(new MutableTimeProvider(Now));

        var result = Build(service, snapshot);

        Assert.Equal("visible", Assert.Single(result.Events).Key.SourceEventId);
    }

    [Fact]
    public void PresentationSnapshot_CachesFlatReadOnlyEventsConsistentWithDays()
    {
        var events = new[]
        {
            CreateTimed("today", "Today", 11, 0, 12, 0),
            CreateTimed(
                "tomorrow",
                "Tomorrow",
                new DateTimeOffset(2026, 8, 28, 11, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero)),
        };

        var result = Build(events, Now);
        var first = result.Events;
        var second = result.Events;

        Assert.Same(first, second);
        Assert.Equal(
            result.Days.SelectMany(day => day.Events).Select(item => item.Key),
            first.Select(item => item.Key));
        var mutableView = Assert.IsAssignableFrom<IList<PresentedEvent>>(first);
        Assert.True(mutableView.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => mutableView[0] = mutableView[1]);
    }

    [Fact]
    public void BuildSnapshot_ReadsInjectedTimeProviderExactlyOnce()
    {
        var calls = 0;
        var provider = new CallbackTimeProvider(Now, () => calls++);
        var service = new CalendarPresentationService(provider);
        var calendarEvent = CreateTimed("event", "Event", 11, 0, 12, 0);

        Build(service, CreateSnapshot(new[] { calendarEvent }));

        Assert.Equal(1, calls);
    }

    private static void AssertStableIdOrder(PresentationSnapshot snapshot)
    {
        var actual = snapshot.Events.Select(item => item.StableId).ToArray();
        var expected = snapshot.Events
            .Select(item => item.StableId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static void AssertAttentionResponse(
        PresentationSnapshot snapshot,
        string sourceEventId,
        string responseResourceKey)
    {
        var attentionText = Find(snapshot, sourceEventId).AttentionResponseText!;
        Assert.Equal(UiTextResourceKeys.AttendeeStatus, attentionText.ResourceKey);
        var responseText = Assert.IsType<UiText>(attentionText.Arguments[0]);
        Assert.Equal(responseResourceKey, responseText.ResourceKey);
    }

    private static string[] EventIds(PresentationSnapshot snapshot) =>
        snapshot.Events.Select(item => item.Key.SourceEventId).ToArray();

    private static PresentedEvent Find(PresentationSnapshot snapshot, string sourceEventId) =>
        Assert.Single(snapshot.Events, item => item.Key.SourceEventId == sourceEventId);

    private static PresentationSnapshot Build(
        IEnumerable<CalendarEvent> events,
        DateTimeOffset now,
        IReadOnlyList<ColorRule>? rules = null,
        DisplaySettings? settings = null,
        TimeZoneInfo? localTimeZone = null)
    {
        var service = new CalendarPresentationService(new MutableTimeProvider(now));
        return service.BuildSnapshot(
            CreateSnapshot(events),
            settings ?? new DisplaySettings(),
            rules ?? Array.Empty<ColorRule>(),
            localTimeZone ?? TimeZoneInfo.Utc);
    }

    private static PresentationSnapshot Build(
        CalendarPresentationService service,
        SyncSnapshot source) => service.BuildSnapshot(
            source,
            new DisplaySettings(),
            Array.Empty<ColorRule>(),
            TimeZoneInfo.Utc);

    private static SyncSnapshot CreateSnapshot(
        IEnumerable<CalendarEvent> source,
        Func<EventKey, bool>? isVisible = null,
        Func<Guid, bool>? isEnabled = null)
    {
        var events = source.ToArray();
        var accounts = events
            .GroupBy(calendarEvent => calendarEvent.Key.InternalAccountId)
            .Select(group => new CalendarAccount(
                group.Key,
                group.First().Key.Provider,
                "subject",
                "Account",
                "user@example.invalid",
                isEnabled?.Invoke(group.Key) ?? true,
                $"{group.First().Key.Provider.ToString().ToLowerInvariant()}/{group.Key:N}"))
            .ToArray();
        var selections = events
            .Select(calendarEvent => calendarEvent.Key)
            .DistinctBy(key => (key.InternalAccountId, key.CalendarId))
            .Select(key => new CalendarSelection(
                key.InternalAccountId,
                key.CalendarId,
                isVisible?.Invoke(key) ?? true))
            .ToArray();

        return new SyncSnapshot(accounts, selections, events, Now);
    }

    private static ColorRule CreateProviderRule(string name, RgbColor color) => new(
        name,
        true,
        ColorRuleOperator.All,
        new[] { ColorRuleCondition.ForProvider(ProviderKind.Google) },
        color);

    private static CalendarEvent CreateTimed(
        string eventId,
        string title,
        int startHour,
        int startMinute,
        int endHour,
        int endMinute,
        ProviderKind provider = ProviderKind.Google,
        AttendeeResponse response = AttendeeResponse.Accepted,
        RgbColor? sourceEventColor = null,
        RgbColor? sourceCalendarColor = null,
        string calendarId = "visible",
        bool isCancelled = false) => CreateTimed(
            eventId,
            title,
            new DateTimeOffset(2026, 8, 27, startHour, startMinute, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, endHour, endMinute, 0, TimeSpan.Zero),
            provider,
            response,
            sourceEventColor,
            sourceCalendarColor,
            calendarId,
            isCancelled);

    private static CalendarEvent CreateTimed(
        string eventId,
        string title,
        DateTimeOffset start,
        DateTimeOffset end,
        ProviderKind provider = ProviderKind.Google,
        AttendeeResponse response = AttendeeResponse.Accepted,
        RgbColor? sourceEventColor = null,
        RgbColor? sourceCalendarColor = null,
        string calendarId = "visible",
        bool isCancelled = false) => new(
            CreateKey(eventId, provider, calendarId),
            title,
            new TimedEventTiming(start, end),
            "Calendar",
            responseStatus: response,
            sourceEventColor: sourceEventColor,
            sourceCalendarColor: sourceCalendarColor,
            isCancelled: isCancelled);

    private static CalendarEvent CreateAllDay(
        string eventId,
        string title,
        DateOnly start,
        DateOnly endExclusive,
        ProviderKind provider = ProviderKind.Google) => new(
            CreateKey(eventId, provider, "visible"),
            title,
            new AllDayEventTiming(start, endExclusive),
            "Calendar",
            responseStatus: AttendeeResponse.Accepted);

    private static EventKey CreateKey(
        string eventId,
        ProviderKind provider,
        string calendarId) => new(
            provider,
            provider == ProviderKind.Google ? GoogleAccountId : MicrosoftAccountId,
            calendarId,
            eventId);
}
