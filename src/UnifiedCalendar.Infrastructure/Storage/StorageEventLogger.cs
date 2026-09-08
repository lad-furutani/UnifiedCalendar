using Serilog;
using UnifiedCalendar.Infrastructure.Logging;

namespace UnifiedCalendar.Infrastructure.Storage;

public enum StorageFileKind
{
    Settings,
    Cache,
    Token,
    Atomic,
}

public enum StorageOperation
{
    Load,
    Save,
    Remove,
    SchemaCheck,
    Migration,
    Quarantine,
    QuarantineRetention,
    AtomicWrite,
    TemporaryCleanup,
}

public enum StorageResult
{
    Success,
    Missing,
    Current,
    Older,
    Future,
    Invalid,
    Quarantined,
    Failed,
}

public sealed class StorageEventLogger
{
    private readonly ILogger _logger;

    public StorageEventLogger(ILogger? logger = null)
    {
        _logger = logger ?? Log.Logger;
    }

    public void Write(
        StorageFileKind fileKind,
        StorageOperation operation,
        StorageResult result,
        int? schemaVersion = null)
    {
        _logger.Information(
            "StorageEvent {FileKind} {Operation} {Result} {SchemaVersion}",
            fileKind,
            operation,
            result,
            schemaVersion);
    }

    public void WriteFailure(
        StorageFileKind fileKind,
        StorageOperation operation,
        Exception exception,
        int? schemaVersion = null)
    {
        var safeException = ExceptionSanitizer.Sanitize(exception);
        _logger.Error(
            "StorageEvent {FileKind} {Operation} {Result} {SchemaVersion} {ExceptionType} {ErrorCategory}",
            fileKind,
            operation,
            StorageResult.Failed,
            schemaVersion,
            safeException.ExceptionType,
            safeException.ErrorCategory);
    }
}
