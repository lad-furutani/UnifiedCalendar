using Microsoft.Win32;
using Serilog;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;

namespace UnifiedCalendar.App.Services;

public interface ITimeZoneChangeSignal : IDisposable
{
    event EventHandler? TimeZoneChanged;

    void Start();
}

public interface IRefreshableLocalTimeZoneProvider : ILocalTimeZoneProvider
{
    TimeZoneInfo Refresh();
}

public sealed class SystemTimeZoneChangeSignal : ITimeZoneChangeSignal
{
    private bool _started;

    public event EventHandler? TimeZoneChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        SystemEvents.TimeChanged += OnTimeChanged;
        _started = true;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        SystemEvents.TimeChanged -= OnTimeChanged;
        _started = false;
    }

    private void OnTimeChanged(object? sender, EventArgs eventArgs) =>
        TimeZoneChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class WindowsLocalTimeZoneProvider : IRefreshableLocalTimeZoneProvider
{
    public TimeZoneInfo GetCurrent() => TimeZoneInfo.Local;

    public TimeZoneInfo Refresh()
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local;
    }
}

public sealed class RuntimeTimeZoneService : IDisposable
{
    private readonly IRefreshableLocalTimeZoneProvider _timeZoneProvider;
    private readonly ITimeZoneChangeSignal _changeSignal;
    private readonly IUiDispatcher _dispatcher;
    private readonly IInternalRefreshRequester _refreshRequester;
    private TimeZoneInfo? _lastTimeZone;
    private bool _started;

    public RuntimeTimeZoneService(
        IRefreshableLocalTimeZoneProvider timeZoneProvider,
        ITimeZoneChangeSignal changeSignal,
        IUiDispatcher dispatcher,
        IInternalRefreshRequester refreshRequester)
    {
        _timeZoneProvider = timeZoneProvider
            ?? throw new ArgumentNullException(nameof(timeZoneProvider));
        _changeSignal = changeSignal ?? throw new ArgumentNullException(nameof(changeSignal));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _refreshRequester = refreshRequester ?? throw new ArgumentNullException(nameof(refreshRequester));
    }

    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("Runtime time-zone monitoring is already running.");
        }

        _lastTimeZone = _timeZoneProvider.GetCurrent();
        _changeSignal.TimeZoneChanged += OnTimeZoneChanged;
        _changeSignal.Start();
        _started = true;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _changeSignal.TimeZoneChanged -= OnTimeZoneChanged;
        _changeSignal.Dispose();
        _lastTimeZone = null;
        _started = false;
    }

    private void OnTimeZoneChanged(object? sender, EventArgs eventArgs) =>
        Observe(RefreshIfChangedAsync());

    private Task RefreshIfChangedAsync() => _dispatcher.InvokeAsync(async () =>
    {
        var current = _timeZoneProvider.Refresh();
        if (_lastTimeZone is not null && AreEquivalent(_lastTimeZone, current))
        {
            return;
        }

        _lastTimeZone = current;
        await _refreshRequester
            .RequestRefreshAsync(InternalRefreshReason.TimeZoneChanged)
            .ConfigureAwait(true);
    });

    private static bool AreEquivalent(TimeZoneInfo left, TimeZoneInfo right) =>
        left.Id.Equals(right.Id, StringComparison.Ordinal)
        && left.HasSameRules(right);

    private static async void Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "RuntimeTimeZoneUpdateFailed {Stage} {ErrorCategory}",
                "TimeZone",
                exception.GetType().Name);
        }
    }
}
