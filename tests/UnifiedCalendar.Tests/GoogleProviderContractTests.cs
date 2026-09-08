using System.Net;
using NSubstitute;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Google;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class GoogleProviderContractTests
{
    [Fact]
    public async Task CalendarList_OnePageMapsPrimaryAndColor()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson("""
            {"items":[{"id":"primary@example.test","summary":"Primary Fixture","primary":true,"accessRole":"owner","backgroundColor":"#123abc"}]}
            """);
        var provider = CreateProvider(handler);

        var calendars = await provider.ListCalendarsAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken);

        var calendar = Assert.Single(calendars);
        Assert.Equal("primary@example.test", calendar.CalendarId);
        Assert.Equal(calendar.CalendarId, calendar.ProviderLocator);
        Assert.Equal("Primary Fixture", calendar.Name);
        Assert.True(calendar.IsPrimary);
        Assert.True(calendar.CanReadEvents);
        Assert.Equal("#123ABC", calendar.SourceColor?.ToHexString());
        Assert.Single(handler.RequestedUris);
        Assert.Contains("showHidden=true", handler.RequestedUris[0].Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("showDeleted=false", handler.RequestedUris[0].Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CalendarList_AllPagesIncludeSharedSubscribedAndFreeBusyEntries()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson(GoogleProviderSamples.LoadFixture("calendar-list-page-1.json"));
        handler.EnqueueJson(GoogleProviderSamples.LoadFixture("calendar-list-page-2.json"));
        var provider = CreateProvider(handler);

        var calendars = await provider.ListCalendarsAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.Equal(5, calendars.Count);
        Assert.True(calendars.Single(calendar => calendar.IsPrimary).CanReadEvents);
        Assert.True(calendars.Single(calendar => calendar.Name == "Fixture Shared").CanReadEvents);
        Assert.True(calendars.Single(calendar => calendar.Name == "Fixture Subscription").CanReadEvents);
        Assert.False(calendars.Single(calendar => calendar.Name == "Fixture Free Busy").CanReadEvents);
        var hiddenWriter = calendars.Single(calendar => calendar.Name == "Fixture Hidden Writer");
        Assert.True(hiddenWriter.CanReadEvents);
        Assert.Equal("#667788", hiddenWriter.SourceColor?.ToHexString());
        Assert.Equal("#445566", calendars[1].SourceColor?.ToHexString());
        Assert.Null(calendars[2].SourceColor);
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.Contains("pageToken=calendar-page-2", handler.RequestedUris[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriterWithoutPrivateAccessCalendar_IsReadableAndEventFetchIsNotShortCircuited()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson("""
            {"items":[{"id":"limited-writer@example.test","summary":"Fixture Limited Writer","accessRole":"writerWithoutPrivateAccess","hidden":true}]}
            """);
        handler.EnqueueJson("""
            {"items":[{"id":"limited-writer-event","summary":"Fixture event","status":"confirmed","start":{"dateTime":"2026-08-28T10:00:00Z"},"end":{"dateTime":"2026-08-28T11:00:00Z"}}]}
            """);
        var provider = CreateProvider(handler);
        var calendar = Assert.Single(await provider.ListCalendarsAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken));

        var result = await provider.GetEventsAsync(
            GoogleProviderSamples.Account,
            calendar,
            GoogleProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.True(calendar.CanReadEvents);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Events);
        Assert.Equal(2, handler.RequestedUris.Count);
        Assert.Contains("limited-writer%40example.test/events", handler.RequestedUris[1].AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CalendarList_MiddlePageFailureIsNotReportedAsSuccess()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson(GoogleProviderSamples.LoadFixture("calendar-list-page-1.json"));
        handler.Enqueue(_ => throw new HttpRequestException("fixture network failure"));
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.ListCalendarsAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken));

        Assert.Equal(ProviderErrorCategory.Network, exception.Error.Category);
        Assert.Equal(2, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task Events_AllPagesApplyContractAndMapCommonModels()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson(GoogleProviderSamples.LoadFixture("events-page-1.json"));
        handler.EnqueueJson(GoogleProviderSamples.LoadFixture("events-page-2.json"));
        handler.EnqueueJson(GoogleProviderSamples.LoadFixture("colors.json"));
        var provider = CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            GoogleProviderSamples.Account,
            GoogleProviderSamples.Calendar,
            GoogleProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Events.Count);

        var timed = result.Events.Single(calendarEvent => calendarEvent.Key.SourceEventId == "fixture-event-1");
        var timing = Assert.IsType<TimedEventTiming>(timed.Timing);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T00:00:00Z"), timing.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T01:00:00Z"), timing.EndUtc);
        Assert.Equal("First\nSecond & safe", timed.DescriptionPlainText);
        Assert.Equal(AttendeeResponse.Accepted, timed.ResponseStatus);
        Assert.Equal("#778899", timed.SourceEventColor?.ToHexString());
        Assert.Equal("#445566", timed.SourceCalendarColor?.ToHexString());
        Assert.Equal("https://meet.example.test/fixture-meeting", timed.MeetingUri?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("https://calendar.example.test/event/fixture-event-1", timed.SourceDetailUri?.AbsoluteUri.TrimEnd('/'));

        var occurrence = result.Events.Single(calendarEvent => calendarEvent.Key.SourceEventId == "fixture-recurring-instance");
        var allDay = Assert.IsType<AllDayEventTiming>(occurrence.Timing);
        Assert.Equal(new DateOnly(2026, 8, 30), allDay.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 1), allDay.EndDateExclusive);
        Assert.Contains("fixture-series", occurrence.Key.OccurrenceKey, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Events, calendarEvent => calendarEvent.Key.SourceEventId == "fixture-cancelled-instance");

        Assert.Equal(3, handler.RequestedUris.Count);
        var firstQuery = handler.RequestedUris[0].Query;
        Assert.Contains("singleEvents=true", firstQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("showDeleted=false", firstQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orderBy=startTime", firstQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timeMin=", firstQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timeMax=", firstQuery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pageToken=events-page-2", handler.RequestedUris[1].Query, StringComparison.Ordinal);
        Assert.EndsWith("/calendar/v3/colors", handler.RequestedUris[2].AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_MiddlePageFailureReturnsFailureWithoutPartialEvents()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson(GoogleProviderSamples.LoadFixture("events-page-1.json"));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"code\":503,\"message\":\"fixture\"}}"),
        });
        var provider = CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            GoogleProviderSamples.Account,
            GoogleProviderSamples.Calendar,
            GoogleProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Events);
        Assert.Equal(ProviderErrorCategory.ServerError, result.Error?.Category);
        Assert.Equal(503, result.Error?.HttpStatusCode);
    }

    [Fact]
    public async Task Events_RateLimitResponseCarriesRetryAfterFromHttpHeader()
    {
        var handler = new SequenceHttpMessageHandler();
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"code\":429,\"message\":\"fixture\"}}"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(23));
            return response;
        });
        var provider = CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            GoogleProviderSamples.Account,
            GoogleProviderSamples.Calendar,
            GoogleProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.RateLimited, result.Error?.Category);
        Assert.Equal(TimeSpan.FromSeconds(23), result.Error?.RetryAfter);
    }

    [Fact]
    public async Task Events_RateLimitResponseCarriesRetryAfterFromHttpDate()
    {
        var now = DateTimeOffset.Parse("2026-08-29T01:02:03Z");
        var timeProvider = new MutableTimeProvider(now);
        var handler = new SequenceHttpMessageHandler();
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"code\":429,\"message\":\"fixture\"}}"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                now.AddSeconds(41));
            return response;
        });
        var provider = CreateProvider(handler, timeProvider);

        var result = await provider.GetEventsAsync(
            GoogleProviderSamples.Account,
            GoogleProviderSamples.Calendar,
            GoogleProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.RateLimited, result.Error?.Category);
        Assert.Equal(TimeSpan.FromSeconds(41), result.Error?.RetryAfter);
    }

    private static GoogleCalendarProvider CreateProvider(
        SequenceHttpMessageHandler handler,
        TimeProvider? timeProvider = null) => new(
        new FixedGoogleClientFactory(() => GoogleProviderSamples.CreateHttpClient(handler, timeProvider)),
        Substitute.For<ITokenStore>());
}
