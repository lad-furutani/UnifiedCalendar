using System.Windows.Threading;

namespace UnifiedCalendar.App.Services;

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatcher
            .InvokeAsync(callback, DispatcherPriority.DataBind, cancellationToken)
            .Task
            .Unwrap();
    }
}
