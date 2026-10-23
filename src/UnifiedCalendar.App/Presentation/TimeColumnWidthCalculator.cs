using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.App.Presentation;

public sealed class TimeColumnWidthCalculator
{
    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");
    private readonly IUiTextService _textService;

    public TimeColumnWidthCalculator(IUiTextService textService)
    {
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
    }

    public double Calculate(
        FontFamily fontFamily,
        FontStyle fontStyle,
        FontWeight fontWeight,
        FontStretch fontStretch,
        double fontSize,
        double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(fontFamily);
        if (!double.IsFinite(fontSize) || fontSize <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }

        if (!double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
        }

        var day = new DateOnly(2026, 12, 27);
        var samples = new[]
        {
            _textService.Get(
                UiTextResourceKeys.EventListTimedRange,
                new TimeOnly(8, 30),
                new TimeOnly(9, 30)),
            _textService.Get(UiTextResourceKeys.EventListAllDay),
            _textService.Get(
                UiTextResourceKeys.EventListAllDayRange,
                day,
                new DateOnly(2026, 12, 29)),
            _textService.Get(
                UiTextResourceKeys.EventListTimedNextDay,
                new TimeOnly(23, 0),
                new TimeOnly(1, 0)),
            _textService.Get(
                UiTextResourceKeys.EventListTimedDateRange,
                day,
                new DateOnly(2026, 12, 29)),
        };
        var typeface = new Typeface(fontFamily, fontStyle, fontWeight, fontStretch);
        var measured = samples.Max(sample => new FormattedText(
            sample,
            JapaneseCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            pixelsPerDip).WidthIncludingTrailingWhitespace);
        return Math.Ceiling(measured + LayoutMetrics.TimeColumnHorizontalPadding);
    }
}
