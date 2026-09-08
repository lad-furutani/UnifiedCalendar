using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.App.Presentation;

public sealed class TimelineViewportCoordinator : ITimelineViewport
{
    private ListBox? _listBox;
    private bool _isRestoring;
    private bool _userScrolledBeforeInitialSync;
    private long _restorationGeneration;

    public event EventHandler? Restored;

    public void Attach(ListBox listBox)
    {
        ArgumentNullException.ThrowIfNull(listBox);
        _listBox = listBox;
    }

    public bool IsRestoring => _isRestoring;

    public void NoteUserScroll()
    {
        if (!_isRestoring)
        {
            _userScrolledBeforeInitialSync = true;
        }
    }

    public TimelineViewportState Capture(
        IReadOnlyList<EventKey> currentKeys,
        EventKey? detailKey)
    {
        ArgumentNullException.ThrowIfNull(currentKeys);
        if (_listBox is null)
        {
            return new TimelineViewportState(
                null,
                0d,
                null,
                detailKey,
                _userScrolledBeforeInitialSync);
        }

        EventKey? anchor = null;
        var anchorOffset = 0d;
        for (var index = 0; index < _listBox.Items.Count; index++)
        {
            if (_listBox.Items[index] is not EventRowViewModel row
                || _listBox.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
            {
                continue;
            }

            var position = container.TranslatePoint(new Point(0d, 0d), _listBox);
            if (position.Y + container.ActualHeight <= 0d)
            {
                continue;
            }

            anchor = row.Key;
            anchorOffset = position.Y;
            break;
        }

        var focused = FindAncestor<ListBoxItem>(Keyboard.FocusedElement as DependencyObject)?.DataContext
            as EventRowViewModel;
        return new TimelineViewportState(
            anchor,
            anchorOffset,
            focused?.Key,
            detailKey,
            _userScrolledBeforeInitialSync);
    }

    public void Restore(TimelineViewportRestoration restoration)
    {
        ArgumentNullException.ThrowIfNull(restoration);
        if (_listBox is null)
        {
            return;
        }

        _isRestoring = true;
        var generation = ++_restorationGeneration;
        _listBox.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () => RestoreCore(restoration, generation));
    }

    private void RestoreCore(TimelineViewportRestoration restoration, long generation)
    {
        if (_listBox is null)
        {
            CompleteRestoration(generation);
            return;
        }

        try
        {
            var scrollKey = restoration.ScrollToUpcoming
                ? restoration.UpcomingKey
                : restoration.AnchorKey;
            if (FindRow(scrollKey) is { } row)
            {
                _listBox.ScrollIntoView(row);
                _listBox.UpdateLayout();
                if (!restoration.ScrollToUpcoming
                    && _listBox.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem container
                    && FindDescendant<ScrollViewer>(_listBox) is { } scrollViewer)
                {
                    var position = container.TranslatePoint(new Point(0d, 0d), _listBox);
                    scrollViewer.ScrollToVerticalOffset(
                        Math.Max(0d, scrollViewer.VerticalOffset
                            + position.Y
                            - restoration.AnchorPixelOffset));
                }
            }

            if (FindRow(restoration.FocusedKey) is { } focusedRow)
            {
                _listBox.ScrollIntoView(focusedRow);
                _listBox.UpdateLayout();
                if (_listBox.ItemContainerGenerator.ContainerFromItem(focusedRow) is ListBoxItem focusedItem)
                {
                    focusedItem.Focus();
                }
            }
        }
        finally
        {
            _listBox.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                () => CompleteRestoration(generation));
        }
    }

    private void CompleteRestoration(long generation)
    {
        if (generation != _restorationGeneration)
        {
            return;
        }

        _isRestoring = false;
        Restored?.Invoke(this, EventArgs.Empty);
    }

    private EventRowViewModel? FindRow(EventKey? key) => key.HasValue && _listBox is not null
        ? _listBox.Items.OfType<EventRowViewModel>().FirstOrDefault(item => item.Key == key.Value)
        : null;

    private static T? FindAncestor<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = child switch
            {
                ContentElement contentElement => ContentOperations.GetParent(contentElement),
                Visual or Visual3D => VisualTreeHelper.GetParent(child),
                _ => LogicalTreeHelper.GetParent(child),
            };
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
