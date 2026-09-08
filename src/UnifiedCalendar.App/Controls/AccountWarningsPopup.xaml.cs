using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UnifiedCalendar.App.Controls;

public partial class AccountWarningsPopup : UserControl
{
    public static readonly RoutedEvent CloseRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CloseRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(AccountWarningsPopup));

    public AccountWarningsPopup()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    public IReadOnlyList<Button> GetTabTargets()
    {
        UpdateLayout();
        return FindDescendants<Button>(this)
            .Where(button => button.IsVisible && button.IsEnabled)
            .ToArray();
    }

    public bool FocusFirstInteractiveElement()
    {
        var first = GetTabTargets().FirstOrDefault();
        return first?.Focus() ?? Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            RaiseEvent(new RoutedEventArgs(CloseRequestedEvent, this));
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key != Key.Tab)
        {
            return;
        }

        var targets = GetTabTargets();
        if (targets.Count == 0)
        {
            Focus();
            eventArgs.Handled = true;
            return;
        }

        var currentIndex = Array.IndexOf(targets.ToArray(), Keyboard.FocusedElement as Button);
        var moveBackward = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var nextIndex = moveBackward
            ? currentIndex <= 0 ? targets.Count - 1 : currentIndex - 1
            : currentIndex < 0 || currentIndex == targets.Count - 1 ? 0 : currentIndex + 1;
        targets[nextIndex].Focus();
        eventArgs.Handled = true;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
