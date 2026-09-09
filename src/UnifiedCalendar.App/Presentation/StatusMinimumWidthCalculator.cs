using System.Globalization;
using System.Windows;
using System.Windows.Media;
using UnifiedCalendar.App.Services;

namespace UnifiedCalendar.App.Presentation;

public sealed record StatusMinimumWidthMeasurement(
    double CurrentTimeTextWidth,
    double RefreshTextWidth,
    double RefreshHorizontalPaddingWidth,
    double FixedControlWidth,
    double MarginAndBorderPaddingWidth)
{
    public double RefreshButtonWidth => RefreshTextWidth + RefreshHorizontalPaddingWidth;

    public double TotalWidth => Math.Ceiling(
        CurrentTimeTextWidth
        + RefreshButtonWidth
        + FixedControlWidth
        + MarginAndBorderPaddingWidth);
}

public sealed class StatusMinimumWidthCalculator
{
    private const double CurrentTimeRightMarginWidth = 16d;
    private const double RefreshHorizontalPaddingWidth = 20d;
    private const double WarningButtonWidth = 32d;
    private const double WarningButtonRightMarginWidth = 10d;
    private const double SettingsButtonWidth = 32d;
    private const double SettingsButtonLeftMarginWidth = 8d;
    private const double StatusHorizontalPaddingWidth = 20d;
    private const double FixedControlWidth = WarningButtonWidth + SettingsButtonWidth;
    private const double MarginAndBorderPaddingWidth = CurrentTimeRightMarginWidth
        + WarningButtonRightMarginWidth
        + SettingsButtonLeftMarginWidth
        + StatusHorizontalPaddingWidth;
    private static readonly CultureInfo JapaneseCulture = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly DateTimeOffset MidnightSample = new(
        2026,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero);
    private readonly IUiTextService _textService;

    public StatusMinimumWidthCalculator(IUiTextService textService)
    {
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
    }

    public StatusMinimumWidthMeasurement Calculate(
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

        var currentTime = _textService.Get(UiResourceKeys.CurrentTime, MidnightSample);
        var refresh = _textService.Get(UiResourceKeys.ManualRefresh);
        var currentTimeWidth = Measure(
            currentTime,
            new Typeface(fontFamily, fontStyle, FontWeights.SemiBold, fontStretch),
            fontSize,
            pixelsPerDip);
        var refreshWidth = Measure(
            refresh,
            new Typeface(fontFamily, fontStyle, fontWeight, fontStretch),
            fontSize,
            pixelsPerDip);

        return new StatusMinimumWidthMeasurement(
            currentTimeWidth,
            refreshWidth,
            RefreshHorizontalPaddingWidth,
            FixedControlWidth,
            MarginAndBorderPaddingWidth);
    }

    private static double Measure(
        string text,
        Typeface typeface,
        double fontSize,
        double pixelsPerDip) => new FormattedText(
            text,
            JapaneseCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            pixelsPerDip).WidthIncludingTrailingWhitespace;
}
