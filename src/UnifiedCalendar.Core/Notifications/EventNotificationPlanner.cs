using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Core.Notifications;

public sealed record EventNotificationSource(
    string StableId,
    DateTimeOffset? LocalStart,
    bool IsAllDay,
    bool IsMultiDay,
    string Title);

public sealed record EventNotificationKey(string StableId, DateTimeOffset LocalStart);

public sealed record EventNotificationMessage(
    DateTimeOffset LocalStart,
    string Title,
    int AdditionalCount);

public sealed class EventNotificationPlan
{
    private readonly IReadOnlyList<EventNotificationKey> _notifiedKeys;

    public EventNotificationPlan(
        EventNotificationMessage? notification,
        IEnumerable<EventNotificationKey> notifiedKeys)
    {
        ArgumentNullException.ThrowIfNull(notifiedKeys);
        Notification = notification;
        _notifiedKeys = Array.AsReadOnly(notifiedKeys.Distinct().ToArray());
    }

    public EventNotificationMessage? Notification { get; }

    public IReadOnlyList<EventNotificationKey> NotifiedKeys => _notifiedKeys;
}

public sealed class EventNotificationPlanner
{
    public EventNotificationPlan Plan(
        IEnumerable<EventNotificationSource> events,
        DateTimeOffset nowLocal,
        int leadMinutes,
        IEnumerable<EventNotificationKey> notifiedKeys,
        bool suppressEligibleNotifications = false)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(notifiedKeys);
        _ = new NotificationPreferences(leadMinutes: leadMinutes);

        var candidates = events
            .Select(Validate)
            .Where(value => !value.IsAllDay && value.LocalStart.HasValue)
            .Select(value => new Candidate(
                value,
                new EventNotificationKey(value.StableId, value.LocalStart!.Value)))
            .ToArray();
        var visibleKeys = candidates.Select(value => value.Key).ToHashSet();
        var updatedKeys = notifiedKeys
            .Where(visibleKeys.Contains)
            .ToHashSet();
        var lead = TimeSpan.FromMinutes(leadMinutes);
        var due = candidates
            .Where(value =>
                value.Source.LocalStart!.Value - lead <= nowLocal
                && nowLocal < value.Source.LocalStart.Value
                && !updatedKeys.Contains(value.Key))
            .OrderBy(value => value.Source.LocalStart)
            .ThenBy(value => value.Source.StableId, StringComparer.Ordinal)
            .ToArray();

        updatedKeys.UnionWith(due.Select(value => value.Key));
        if (suppressEligibleNotifications || due.Length == 0)
        {
            return new EventNotificationPlan(null, updatedKeys);
        }

        var representative = due[0].Source;
        return new EventNotificationPlan(
            new EventNotificationMessage(
                representative.LocalStart!.Value,
                representative.Title,
                due.Length - 1),
            updatedKeys);
    }

    private static EventNotificationSource Validate(EventNotificationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.StableId);
        ArgumentNullException.ThrowIfNull(source.Title);
        return source;
    }

    private sealed record Candidate(
        EventNotificationSource Source,
        EventNotificationKey Key);
}
