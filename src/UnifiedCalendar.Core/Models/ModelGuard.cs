namespace UnifiedCalendar.Core.Models;

internal static class ModelGuard
{
    public static void NotEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The value cannot be an empty GUID.", parameterName);
        }
    }

    public static string NotBlank(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    public static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The enum value is not defined.");
        }

        return value;
    }
}
