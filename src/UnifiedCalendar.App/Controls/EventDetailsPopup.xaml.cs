using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace UnifiedCalendar.App.Controls;

public partial class EventDetailsPopup : UserControl
{
    public static readonly RoutedEvent CloseRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CloseRequested),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(EventDetailsPopup));

    public EventDetailsPopup()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler CloseRequested
    {
        add => AddHandler(CloseRequestedEvent, value);
        remove => RemoveHandler(CloseRequestedEvent, value);
    }

    public IReadOnlyList<IInputElement> GetTabTargets()
    {
        var targets = new List<IInputElement>();
        AddVisibleLinks(targets, LocationText);
        AddVisibleLinks(targets, MeetingText);
        AddVisibleLinks(targets, DescriptionText);
        if (SourceButton.IsVisible && SourceButton.IsEnabled)
        {
            targets.Add(SourceButton);
        }

        return targets;
    }

    public bool FocusFirstInteractiveElement()
    {
        UpdateLayout();
        var first = GetTabTargets().FirstOrDefault();
        return first is not null ? Focus(first) : Focus();
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

        var currentIndex = Array.IndexOf(targets.ToArray(), Keyboard.FocusedElement);
        var moveBackward = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var nextIndex = moveBackward
            ? currentIndex <= 0 ? targets.Count - 1 : currentIndex - 1
            : currentIndex < 0 || currentIndex == targets.Count - 1 ? 0 : currentIndex + 1;
        Focus(targets[nextIndex]);
        eventArgs.Handled = true;
    }

    private static void AddVisibleLinks(
        ICollection<IInputElement> targets,
        SelectableLinkTextBox textBox)
    {
        if (!textBox.IsVisible || !textBox.IsEnabled)
        {
            return;
        }

        foreach (var hyperlink in textBox.FocusableLinks.Where(value => value.IsEnabled))
        {
            targets.Add(hyperlink);
        }
    }

    private static bool Focus(IInputElement target) => target switch
    {
        UIElement element => element.Focus(),
        ContentElement element => element.Focus(),
        _ => false,
    };
}
