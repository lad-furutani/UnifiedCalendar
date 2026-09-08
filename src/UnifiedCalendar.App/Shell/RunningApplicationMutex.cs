using UnifiedCalendar.Core;

namespace UnifiedCalendar.App.Shell;

internal sealed class RunningApplicationMutex : IDisposable
{
    private Mutex? _mutex;

    public RunningApplicationMutex()
        : this(AppIdentity.RunningApplicationMutexName)
    {
    }

    internal RunningApplicationMutex(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _mutex = new Mutex(initiallyOwned: false, name);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _mutex, null)?.Dispose();
    }
}
