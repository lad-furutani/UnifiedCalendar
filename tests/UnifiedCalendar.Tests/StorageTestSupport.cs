using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Infrastructure.Storage;

namespace UnifiedCalendar.Tests;

internal sealed class TemporaryAppDirectory : IDisposable
{
    public TemporaryAppDirectory()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "UnifiedCalendar.Tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(RootPath);
        Paths.EnsureDirectories();
    }

    public string RootPath { get; }

    public AppPaths Paths { get; }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}

internal static class StorageSamples
{
    public static readonly Guid GoogleAccountId =
        Guid.Parse("168fc29f-5d6f-4d32-a7a8-792392de0a9d");

    public static readonly Guid MicrosoftAccountId =
        Guid.Parse("eb03834c-e30c-4fc7-8ec9-0754a5b1f3bf");

    public static readonly DateTimeOffset Now =
        new(2026, 8, 28, 5, 12, 34, TimeSpan.Zero);

    public static AppSettings CreateSettings() => new(
        new DisplayPreferences(14, 18, DisplayDensity.Compact, RgbColor.Parse("#123ABC")),
        new SyncPreferences(30),
        new GeneralPreferences(false),
        new WindowPreferences(
            new WindowPlacement(-200, 45, 800, 600, @"\\.\DISPLAY1", 144, 144),
            new WindowPlacement(25, 75, 640, 480, @"\\.\DISPLAY2")),
        [
            new AccountSettings(
                GoogleAccountId,
                ProviderKind.Google,
                "google-subject",
                "Example User",
                "user@example.invalid",
                true,
                $"google/{GoogleAccountId:N}",
                [new CalendarSetting("primary", true), new CalendarSetting("team", false)]),
        ],
        [
            new ColorRule(
                "Google meetings",
                true,
                ColorRuleOperator.All,
                [
                    ColorRuleCondition.ForProvider(ProviderKind.Google),
                    ColorRuleCondition.ForTitle(TextMatchKind.Contains, "meeting"),
                ],
                RgbColor.Parse("#A1B2C3")),
        ],
        new NotificationPreferences(enabled: false, leadMinutes: 45));

    public static AccountCache CreateGoogleCache(Guid? accountId = null)
    {
        var id = accountId ?? GoogleAccountId;
        var timed = new CalendarEvent(
            new EventKey(ProviderKind.Google, id, "primary", "event-1", "occurrence-1"),
            "Planning meeting",
            new TimedEventTiming(Now, Now.AddHours(1)),
            "Primary",
            "Plain description",
            "Tokyo",
            "Asia/Tokyo",
            AttendeeResponse.Accepted,
            RgbColor.Parse("#112233"),
            RgbColor.Parse("#445566"),
            new Uri("https://meet.example.invalid/meeting"),
            new Uri("https://calendar.example.invalid/event/1"));
        var allDay = new CalendarEvent(
            new EventKey(ProviderKind.Google, id, "primary", "event-2"),
            "Holiday",
            new AllDayEventTiming(new DateOnly(2026, 8, 29), new DateOnly(2026, 8, 31)),
            "Primary",
            responseStatus: AttendeeResponse.NotResponded);

        return new AccountCache(
            id,
            ProviderKind.Google,
            Now.AddMinutes(1),
            [
                new CachedCalendar(
                    "primary",
                    "Primary",
                    RgbColor.Parse("#778899"),
                    Now,
                    [timed, allDay]),
            ]);
    }
}
