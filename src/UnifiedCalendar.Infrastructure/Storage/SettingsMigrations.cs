using System.Text.Json.Nodes;

namespace UnifiedCalendar.Infrastructure.Storage;

public interface ISettingsMigration
{
    int FromVersion { get; }

    int ToVersion { get; }

    /// <summary>
    /// Returns a migrated clone. Implementations use <see cref="SettingsMigrationException"/> only
    /// when source data cannot be transformed; cancellation, I/O, and implementation failures are
    /// allowed to propagate without being classified as bad persisted data.
    /// </summary>
    JsonObject Migrate(JsonObject source);
}

public sealed class SettingsMigrationException : Exception
{
    public SettingsMigrationException(string message)
        : base(message)
    {
    }

    public SettingsMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Migrates a pre-release fixture used to prove the migration infrastructure; schema v0 was never
/// released as a product schema. It renames display.displayDays to display.days and supplies the
/// documented v1 defaults for properties that the fixture did not contain.
/// </summary>
public sealed class SettingsSchemaV0ToV1Migration : ISettingsMigration
{
    public int FromVersion => 0;

    public int ToVersion => 1;

    public JsonObject Migrate(JsonObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = (JsonObject)source.DeepClone();
        result["schemaVersion"] = ToVersion;

        var display = GetOrCreateObject(result, "display");
        if (display["days"] is null && display["displayDays"] is JsonNode legacyDisplayDays)
        {
            display["days"] = legacyDisplayDays.DeepClone();
            display.Remove("displayDays");
        }

        display["days"] ??= 7;
        display["fontSizeDip"] ??= 14;
        display["density"] ??= "standard";
        display["defaultEventColor"] ??= "#2F6FED";

        var sync = GetOrCreateObject(result, "sync");
        sync["intervalMinutes"] ??= 5;
        var general = GetOrCreateObject(result, "general");
        general["startWithWindows"] ??= true;

        var windows = GetOrCreateObject(result, "windows");
        if (!windows.ContainsKey("main"))
        {
            windows["main"] = null;
        }

        if (!windows.ContainsKey("settings"))
        {
            windows["settings"] = null;
        }

        result["accounts"] ??= new JsonArray();
        result["colorRules"] ??= new JsonArray();
        return result;
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string propertyName)
    {
        if (!parent.TryGetPropertyValue(propertyName, out var value))
        {
            var created = new JsonObject();
            parent[propertyName] = created;
            return created;
        }

        if (value is JsonObject existing)
        {
            return existing;
        }

        throw new SettingsMigrationException($"The pre-release {propertyName} section is not an object.");
    }
}
