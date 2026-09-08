using System.Security.Cryptography;
using System.Text;

namespace UnifiedCalendar.Infrastructure.Storage;

internal sealed class QuarantineManager
{
    private const int RetainedFileCount = 3;
    private readonly AppPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly StorageEventLogger _logger;

    public QuarantineManager(
        AppPaths paths,
        TimeProvider timeProvider,
        StorageEventLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? new StorageEventLogger();
    }

    public bool Quarantine(
        string sourcePath,
        string fileType,
        string kindGroup,
        string kindDetail,
        int? schemaVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ValidateNamePart(fileType, nameof(fileType));
        ValidateNamePart(kindGroup, nameof(kindGroup));
        ValidateNamePart(kindDetail, nameof(kindDetail));
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        var sourceKey = GetSourceKey(sourcePath);
        var timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss.fffffff'Z'");
        var destination = Path.Combine(
            _paths.RecoveryDirectory,
            $"{fileType}-{sourceKey}-{kindGroup}-{timestamp}-{kindDetail}-{Guid.NewGuid():N}.json");

        try
        {
            Directory.CreateDirectory(_paths.RecoveryDirectory);
            File.Move(sourcePath, destination);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.WriteFailure(
                GetFileKind(fileType),
                StorageOperation.Quarantine,
                exception,
                schemaVersion);
            return false;
        }

        try
        {
            RetainNewest(fileType, sourceKey, kindGroup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.WriteFailure(
                GetFileKind(fileType),
                StorageOperation.QuarantineRetention,
                exception,
                schemaVersion);
            // Retention is best effort; the quarantined input must not become active again.
        }

        _logger.Write(
            GetFileKind(fileType),
            StorageOperation.Quarantine,
            StorageResult.Quarantined,
            schemaVersion);
        return true;
    }

    public void RemoveForSource(string sourcePath, string fileType)
    {
        ValidateNamePart(fileType, nameof(fileType));
        if (!Directory.Exists(_paths.RecoveryDirectory))
        {
            return;
        }

        var sourceKey = GetSourceKey(sourcePath);
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         _paths.RecoveryDirectory,
                         $"{fileType}-{sourceKey}-*.json",
                         SearchOption.TopDirectoryOnly))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.WriteFailure(GetFileKind(fileType), StorageOperation.Remove, exception);
            throw;
        }
    }

    private void RetainNewest(string fileType, string sourceKey, string kindGroup)
    {
        var files = Directory.EnumerateFiles(
                _paths.RecoveryDirectory,
                $"{fileType}-{sourceKey}-{kindGroup}-*.json",
                SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(RetainedFileCount);

        foreach (var file in files)
        {
            File.Delete(file);
        }
    }

    private static string GetSourceKey(string sourcePath)
    {
        var normalized = Path.GetFullPath(sourcePath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }

    private static void ValidateNamePart(string value, string parameterName)
    {
        if (value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("The quarantine name part contains unsupported characters.", parameterName);
        }
    }

    private static StorageFileKind GetFileKind(string fileType) => fileType switch
    {
        "settings" => StorageFileKind.Settings,
        "cache" => StorageFileKind.Cache,
        "token" => StorageFileKind.Token,
        _ => StorageFileKind.Atomic,
    };
}
