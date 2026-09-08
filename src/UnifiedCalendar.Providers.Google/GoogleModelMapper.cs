using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Google.Apis.Calendar.v3.Data;
using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Providers.Google;

internal static partial class GoogleModelMapper
{
    public static CalendarDescriptor? ToCalendarDescriptor(CalendarListEntry? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Id))
        {
            return null;
        }

        var name = FirstNonBlank(source.SummaryOverride, source.Summary, "Untitled calendar");
        return new CalendarDescriptor(
            source.Id,
            source.Id,
            name,
            source.Primary == true,
            CanReadEvents(source.AccessRole),
            ParseColor(source.BackgroundColor));
    }

    public static GoogleEventMappingResult ToCalendarEvent(
        Event? source,
        CalendarAccount account,
        CalendarDescriptor calendar,
        IReadOnlyDictionary<string, RgbColor> eventColors)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(eventColors);

        if (source?.Status?.Equals("cancelled", StringComparison.OrdinalIgnoreCase) == true)
        {
            return GoogleEventMappingResult.Excluded(GoogleEventExclusionReason.Cancelled);
        }

        if (source is null || string.IsNullOrWhiteSpace(source.Id))
        {
            return GoogleEventMappingResult.Excluded(GoogleEventExclusionReason.MissingIdentifier);
        }

        if (!TryGetTiming(source.Start, source.End, out var timing, out var sourceTimeZoneId))
        {
            return GoogleEventMappingResult.Excluded(GoogleEventExclusionReason.InvalidTiming);
        }

        if (!TryGetOccurrenceKey(source, out var occurrenceKey))
        {
            return GoogleEventMappingResult.Excluded(GoogleEventExclusionReason.InvalidOccurrenceKey);
        }

        var sourceEventColor = !string.IsNullOrWhiteSpace(source.ColorId)
            && eventColors.TryGetValue(source.ColorId, out var eventColor)
                ? eventColor
                : (RgbColor?)null;

        return GoogleEventMappingResult.Mapped(new CalendarEvent(
            new EventKey(
                ProviderKind.Google,
                account.InternalAccountId,
                calendar.CalendarId,
                source.Id,
                occurrenceKey),
            source.Summary ?? string.Empty,
            timing,
            calendar.Name,
            HtmlToPlainText(source.Description),
            NullIfWhiteSpace(source.Location),
            sourceTimeZoneId,
            GetParticipation(source.Attendees),
            sourceEventColor,
            calendar.SourceColor,
            GetMeetingUri(source),
            TryGetWebUri(source.HtmlLink),
            isCancelled: false));
    }

    public static IReadOnlyDictionary<string, RgbColor> ToEventColorMap(Colors? colors)
    {
        if (colors?.Event__ is null)
        {
            return new Dictionary<string, RgbColor>(StringComparer.Ordinal);
        }

        return colors.Event__
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => (pair.Key, Color: ParseColor(pair.Value?.Background)))
            .Where(pair => pair.Color.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Color!.Value, StringComparer.Ordinal);
    }

    internal static string? HtmlToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withLineBreaks = BlockBreakRegex().Replace(value, "\n");
        var withoutTags = HtmlTagRegex().Replace(withLineBreaks, string.Empty);
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace("\u00a0", " ", StringComparison.Ordinal);
        var normalized = NewlineRegex().Replace(decoded.Replace("\r\n", "\n", StringComparison.Ordinal), "\n").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool TryGetTiming(
        EventDateTime? start,
        EventDateTime? end,
        out EventTiming timing,
        out string? sourceTimeZoneId)
    {
        timing = null!;
        sourceTimeZoneId = NullIfWhiteSpace(start?.TimeZone) ?? NullIfWhiteSpace(end?.TimeZone);
        if (start is null || end is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(start.Date) || !string.IsNullOrWhiteSpace(end.Date))
        {
            if (!string.IsNullOrWhiteSpace(start.DateTimeRaw)
                || !string.IsNullOrWhiteSpace(end.DateTimeRaw)
                || !DateOnly.TryParseExact(start.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate)
                || !DateOnly.TryParseExact(end.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate)
                || endDate <= startDate)
            {
                return false;
            }

            timing = new AllDayEventTiming(startDate, endDate);
            return true;
        }

        var startValue = start.DateTimeDateTimeOffset;
        var endValue = end.DateTimeDateTimeOffset;
        if (!startValue.HasValue || !endValue.HasValue)
        {
            return false;
        }

        var startUtc = startValue.Value.ToUniversalTime();
        var endUtc = endValue.Value.ToUniversalTime();
        if (endUtc < startUtc)
        {
            return false;
        }

        timing = new TimedEventTiming(startUtc, endUtc);
        return true;
    }

    private static bool TryGetOccurrenceKey(Event source, out string occurrenceKey)
    {
        occurrenceKey = string.Empty;
        if (string.IsNullOrWhiteSpace(source.RecurringEventId))
        {
            return true;
        }

        if (!TryNormalizeOriginalStart(source.OriginalStartTime, out var originalStart))
        {
            return false;
        }

        occurrenceKey = EncodeOccurrenceComponent(source.RecurringEventId)
            + EncodeOccurrenceComponent(originalStart);
        return true;
    }

    private static bool TryNormalizeOriginalStart(EventDateTime? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(value.Date))
        {
            if (!DateOnly.TryParseExact(value.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return false;
            }

            normalized = $"D:{date:yyyy-MM-dd}";
            return true;
        }

        if (!value.DateTimeDateTimeOffset.HasValue)
        {
            return false;
        }

        normalized = "T:" + value.DateTimeDateTimeOffset.Value
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);
        return true;
    }

    private static string EncodeOccurrenceComponent(string value) =>
        value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static AttendeeResponse GetParticipation(IList<EventAttendee>? attendees)
    {
        var self = attendees?.FirstOrDefault(attendee => attendee.Self == true);
        return self?.ResponseStatus?.ToLowerInvariant() switch
        {
            "accepted" => AttendeeResponse.Accepted,
            "tentative" => AttendeeResponse.Tentative,
            "needsaction" => AttendeeResponse.NotResponded,
            "declined" => AttendeeResponse.Declined,
            _ => AttendeeResponse.Unknown,
        };
    }

    private static Uri? GetMeetingUri(Event source)
    {
        var entryPoints = source.ConferenceData?.EntryPoints;
        var video = entryPoints?.FirstOrDefault(entry =>
            entry.EntryPointType?.Equals("video", StringComparison.OrdinalIgnoreCase) == true
            && TryGetWebUri(entry.Uri) is not null);
        if (TryGetWebUri(video?.Uri) is { } videoUri)
        {
            return videoUri;
        }

        var anyEntryPoint = entryPoints?
            .Select(entry => TryGetWebUri(entry.Uri))
            .FirstOrDefault(uri => uri is not null);
        return anyEntryPoint ?? TryGetWebUri(source.HangoutLink);
    }

    private static Uri? TryGetWebUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri;
    }

    private static RgbColor? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return RgbColor.Parse(value.ToUpperInvariant());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool CanReadEvents(string? accessRole) => accessRole?.ToLowerInvariant() is
        "reader" or "writer" or "writerwithoutprivateaccess" or "owner";

    private static string FirstNonBlank(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"(?is)<\s*(?:br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"(?is)<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex NewlineRegex();
}

internal enum GoogleEventExclusionReason
{
    Cancelled,
    MissingIdentifier,
    InvalidTiming,
    InvalidOccurrenceKey,
}

internal readonly record struct GoogleEventMappingResult(
    CalendarEvent? Event,
    GoogleEventExclusionReason? ExclusionReason)
{
    public bool IsMapped => Event is not null;

    public static GoogleEventMappingResult Mapped(CalendarEvent calendarEvent) =>
        new(calendarEvent ?? throw new ArgumentNullException(nameof(calendarEvent)), null);

    public static GoogleEventMappingResult Excluded(GoogleEventExclusionReason reason) =>
        new(null, reason);
}
