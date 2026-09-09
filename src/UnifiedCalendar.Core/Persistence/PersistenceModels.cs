using System.Text;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.Core.Persistence;

public enum DisplayDensity
{
    Compact,
    Standard,
    Comfortable,
}

public sealed record DisplayPreferences
{
    public const int MinimumDays = 1;
    public const int MaximumDays = 90;
    public const int MinimumFontSizeDip = 10;
    public const int MaximumFontSizeDip = 24;

    public DisplayPreferences(
        int days = 7,
        int fontSizeDip = 14,
        DisplayDensity density = DisplayDensity.Standard,
        RgbColor? defaultEventColor = null)
    {
        if (days is < MinimumDays or > MaximumDays)
        {
            throw new ArgumentOutOfRangeException(nameof(days), "Display days must be between 1 and 90.");
        }

        if (fontSizeDip is < MinimumFontSizeDip or > MaximumFontSizeDip)
        {
            throw new ArgumentOutOfRangeException(nameof(fontSizeDip), "Font size must be between 10 and 24 DIP.");
        }

        if (!Enum.IsDefined(density))
        {
            throw new ArgumentOutOfRangeException(nameof(density), density, "The display density is not defined.");
        }

        Days = days;
        FontSizeDip = fontSizeDip;
        Density = density;
        DefaultEventColor = defaultEventColor ?? DisplaySettings.InitialDefaultEventColor;
    }

    public int Days { get; }

    public int FontSizeDip { get; }

    public DisplayDensity Density { get; }

    public RgbColor DefaultEventColor { get; }
}

public sealed record SyncPreferences
{
    private static readonly int[] AllowedIntervals = [1, 5, 10, 15, 30, 60];

    public SyncPreferences(int intervalMinutes = 5)
    {
        if (!AllowedIntervals.Contains(intervalMinutes))
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalMinutes),
                "The sync interval must be one of 1, 5, 10, 15, 30, or 60 minutes.");
        }

        IntervalMinutes = intervalMinutes;
    }

    public int IntervalMinutes { get; }
}

public sealed record GeneralPreferences(bool StartWithWindows = true);

public sealed record NotificationPreferences
{
    public const int MinimumLeadMinutes = 1;
    public const int MaximumLeadMinutes = 60;

    public NotificationPreferences(bool enabled = true, int leadMinutes = 5)
    {
        if (leadMinutes is < MinimumLeadMinutes or > MaximumLeadMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leadMinutes),
                "Notification lead minutes must be between 1 and 60.");
        }

        Enabled = enabled;
        LeadMinutes = leadMinutes;
    }

    public bool Enabled { get; }

    public int LeadMinutes { get; }
}

public sealed record WindowPlacement
{
    public WindowPlacement(
        double leftDip,
        double topDip,
        double widthDip,
        double heightDip,
        string monitorDeviceName,
        double? savedDpiX = null,
        double? savedDpiY = null)
    {
        if (!double.IsFinite(leftDip) || !double.IsFinite(topDip))
        {
            throw new ArgumentOutOfRangeException(nameof(leftDip), "Window coordinates must be finite.");
        }

        if (!double.IsFinite(widthDip) || widthDip <= 0
            || !double.IsFinite(heightDip) || heightDip <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthDip), "Window dimensions must be finite and positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(monitorDeviceName);
        if (savedDpiX.HasValue != savedDpiY.HasValue
            || (savedDpiX.HasValue
                && (!double.IsFinite(savedDpiX.Value) || savedDpiX.Value <= 0
                    || !double.IsFinite(savedDpiY!.Value) || savedDpiY.Value <= 0)))
        {
            throw new ArgumentOutOfRangeException(nameof(savedDpiX), "Saved DPI values must be absent together or finite and positive.");
        }

        LeftDip = leftDip;
        TopDip = topDip;
        WidthDip = widthDip;
        HeightDip = heightDip;
        MonitorDeviceName = monitorDeviceName;
        SavedDpiX = savedDpiX;
        SavedDpiY = savedDpiY;
    }

    public double LeftDip { get; }

    public double TopDip { get; }

    public double WidthDip { get; }

    public double HeightDip { get; }

    public string MonitorDeviceName { get; }

    public double? SavedDpiX { get; }

    public double? SavedDpiY { get; }
}

