using System.Net;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Providers;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class MicrosoftProviderContractTests
{
    [Fact]
    public async Task CalendarEnumeration_FollowsMeCalendarsPagingAndIncludesOwnedSharedAndSubscribedCalendars()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson(MicrosoftProviderSamples.LoadFixture("calendars-page-1.json"));
        handler.EnqueueJson(MicrosoftProviderSamples.LoadFixture("calendars-page-2.json"));
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var calendars = await provider.ListCalendarsAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, calendars.Count);
        var primary = Assert.Single(calendars, value => value.IsPrimary);
        Assert.Equal("fixture-primary-calendar", primary.CalendarId);
        Assert.Equal(primary.CalendarId, primary.ProviderLocator);
        Assert.Equal("#123ABC", primary.SourceColor?.ToHexString());
        var shared = Assert.Single(calendars, value => value.Name == "Fixture Shared");
        Assert.True(shared.CanReadEvents);
        Assert.False(shared.IsPrimary);
        Assert.Equal(shared.CalendarId, shared.ProviderLocator);
        Assert.Equal("#A6E7D8", shared.SourceColor?.ToHexString());
        var subscription = Assert.Single(calendars, value => value.Name == "Fixture Subscription");
        Assert.True(subscription.CanReadEvents);
        Assert.False(subscription.IsPrimary);
        Assert.Equal(subscription.CalendarId, subscription.ProviderLocator);
        Assert.Equal("#FFD3A6", subscription.SourceColor?.ToHexString());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/v1.0/me/calendars", handler.Requests[0].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("skiptoken=calendar-page-2", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.True(request.HasAuthorizationHeader));
        Assert.All(handler.Requests, request =>
            Assert.Contains("ImmutableId", string.Join(',', request.Preferences), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CalendarEnumeration_MiddlePageFailureDoesNotExposePartialCalendars()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson(MicrosoftProviderSamples.LoadFixture("calendars-page-1.json"));
        handler.Enqueue(_ => throw new HttpRequestException("fixture network failure"));
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.ListCalendarsAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken));

        Assert.Equal(ProviderErrorCategory.Network, exception.Error.Category);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SharedCalendarView_UsesMeCalendarsLocatorAndReturnsEvents()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson("""
            {"value":[{
              "id":"fixture-shared-event","subject":"Fixture shared event",
              "start":{"dateTime":"2026-08-28T10:00:00Z","timeZone":"UTC"},
              "end":{"dateTime":"2026-08-28T11:00:00Z","timeZone":"UTC"},
              "isAllDay":false,"isCancelled":false
            }]}
            """);
        var provider = MicrosoftProviderSamples.CreateProvider(handler);
        var sharedCalendar = new CalendarDescriptor(
            "fixture-shared-calendar",
            "fixture-shared-calendar",
            "Fixture Shared",
            isPrimary: false,
            canReadEvents: true,
            RgbColor.Parse("#A6E7D8"));

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            sharedCalendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Events);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            "/v1.0/me/calendars/fixture-shared-calendar/calendarView",
            request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task CalendarViewAndCategories_FollowAllPagesAndMapCommonModels()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson(MicrosoftProviderSamples.LoadFixture("calendar-view-page-1.json"));
        handler.EnqueueJson(MicrosoftProviderSamples.LoadFixture("calendar-view-page-2.json"));
        handler.EnqueueJson(MicrosoftProviderSamples.LoadFixture("master-categories-page-1.json"));
        handler.EnqueueJson(MicrosoftProviderSamples.LoadFixture("master-categories-page-2.json"));
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Events.Count);
        var timed = result.Events.Single(value => value.Key.SourceEventId == "fixture-event-1");
        var timedTiming = Assert.IsType<TimedEventTiming>(timed.Timing);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T00:00:00Z"), timedTiming.StartUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-28T01:00:00Z"), timedTiming.EndUtc);
        Assert.Equal("First\nSecond & safe", timed.DescriptionPlainText);
        Assert.Equal("Fixture location", timed.Location);
        Assert.Equal(AttendeeResponse.Accepted, timed.ResponseStatus);
        Assert.Equal("#0078D4", timed.SourceEventColor?.ToHexString());
        Assert.Equal("#445566", timed.SourceCalendarColor?.ToHexString());
        Assert.Equal("https://meeting.example.test/fixture-meeting", timed.MeetingUri?.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("https://calendar.example.test/event/fixture-event-1", timed.SourceDetailUri?.AbsoluteUri.TrimEnd('/'));

        var occurrence = result.Events.Single(value => value.Key.OccurrenceKey.Contains(
            "fixture-series",
            StringComparison.Ordinal));
        Assert.Equal("fixture-series", occurrence.Key.SourceEventId);
        var allDay = Assert.IsType<AllDayEventTiming>(occurrence.Timing);
        Assert.Equal(new DateOnly(2026, 8, 30), allDay.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 1), allDay.EndDateExclusive);
        Assert.Contains("fixture-series", occurrence.Key.OccurrenceKey, StringComparison.Ordinal);
        Assert.Equal(AttendeeResponse.Tentative, occurrence.ResponseStatus);
        Assert.DoesNotContain(result.Events, value => value.Key.SourceEventId == "fixture-cancelled");

        Assert.Equal(4, handler.Requests.Count);
        var query = handler.Requests[0].Uri.Query;
        Assert.Contains("startDateTime=", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("endDateTime=", query, StringComparison.OrdinalIgnoreCase);
        var preferences = string.Join(',', handler.Requests[0].Preferences);
        Assert.Contains("ImmutableId", preferences, StringComparison.Ordinal);
        Assert.Contains("outlook.timezone=\"UTC\"", preferences, StringComparison.Ordinal);
        Assert.Contains("outlook.body-content-type=\"text\"", preferences, StringComparison.Ordinal);
        Assert.Contains("skiptoken=event-page-2", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("/outlook/masterCategories", handler.Requests[2].Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("skiptoken=category-page-2", handler.Requests[3].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CalendarView_MiddlePageFailureReturnsFailureWithoutPartialEvents()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson(MicrosoftProviderSamples.LoadFixture("calendar-view-page-1.json"));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"code\":\"ServiceUnavailable\",\"message\":\"fixture\"}}"),
        });
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Events);
        Assert.Equal(ProviderErrorCategory.ServerError, result.Error?.Category);
        Assert.Equal(503, result.Error?.HttpStatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task MasterCategories_ForbiddenFallsBackToCalendarColorWithoutLosingEvents()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson("""
            {"value":[{
              "id":"fixture-category-event","subject":"Fixture category event",
              "start":{"dateTime":"2026-08-28T10:00:00Z","timeZone":"UTC"},
              "end":{"dateTime":"2026-08-28T11:00:00Z","timeZone":"UTC"},
              "isAllDay":false,"isCancelled":false,"categories":["Fixture Blue"]
            }]}
            """);
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"fixture\"}}"),
        });
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var calendarEvent = Assert.Single(result.Events);
        Assert.Null(calendarEvent.SourceEventColor);
        Assert.Equal("#445566", calendarEvent.SourceCalendarColor?.ToHexString());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("/outlook/masterCategories", handler.Requests[1].Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CalendarView_RateLimitCarriesRetryAfterAndDoesNotRetry()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"fixture\"}}"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(19));
            return response;
        });
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.RateLimited, result.Error?.Category);
        Assert.Equal(TimeSpan.FromSeconds(19), result.Error?.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CalendarView_RateLimitHttpDateCarriesRetryAfter()
    {
        var now = DateTimeOffset.Parse("2026-08-29T01:02:03Z");
        var timeProvider = new MutableTimeProvider(now);
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"fixture\"}}"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now.AddSeconds(41));
            return response;
        });
        var provider = MicrosoftProviderSamples.CreateProvider(handler, timeProvider);

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.RateLimited, result.Error?.Category);
        Assert.Equal(TimeSpan.FromSeconds(41), result.Error?.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CalendarView_MalformedSuccessPayloadIsClassifiedWithoutPartialEvents()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson("{ malformed fixture");
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Events);
        Assert.Equal(ProviderErrorCategory.MalformedResponse, result.Error?.Category);
    }

    [Fact]
    public async Task CalendarEnumeration_RejectsCrossHostNextLinkBeforeSendingCredentials()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson("""
            {"@odata.nextLink":"https://untrusted.example.test/v1.0/me/calendars?page=2","value":[
              {"id":"fixture-calendar","name":"Fixture Calendar","isDefaultCalendar":true}
            ]}
            """);
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.ListCalendarsAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken));

        Assert.Equal(ProviderErrorCategory.MalformedResponse, exception.Error.Category);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CalendarView_TransportCancellationWithoutCallerCancellationIsTimeout()
    {
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.Enqueue(_ => throw new TaskCanceledException("fixture transport timeout"));
        var provider = MicrosoftProviderSamples.CreateProvider(handler);

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.Timeout, result.Error?.Category);
    }
}
