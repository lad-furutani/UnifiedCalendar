using System.Security.Cryptography;
using System.Text.Json;
using UnifiedCalendar.Infrastructure.Storage;

namespace UnifiedCalendar.Infrastructure.Logging;

public enum SafeErrorCategory
{
    Io,
    AccessDenied,
    InvalidData,
    Cryptography,
    Cancelled,
    Unexpected,
}

public readonly record struct SanitizedException(
    string ExceptionType,
    SafeErrorCategory ErrorCategory);

public static class ExceptionSanitizer
{
    public static SanitizedException Sanitize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new SanitizedException(
            exception.GetType().Name,
            exception switch
            {
                OperationCanceledException => SafeErrorCategory.Cancelled,
                UnauthorizedAccessException => SafeErrorCategory.AccessDenied,
                IOException => SafeErrorCategory.Io,
                JsonException or InvalidDataException or SettingsMigrationException or ArgumentException
                    or FormatException or OverflowException => SafeErrorCategory.InvalidData,
                CryptographicException => SafeErrorCategory.Cryptography,
                _ => SafeErrorCategory.Unexpected,
            });
    }
}
