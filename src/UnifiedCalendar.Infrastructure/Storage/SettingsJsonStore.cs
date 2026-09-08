using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Infrastructure.Storage;

public sealed class SettingsJsonStore : ISettingsStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly AppPaths _paths;
    private readonly AtomicFileWriter _atomicFileWriter;
    private readonly QuarantineManager _quarantine;
    private readonly IReadOnlyDictionary<int, ISettingsMigration> _migrations;
    private readonly StorageEventLogger _logger;

    public SettingsJsonStore(
        AppPaths paths,
        AtomicFileWriter atomicFileWriter,
        TimeProvider timeProvider,
        IEnumerable<ISettingsMigration>? migrations = null,
        StorageEventLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _atomicFileWriter = atomicFileWriter ?? throw new ArgumentNullException(nameof(atomicFileWriter));
        _logger = logger ?? new StorageEventLogger();
        _quarantine = new QuarantineManager(
            paths,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)),
            _logger);
        var migrationArray = migrations?.ToArray() ?? [new SettingsSchemaV0ToV1Migration()];
        if (migrationArray.Any(migration => migration is null))
        {
            throw new ArgumentException("Migrations cannot contain null elements.", nameof(migrations));
        }

        if (migrationArray.Any(migration => migration.ToVersion != migration.FromVersion + 1))
        {
            throw new ArgumentException("Every settings migration must advance exactly one version.", nameof(migrations));
        }

        try
        {
            _migrations = migrationArray.ToDictionary(migration => migration.FromVersion);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Only one settings migration may start at each version.", nameof(migrations), exception);
        }
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            _logger.Write(StorageFileKind.Settings, StorageOperation.Load, StorageResult.Missing);
            return AppSettings.CreateDefault();
        }

        JsonObject root;
        try
        {
            var bytes = await File.ReadAllBytesAsync(_paths.SettingsFile, cancellationToken).ConfigureAwait(false);
            root = JsonNode.Parse(bytes) as JsonObject
                ?? throw new JsonException("The settings root must be an object.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            _logger.Write(StorageFileKind.Settings, StorageOperation.Load, StorageResult.Missing);
            return AppSettings.CreateDefault();
        }
        catch (JsonException exception)
        {
            _logger.WriteFailure(StorageFileKind.Settings, StorageOperation.Load, exception);
            _quarantine.Quarantine(_paths.SettingsFile, "settings", "corrupt", "corrupt");
            return AppSettings.CreateDefault();
        }

        if (!TryReadSchemaVersion(root, out var schemaVersion) || schemaVersion < 0)
        {
            _logger.Write(StorageFileKind.Settings, StorageOperation.SchemaCheck, StorageResult.Invalid);
            _quarantine.Quarantine(_paths.SettingsFile, "settings", "corrupt", "corrupt");
            return AppSettings.CreateDefault();
        }

        if (schemaVersion > CurrentSchemaVersion)
        {
            _logger.Write(
                StorageFileKind.Settings,
                StorageOperation.SchemaCheck,
                StorageResult.Future,
                schemaVersion);
            _quarantine.Quarantine(
                _paths.SettingsFile,
                "settings",
                "future",
                $"future-v{schemaVersion}",
                schemaVersion);
            return AppSettings.CreateDefault();
        }

        if (schemaVersion < CurrentSchemaVersion)
        {
            _logger.Write(
                StorageFileKind.Settings,
                StorageOperation.SchemaCheck,
                StorageResult.Older,
                schemaVersion);
            return await LoadAndMigrateAsync(root, schemaVersion, cancellationToken).ConfigureAwait(false);
        }

        _logger.Write(
            StorageFileKind.Settings,
            StorageOperation.SchemaCheck,
            StorageResult.Current,
            schemaVersion);
        try
        {
            var settings = DeserializeAndValidate(root);
            _logger.Write(
                StorageFileKind.Settings,
                StorageOperation.Load,
                StorageResult.Success,
                schemaVersion);
            return settings;
        }
        catch (Exception exception) when (IsInvalidDataException(exception))
        {
            _logger.WriteFailure(
                StorageFileKind.Settings,
                StorageOperation.Load,
                exception,
                schemaVersion);
            _quarantine.Quarantine(
                _paths.SettingsFile,
                "settings",
                "corrupt",
                "corrupt",
                schemaVersion);
            return AppSettings.CreateDefault();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var document = SettingsJsonDocument.FromDomain(settings);
            await _atomicFileWriter.WriteJsonAsync(
                _paths.SettingsFile,
                document,
                StorageJson.Options,
                cancellationToken).ConfigureAwait(false);
            _logger.Write(
                StorageFileKind.Settings,
                StorageOperation.Save,
                StorageResult.Success,
                CurrentSchemaVersion);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.WriteFailure(
                StorageFileKind.Settings,
                StorageOperation.Save,
                exception,
                CurrentSchemaVersion);
            throw;
        }
    }

    private async Task<AppSettings> LoadAndMigrateAsync(
        JsonObject original,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        var sourceSchemaVersion = schemaVersion;
        JsonObject migrated;
        try
        {
            migrated = (JsonObject)original.DeepClone();
            while (schemaVersion < CurrentSchemaVersion)
            {
                if (!_migrations.TryGetValue(schemaVersion, out var migration))
                {
                    throw new InvalidOperationException(
                        $"No migration is registered from settings schema v{schemaVersion}.");
                }

                migrated = migration.Migrate(migrated)
                    ?? throw new SettingsMigrationException("A settings migration returned null.");
                if (!TryReadSchemaVersion(migrated, out var resultingVersion)
                    || resultingVersion != migration.ToVersion)
                {
                    throw new SettingsMigrationException(
                        "A settings migration produced an unexpected schema version.");
                }

                schemaVersion = resultingVersion;
            }
        }
        catch (SettingsMigrationException exception)
        {
            _logger.WriteFailure(
                StorageFileKind.Settings,
                StorageOperation.Migration,
                exception,
                sourceSchemaVersion);
            _quarantine.Quarantine(
                _paths.SettingsFile,
                "settings",
                "migration",
                $"migration-v{sourceSchemaVersion}",
                sourceSchemaVersion);
            return AppSettings.CreateDefault();
        }

        AppSettings settings;
        try
        {
            settings = DeserializeAndValidate(migrated);
        }
        catch (Exception exception) when (IsInvalidDataException(exception))
        {
            _logger.WriteFailure(
                StorageFileKind.Settings,
                StorageOperation.Migration,
                exception,
                sourceSchemaVersion);
            _quarantine.Quarantine(
                _paths.SettingsFile,
                "settings",
                "migration",
                $"migration-v{sourceSchemaVersion}",
                sourceSchemaVersion);
            return AppSettings.CreateDefault();
        }

        // Commit failures are deliberately outside the migration-data catches. AtomicFileWriter
        // preserves the active v0 body and the I/O or cancellation exception is propagated.
        await SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        _logger.Write(
            StorageFileKind.Settings,
            StorageOperation.Migration,
            StorageResult.Success,
            sourceSchemaVersion);
        return settings;
    }

    private static AppSettings DeserializeAndValidate(JsonObject root)
    {
        var document = root.Deserialize<SettingsJsonDocument>(StorageJson.Options)
            ?? throw new JsonException("The settings document is empty.");
        return document.ToDomain();
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
