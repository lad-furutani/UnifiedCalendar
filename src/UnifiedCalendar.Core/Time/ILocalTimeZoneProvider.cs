namespace UnifiedCalendar.Core.Time;

public interface ILocalTimeZoneProvider
{
    TimeZoneInfo GetCurrent();
}
