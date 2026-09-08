using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnifiedCalendar.Infrastructure.Storage;

public enum AtomicWriteStage
{
    TemporaryFileCreated,
    ContentWritten,
    FlushAsyncCompleted,
    FlushToDiskCompleted,
    BeforeReplace,
    BeforeMove,
}

public interface IAtomicWriteFaultInjector
{
    void OnStage(AtomicWriteStage stage, string targetPath);
}

public class AtomicFileWriter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex TemporaryFilePattern = new(
        @"^\..+\.\d+\.[0-9a-f]{32}\.tmp$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IAtomicWriteFaultInjector? _faultInjector;
    private readonly StorageEventLogger _logger;

    public AtomicFileWriter(
        IAtomicWriteFaultInjector? faultInjector = null,
        StorageEventLogger? logger = null)
    {
        _faultInjector = faultInjector;
        _logger = logger ?? new StorageEventLogger();
    }

    public Task WriteJsonAsync<T>(
        string targetPath,
        T value,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serializerOptions);
        return WriteAsync(
            targetPath,
            (stream, token) => JsonSerializer.SerializeAsync(stream, value, serializerOptions, token),
            cancellationToken);
    }

    public async Task WriteAsync(
        string targetPath,
        Func<Stream, CancellationToken, Task> writeContentAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(writeContentAsync);

        var fullTargetPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullTargetPath)
            ?? throw new ArgumentException("The target path must have a parent directory.", nameof(targetPath));
        var pathLock = PathLocks.GetOrAdd(fullTargetPath, static _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullTargetPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous))
            {
                Notify(AtomicWriteStage.TemporaryFileCreated, fullTargetPath);
                await writeContentAsync(stream, cancellationToken).ConfigureAwait(false);
                Notify(AtomicWriteStage.ContentWritten, fullTargetPath);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                Notify(AtomicWriteStage.FlushAsyncCompleted, fullTargetPath);
                stream.Flush(flushToDisk: true);
            }

            Notify(AtomicWriteStage.FlushToDiskCompleted, fullTargetPath);
            if (File.Exists(fullTargetPath))
            {
                Notify(AtomicWriteStage.BeforeReplace, fullTargetPath);
                ReplaceFile(temporaryPath, fullTargetPath);
            }
            else
            {
                Notify(AtomicWriteStage.BeforeMove, fullTargetPath);
                MoveFile(temporaryPath, fullTargetPath);
            }

            temporaryPath = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.WriteFailure(StorageFileKind.Atomic, StorageOperation.AtomicWrite, exception);
            throw;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteFile(temporaryPath);
            }

            pathLock.Release();
        }
    }

    public async Task DeleteAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var fullTargetPath = Path.GetFullPath(targetPath);
        var pathLock = PathLocks.GetOrAdd(fullTargetPath, static _ => new SemaphoreSlim(1, 1));
        await pathLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(fullTargetPath))
            {
                File.Delete(fullTargetPath);
            }
        }
        finally
        {
            pathLock.Release();
        }
    }

    public int CleanupStaleTemporaryFiles(string directory, bool recursive = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removedCount = 0;
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.tmp", searchOption))
            {
                if (!TemporaryFilePattern.IsMatch(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    removedCount++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.WriteFailure(
                        StorageFileKind.Atomic,
                        StorageOperation.TemporaryCleanup,
                        exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.WriteFailure(StorageFileKind.Atomic, StorageOperation.TemporaryCleanup, exception);
        }

        return removedCount;
    }

    protected virtual void ReplaceFile(string temporaryPath, string targetPath) =>
        File.Replace(temporaryPath, targetPath, destinationBackupFileName: null);

    protected virtual void MoveFile(string temporaryPath, string targetPath) =>
        File.Move(temporaryPath, targetPath);

    private void Notify(AtomicWriteStage stage, string targetPath) =>
        _faultInjector?.OnStage(stage, targetPath);

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.WriteFailure(StorageFileKind.Atomic, StorageOperation.TemporaryCleanup, exception);
        }
    }
}
