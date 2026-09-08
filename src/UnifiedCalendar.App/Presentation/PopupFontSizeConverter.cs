using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UnifiedCalendar.App.Presentation;

public sealed class PopupFontSizeConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is not double bodyFontSizeDip || parameter is not string role)
        {
            return DependencyProperty.UnsetValue;
        }

        return role switch
        {
            "Title" => LayoutMetrics.CalculatePopupTitleFontSize(bodyFontSizeDip),
            "Label" => LayoutMetrics.CalculatePopupLabelFontSize(bodyFontSizeDip),
            _ => DependencyProperty.UnsetValue,
        };
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) => throw new NotSupportedException();
}
