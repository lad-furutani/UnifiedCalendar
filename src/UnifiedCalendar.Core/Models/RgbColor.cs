using System.Globalization;

namespace UnifiedCalendar.Core.Models;

public readonly record struct RgbColor(byte Red, byte Green, byte Blue)
{
    public static RgbColor Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length != 7 || value[0] != '#')
        {
            throw new FormatException("A color must use the #RRGGBB format.");
        }

        return new RgbColor(
            byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    public string ToHexString() => $"#{Red:X2}{Green:X2}{Blue:X2}";

    public double GetRelativeLuminance()
    {
        static double ToLinear(byte channel)
        {
            var srgb = channel / 255d;
            return srgb <= 0.04045d
                ? srgb / 12.92d
                : Math.Pow((srgb + 0.055d) / 1.055d, 2.4d);
        }

        return (0.2126d * ToLinear(Red))
            + (0.7152d * ToLinear(Green))
            + (0.0722d * ToLinear(Blue));
    }

    public double GetContrastRatio(RgbColor other)
    {
        var first = GetRelativeLuminance();
        var second = other.GetRelativeLuminance();
        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    public RgbColor AdjustLightness(double delta)
    {
        var red = Red / 255d;
        var green = Green / 255d;
        var blue = Blue / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var lightness = (max + min) / 2d;
        var adjustedLightness = Math.Clamp(lightness + delta, 0d, 1d);

        if (Math.Abs(max - min) < double.Epsilon)
        {
            var gray = ToByte(adjustedLightness);
            return new RgbColor(gray, gray, gray);
        }

        var difference = max - min;
        var saturation = lightness > 0.5d
            ? difference / (2d - max - min)
            : difference / (max + min);

        double hue;
        if (Math.Abs(max - red) < double.Epsilon)
        {
            hue = ((green - blue) / difference) + (green < blue ? 6d : 0d);
        }
        else if (Math.Abs(max - green) < double.Epsilon)
        {
            hue = ((blue - red) / difference) + 2d;
        }
        else
        {
            hue = ((red - green) / difference) + 4d;
        }

        hue /= 6d;
        var q = adjustedLightness < 0.5d
            ? adjustedLightness * (1d + saturation)
            : adjustedLightness + saturation - (adjustedLightness * saturation);
        var p = (2d * adjustedLightness) - q;

        return new RgbColor(
            ToByte(HueToRgb(p, q, hue + (1d / 3d))),
            ToByte(HueToRgb(p, q, hue)),
            ToByte(HueToRgb(p, q, hue - (1d / 3d))));
    }

    public override string ToString() => ToHexString();

    private static double HueToRgb(double p, double q, double hue)
    {
        if (hue < 0d)
        {
            hue += 1d;
        }

        if (hue > 1d)
        {
            hue -= 1d;
        }

        if (hue < 1d / 6d)
        {
            return p + ((q - p) * 6d * hue);
        }

        if (hue < 1d / 2d)
        {
            return q;
        }

        if (hue < 2d / 3d)
        {
            return p + ((q - p) * ((2d / 3d) - hue) * 6d);
        }

        return p;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(
        Math.Round(value * 255d, MidpointRounding.AwayFromZero),
        byte.MinValue,
        byte.MaxValue);
}
