using UnifiedCalendar.Core.Notifications;
using UnifiedCalendar.Core.Persistence;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class EventNotificationPlannerTests
{
    private static readonly DateTimeOffset NowLocal =
        new(2026, 9, 9, 10, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void NotificationPreferencesUseDocumentedDefaultsAndRejectOutOfRangeLeadMinutes()
    {
        var defaults = new NotificationPreferences();

        Assert.True(defaults.Enabled);
        Assert.Equal(5, defaults.LeadMinutes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotificationPreferences(leadMinutes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotificationPreferences(leadMinutes: 61));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(5, false)]
    public void NotificationWindowHasInclusiveLeadBoundaryAndExclusiveStartBoundary(
        int minutesAfterNotificationTime,
        bool expectedNotification)
    {
        var start = NowLocal.AddMinutes(10);
        var now = start.AddMinutes(-5 + minutesAfterNotificationTime);

        var result = Plan([Source("event", start)], now: now);

        Assert.Equal(expectedNotification, result.Notification is not null);
    }

    [Fact]
    public void PreviouslyNotifiedKeyIsNotReturnedAgain()
    {
        var start = NowLocal.AddMinutes(5);
        var key = new EventNotificationKey("event", start);

        var result = Plan([Source("event", start)], notifiedKeys: [key]);

        Assert.Null(result.Notification);
        Assert.Equal([key], result.NotifiedKeys);
    }

    [Fact]
    public void ChangedStartCreatesANewNotificationKey()
    {
        var previousStart = NowLocal.AddMinutes(4);
        var changedStart = NowLocal.AddMinutes(5);
        var previousKey = new EventNotificationKey("event", previousStart);

        var result = Plan(
            [Source("event", changedStart)],
            notifiedKeys: [previousKey]);

        Assert.NotNull(result.Notification);
        Assert.DoesNotContain(previousKey, result.NotifiedKeys);
        Assert.Contains(new EventNotificationKey("event", changedStart), result.NotifiedKeys);
    }

    [Fact]
    public void AllDayAndMissingStartAreExcludedWhileMultiDayAndZeroDurationSourcesAreIncluded()
    {
        var start = NowLocal.AddMinutes(5);

        var result = Plan(
        [
            Source("all-day", start, isAllDay: true),
            Source("missing-start", null),
            Source("multi-day", start, isMultiDay: true),
            Source("zero-duration", start),
        ]);

        Assert.NotNull(result.Notification);
        Assert.Equal(1, result.Notification.AdditionalCount);
        Assert.Equal(2, result.NotifiedKeys.Count);
        Assert.Contains(result.NotifiedKeys, key => key.StableId == "multi-day");
        Assert.Contains(result.NotifiedKeys, key => key.StableId == "zero-duration");
    }

    [Fact]
    public void MultipleDueEventsUseEarliestRepresentativeAndMarkEveryKey()
    {
        var result = Plan(
        [
            Source("later", NowLocal.AddMinutes(5), title: "Later"),
            Source("earliest", NowLocal.AddMinutes(3), title: "Earliest"),
            Source("middle", NowLocal.AddMinutes(4), title: "Middle"),
        ]);

        Assert.NotNull(result.Notification);
        Assert.Equal("Earliest", result.Notification.Title);
        Assert.Equal(NowLocal.AddMinutes(3), result.Notification.LocalStart);
        Assert.Equal(2, result.Notification.AdditionalCount);
        Assert.Equal(3, result.NotifiedKeys.Count);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("reenabled")]
    [InlineData("lead-minutes-changed")]
    public void InitializationModesMarkDueEventsWithoutReturningNotification(string reason)
    {
        var start = NowLocal.AddMinutes(5);

        var result = Plan(
            [Source(reason, start)],
            suppressEligibleNotifications: true);

        Assert.Null(result.Notification);
        Assert.Contains(new EventNotificationKey(reason, start), result.NotifiedKeys);
    }

    [Fact]
    public void KeysForEventsNoLongerInPresentationAreRemoved()
    {
        var retained = new EventNotificationKey("retained", NowLocal.AddMinutes(5));
        var removed = new EventNotificationKey("removed", NowLocal.AddMinutes(5));

        var result = Plan(
            [Source(retained.StableId, retained.LocalStart)],
            notifiedKeys: [retained, removed]);

        Assert.Equal([retained], result.NotifiedKeys);
    }

    private static EventNotificationPlan Plan(
        IEnumerable<EventNotificationSource> events,
        DateTimeOffset? now = null,
        IEnumerable<EventNotificationKey>? notifiedKeys = null,
        bool suppressEligibleNotifications = false) => new EventNotificationPlanner().Plan(
            events,
            now ?? NowLocal,
            leadMinutes: 5,
            notifiedKeys ?? [],
            suppressEligibleNotifications);

    private static EventNotificationSource Source(
        string stableId,
        DateTimeOffset? start,
        bool isAllDay = false,
        bool isMultiDay = false,
        string? title = null) => new(
            stableId,
            start,
            isAllDay,
            isMultiDay,
            title ?? stableId);
}
