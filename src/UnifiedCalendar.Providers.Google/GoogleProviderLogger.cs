using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.Providers.Google;

internal sealed class GoogleProviderLogger
{
    private readonly ILogger _logger;

    public GoogleProviderLogger(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public void OperationCompleted(
        string operation,
        Guid internalAccountId,
        string? calendarId,
        int count)
    {
        _logger.Information(
            "GoogleProviderOperation {Operation} {Provider} {InternalAccountId} {CalendarKeyHash} {Count} {Result}",
            operation,
            "Google",
            internalAccountId,
            HashIdentifier(calendarId),
            count,
            "Success");
    }

    public void OperationFailed(
        string operation,
        Guid internalAccountId,
        string? calendarId,
        ProviderError error)
    {
        _logger.Error(
            "GoogleProviderOperation {Operation} {Provider} {InternalAccountId} {CalendarKeyHash} {ErrorCategory} {HttpStatusCategory} {Result}",
            operation,
            "Google",
            internalAccountId,
            HashIdentifier(calendarId),
            error.Category,
            GetStatusCategory(error.HttpStatusCode),
            "Failed");
    }

    public void EventsExcluded(
        Guid internalAccountId,
        string calendarId,
        IEnumerable<IGrouping<GoogleEventExclusionReason, GoogleEventMappingResult>> excludedGroups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        ArgumentNullException.ThrowIfNull(excludedGroups);
        foreach (var group in excludedGroups)
        {
            _logger.Warning(
                "GoogleProviderEventExcluded {Provider} {InternalAccountId} {CalendarKeyHash} {ExclusionReason} {Count}",
                "Google",
                internalAccountId,
                HashIdentifier(calendarId),
                group.Key,
                group.Count());
        }
    }

    private static string? HashIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(identifier);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        var input = new byte[length.Length + bytes.Length];
        length.CopyTo(input);
        bytes.CopyTo(input.AsSpan(length.Length));
        return Convert.ToBase64String(SHA256.HashData(input).AsSpan(0, 12))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string GetStatusCategory(int? statusCode) => statusCode switch
    {
        >= 400 and <= 499 => "4xx",
        >= 500 and <= 599 => "5xx",
        _ => "None",
    };
}
