using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.App.Presentation;

public sealed record TimelineViewportState(
    EventKey? AnchorKey,
    double AnchorPixelOffset,
    EventKey? FocusedKey,
    EventKey? DetailKey,
    bool UserScrolledBeforeInitialSync);

public sealed record TimelineViewportRestoration(
    EventKey? AnchorKey,
    double AnchorPixelOffset,
    EventKey? FocusedKey,
    bool CloseDetails,
    bool ScrollToUpcoming,
    EventKey? UpcomingKey);

public interface ITimelineViewport
{
    TimelineViewportState Capture(
        IReadOnlyList<EventKey> currentKeys,
        EventKey? detailKey);

    void Restore(TimelineViewportRestoration restoration);
}

public static class ViewportRestorationPlanner
{
    public static TimelineViewportRestoration Create(
        TimelineViewportState before,
        IReadOnlyList<EventKey> previousKeys,
        IReadOnlyList<EventKey> currentKeys,
        bool scrollToUpcoming,
        EventKey? upcomingKey)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(previousKeys);
        ArgumentNullException.ThrowIfNull(currentKeys);

        var current = currentKeys.ToHashSet();
        var anchor = ResolveNearest(before.AnchorKey, previousKeys, currentKeys, current);
        var focus = ResolveFocus(before.FocusedKey, previousKeys, current);
        var closeDetails = before.DetailKey.HasValue && !current.Contains(before.DetailKey.Value);
        return new TimelineViewportRestoration(
            anchor,
            before.AnchorPixelOffset,
            focus,
            closeDetails,
            scrollToUpcoming && !before.UserScrolledBeforeInitialSync,
            upcomingKey);
    }

    private static EventKey? ResolveNearest(
        EventKey? key,
        IReadOnlyList<EventKey> previous,
        IReadOnlyList<EventKey> current,
        IReadOnlySet<EventKey> currentSet)
    {
        if (!key.HasValue || current.Count == 0)
        {
            return null;
        }

        if (currentSet.Contains(key.Value))
        {
            return key;
        }

        var previousIndex = IndexOf(previous, key.Value);
        if (previousIndex < 0)
        {
            return current[0];
        }

        for (var distance = 1; distance < previous.Count; distance++)
        {
            var next = previousIndex + distance;
            if (next < previous.Count && currentSet.Contains(previous[next]))
            {
                return previous[next];
            }

            var prior = previousIndex - distance;
            if (prior >= 0 && currentSet.Contains(previous[prior]))
            {
                return previous[prior];
            }
        }

        return current[Math.Min(previousIndex, current.Count - 1)];
    }

    private static EventKey? ResolveFocus(
        EventKey? key,
        IReadOnlyList<EventKey> previous,
        IReadOnlySet<EventKey> current)
    {
        if (!key.HasValue)
        {
            return null;
        }

        if (current.Contains(key.Value))
        {
            return key;
        }

        var index = IndexOf(previous, key.Value);
        if (index < 0)
        {
            return null;
        }

        for (var next = index + 1; next < previous.Count; next++)
        {
            if (current.Contains(previous[next]))
            {
                return previous[next];
            }
        }

        for (var prior = index - 1; prior >= 0; prior--)
        {
            if (current.Contains(previous[prior]))
            {
                return previous[prior];
            }
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<EventKey> values, EventKey key)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (values[index] == key)
            {
                return index;
            }
        }

        return -1;
    }
}
