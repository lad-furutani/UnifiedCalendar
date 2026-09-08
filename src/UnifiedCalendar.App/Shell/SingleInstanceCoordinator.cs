using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Serilog;
using UnifiedCalendar.Core;

namespace UnifiedCalendar.App.Shell;

public sealed record SingleInstanceNames(string MutexName, string PipeName)
{
    public static SingleInstanceNames ForSid(string currentUserSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentUserSid);
        return new SingleInstanceNames(
            AppIdentity.InstanceMutexPrefix + currentUserSid,
            AppIdentity.ActivationPipePrefix + currentUserSid);
    }

    public static SingleInstanceNames ForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        return ForSid(sid);
    }
}

public interface IForegroundPermissionService
{
    bool AllowSetForegroundWindow(int processId);
}

public sealed class WindowsForegroundPermissionService : IForegroundPermissionService
{
    public bool AllowSetForegroundWindow(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        return NativeMethods.AllowSetForegroundWindow((uint)processId);
    }
}

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const string ActivationCommand = "activate";
    private const int ConnectAttemptCount = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly SingleInstanceNames _names;
    private readonly IForegroundPermissionService _foregroundPermission;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private readonly ManualResetEventSlim _mutexRelease = new(initialState: false);
    private readonly TaskCompletionSource<bool> _mutexAcquired = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _mutexThread;
    private Task? _listenerTask;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator(
        SingleInstanceNames names,
        IForegroundPermissionService? foregroundPermission = null)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
        _foregroundPermission = foregroundPermission ?? new WindowsForegroundPermissionService();
    }

    public bool TryAcquirePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mutexThread is not null)
        {
            return _ownsMutex;
        }

        _mutexThread = new Thread(HoldMutex)
        {
            IsBackground = true,
            Name = "UnifiedCalendar instance mutex",
        };
        _mutexThread.Start();
        _ownsMutex = _mutexAcquired.Task
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
        return _ownsMutex;
    }

    public void StartListening(Func<Task> activationHandler)
    {
        ArgumentNullException.ThrowIfNull(activationHandler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ownsMutex)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation.");
        }

        if (_listenerTask is not null)
        {
            throw new InvalidOperationException("The activation listener is already running.");
        }

        _listenerTask = ListenAsync(activationHandler, _listenerCancellation.Token);
    }

    public async Task<bool> SignalPrimaryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var attempt = 0; attempt < ConnectAttemptCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    _names.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync((int)RetryDelay.TotalMilliseconds, cancellationToken)
                    .ConfigureAwait(false);

                using var reader = new StreamReader(
                    client,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);
                await using var writer = new StreamWriter(
                    client,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };
                var processIdText = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!int.TryParse(
                    processIdText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId)
                    || processId <= 0)
                {
                    return false;
                }

                _ = _foregroundPermission.AllowSetForegroundWindow(processId);
                await writer.WriteLineAsync(ActivationCommand.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                if (attempt == ConnectAttemptCount - 1)
                {
                    return false;
                }

                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation.Cancel();
        _mutexRelease.Set();
        var mutexThreadJoined = _mutexThread is null
            || _mutexThread.Join(TimeSpan.FromSeconds(5));
        if (!mutexThreadJoined)
        {
            Log.Warning(
                "InstanceMutexReleaseFailed {Stage} {ErrorCategory}",
                "Mutex",
                "Timeout");
        }

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Log.Warning(
                    "InstanceActivationListenerFailed {Stage} {ErrorCategory}",
                    "Dispose",
                    exception.GetType().Name);
            }
        }

        _listenerCancellation.Dispose();
        if (mutexThreadJoined)
        {
            _mutexRelease.Dispose();
        }

        _mutexThread = null;
        _ownsMutex = false;
    }

    private void HoldMutex()
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, _names.MutexName);
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            _mutexAcquired.TrySetResult(acquired);
            if (!acquired)
            {
                return;
            }

            _mutexRelease.Wait();
            mutex.ReleaseMutex();
        }
        catch (Exception exception)
        {
            _mutexAcquired.TrySetException(exception);
        }
    }

    private async Task ListenAsync(Func<Task> activationHandler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _names.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(
                    server,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);
                await using var writer = new StreamWriter(
                    server,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: true)
                {
                    AutoFlush = true,
                };
                await writer.WriteLineAsync(
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture).AsMemory(),
                    cancellationToken).ConfigureAwait(false);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (command?.Equals(ActivationCommand, StringComparison.Ordinal) == true)
                {
                    try
                    {
                        await activationHandler().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Log.Warning(
                            "InstanceActivationListenerFailed {Stage} {ErrorCategory}",
                            "Handler",
                            exception.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.Warning(
                    "InstanceActivationListenerFailed {Stage} {ErrorCategory}",
                    "Pipe",
                    exception.GetType().Name);
            }
        }
    }
}
