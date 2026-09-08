using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Graph.Models;
using UnifiedCalendar.Core.Models;
using GraphCalendar = Microsoft.Graph.Models.Calendar;
using GraphEvent = Microsoft.Graph.Models.Event;
using RgbColor = UnifiedCalendar.Core.Models.RgbColor;

namespace UnifiedCalendar.Providers.Microsoft;

internal static partial class MicrosoftModelMapper
{
    public static CalendarDescriptor? ToCalendarDescriptor(GraphCalendar? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Id))
        {
            return null;
        }

        return new CalendarDescriptor(
            source.Id,
            source.Id,
            string.IsNullOrWhiteSpace(source.Name) ? "Untitled calendar" : source.Name,
            source.IsDefaultCalendar == true,
            CanReadEvents(source),
            ParseColor(source.HexColor) ?? MicrosoftColorPalette.FromCalendarColor(source.Color?.ToString()));
    }

    public static IReadOnlyDictionary<string, RgbColor> ToCategoryColorMap(
        IEnumerable<OutlookCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var result = new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in categories)
        {
            if (string.IsNullOrWhiteSpace(category.DisplayName)
                || MicrosoftColorPalette.FromCategoryColor(category.Color?.ToString()) is not { } color)
            {
                continue;
            }

            result.TryAdd(category.DisplayName, color);
        }

        return result;
    }

    public static MicrosoftEventMappingResult ToCalendarEvent(
        GraphEvent? source,
        CalendarAccount account,
        CalendarDescriptor calendar,
        IReadOnlyDictionary<string, RgbColor> categoryColors)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(categoryColors);

        if (source?.IsCancelled == true)
        {
            return MicrosoftEventMappingResult.Excluded(MicrosoftEventExclusionReason.Cancelled);
        }

        if (source is null || string.IsNullOrWhiteSpace(source.Id))
        {
            return MicrosoftEventMappingResult.Excluded(MicrosoftEventExclusionReason.MissingIdentifier);
        }

        if (!TryGetTiming(source, out var timing, out var sourceTimeZoneId))
        {
            return MicrosoftEventMappingResult.Excluded(MicrosoftEventExclusionReason.InvalidTiming);
        }

        if (!TryGetEventIdentity(source, out var sourceEventId, out var occurrenceKey))
        {
            return MicrosoftEventMappingResult.Excluded(MicrosoftEventExclusionReason.InvalidOccurrenceKey);
        }

        var sourceEventColor = source.Categories?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => categoryColors.TryGetValue(name, out var color) ? color : (RgbColor?)null)
            .FirstOrDefault(color => color.HasValue);

        return MicrosoftEventMappingResult.Mapped(new CalendarEvent(
            new EventKey(
                ProviderKind.Microsoft,
                account.InternalAccountId,
                calendar.CalendarId,
                sourceEventId,
                occurrenceKey),
            source.Subject ?? string.Empty,
            timing,
            calendar.Name,
            ToPlainText(source.Body),
            NullIfWhiteSpace(source.Location?.DisplayName),
            sourceTimeZoneId,
            GetParticipation(source.ResponseStatus?.Response?.ToString()),
            sourceEventColor,
            calendar.SourceColor,
            TryGetWebUri(source.OnlineMeeting?.JoinUrl) ?? TryGetWebUri(source.OnlineMeetingUrl),
            TryGetWebUri(source.WebLink),
            isCancelled: false));
    }

    internal static string? ToPlainText(ItemBody? body)
    {
        if (string.IsNullOrWhiteSpace(body?.Content))
        {
            return null;
        }

        if (!string.Equals(body.ContentType?.ToString(), "html", StringComparison.OrdinalIgnoreCase))
        {
            return body.Content;
        }

        var withBreaks = BlockBreakRegex().Replace(body.Content, "\n");
        var withoutTags = HtmlTagRegex().Replace(withBreaks, string.Empty);
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace("\u00a0", " ", StringComparison.Ordinal);
        var normalized = NewlineRegex().Replace(
            decoded.Replace("\r\n", "\n", StringComparison.Ordinal),
            "\n").Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool TryGetTiming(
        GraphEvent source,
        out EventTiming timing,
        out string? sourceTimeZoneId)
    {
        timing = null!;
        sourceTimeZoneId = NullIfWhiteSpace(source.Start?.TimeZone)
            ?? NullIfWhiteSpace(source.End?.TimeZone);
        if (source.Start is null || source.End is null)
        {
            return false;
        }

        if (source.IsAllDay == true)
        {
            if (!TryGetDate(source.Start.DateTime, out var startDate)
                || !TryGetDate(source.End.DateTime, out var endDate)
                || endDate <= startDate)
            {
                return false;
            }

            timing = new AllDayEventTiming(startDate, endDate);
            return true;
        }

        if (!TryGetUtc(source.Start, out var startUtc)
            || !TryGetUtc(source.End, out var endUtc)
            || endUtc < startUtc)
        {
            return false;
        }

        timing = new TimedEventTiming(startUtc, endUtc);
        return true;
    }

    private static bool TryGetEventIdentity(
        GraphEvent source,
        out string sourceEventId,
        out string occurrenceKey)
    {
        sourceEventId = source.Id!;
        occurrenceKey = string.Empty;
        var eventType = source.Type?.ToString();
        var isOccurrence = eventType?.Equals("occurrence", StringComparison.OrdinalIgnoreCase) == true
            || eventType?.Equals("exception", StringComparison.OrdinalIgnoreCase) == true
            || !string.IsNullOrWhiteSpace(source.SeriesMasterId);
        if (!isOccurrence)
        {
            return true;
        }

        var seriesIdentity = NullIfWhiteSpace(source.SeriesMasterId) ?? NullIfWhiteSpace(source.ICalUId);
        if (seriesIdentity is null
            || !source.OriginalStart.HasValue)
        {
            return false;
        }

        sourceEventId = seriesIdentity;
        var originalStartUtc = source.OriginalStart.Value.ToUniversalTime();
        var normalized = originalStartUtc.ToString("O", CultureInfo.InvariantCulture);
        occurrenceKey = EncodeOccurrenceComponent(seriesIdentity)
            + EncodeOccurrenceComponent(normalized);
        return true;
    }

    private static AttendeeResponse GetParticipation(string? response) => response?.ToLowerInvariant() switch
    {
        "accepted" => AttendeeResponse.Accepted,
        "tentativelyaccepted" or "tentative" => AttendeeResponse.Tentative,
        "notresponded" or "none" => AttendeeResponse.NotResponded,
        "declined" => AttendeeResponse.Declined,
        _ => AttendeeResponse.Unknown,
    };

    private static bool TryGetDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length < 10)
        {
            return false;
        }

        return DateOnly.TryParseExact(
            value.AsSpan(0, 10),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static bool TryGetUtc(DateTimeTimeZone value, out DateTimeOffset utc) =>
        TryParseUtc(value.DateTime, value.TimeZone, out utc);

    private static bool TryParseUtc(string? value, string? timeZoneId, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var offsetValue)
            && HasOffset(value))
        {
            utc = offsetValue.ToUniversalTime();
            return true;
        }

        if (!DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var localValue))
        {
            return false;
        }

        localValue = DateTime.SpecifyKind(localValue, DateTimeKind.Unspecified);
        if (string.IsNullOrWhiteSpace(timeZoneId)
            || timeZoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            utc = new DateTimeOffset(localValue, TimeSpan.Zero);
            return true;
        }

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            if (zone.IsInvalidTime(localValue))
            {
                return false;
            }

            utc = TimeZoneInfo.ConvertTimeToUtc(localValue, zone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static bool HasOffset(string value)
    {
        var separator = value.IndexOf('T');
        return value.EndsWith('Z')
            || (separator >= 0 && (value.IndexOf('+', separator) >= 0 || value.IndexOf('-', separator) >= 0));
    }

    private static bool CanReadEvents(GraphCalendar source)
    {
        if (source.AdditionalData.TryGetValue("canReadEvents", out var explicitValue)
            && (explicitValue is bool
                || explicitValue is JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False }))
        {
            return explicitValue is bool canRead
                ? canRead
                : ((JsonElement)explicitValue).GetBoolean();
        }

        // Graph has no general can-read flag on calendar. Any calendar returned by these
        // read-only collections is retained as readable unless a service extension says otherwise.
        return true;
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
        try
        {
            return string.IsNullOrWhiteSpace(value) ? null : RgbColor.Parse(value.ToUpperInvariant());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string EncodeOccurrenceComponent(string value) =>
        value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"(?is)<\s*(?:br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"(?is)<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex NewlineRegex();
}

internal enum MicrosoftEventExclusionReason
{
    Cancelled,
    MissingIdentifier,
    InvalidTiming,
    InvalidOccurrenceKey,
}

internal readonly record struct MicrosoftEventMappingResult(
    CalendarEvent? Event,
    MicrosoftEventExclusionReason? ExclusionReason)
{
    public bool IsMapped => Event is not null;

    public static MicrosoftEventMappingResult Mapped(CalendarEvent calendarEvent) =>
        new(calendarEvent ?? throw new ArgumentNullException(nameof(calendarEvent)), null);

    public static MicrosoftEventMappingResult Excluded(MicrosoftEventExclusionReason reason) =>
        new(null, reason);
}
