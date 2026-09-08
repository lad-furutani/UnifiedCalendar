namespace UnifiedCalendar.Core.Models;

public sealed record CalendarEvent
{
    public CalendarEvent(
        EventKey key,
        string title,
        EventTiming timing,
        string calendarName,
        string? descriptionPlainText = null,
        string? location = null,
        string? sourceTimeZoneId = null,
        AttendeeResponse responseStatus = AttendeeResponse.Unknown,
        RgbColor? sourceEventColor = null,
        RgbColor? sourceCalendarColor = null,
        Uri? meetingUri = null,
        Uri? sourceDetailUri = null,
        bool isCancelled = false)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentNullException.ThrowIfNull(title);
        if (!key.IsValid)
        {
            throw new ArgumentException("A valid event key is required.", nameof(key));
        }

        if (timing is not TimedEventTiming and not AllDayEventTiming)
        {
            throw new ArgumentException("The event timing type is not supported.", nameof(timing));
        }

        ModelGuard.NotBlank(calendarName, nameof(calendarName));
        ModelGuard.Defined(responseStatus, nameof(responseStatus));

        ValidateWebUri(meetingUri, nameof(meetingUri));
        ValidateWebUri(sourceDetailUri, nameof(sourceDetailUri));

        Key = key;
        Title = title;
        Timing = timing;
        CalendarName = calendarName;
        DescriptionPlainText = descriptionPlainText;
        Location = location;
        SourceTimeZoneId = sourceTimeZoneId;
        ResponseStatus = responseStatus;
        SourceEventColor = sourceEventColor;
        SourceCalendarColor = sourceCalendarColor;
        MeetingUri = meetingUri;
        SourceDetailUri = sourceDetailUri;
        IsCancelled = isCancelled;
    }

    public EventKey Key { get; }

    public string Title { get; }

    public string? DescriptionPlainText { get; }

    public string? Location { get; }

    public EventTiming Timing { get; }

    public string? SourceTimeZoneId { get; }

    public AttendeeResponse ResponseStatus { get; }

    public RgbColor? SourceEventColor { get; }

    public RgbColor? SourceCalendarColor { get; }

    public Uri? MeetingUri { get; }

    public Uri? SourceDetailUri { get; }

    public bool IsCancelled { get; }

    public string CalendarName { get; }

    private static void ValidateWebUri(Uri? uri, string parameterName)
    {
        if (uri is null)
        {
            return;
        }

        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP or HTTPS URIs are allowed.", parameterName);
        }
    }
}
