using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class WpfApplicationFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Application> _applicationReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public WpfApplicationFixture()
    {
        _thread = new Thread(RunApplication)
        {
            IsBackground = true,
            Name = "UnifiedCalendar WPF tests",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Application = _applicationReady.Task.WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter()
            .GetResult();
    }

    public Application Application { get; }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Application.Dispatcher.Invoke(action);
    }

    public T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Application.Dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Application.Dispatcher.Invoke(() =>
        {
            Application.Shutdown();
            Application.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        });
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("The shared WPF dispatcher did not stop.");
        }
    }

    private void RunApplication()
    {
        try
        {
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/UnifiedCalendar.App;component/Resources/Controls.xaml",
                    UriKind.Relative),
            });
            _applicationReady.SetResult(application);
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            _applicationReady.TrySetException(exception);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfApplicationCollection : ICollectionFixture<WpfApplicationFixture>
{
    public const string Name = "WPF Application";
}
