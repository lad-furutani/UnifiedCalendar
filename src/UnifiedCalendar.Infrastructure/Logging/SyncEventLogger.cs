using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Serilog;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Sync;

namespace UnifiedCalendar.Infrastructure.Logging;

public sealed class SyncEventLogger : ISyncEventLogger
{
    private readonly ILogger _logger;

    public SyncEventLogger(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public void AccountStarted(
        ProviderKind provider,
        Guid accountId,
        SyncTriggerReason reason)
    {
        _logger.Information(
            "AccountSyncStarted {Provider} {InternalAccountId} {Reason}",
            provider,
            accountId,
            reason);
    }

    public void AccountCompleted(
        ProviderKind provider,
        Guid accountId,
        SyncAccountResultKind result,
        int count,
        long elapsedMilliseconds)
    {
        _logger.Information(
            "AccountSyncCompleted {Provider} {InternalAccountId} {Count} {ElapsedMs} {Result}",
            provider,
            accountId,
            count,
            elapsedMilliseconds,
            result);
    }

    public void CalendarCompleted(
        ProviderKind provider,
        Guid accountId,
        string calendarId,
        int count,
        SyncStatus result,
        SyncErrorCategory errorCategory,
        long elapsedMilliseconds)
    {
        _logger.Information(
            "CalendarFetchCompleted {Provider} {InternalAccountId} {CalendarKeyHash} {Count} {ElapsedMs} {Result} {ErrorCategory}",
            provider,
            accountId,
            HashIdentifier(calendarId),
            count,
            elapsedMilliseconds,
            result,
            errorCategory);
    }

    public void RetryScheduled(
        ProviderKind provider,
        Guid accountId,
        string? calendarId,
        int attempt,
        TimeSpan delay,
        SyncErrorCategory errorCategory)
    {
        _logger.Warning(
            "TransientRetryScheduled {Provider} {InternalAccountId} {CalendarKeyHash} {Attempt} {DelayMs} {ErrorCategory}",
            provider,
            accountId,
            HashIdentifier(calendarId),
            attempt,
            (long)delay.TotalMilliseconds,
            errorCategory);
    }

    public void RateLimitDeferred(
        ProviderKind provider,
        Guid accountId,
        DateTimeOffset retryAtUtc)
    {
        _logger.Warning(
            "RateLimitDeferred {Provider} {InternalAccountId} {RetryAtUtc} {ErrorCategory}",
            provider,
            accountId,
            retryAtUtc.ToUniversalTime(),
            SyncErrorCategory.RateLimited);
    }

    public void StorageFailed(string stage, Guid? accountId)
    {
        _logger.Error(
            "SyncStorageFailed {Stage} {InternalAccountId} {ErrorCategory}",
            stage,
            accountId,
            SyncErrorCategory.Storage);
    }

    public void UnexpectedFailed(string stage, ProviderKind? provider, Guid? accountId)
    {
        _logger.Error(
            "SyncUnexpectedFailed {Stage} {Provider} {InternalAccountId} {ErrorCategory}",
            stage,
            provider,
            accountId,
            SyncErrorCategory.Unexpected);
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
}
