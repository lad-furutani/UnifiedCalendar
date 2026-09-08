namespace UnifiedCalendar.Core.Time;

public sealed class SystemLocalTimeZoneProvider : ILocalTimeZoneProvider
{
    public TimeZoneInfo GetCurrent() => TimeZoneInfo.Local;
}

public sealed class FixedLocalTimeZoneProvider : ILocalTimeZoneProvider
{
    private readonly TimeZoneInfo _timeZone;

    public FixedLocalTimeZoneProvider(TimeZoneInfo timeZone)
    {
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
    }

    public TimeZoneInfo GetCurrent() => _timeZone;
}