public sealed record WindowPreferences(
    WindowPlacement? Main = null,
    WindowPlacement? Settings = null);

public sealed record CalendarSetting
{
    public CalendarSetting(string calendarId, bool isVisible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        CalendarId = calendarId;
        IsVisible = isVisible;
    }

    public string CalendarId { get; }

    public bool IsVisible { get; }
}

public sealed class AccountSettings
{
    private readonly IReadOnlyList<CalendarSetting> _calendars;

    public AccountSettings(
        Guid internalAccountId,
        ProviderKind provider,
        string providerSubjectId,
        string displayName,
        string email,
        bool enabled,
        string tokenRef,
        IEnumerable<CalendarSetting> calendars)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The internal account ID cannot be empty.", nameof(internalAccountId));
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "The provider is not defined.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerSubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ValidateTokenReference(tokenRef, provider, internalAccountId);
        ArgumentNullException.ThrowIfNull(calendars);

        var calendarArray = calendars.ToArray();
        if (calendarArray.Any(calendar => calendar is null))
        {
            throw new ArgumentException("Calendar settings cannot contain null elements.", nameof(calendars));
        }

        if (calendarArray.Select(calendar => calendar.CalendarId).Distinct(StringComparer.Ordinal).Count()
            != calendarArray.Length)
        {
            throw new ArgumentException("Calendar IDs must be unique within an account.", nameof(calendars));
        }

        InternalAccountId = internalAccountId;
        Provider = provider;
        ProviderSubjectId = providerSubjectId;
        DisplayName = displayName;
        Email = email;
        Enabled = enabled;
        TokenRef = tokenRef;
        _calendars = Array.AsReadOnly(calendarArray);
    }

    public Guid InternalAccountId { get; }

    public ProviderKind Provider { get; }

    public string ProviderSubjectId { get; }

    public string DisplayName { get; }

    public string Email { get; }

    public bool Enabled { get; }

    public string TokenRef { get; }

    public IReadOnlyList<CalendarSetting> Calendars => _calendars;

    private static void ValidateTokenReference(string tokenRef, ProviderKind provider, Guid accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenRef);
        if (tokenRef.StartsWith("/", StringComparison.Ordinal)
            || tokenRef.Contains('\\')
            || tokenRef.Contains(':')
            || tokenRef.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("The token reference must be a relative provider/account reference.", nameof(tokenRef));
        }

        var parts = tokenRef.Split('/');
        var expectedProvider = provider.ToString().ToLowerInvariant();
        if (parts.Length != 2
            || !parts[0].Equals(expectedProvider, StringComparison.Ordinal)
            || !Guid.TryParse(parts[1], out var referencedAccountId)
            || referencedAccountId != accountId)
        {
            throw new ArgumentException("The token reference does not match the account provider and ID.", nameof(tokenRef));
        }
    }
}

public sealed class AppSettings
{
    private readonly IReadOnlyList<AccountSettings> _accounts;
    private readonly IReadOnlyList<ColorRule> _colorRules;

    public AppSettings(
        DisplayPreferences? display = null,
        SyncPreferences? sync = null,
        GeneralPreferences? general = null,
        WindowPreferences? windows = null,
        IEnumerable<AccountSettings>? accounts = null,
        IEnumerable<ColorRule>? colorRules = null,
        NotificationPreferences? notifications = null)
    {
        var accountArray = accounts?.ToArray() ?? [];
        var colorRuleArray = colorRules?.ToArray() ?? [];
        if (accountArray.Any(account => account is null))
        {
            throw new ArgumentException("Accounts cannot contain null elements.", nameof(accounts));
        }

        if (colorRuleArray.Any(rule => rule is null))
        {
            throw new ArgumentException("Color rules cannot contain null elements.", nameof(colorRules));
        }

        if (accountArray.Select(account => account.InternalAccountId).Distinct().Count() != accountArray.Length)
        {
            throw new ArgumentException("Internal account IDs must be unique.", nameof(accounts));
        }

        if (accountArray
            .Select(account => (account.Provider, account.ProviderSubjectId))
            .Distinct()
            .Count() != accountArray.Length)
        {
            throw new ArgumentException("Provider subject IDs must be unique within a provider.", nameof(accounts));
        }

        Display = display ?? new DisplayPreferences();
        Sync = sync ?? new SyncPreferences();
        General = general ?? new GeneralPreferences();
        Windows = windows ?? new WindowPreferences();
        _accounts = Array.AsReadOnly(accountArray);
        _colorRules = Array.AsReadOnly(colorRuleArray);
        Notifications = notifications ?? new NotificationPreferences();
    }

