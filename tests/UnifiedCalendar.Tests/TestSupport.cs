using System.Collections;
using System.Globalization;
using Xunit;

namespace UnifiedCalendar.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CultureSensitiveCollection
{
    public const string Name = "Culture-sensitive presentation tests";
}

internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

    public CultureScope(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
    }
}

internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public MutableTimeProvider(DateTimeOffset utcNow)
    {
        SetUtcNow(utcNow);
    }

    public void SetUtcNow(DateTimeOffset utcNow)
    {
        _utcNow = utcNow.ToUniversalTime();
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
}

internal sealed class CallbackTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;
    private readonly Action _callback;

    public CallbackTimeProvider(DateTimeOffset utcNow, Action callback)
    {
        _utcNow = utcNow.ToUniversalTime();
        _callback = callback;
    }

    public override DateTimeOffset GetUtcNow()
    {
        _callback();
        return _utcNow;
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _syncRoot = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public ManualTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? localTimeZone = null)
    {
        _utcNow = utcNow.ToUniversalTime();
        LocalTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
    }

    public override TimeZoneInfo LocalTimeZone { get; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public int PendingTimerCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_syncRoot)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_syncRoot)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_syncRoot)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_syncRoot)
        {
            _utcNow = _utcNow.Add(amount);
            _timestamp += amount.Ticks;
            foreach (var timer in _timers.ToArray())
            {
                timer.CollectCallbacks(_utcNow, callbacks);
            }
        }

        foreach (var callback in callbacks)
        {
            callback.Callback(callback.State);
        }
    }

    private void ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_syncRoot)
        {
            timer.ChangeCore(_utcNow, dueTime, period);
        }
    }

    private void RemoveTimer(ManualTimer timer)
    {
        lock (_syncRoot)
        {
            _timers.Remove(timer);
            timer.DisposeCore();
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAtUtc;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            ChangeCore(owner._utcNow, dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _owner.ChangeTimer(this, dueTime, period);
            return true;
        }

        public void Dispose() => _owner.RemoveTimer(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void CollectCallbacks(
            DateTimeOffset utcNow,
            ICollection<(TimerCallback Callback, object? State)> callbacks)
        {
            if (_disposed || !_dueAtUtc.HasValue || _dueAtUtc.Value > utcNow)
            {
                return;
            }

            callbacks.Add((_callback, _state));
            if (_period == Timeout.InfiniteTimeSpan)
            {
                _dueAtUtc = null;
                return;
            }

            do
            {
                _dueAtUtc = _dueAtUtc.Value.Add(_period);
            }
            while (_dueAtUtc <= utcNow);
        }

        public void ChangeCore(
            DateTimeOffset utcNow,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _period = period;
            _dueAtUtc = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : utcNow.Add(dueTime);
        }

        public void DisposeCore()
        {
            _disposed = true;
            _dueAtUtc = null;
        }
    }
}

internal sealed class CountingReadOnlyList<T> : IReadOnlyList<T>
{
    private readonly List<T> _items;

    public CountingReadOnlyList(IEnumerable<T> items)
    {
        _items = items.ToList();
    }

    public int EnumerationCount { get; private set; }

    public List<T> Items => _items;

    public int Count => _items.Count;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator()
    {
        EnumerationCount++;
        return _items.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
