using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;

namespace UnifiedCalendar.Infrastructure.Storage;

internal sealed class SettingsJsonDocument
{
    public required int SchemaVersion { get; set; }

    public required DisplayJsonDocument Display { get; set; }

    public required SyncJsonDocument Sync { get; set; }

    public required GeneralJsonDocument General { get; set; }

    public required NotificationsJsonDocument Notifications { get; set; }

    public required WindowsJsonDocument Windows { get; set; }

    public required List<AccountJsonDocument> Accounts { get; set; }

    public required List<ColorRuleJsonDocument> ColorRules { get; set; }

    public AppSettings ToDomain()
    {
        if (SchemaVersion != SettingsJsonStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The settings schema version is not current.");
        }

        if (Display is null || Sync is null || General is null || Notifications is null || Windows is null
            || Accounts is null || ColorRules is null)
        {
            throw new InvalidDataException("The settings document is missing required sections.");
        }

        return new AppSettings(
            Display.ToDomain(),
            Sync.ToDomain(),
            General.ToDomain(),
            Windows.ToDomain(),
            Accounts.Select(account => account.ToDomain()),
            ColorRules.Select(rule => rule.ToDomain()),
            Notifications.ToDomain());
    }

    public static SettingsJsonDocument FromDomain(AppSettings settings) => new()
    {
        SchemaVersion = SettingsJsonStore.CurrentSchemaVersion,
        Display = DisplayJsonDocument.FromDomain(settings.Display),
        Sync = SyncJsonDocument.FromDomain(settings.Sync),
        General = GeneralJsonDocument.FromDomain(settings.General),
        Notifications = NotificationsJsonDocument.FromDomain(settings.Notifications),
        Windows = WindowsJsonDocument.FromDomain(settings.Windows),
        Accounts = settings.Accounts.Select(AccountJsonDocument.FromDomain).ToList(),
        ColorRules = settings.ColorRules.Select(ColorRuleJsonDocument.FromDomain).ToList(),
    };
}

internal sealed class DisplayJsonDocument
{
    public required int Days { get; set; }

    public required int FontSizeDip { get; set; }

    public required DisplayDensity Density { get; set; }

    public required string DefaultEventColor { get; set; }

    public DisplayPreferences ToDomain() => new(
        Days,
        FontSizeDip,
        Density,
        RgbColor.Parse(DefaultEventColor));

    public static DisplayJsonDocument FromDomain(DisplayPreferences value) => new()
    {
        Days = value.Days,
        FontSizeDip = value.FontSizeDip,
        Density = value.Density,
        DefaultEventColor = value.DefaultEventColor.ToHexString(),
    };
}

internal sealed class SyncJsonDocument
{
    public required int IntervalMinutes { get; set; }

    public SyncPreferences ToDomain() => new(IntervalMinutes);

    public static SyncJsonDocument FromDomain(SyncPreferences value) => new()
    {
        IntervalMinutes = value.IntervalMinutes,
    };
}

internal sealed class GeneralJsonDocument
{
    public required bool StartWithWindows { get; set; }

    public GeneralPreferences ToDomain() => new(StartWithWindows);

    public static GeneralJsonDocument FromDomain(GeneralPreferences value) => new()
    {
        StartWithWindows = value.StartWithWindows,
    };
}

internal sealed class NotificationsJsonDocument
{
    public required bool Enabled { get; set; }

    public required int LeadMinutes { get; set; }

    public NotificationPreferences ToDomain() => new(Enabled, LeadMinutes);

    public static NotificationsJsonDocument FromDomain(NotificationPreferences value) => new()
    {
        Enabled = value.Enabled,
        LeadMinutes = value.LeadMinutes,
    };
}

internal sealed class WindowsJsonDocument
{
    public required WindowPlacementJsonDocument? Main { get; set; }

    public required WindowPlacementJsonDocument? Settings { get; set; }

    public WindowPreferences ToDomain() => new(Main?.ToDomain(), Settings?.ToDomain());

    public static WindowsJsonDocument FromDomain(WindowPreferences value) => new()
    {
        Main = value.Main is null ? null : WindowPlacementJsonDocument.FromDomain(value.Main),
        Settings = value.Settings is null ? null : WindowPlacementJsonDocument.FromDomain(value.Settings),
    };
}

internal sealed class WindowPlacementJsonDocument
{
    public required double LeftDip { get; set; }

    public required double TopDip { get; set; }

    public required double WidthDip { get; set; }

    public required double HeightDip { get; set; }

    public required string MonitorDeviceName { get; set; }

    public required double? SavedDpiX { get; set; }

