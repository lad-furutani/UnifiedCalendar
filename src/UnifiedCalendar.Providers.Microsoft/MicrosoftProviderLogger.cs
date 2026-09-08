using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.Providers.Microsoft;

internal sealed class MicrosoftProviderLogger
{
    private readonly ILogger _logger;

    public MicrosoftProviderLogger(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public void OperationCompleted(string operation, Guid accountId, string? calendarId, int count)
    {
        _logger.Information(
            "MicrosoftProviderOperation {Operation} {Provider} {InternalAccountId} {CalendarKeyHash} {Count} {Result}",
            operation,
            "Microsoft",
            accountId,
            HashIdentifier(calendarId),
            count,
            "Success");
    }

    public void OperationFailed(string operation, Guid accountId, string? calendarId, ProviderError error)
    {
        _logger.Error(
            "MicrosoftProviderOperation {Operation} {Provider} {InternalAccountId} {CalendarKeyHash} {ErrorCategory} {HttpStatusCategory} {Result}",
            operation,
            "Microsoft",
            accountId,
            HashIdentifier(calendarId),
            error.Category,
            GetStatusCategory(error.HttpStatusCode),
            "Failed");
    }

    public void EventsExcluded(
        Guid accountId,
        string calendarId,
        IEnumerable<IGrouping<MicrosoftEventExclusionReason, MicrosoftEventMappingResult>> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        foreach (var group in groups)
        {
            _logger.Warning(
                "MicrosoftProviderEventExcluded {Provider} {InternalAccountId} {CalendarKeyHash} {ExclusionReason} {Count}",
                "Microsoft",
                accountId,
                HashIdentifier(calendarId),
                group.Key,
                group.Count());
        }
    }

    public void CategoryColorsUnavailable(
        Guid accountId,
        string calendarId,
        ProviderError error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        ArgumentNullException.ThrowIfNull(error);
        _logger.Warning(
            "MicrosoftProviderAuxiliaryDataUnavailable {Operation} {Provider} {InternalAccountId} {CalendarKeyHash} {ErrorCategory} {HttpStatusCategory} {Result}",
            "GetMasterCategories",
            "Microsoft",
            accountId,
            HashIdentifier(calendarId),
            error.Category,
            GetStatusCategory(error.HttpStatusCode),
            "Fallback");
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