    public DisplayPreferences Display { get; }

    public SyncPreferences Sync { get; }

    public GeneralPreferences General { get; }

    public WindowPreferences Windows { get; }

    public IReadOnlyList<AccountSettings> Accounts => _accounts;

    public IReadOnlyList<ColorRule> ColorRules => _colorRules;

    public NotificationPreferences Notifications { get; }

    public static AppSettings CreateDefault() => new();
}

public sealed class CachedCalendar
{
    private readonly IReadOnlyList<CalendarEvent> _events;

    public CachedCalendar(
        string calendarId,
        string name,
        RgbColor? sourceColor,
        DateTimeOffset lastSuccessfulSyncUtc,
        IEnumerable<CalendarEvent> events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(events);
        var eventArray = events.ToArray();
        if (eventArray.Any(calendarEvent => calendarEvent is null))
        {
            throw new ArgumentException("Cached events cannot contain null elements.", nameof(events));
        }

        if (eventArray.Select(calendarEvent => calendarEvent.Key).Distinct().Count() != eventArray.Length)
        {
            throw new ArgumentException("Cached event keys must be unique.", nameof(events));
        }

        CalendarId = calendarId;
        Name = name;
        SourceColor = sourceColor;
        LastSuccessfulSyncUtc = lastSuccessfulSyncUtc.ToUniversalTime();
        _events = Array.AsReadOnly(eventArray);
    }

    public string CalendarId { get; }

    public string Name { get; }

    public RgbColor? SourceColor { get; }

    public DateTimeOffset LastSuccessfulSyncUtc { get; }

    public IReadOnlyList<CalendarEvent> Events => _events;
}

public sealed class AccountCache
{
    private readonly IReadOnlyList<CachedCalendar> _calendars;

    public AccountCache(
        Guid internalAccountId,
        ProviderKind provider,
        DateTimeOffset generatedAtUtc,
        IEnumerable<CachedCalendar> calendars)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The internal account ID cannot be empty.", nameof(internalAccountId));
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), provider, "The provider is not defined.");
        }

        ArgumentNullException.ThrowIfNull(calendars);
        var calendarArray = calendars.ToArray();
        if (calendarArray.Any(calendar => calendar is null))
        {
            throw new ArgumentException("Cached calendars cannot contain null elements.", nameof(calendars));
        }

        if (calendarArray.Select(calendar => calendar.CalendarId).Distinct(StringComparer.Ordinal).Count()
            != calendarArray.Length)
        {
            throw new ArgumentException("Cached calendar IDs must be unique.", nameof(calendars));
        }

        foreach (var calendar in calendarArray)
        {
            if (calendar.Events.Any(calendarEvent =>
                    calendarEvent.Key.InternalAccountId != internalAccountId
                    || calendarEvent.Key.Provider != provider
                    || !calendarEvent.Key.CalendarId.Equals(calendar.CalendarId, StringComparison.Ordinal)))
            {
                throw new ArgumentException("Cached event keys must match their account, provider, and calendar.", nameof(calendars));
            }
        }

        InternalAccountId = internalAccountId;
        Provider = provider;
        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();
        _calendars = Array.AsReadOnly(calendarArray);
    }

    public Guid InternalAccountId { get; }

    public ProviderKind Provider { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    public IReadOnlyList<CachedCalendar> Calendars => _calendars;
}

public static class TokenStoreTextExtensions
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Task WriteTextAsync(
        this ITokenStore store,
        ProviderKind provider,
        Guid internalAccountId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(payload);
        return store.WriteAsync(provider, internalAccountId, StrictUtf8.GetBytes(payload), cancellationToken);
    }

    public static async Task<string?> ReadTextAsync(
        this ITokenStore store,
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var payload = await store.ReadAsync(provider, internalAccountId, cancellationToken).ConfigureAwait(false);
        return payload is null ? null : StrictUtf8.GetString(payload);
    }
}
