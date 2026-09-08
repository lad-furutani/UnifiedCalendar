using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Infrastructure.Storage;

public sealed class AccountCacheJsonStore : ICacheStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly AppPaths _paths;
    private readonly AtomicFileWriter _atomicFileWriter;
    private readonly QuarantineManager _quarantine;
    private readonly StorageEventLogger _logger;

    public AccountCacheJsonStore(
        AppPaths paths,
        AtomicFileWriter atomicFileWriter,
        TimeProvider timeProvider,
        StorageEventLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _atomicFileWriter = atomicFileWriter ?? throw new ArgumentNullException(nameof(atomicFileWriter));
        _logger = logger ?? new StorageEventLogger();
        _quarantine = new QuarantineManager(
            paths,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)),
            _logger);
    }

    public async Task<AccountCache?> LoadAccountAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetAccountCacheFile(internalAccountId);
        if (!File.Exists(path))
        {
            _logger.Write(StorageFileKind.Cache, StorageOperation.Load, StorageResult.Missing);
            return null;
        }

        JsonObject root;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            root = JsonNode.Parse(bytes) as JsonObject
                ?? throw new JsonException("The account-cache root must be an object.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            _logger.Write(StorageFileKind.Cache, StorageOperation.Load, StorageResult.Missing);
            return null;
        }
        catch (JsonException exception)
        {
            _logger.WriteFailure(StorageFileKind.Cache, StorageOperation.Load, exception);
            _quarantine.Quarantine(path, "cache", "corrupt", "corrupt");
            return null;
        }

        if (!TryReadSchemaVersion(root, out var schemaVersion))
        {
            _logger.Write(StorageFileKind.Cache, StorageOperation.SchemaCheck, StorageResult.Invalid);
            _quarantine.Quarantine(path, "cache", "corrupt", "corrupt");
            return null;
        }

        if (schemaVersion < 0)
        {
            _logger.Write(
                StorageFileKind.Cache,
                StorageOperation.SchemaCheck,
                StorageResult.Invalid,
                schemaVersion);
            _quarantine.Quarantine(
                path,
                "cache",
                "corrupt",
                "corrupt",
                schemaVersion);
            return null;
        }

        if (schemaVersion < CurrentSchemaVersion)
        {
            _logger.Write(
                StorageFileKind.Cache,
                StorageOperation.SchemaCheck,
                StorageResult.Older,
                schemaVersion);
            _quarantine.Quarantine(
                path,
                "cache",
                "older",
                $"older-v{schemaVersion}",
                schemaVersion);
            return null;
        }

        if (schemaVersion > CurrentSchemaVersion)
        {
            _logger.Write(
                StorageFileKind.Cache,
                StorageOperation.SchemaCheck,
                StorageResult.Future,
                schemaVersion);
            _quarantine.Quarantine(
                path,
                "cache",
                "future",
                $"future-v{schemaVersion}",
                schemaVersion);
            return null;
        }

        _logger.Write(
            StorageFileKind.Cache,
            StorageOperation.SchemaCheck,
            StorageResult.Current,
            schemaVersion);
        try
        {
            var document = root.Deserialize<AccountCacheJsonDocument>(StorageJson.Options)
                ?? throw new JsonException("The account-cache document is empty.");
            var cache = document.ToDomain();
            if (cache.InternalAccountId != internalAccountId)
            {
                throw new InvalidDataException("The account-cache ID does not match its file name.");
            }

            _logger.Write(
                StorageFileKind.Cache,
                StorageOperation.Load,
                StorageResult.Success,
                schemaVersion);
            return cache;
        }
        catch (Exception exception) when (IsInvalidDataException(exception))
        {
            _logger.WriteFailure(
                StorageFileKind.Cache,
                StorageOperation.Load,
                exception,
                schemaVersion);
            _quarantine.Quarantine(
                path,
                "cache",
                "corrupt",
                "corrupt",
                schemaVersion);
            return null;
        }
    }

    public async Task SaveAccountAsync(
        AccountCache accountCache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountCache);
        try
        {
            await _atomicFileWriter.WriteJsonAsync(
                _paths.GetAccountCacheFile(accountCache.InternalAccountId),
                AccountCacheJsonDocument.FromDomain(accountCache),
                StorageJson.Options,
                cancellationToken).ConfigureAwait(false);
            _logger.Write(
                StorageFileKind.Cache,
                StorageOperation.Save,
                StorageResult.Success,
                CurrentSchemaVersion);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.WriteFailure(
                StorageFileKind.Cache,
                StorageOperation.Save,
                exception,
                CurrentSchemaVersion);
            throw;
        }
    }

    public async Task RemoveAccountAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetAccountCacheFile(internalAccountId);
        try
        {
            await _atomicFileWriter.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            _quarantine.RemoveForSource(path, "cache");
            _logger.Write(StorageFileKind.Cache, StorageOperation.Remove, StorageResult.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.WriteFailure(StorageFileKind.Cache, StorageOperation.Remove, exception);
            throw;
        }
    }

    private static bool TryReadSchemaVersion(JsonObject root, out int schemaVersion)
    {
        schemaVersion = default;
        return root["schemaVersion"] is JsonValue value
            && value.TryGetValue(out schemaVersion);
    }

    private static bool IsInvalidDataException(Exception exception) => exception is
        JsonException
        or InvalidDataException
        or ArgumentException
        or FormatException
        or OverflowException;
}
