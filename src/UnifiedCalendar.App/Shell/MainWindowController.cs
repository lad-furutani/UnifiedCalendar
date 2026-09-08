using System.Windows;
using System.Windows.Interop;
using UnifiedCalendar.App.Services;

namespace UnifiedCalendar.App.Shell;

public interface IMainWindowController
{
    Task ShowAndActivateAsync();

    Task ToggleVisibilityAsync();
}

public sealed class MainWindowController : IMainWindowController
{
    private readonly IUiDispatcher _dispatcher;
    private readonly object _gate = new();
    private MainWindow? _window;
    private bool _pendingActivation;

    public MainWindowController(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var activatePending = false;
        lock (_gate)
        {
            if (_window is not null && !ReferenceEquals(_window, window))
            {
                throw new InvalidOperationException("A main window is already attached.");
            }

            _window = window;
            activatePending = _pendingActivation;
            _pendingActivation = false;
        }

        if (activatePending)
        {
            _ = ActivateAsync(window);
        }
    }

    public Task ShowAndActivateAsync()
    {
        MainWindow? window;
        lock (_gate)
        {
            window = _window;
            if (window is null)
            {
                _pendingActivation = true;
                return Task.CompletedTask;
            }
        }

        return ActivateAsync(window);
    }

    public Task ToggleVisibilityAsync() => _dispatcher.InvokeAsync(() =>
    {
        var window = GetWindow();
        if (window.IsVisible)
        {
            window.HideToTray();
            return Task.CompletedTask;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        window.WindowState = WindowState.Normal;
        _ = window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0)
        {
            _ = NativeMethods.SetForegroundWindow(handle);
        }

        return Task.CompletedTask;
    });

    private Task ActivateAsync(MainWindow window) => _dispatcher.InvokeAsync(() =>
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        window.WindowState = WindowState.Normal;
        _ = window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0)
        {
            _ = NativeMethods.SetForegroundWindow(handle);
        }

        return Task.CompletedTask;
    });

    private MainWindow GetWindow()
    {
        lock (_gate)
        {
            return _window
                ?? throw new InvalidOperationException("The main window has not been attached.");
        }
    }
}
