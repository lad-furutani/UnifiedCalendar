using Microsoft.Win32;

namespace UnifiedCalendar.App.Sync;

public interface ISystemResumeSignal
{
    event EventHandler? Resumed;
}

public sealed class WindowsSystemResumeSignal : ISystemResumeSignal, IDisposable
{
    private bool _disposed;

    public WindowsSystemResumeSignal()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public event EventHandler? Resumed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _disposed = true;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Resume)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }
}
