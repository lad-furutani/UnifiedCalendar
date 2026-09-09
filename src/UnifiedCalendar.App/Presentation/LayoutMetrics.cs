using System.Windows;

namespace UnifiedCalendar.App.Presentation;

public static class LayoutMetrics
{
    public const double DipsPerInch = 96d;
    public const double InitialMainWidth = 820d;
    public const double InitialMainHeight = 640d;
    public const double MinimumMainWidth = 370d;
    public const double MinimumMainHeight = 400d;
    public const double InitialSettingsWidth = 760d;
    public const double InitialSettingsHeight = 560d;
    public const double MinimumSettingsWidth = 640d;
    public const double MinimumSettingsHeight = 440d;
    public const double PopupMaximumWidth = 520d;
    public const double PopupMaximumHeight = 600d;
    public const double StandardHorizontalPadding = 10d;
    public const double StandardVerticalPadding = 6d;
    public const double SeparatorThickness = 1d;
    public const double FocusBorderThickness = 2d;
    public const double StatusHeight = 48d;
    public const double DefaultFontSizeDip = 14d;
    public const double StatusHeightPerFontSizeDip = 1.5d;
    public const double WarningButtonHeight = 30d;
    public const double TimeColumnHorizontalPadding = 20d;
    public const double MinimumVisibleTitleBarWidth = 120d;
    public const double MinimumVisibleTitleBarHeight = 32d;
    public const double EventRowLineHeightFactor = 1.4d;
    public const double EventRowIconMaximumSize = 30d;
    public const double PopupPlacementGap = 8d;
    public const double PopupTitleFontSizeOffset = 4d;
    public const double PopupLabelFontSizeOffset = 2d;
    public const double PopupMinimumLabelFontSize = 9d;
    public const double SettingsSpinnerWidth = 128d;
    public const double SettingsSpinnerButtonWidth = 30d;
    public const double SettingsColorSwatchSize = 30d;
    public const double SettingsPaletteWidth = 330d;
    public const double SettingsPaletteItemWidth = 150d;

    public static Thickness SeparatorBorder { get; } = new(SeparatorThickness);

    public static Thickness BottomSeparatorBorder { get; } = new(0d, 0d, 0d, SeparatorThickness);

    public static Thickness HeaderSeparatorBorder { get; } = new(
        0d,
        SeparatorThickness,
        0d,
        SeparatorThickness);

    public static Thickness FocusBorder { get; } = new(FocusBorderThickness);

    public static Thickness SettingsFieldMargin { get; } = new(0d, 0d, 0d, 18d);

    public static Thickness SettingsLabelMargin { get; } = new(0d, 0d, 0d, 6d);

    public static Thickness SettingsInlineItemMargin { get; } = new(0d, 0d, 14d, 0d);

    public static Thickness SettingsPaletteItemMargin { get; } = new(0d, 0d, 8d, 8d);

    public static Thickness SettingsPaletteSelectionPadding { get; } = new(2d);

    public static Thickness SettingsSectionMargin { get; } = new(0d, 0d, 0d, 24d);

    public static Thickness SettingsInfoValueMargin { get; } = new(12d, 0d, 0d, 6d);

    public static GridLength SettingsSpinnerButtonGridWidth { get; } = new(SettingsSpinnerButtonWidth);

    public static Thickness SettingsButtonPadding { get; } = new(12d, 6d, 12d, 6d);

    public static Thickness SettingsSpinnerTextPadding { get; } = new(6d, 4d, 6d, 4d);

    public static Thickness UnregisteredOperationMessageMargin { get; } = new(0d, 12d, 0d, 0d);

    public static Thickness WarningOperationMessageMargin { get; } = new(0d, 4d, 0d, 0d);

    public static double CalculateEventRowMinHeight(
        double fontSizeDip,
        double verticalPaddingDip,
        double pixelsPerDip)
    {
        if (!double.IsFinite(fontSizeDip) || fontSizeDip <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSizeDip));
        }

        if (!double.IsFinite(verticalPaddingDip) || verticalPaddingDip < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalPaddingDip));
        }

        if (!double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
        }

        var contentHeight = Math.Max(
            fontSizeDip * EventRowLineHeightFactor,
            EventRowIconMaximumSize);
        var unrounded = contentHeight + (verticalPaddingDip * 2d) + SeparatorThickness;
        return Math.Ceiling(unrounded * pixelsPerDip) / pixelsPerDip;
    }

    public static double CalculateStatusHeight(double fontSizeDip)
    {
        ValidateFontSize(fontSizeDip);
        return StatusHeight
            + ((fontSizeDip - DefaultFontSizeDip) * StatusHeightPerFontSizeDip);
    }

    public static double CalculateWarningButtonHeight(double fontSizeDip)
    {
        ValidateFontSize(fontSizeDip);
        return WarningButtonHeight
            + ((fontSizeDip - DefaultFontSizeDip) * StatusHeightPerFontSizeDip);
    }

    public static double CalculatePopupTitleFontSize(double bodyFontSizeDip)
    {
        ValidatePopupBodyFontSize(bodyFontSizeDip);
        return bodyFontSizeDip + PopupTitleFontSizeOffset;
    }

    public static double CalculatePopupLabelFontSize(double bodyFontSizeDip)
    {
        ValidatePopupBodyFontSize(bodyFontSizeDip);
        return Math.Max(
            PopupMinimumLabelFontSize,
            bodyFontSizeDip - PopupLabelFontSizeOffset);
    }

    private static void ValidatePopupBodyFontSize(double bodyFontSizeDip)
    {
        ValidateFontSize(bodyFontSizeDip);
    }

    private static void ValidateFontSize(double fontSizeDip)
    {
        if (!double.IsFinite(fontSizeDip) || fontSizeDip <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSizeDip));
        }
    }
}
