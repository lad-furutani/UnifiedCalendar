using System.Diagnostics;
using System.IO;
using Serilog;
using UnifiedCalendar.Infrastructure.Storage;

namespace UnifiedCalendar.App.Services;

public enum AppLocalPathTarget
{
    RootDirectory,
    SettingsFile,
    LogsDirectory,
    LatestLogFile,
}

public interface IAppLocalPathLauncher
{
    Task OpenAsync(
        AppLocalPathTarget target,
        CancellationToken cancellationToken = default);
}

public interface ILocalPathOpenAdapter
{
    void Open(string path);
}

public sealed class ShellLocalPathOpenAdapter : ILocalPathOpenAdapter
{
    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }
}

public sealed class AppLocalPathLauncher : IAppLocalPathLauncher
{
    private const string LogFilePattern = "unifiedcalendar-*.log";

    private readonly AppPaths _paths;
    private readonly ILocalPathOpenAdapter _openAdapter;

    public AppLocalPathLauncher(AppPaths paths, ILocalPathOpenAdapter openAdapter)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _openAdapter = openAdapter ?? throw new ArgumentNullException(nameof(openAdapter));
    }

    public Task OpenAsync(
        AppLocalPathTarget target,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        try
        {
            var path = ResolveExistingPath(target);
            if (path is not null)
            {
                _openAdapter.Open(path);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(
                "LocalPathOpenFailed {Stage} {ErrorCategory}",
                target.ToString(),
                exception.GetType().Name);
        }

        return Task.CompletedTask;
    }

    private string? ResolveExistingPath(AppLocalPathTarget target) => target switch
    {
        AppLocalPathTarget.RootDirectory => ExistingDirectory(_paths.RootDirectory),
        AppLocalPathTarget.SettingsFile => ExistingFile(_paths.SettingsFile),
        AppLocalPathTarget.LogsDirectory => ExistingDirectory(_paths.LogsDirectory),
        AppLocalPathTarget.LatestLogFile => FindLatestLogFile(),
        _ => null,
    };

    private string? FindLatestLogFile()
    {
        if (!Directory.Exists(_paths.LogsDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(_paths.LogsDirectory, LogFilePattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Select(file => file.FullName)
            .FirstOrDefault();
    }

    private static string? ExistingDirectory(string path) =>
        Directory.Exists(path) ? path : null;

    private static string? ExistingFile(string path) => File.Exists(path) ? path : null;
}