    public required double? SavedDpiY { get; set; }

    public WindowPlacement ToDomain() => new(
        LeftDip,
        TopDip,
        WidthDip,
        HeightDip,
        MonitorDeviceName,
        SavedDpiX,
        SavedDpiY);

    public static WindowPlacementJsonDocument FromDomain(WindowPlacement value) => new()
    {
        LeftDip = value.LeftDip,
        TopDip = value.TopDip,
        WidthDip = value.WidthDip,
        HeightDip = value.HeightDip,
        MonitorDeviceName = value.MonitorDeviceName,
        SavedDpiX = value.SavedDpiX,
        SavedDpiY = value.SavedDpiY,
    };
}

internal sealed class AccountJsonDocument
{
    public required Guid InternalAccountId { get; set; }

    public required ProviderKind Provider { get; set; }

    public required string ProviderSubjectId { get; set; }

    public required string DisplayName { get; set; }

    public required string Email { get; set; }

    public required bool Enabled { get; set; }

    public required string TokenRef { get; set; }

    public required List<CalendarSettingJsonDocument> Calendars { get; set; }

    public AccountSettings ToDomain() => new(
        InternalAccountId,
        Provider,
        ProviderSubjectId,
        DisplayName,
        Email,
        Enabled,
        TokenRef,
        (Calendars ?? throw new InvalidDataException("The account calendars are required."))
            .Select(calendar => calendar?.ToDomain()
                ?? throw new InvalidDataException("A calendar entry cannot be null.")));

    public static AccountJsonDocument FromDomain(AccountSettings value) => new()
    {
        InternalAccountId = value.InternalAccountId,
        Provider = value.Provider,
        ProviderSubjectId = value.ProviderSubjectId,
        DisplayName = value.DisplayName,
        Email = value.Email,
        Enabled = value.Enabled,
        TokenRef = value.TokenRef,
        Calendars = value.Calendars.Select(CalendarSettingJsonDocument.FromDomain).ToList(),
    };
}

internal sealed class CalendarSettingJsonDocument
{
    public required string CalendarId { get; set; }

    public required bool IsVisible { get; set; }

    public CalendarSetting ToDomain() => new(CalendarId, IsVisible);

    public static CalendarSettingJsonDocument FromDomain(CalendarSetting value) => new()
    {
        CalendarId = value.CalendarId,
        IsVisible = value.IsVisible,
    };
}

internal sealed class ColorRuleJsonDocument
{
    public required string Name { get; set; }

    public required bool IsEnabled { get; set; }

    public required ColorRuleOperator Operator { get; set; }

    public required List<ColorRuleConditionJsonDocument> Conditions { get; set; }

    public required string Color { get; set; }

    public ColorRule ToDomain() => new(
        Name,
        IsEnabled,
        Operator,
        (Conditions ?? throw new InvalidDataException("Color-rule conditions are required."))
            .Select(condition => condition?.ToDomain()
                ?? throw new InvalidDataException("A color-rule condition cannot be null.")),
        RgbColor.Parse(Color));

    public static ColorRuleJsonDocument FromDomain(ColorRule value) => new()
    {
        Name = value.Name,
        IsEnabled = value.IsEnabled,
        Operator = value.Operator,
        Conditions = value.Conditions.Select(ColorRuleConditionJsonDocument.FromDomain).ToList(),
        Color = value.Color.ToHexString(),
    };
}

internal sealed class ColorRuleConditionJsonDocument
{
    public required ColorRuleField Field { get; set; }

    public required TextMatchKind MatchKind { get; set; }

    public required string? ComparisonValue { get; set; }

    public required ProviderKind? Provider { get; set; }

    public ColorRuleCondition ToDomain() => Field switch
    {
        ColorRuleField.Title when Provider is null && ComparisonValue is not null =>
            ColorRuleCondition.ForTitle(MatchKind, ComparisonValue),
        ColorRuleField.CalendarName when Provider is null && ComparisonValue is not null =>
            ColorRuleCondition.ForCalendarName(MatchKind, ComparisonValue),
        ColorRuleField.Provider when Provider.HasValue && ComparisonValue is null && MatchKind == TextMatchKind.Exact =>
            ColorRuleCondition.ForProvider(Provider.Value),
        _ => throw new InvalidDataException("The color-rule condition is inconsistent with its field."),
    };

    public static ColorRuleConditionJsonDocument FromDomain(ColorRuleCondition value) => new()
    {
        Field = value.Field,
        MatchKind = value.MatchKind,
        ComparisonValue = value.ComparisonValue,
        Provider = value.Provider,
    };
}
