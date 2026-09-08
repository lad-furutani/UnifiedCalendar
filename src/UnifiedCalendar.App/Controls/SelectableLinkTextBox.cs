using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace UnifiedCalendar.App.Controls;

public sealed class SelectableLinkTextBox : RichTextBox
{
    private static readonly Regex WebUriPattern = new(
        @"https?://[^\s<>\""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(SelectableLinkTextBox),
        new FrameworkPropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty OpenUriCommandProperty = DependencyProperty.Register(
        nameof(OpenUriCommand),
        typeof(ICommand),
        typeof(SelectableLinkTextBox),
        new FrameworkPropertyMetadata(null, OnContentChanged));

    public SelectableLinkTextBox()
    {
        IsReadOnly = true;
        IsTabStop = false;
        IsDocumentEnabled = true;
        IsUndoEnabled = false;
        AcceptsReturn = true;
        BorderThickness = new Thickness(0d);
        Padding = new Thickness(0d);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document.PagePadding = new Thickness(0d);
        Document.ColumnWidth = double.PositiveInfinity;
        RebuildDocument();
    }

    public IReadOnlyList<Hyperlink> FocusableLinks { get; private set; } = [];

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? OpenUriCommand
    {
        get => (ICommand?)GetValue(OpenUriCommandProperty);
        set => SetValue(OpenUriCommandProperty, value);
    }

    private static void OnContentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((SelectableLinkTextBox)dependencyObject).RebuildDocument();

    private void RebuildDocument()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0d) };
        var focusableLinks = new List<Hyperlink>();
        var text = Text ?? string.Empty;
        var cursor = 0;
        foreach (Match match in WebUriPattern.Matches(text))
        {
            if (match.Index > cursor)
            {
                paragraph.Inlines.Add(new Run(text[cursor..match.Index]));
            }

            var candidate = match.Value.TrimEnd('.', ',', ')', ']');
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var hyperlink = new Hyperlink(new Run(candidate))
                {
                    Command = OpenUriCommand,
                    CommandParameter = uri,
                    Cursor = Cursors.Hand,
                    Focusable = true,
                };
                hyperlink.SetResourceReference(TextElement.ForegroundProperty, "LinkBrush");
                KeyboardNavigation.SetIsTabStop(hyperlink, true);
                hyperlink.GotKeyboardFocus += HyperlinkGotKeyboardFocus;
                hyperlink.LostKeyboardFocus += HyperlinkLostKeyboardFocus;
                paragraph.Inlines.Add(hyperlink);
                focusableLinks.Add(hyperlink);
                if (candidate.Length < match.Length)
                {
                    paragraph.Inlines.Add(new Run(match.Value[candidate.Length..]));
                }
            }
            else
            {
                paragraph.Inlines.Add(new Run(match.Value));
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[cursor..]));
        }

        Document.Blocks.Clear();
        Document.Blocks.Add(paragraph);
        FocusableLinks = focusableLinks;
    }

    private static void HyperlinkGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (sender is Hyperlink hyperlink)
        {
            hyperlink.SetResourceReference(TextElement.BackgroundProperty, "FocusBrush");
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "FocusForegroundBrush");
        }
    }

    private static void HyperlinkLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (sender is Hyperlink hyperlink)
        {
            hyperlink.ClearValue(TextElement.BackgroundProperty);
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "LinkBrush");
        }
    }
}
