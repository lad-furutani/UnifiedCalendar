using System.Collections.ObjectModel;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.App.Presentation;

public sealed class SnapshotDiffer
{
    public SnapshotDiffResult Apply(
        ObservableCollection<TimelineItemViewModel> target,
        PresentationSnapshot snapshot,
        Func<PresentedEvent, EventRowViewModel> eventFactory,
        Func<PresentedDay, DayHeaderItemViewModel> dayFactory)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(eventFactory);
        ArgumentNullException.ThrowIfNull(dayFactory);

        var previousKeys = target.OfType<EventRowViewModel>().Select(item => item.Key).ToArray();
        var existingEvents = target
            .OfType<EventRowViewModel>()
            .ToDictionary(item => item.Key);
        var existingDays = target
            .OfType<DayHeaderItemViewModel>()
            .ToDictionary(item => item.Date);
        var desired = new List<TimelineItemViewModel>(snapshot.Events.Count + snapshot.Days.Count);

        foreach (var day in snapshot.Days)
        {
            var candidateHeader = dayFactory(day);
            var header = existingDays.GetValueOrDefault(day.Date) ?? candidateHeader;
            header.UpdateHeader(candidateHeader.Header);
            desired.Add(header);
            foreach (var presented in day.Events)
            {
                if (existingEvents.TryGetValue(presented.Key, out var existing))
                {
                    existing.UpdateFrom(presented);
                    desired.Add(existing);
                }
                else
                {
                    desired.Add(eventFactory(presented));
                }
            }
        }

        for (var index = 0; index < desired.Count; index++)
        {
            if (index < target.Count && target[index].Identity == desired[index].Identity)
            {
                continue;
            }

            var existingIndex = FindIndex(target, desired[index].Identity, index + 1);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
            }
            else
            {
                target.Insert(index, desired[index]);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        return new SnapshotDiffResult(
            previousKeys,
            snapshot.Events.Select(item => item.Key).ToArray());
    }

    private static int FindIndex(
        IReadOnlyList<TimelineItemViewModel> items,
        string identity,
        int startIndex)
    {
        for (var index = Math.Max(0, startIndex); index < items.Count; index++)
        {
            if (items[index].Identity == identity)
            {
                return index;
            }
        }

        return -1;
    }
}

public sealed record SnapshotDiffResult(
    IReadOnlyList<EventKey> PreviousEventKeys,
    IReadOnlyList<EventKey> CurrentEventKeys);
