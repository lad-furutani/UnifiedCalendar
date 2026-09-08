using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Core.Presentation;

public sealed class CalendarPresentationService
{
    private const double DefaultProgressLightnessDelta = 0.08d;
    private static readonly RgbColor DarkForeground = RgbColor.Parse("#111111");
    private static readonly RgbColor LightForeground = RgbColor.Parse("#FFFFFF");
    private readonly TimeProvider _timeProvider;
    private readonly double _progressLightnessDelta;

    public CalendarPresentationService(TimeProvider timeProvider)
        : this(timeProvider, DefaultProgressLightnessDelta)
    {
    }

    public CalendarPresentationService(TimeProvider timeProvider, double progressLightnessDelta)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!double.IsFinite(progressLightnessDelta)
            || progressLightnessDelta is <= 0d or >= 0.5d)
        {
            throw new ArgumentOutOfRangeException(nameof(progressLightnessDelta));
        }

        _timeProvider = timeProvider;
        _progressLightnessDelta = progressLightnessDelta;
    }

    public PresentationSnapshot BuildSnapshot(
        SyncSnapshot source,
        DisplaySettings settings,
        IReadOnlyList<ColorRule> colorRules,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(colorRules);
        ArgumentNullException.ThrowIfNull(localTimeZone);

        var colorRuleSnapshot = colorRules.ToArray();
        if (colorRuleSnapshot.Any(rule => rule is null))
        {
            throw new ArgumentException("Color rules cannot contain null elements.", nameof(colorRules));
        }

        var nowUtc = _timeProvider.GetUtcNow().ToUniversalTime();
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, localTimeZone);
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var displayEndDateExclusive = today.AddDays(settings.DisplayDays);
        var enabledAccounts = source.Accounts
            .Where(account => account.Enabled)
            .Select(account => account.InternalAccountId)
            .ToHashSet();
        var visibleCalendars = source.CalendarSelections
            .Where(selection => selection.IsVisible)
            .Select(selection => (selection.InternalAccountId, selection.CalendarId))
            .ToHashSet();

        var presentedEvents = source.Events
            .Where(calendarEvent => !calendarEvent.IsCancelled)
            .Where(calendarEvent => enabledAccounts.Contains(calendarEvent.Key.InternalAccountId))
            .Where(calendarEvent => visibleCalendars.Contains(
                (calendarEvent.Key.InternalAccountId, calendarEvent.Key.CalendarId)))
            .Select(calendarEvent => Project(
                calendarEvent,
                settings,
                colorRuleSnapshot,
                nowUtc,
                today,
                displayEndDateExclusive,
                localTimeZone,
                _progressLightnessDelta))
            .Where(presentedEvent => presentedEvent is not null)
            .Cast<PresentedEvent>()
            .ToArray();

        var days = presentedEvents
            .GroupBy(presentedEvent => presentedEvent.GroupDate)
            .OrderBy(group => group.Key)
            .Select(group => new PresentedDay(
                group.Key,
                CreateDayHeader(group.Key, today),
                group.OrderBy(item => item, PresentedEventComparer.Instance)))
            .ToArray();

        return new PresentationSnapshot(nowUtc, today, days);
    }

    private static PresentedEvent? Project(
        CalendarEvent calendarEvent,
        DisplaySettings settings,
        IReadOnlyList<ColorRule> colorRules,
        DateTimeOffset nowUtc,
        DateOnly today,
        DateOnly displayEndDateExclusive,
        TimeZoneInfo localTimeZone,
        double progressLightnessDelta)
    {
        DateOnly groupDate;
        DateTimeOffset? localStart;
        UiText listTime;
        UiText detailDateTime;
        bool isAllDay;
        bool isMultiDay;
        double? progress;

        switch (calendarEvent.Timing)
        {
            case TimedEventTiming timed:
                {
                    if (timed.IsZeroDuration ? nowUtc >= timed.StartUtc : nowUtc >= timed.EndUtc)
                    {
                        return null;
                    }

                    var startLocal = TimeZoneInfo.ConvertTime(timed.StartUtc, localTimeZone);
                    var endLocal = TimeZoneInfo.ConvertTime(timed.EndUtc, localTimeZone);
                    var startDate = DateOnly.FromDateTime(startLocal.DateTime);

                    if (startDate < today)
                    {
                        if (timed.IsZeroDuration || nowUtc < timed.StartUtc || nowUtc >= timed.EndUtc)
                        {
                            return null;
                        }

                        groupDate = today;
                    }
                    else
                    {
                        groupDate = startDate;
                    }

                    if (groupDate >= displayEndDateExclusive)
                    {
                        return null;
                    }

                    localStart = startLocal;
                    listTime = CreateTimedListTime(timed, startLocal, endLocal);
                    detailDateTime = CreateTimedDetailDateTime(startLocal, endLocal);
                    isAllDay = false;
                    isMultiDay = startDate != DateOnly.FromDateTime(endLocal.DateTime);
                    progress = CalculateProgress(timed, nowUtc);
                    break;
                }

            case AllDayEventTiming allDay:
                {
                    if (today >= allDay.EndDateExclusive)
                    {
                        return null;
                    }

                    groupDate = allDay.StartDate < today ? today : allDay.StartDate;
                    if (groupDate >= displayEndDateExclusive)
                    {
                        return null;
                    }

                    localStart = null;
                    listTime = allDay.IsMultiDay
                        ? UiText.FromResource(
                            UiTextResourceKeys.EventListAllDayRange,
                            allDay.StartDate,
                            allDay.DisplayEndDate)
                        : UiText.FromResource(UiTextResourceKeys.EventListAllDay);
                    detailDateTime = allDay.IsMultiDay
                        ? UiText.FromResource(
                            UiTextResourceKeys.EventDetailAllDayRange,
                            allDay.StartDate,
                            allDay.DisplayEndDate)
                        : UiText.FromResource(
                            UiTextResourceKeys.EventDetailAllDay,
                            allDay.StartDate);
                    isAllDay = true;
                    isMultiDay = allDay.IsMultiDay;
                    progress = null;
                    break;
                }

            default:
                throw new InvalidOperationException($"Unsupported timing type: {calendarEvent.Timing.GetType().Name}");
        }

        var title = CreateTitle(calendarEvent.Title);
        var attentionResponse = GetAttentionResponse(calendarEvent.ResponseStatus);
        var attentionResponseText = attentionResponse is null
            ? null
            : UiText.FromResource(
                UiTextResourceKeys.AttendeeStatus,
                CreateResponseText(attentionResponse.Value));
        var detailResponseText = calendarEvent.ResponseStatus == AttendeeResponse.Unknown
            ? null
            : CreateResponseText(calendarEvent.ResponseStatus);
        var background = ResolveBackground(calendarEvent, colorRules, settings.DefaultEventColor);
        var foreground = SelectForeground(background);
        var elapsed = background.AdjustLightness(-progressLightnessDelta);
        var remaining = background.AdjustLightness(progressLightnessDelta);
        var tooltip = attentionResponseText is null
            ? UiText.FromResource(UiTextResourceKeys.EventTooltip, title, detailDateTime)
            : UiText.FromResource(
                UiTextResourceKeys.EventTooltipWithAttention,
                title,
                detailDateTime,
                attentionResponseText);

        return new PresentedEvent(
            calendarEvent.Key,
            calendarEvent.Key.ToStableId(),
            calendarEvent,
            groupDate,
            title,
            listTime,
            tooltip,
            detailDateTime,
            localStart,
            isAllDay,
            isMultiDay,
            progress,
            attentionResponse,
            attentionResponseText,
            detailResponseText,
            background,
            foreground,
            elapsed,
            remaining);
    }

    private static double? CalculateProgress(TimedEventTiming timing, DateTimeOffset nowUtc)
    {
        if (timing.IsZeroDuration || nowUtc < timing.StartUtc || nowUtc >= timing.EndUtc)
        {
            return null;
        }

        var elapsed = (nowUtc - timing.StartUtc).TotalMilliseconds;
        var duration = (timing.EndUtc - timing.StartUtc).TotalMilliseconds;
        return Math.Clamp(elapsed / duration, 0d, 1d);
    }

    private static RgbColor ResolveBackground(
        CalendarEvent calendarEvent,
        IReadOnlyList<ColorRule> colorRules,
        RgbColor defaultColor)
    {
        foreach (var rule in colorRules)
        {
            if (rule.IsEnabled && rule.IsMatch(calendarEvent))
            {
                return rule.Color;
            }
        }

        return calendarEvent.SourceEventColor
            ?? calendarEvent.SourceCalendarColor
            ?? defaultColor;
    }

    private static RgbColor SelectForeground(RgbColor background) =>
        background.GetContrastRatio(DarkForeground) >= background.GetContrastRatio(LightForeground)
            ? DarkForeground
            : LightForeground;

    private static UiText CreateTitle(string title) => string.IsNullOrWhiteSpace(title)
        ? UiText.FromResource(UiTextResourceKeys.EventUntitled)
        : UiText.FromLiteral(title);

    private static UiText CreateDayHeader(DateOnly date, DateOnly today)
    {
        if (date == today)
        {
            return UiText.FromResource(UiTextResourceKeys.DayHeaderToday, date);
        }

        if (date == today.AddDays(1))
        {
            return UiText.FromResource(UiTextResourceKeys.DayHeaderTomorrow, date);
        }

        return UiText.FromResource(UiTextResourceKeys.DayHeaderDate, date);
    }

    private static UiText CreateTimedListTime(
        TimedEventTiming timing,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var startTime = TimeOnly.FromDateTime(start.DateTime);
        return timing.IsZeroDuration
            ? UiText.FromResource(UiTextResourceKeys.EventListInstant, startTime)
            : UiText.FromResource(
                UiTextResourceKeys.EventListTimedRange,
                startTime,
                TimeOnly.FromDateTime(end.DateTime));
    }

    private static UiText CreateTimedDetailDateTime(DateTimeOffset start, DateTimeOffset end)
    {
        var startDate = DateOnly.FromDateTime(start.DateTime);
        var endDate = DateOnly.FromDateTime(end.DateTime);
        var startTime = TimeOnly.FromDateTime(start.DateTime);
        var endTime = TimeOnly.FromDateTime(end.DateTime);
        return startDate == endDate
            ? UiText.FromResource(
                UiTextResourceKeys.EventDetailTimedSingleDay,
                startDate,
                startTime,
                endTime)
            : UiText.FromResource(
                UiTextResourceKeys.EventDetailTimedRange,
                startDate,
                startTime,
                endDate,
                endTime);
    }

    private static AttendeeResponse? GetAttentionResponse(AttendeeResponse response) => response switch
    {
        AttendeeResponse.Accepted => null,
        AttendeeResponse.Tentative => response,
        AttendeeResponse.NotResponded => response,
        AttendeeResponse.Declined => response,
        AttendeeResponse.Unknown => response,
        _ => throw new ArgumentOutOfRangeException(nameof(response)),
    };

    private static UiText CreateResponseText(AttendeeResponse response) => UiText.FromResource(response switch
    {
        AttendeeResponse.Accepted => UiTextResourceKeys.AttendeeAccepted,
        AttendeeResponse.Tentative => UiTextResourceKeys.AttendeeTentative,
        AttendeeResponse.NotResponded => UiTextResourceKeys.AttendeeNotResponded,
        AttendeeResponse.Declined => UiTextResourceKeys.AttendeeDeclined,
        AttendeeResponse.Unknown => UiTextResourceKeys.AttendeeUnknown,
        _ => throw new ArgumentOutOfRangeException(nameof(response)),
    });

    private sealed class PresentedEventComparer : IComparer<PresentedEvent>
    {
        public static PresentedEventComparer Instance { get; } = new();

        public int Compare(PresentedEvent? x, PresentedEvent? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var allDayComparison = y.IsAllDay.CompareTo(x.IsAllDay);
            if (allDayComparison != 0)
            {
                return allDayComparison;
            }

            if (!x.IsAllDay)
            {
                var startComparison = Nullable.Compare(x.LocalStart, y.LocalStart);
                if (startComparison != 0)
                {
                    return startComparison;
                }
            }

            var titleComparison = StringComparer.CurrentCultureIgnoreCase.Compare(
                x.Source.Title,
                y.Source.Title);
            return titleComparison != 0
                ? titleComparison
                : StringComparer.Ordinal.Compare(x.StableId, y.StableId);
        }
    }
}
