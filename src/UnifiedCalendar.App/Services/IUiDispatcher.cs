namespace UnifiedCalendar.App.Services;

public interface IUiDispatcher
{
    Task InvokeAsync(Func<Task> callback, CancellationToken cancellationToken = default);
}
