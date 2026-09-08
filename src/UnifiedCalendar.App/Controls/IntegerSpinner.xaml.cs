using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace UnifiedCalendar.App.Controls;

public partial class IntegerSpinner : UserControl
{
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(int),
        typeof(IntegerSpinner),
        new FrameworkPropertyMetadata(0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(int),
        typeof(IntegerSpinner),
        new FrameworkPropertyMetadata(100, OnRangeChanged, CoerceMaximum));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(int),
        typeof(IntegerSpinner),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged,
            CoerceValue));

    private bool _updatingText;

    public IntegerSpinner()
    {
        InitializeComponent();
        DataObject.AddPastingHandler(ValueTextBox, OnPaste);
        UpdateText(Value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static object CoerceMaximum(DependencyObject dependencyObject, object baseValue) =>
        Math.Max((int)baseValue, ((IntegerSpinner)dependencyObject).Minimum);

    private static object CoerceValue(DependencyObject dependencyObject, object baseValue)
    {
        var spinner = (IntegerSpinner)dependencyObject;
        return Math.Clamp((int)baseValue, spinner.Minimum, spinner.Maximum);
    }

    private static void OnRangeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs)
    {
        var spinner = (IntegerSpinner)dependencyObject;
        spinner.CoerceValue(MaximumProperty);
        spinner.CoerceValue(ValueProperty);
        spinner.UpdateText(spinner.Value);
    }

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs eventArgs) =>
        ((IntegerSpinner)dependencyObject).UpdateText((int)eventArgs.NewValue);

    private void ValueTextBoxPreviewTextInput(object sender, TextCompositionEventArgs eventArgs) =>
        eventArgs.Handled = eventArgs.Text.Any(character => !char.IsDigit(character));

    private void ValueTextBoxPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            CommitText();
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Escape)
        {
            UpdateText(Value);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Up)
        {
            ChangeValue(1);
            eventArgs.Handled = true;
        }
        else if (eventArgs.Key == Key.Down)
        {
            ChangeValue(-1);
            eventArgs.Handled = true;
        }
    }

    private void ValueTextBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs eventArgs) =>
        CommitText();

    private void ValueTextBoxTextChanged(object sender, TextChangedEventArgs eventArgs)
    {
        if (!_updatingText && ValueTextBox.Text.Any(character => !char.IsDigit(character)))
        {
            UpdateText(Value);
        }
    }

    private void DecreaseButtonClick(object sender, RoutedEventArgs eventArgs) => ChangeValue(-1);

    private void IncreaseButtonClick(object sender, RoutedEventArgs eventArgs) => ChangeValue(1);

    private void ChangeValue(int delta)
    {
        CommitText();
        Value = Math.Clamp(Value + delta, Minimum, Maximum);
        ValueTextBox.SelectAll();
        ValueTextBox.Focus();
    }

    private void CommitText()
    {
        if (int.TryParse(ValueTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= Minimum
            && value <= Maximum)
        {
            Value = value;
            UpdateText(Value);
            return;
        }

        UpdateText(Value);
    }

    private void UpdateText(int value)
    {
        if (ValueTextBox is null)
        {
            return;
        }

        var text = value.ToString(CultureInfo.InvariantCulture);
        if (ValueTextBox.Text == text)
        {
            return;
        }

        _updatingText = true;
        ValueTextBox.Text = text;
        _updatingText = false;
    }

    private void OnPaste(object sender, DataObjectPastingEventArgs eventArgs)
    {
        if (!eventArgs.SourceDataObject.GetDataPresent(DataFormats.UnicodeText)
            || eventArgs.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text
            || text.Length == 0
            || text.Any(character => !char.IsDigit(character)))
        {
            eventArgs.CancelCommand();
        }
    }
}
