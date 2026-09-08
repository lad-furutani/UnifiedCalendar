using UnifiedCalendar.Core.Models;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class CoreModelTests
{
    private static readonly Guid AccountId = Guid.Parse("4f5c48b1-f23a-4521-9fa5-2d3b41f8ccf7");

    [Fact]
    public void EventKey_StableIdIsDeterministicUrlSafeAndLengthPrefixed()
    {
        var first = new EventKey(ProviderKind.Google, AccountId, "ab", "c", "occurrence");
        var same = new EventKey(ProviderKind.Google, AccountId, "ab", "c", "occurrence");
        var ambiguousWithoutLengths = new EventKey(
            ProviderKind.Google,
            AccountId,
            "a",
            "bc",
            "occurrence");

        Assert.Equal(first.ToStableId(), same.ToStableId());
        Assert.NotEqual(first.ToStableId(), ambiguousWithoutLengths.ToStableId());
        Assert.DoesNotContain("+", first.ToStableId(), StringComparison.Ordinal);
        Assert.DoesNotContain("/", first.ToStableId(), StringComparison.Ordinal);
        Assert.DoesNotContain("=", first.ToStableId(), StringComparison.Ordinal);
    }

    [Fact]
    public void EventKey_RejectsInvalidIdentityAndProvider()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "provider",
            () => new EventKey((ProviderKind)99, AccountId, "calendar", "event"));
        AssertParamName<ArgumentException>(
            "internalAccountId",
            () => new EventKey(ProviderKind.Google, Guid.Empty, "calendar", "event"));
        AssertParamName<ArgumentException>(
            "calendarId",
            () => new EventKey(ProviderKind.Google, AccountId, " ", "event"));
        AssertParamName<ArgumentException>(
            "sourceEventId",
            () => new EventKey(ProviderKind.Google, AccountId, "calendar", string.Empty));
        Assert.Throws<InvalidOperationException>(() => default(EventKey).ToStableId());
    }

    [Fact]
    public void TimedEventTiming_NormalizesToUtcAndRejectsNegativeDuration()
    {
        var timing = new TimedEventTiming(
            new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.FromHours(9)),
            new DateTimeOffset(2026, 8, 27, 11, 0, 0, TimeSpan.FromHours(9)));

        Assert.Equal(TimeSpan.Zero, timing.StartUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 1, 0, 0, TimeSpan.Zero), timing.StartUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimedEventTiming(
            new DateTimeOffset(2026, 8, 27, 2, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 27, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void AllDayEventTiming_UsesExclusiveEndDate()
    {
        var timing = new AllDayEventTiming(
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 30));

        Assert.True(timing.IsMultiDay);
        Assert.Equal(new DateOnly(2026, 8, 29), timing.DisplayEndDate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AllDayEventTiming(
            new DateOnly(2026, 8, 27),
            new DateOnly(2026, 8, 27)));
    }

    [Fact]
    public void CalendarEvent_AllowsEmptyTitleButRejectsDefaultKeyAndUndefinedResponse()
    {
        var valid = CreateEvent(title: string.Empty);

        Assert.Equal(string.Empty, valid.Title);
        AssertParamName<ArgumentException>(
            "key",
            () => new CalendarEvent(
                default,
                "event",
                CreateTiming(),
                "calendar"));
        AssertParamName<ArgumentOutOfRangeException>(
            "responseStatus",
            () => new CalendarEvent(
                CreateKey(),
                "event",
                CreateTiming(),
                "calendar",
                responseStatus: (AttendeeResponse)99));
    }

    [Fact]
    public void CalendarEvent_RejectsMissingCalendarNameAndUnsafeLinks()
    {
        AssertParamName<ArgumentException>(
            "calendarName",
            () => new CalendarEvent(CreateKey(), "event", CreateTiming(), " "));
        AssertParamName<ArgumentException>(
            "meetingUri",
            () => new CalendarEvent(
                CreateKey(),
                "unsafe",
                CreateTiming(),
                "calendar",
                meetingUri: new Uri("file:///C:/Windows/System32/calc.exe")));
    }

    [Fact]
    public void CalendarAccount_RejectsEmptyIdentityUndefinedProviderAndRequiredStrings()
    {
        AssertParamName<ArgumentException>(
            "internalAccountId",
            () => CreateAccount(Guid.Empty));
        AssertParamName<ArgumentOutOfRangeException>(
            "provider",
            () => CreateAccount(AccountId, provider: (ProviderKind)99));
        AssertParamName<ArgumentException>(
            "providerSubjectId",
            () => CreateAccount(AccountId, providerSubjectId: " "));
        AssertParamName<ArgumentException>(
            "displayName",
            () => CreateAccount(AccountId, displayName: string.Empty));
        AssertParamName<ArgumentException>(
            "email",
            () => CreateAccount(AccountId, email: null!));
        AssertParamName<ArgumentException>(
            "tokenRef",
            () => CreateAccount(AccountId, tokenRef: " "));
    }

    [Fact]
    public void CalendarDescriptorAndSelection_RejectMissingRequiredIdentity()
    {
        AssertParamName<ArgumentException>(
            "calendarId",
            () => new CalendarDescriptor("", "locator", "Calendar", false, true, null));
        AssertParamName<ArgumentException>(
            "providerLocator",
            () => new CalendarDescriptor("calendar", " ", "Calendar", false, true, null));
        AssertParamName<ArgumentException>(
            "name",
            () => new CalendarDescriptor("calendar", "locator", null!, false, true, null));
        AssertParamName<ArgumentException>(
            "internalAccountId",
            () => new CalendarSelection(Guid.Empty, "calendar", true));
        AssertParamName<ArgumentException>(
            "calendarId",
            () => new CalendarSelection(AccountId, " ", true));
    }

    [Fact]
    public void CalendarSyncState_NormalizesAllTimestampsToUtc()
    {
        var attempt = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.FromHours(9));
        var success = new DateTimeOffset(2026, 8, 27, 11, 0, 0, TimeSpan.FromHours(8));
        var retry = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.FromHours(-4));

        var state = new CalendarSyncState(
            "calendar",
            SyncStatus.RateLimited,
            attempt,
            success,
            retry,
            SyncErrorCategory.RateLimited);

        Assert.Equal(TimeSpan.Zero, state.LastAttemptUtc!.Value.Offset);
        Assert.Equal(attempt.ToUniversalTime(), state.LastAttemptUtc);
        Assert.Equal(TimeSpan.Zero, state.LastSuccessUtc!.Value.Offset);
        Assert.Equal(success.ToUniversalTime(), state.LastSuccessUtc);
        Assert.Equal(TimeSpan.Zero, state.RetryAtUtc!.Value.Offset);
        Assert.Equal(retry.ToUniversalTime(), state.RetryAtUtc);
    }

    [Fact]
    public void SyncModels_RejectUndefinedEnumsAndEmptyIdentity()
    {
        AssertParamName<ArgumentOutOfRangeException>(
            "status",
            () => new CalendarSyncState(
                "calendar",
                (SyncStatus)99,
                null,
                null,
                null,
                SyncErrorCategory.None));
        AssertParamName<ArgumentOutOfRangeException>(
            "errorCategory",
            () => new CalendarSyncState(
                "calendar",
                SyncStatus.Succeeded,
                null,
                null,
                null,
                (SyncErrorCategory)99));
        AssertParamName<ArgumentException>(
            "internalAccountId",
            () => new AccountSyncState(
                Guid.Empty,
                SyncStatus.NotStarted,
                null,
                null,
                Array.Empty<CalendarSyncState>()));
    }

    [Fact]
    public void SyncSnapshot_CopiesInputCollectionsAndNormalizesCapturedTime()
    {
        var accounts = new List<CalendarAccount>
        {
            CreateAccount(AccountId),
        };
        var selections = new List<CalendarSelection>
        {
            new(AccountId, "calendar", true),
        };
        var events = new List<CalendarEvent>();
        var captured = new DateTimeOffset(2026, 8, 27, 9, 0, 0, TimeSpan.FromHours(9));
        var snapshot = new SyncSnapshot(accounts, selections, events, captured);

        accounts.Clear();
        selections.Clear();

        Assert.Single(snapshot.Accounts);
        Assert.Single(snapshot.CalendarSelections);
        Assert.Empty(snapshot.Events);
        Assert.Equal(captured.ToUniversalTime(), snapshot.CapturedAtUtc);
        Assert.Equal(TimeSpan.Zero, snapshot.CapturedAtUtc.Offset);
    }

    private static void AssertParamName<TException>(string expected, Action action)
        where TException : ArgumentException
    {
        var exception = Assert.ThrowsAny<TException>(action);
        Assert.Equal(expected, exception.ParamName);
    }

    private static CalendarAccount CreateAccount(
        Guid accountId,
        ProviderKind provider = ProviderKind.Google,
        string providerSubjectId = "subject",
        string displayName = "Account",
        string email = "user@example.invalid",
        string tokenRef = "google/token") => new(
            accountId,
            provider,
            providerSubjectId,
            displayName,
            email,
            true,
            tokenRef);

    private static EventKey CreateKey() => new(
        ProviderKind.Google,
        AccountId,
        "calendar",
        "event");

    private static TimedEventTiming CreateTiming() => new(
        new DateTimeOffset(2026, 8, 27, 1, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 27, 2, 0, 0, TimeSpan.Zero));

    private static CalendarEvent CreateEvent(string title) => new(
        CreateKey(),
        title,
        CreateTiming(),
        "calendar");
}
