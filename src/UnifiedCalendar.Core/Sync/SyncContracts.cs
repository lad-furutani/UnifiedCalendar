using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Core.Sync;

public static class SyncPolicy
{
    private static readonly IReadOnlyList<TimeSpan> RetryDelays =
        Array.AsReadOnly(
        [
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        ]);

    public static IReadOnlyList<TimeSpan> TransientRetryDelays => RetryDelays;

    public static TimeSpan StartupRetryDelay { get; } = TimeSpan.FromSeconds(30);

    public const int StartupRetryCount = 3;

    public const int MaxConcurrentAccounts = 4;

    public static TimeSpan DisposeTimeout { get; } = TimeSpan.FromSeconds(5);
}

public static class AccountSyncTargetPolicy
{
    public static bool IsSyncTarget(AccountSettings account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Enabled && account.Calendars.Any(calendar => calendar.IsVisible);
    }
}

public enum SyncTriggerReason
{
    Startup,
    StartupRetry,
    Scheduled,
    Resume,
    Manual,
    RateLimitRetry,
}

public enum SyncAccountResultKind
{
    Succeeded,
    PartiallySucceeded,
    Failed,
    Cancelled,
    Deferred,
}

public sealed record SyncAccountResult(
    Guid InternalAccountId,
    SyncAccountResultKind Result,
    AccountSyncState State,
    DateTimeOffset? RetryAtUtc = null)
{
    public bool RequiresStartupRetry => State.CalendarStates.Any(calendar =>
        calendar.ErrorCategory is SyncErrorCategory.Network or SyncErrorCategory.Timeout);
}

public sealed class SyncRunResult
{
    private readonly IReadOnlyList<SyncAccountResult> _accounts;

    public SyncRunResult(
        SyncTriggerReason reason,
        IEnumerable<SyncAccountResult> accounts,
        SyncErrorCategory? requestError = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        var values = accounts.ToArray();
        if (values.Any(account => account is null))
        {
            throw new ArgumentException("Sync results cannot contain null accounts.", nameof(accounts));
        }

        Reason = reason;
        _accounts = Array.AsReadOnly(values);
        RequestError = requestError;
    }

    public SyncTriggerReason Reason { get; }

    public IReadOnlyList<SyncAccountResult> Accounts => _accounts;

    public SyncErrorCategory? RequestError { get; }

    public IReadOnlyList<Guid> StartupRetryAccountIds => Accounts
        .Where(account => account.RequiresStartupRetry)
        .Select(account => account.InternalAccountId)
        .ToArray();
}

public sealed record StartupCacheResult(
    SyncSnapshot Snapshot,
    IReadOnlyList<AccountSyncState> AccountStates,
    bool HasCache,
    SyncErrorCategory? SettingsError = null);

public sealed class SyncSnapshotChangedEventArgs : EventArgs
{
    public SyncSnapshotChangedEventArgs(SyncSnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public SyncSnapshot Snapshot { get; }
}

public sealed class AccountSyncStateChangedEventArgs : EventArgs
{
    public AccountSyncStateChangedEventArgs(AccountSyncState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public AccountSyncState State { get; }
}

public interface ICalendarSyncService
{
    event EventHandler<SyncSnapshotChangedEventArgs>? SnapshotChanged;

    event EventHandler<AccountSyncStateChangedEventArgs>? AccountStateChanged;

    SyncSnapshot CurrentSnapshot { get; }

    IReadOnlyList<AccountSyncState> AccountStates { get; }

    Task<StartupCacheResult> InitializeAsync(CancellationToken cancellationToken = default);

    Task RefreshFromSettingsAsync(CancellationToken cancellationToken = default);

    Task<SyncRunResult> RequestSyncAsync(
        SyncTriggerReason reason,
        CancellationToken cancellationToken = default);

    Task<SyncRunResult> RequestSyncAsync(
        SyncTriggerReason reason,
        IReadOnlyCollection<Guid> internalAccountIds,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface ISyncEventLogger
{
    void AccountStarted(ProviderKind provider, Guid accountId, SyncTriggerReason reason);

    void AccountCompleted(
        ProviderKind provider,
        Guid accountId,
        SyncAccountResultKind result,
        int count,
        long elapsedMilliseconds);

    void CalendarCompleted(
        ProviderKind provider,
        Guid accountId,
        string calendarId,
        int count,
        SyncStatus result,
        SyncErrorCategory errorCategory,
        long elapsedMilliseconds);

    void RetryScheduled(
        ProviderKind provider,
        Guid accountId,
        string? calendarId,
        int attempt,
        TimeSpan delay,
        SyncErrorCategory errorCategory);

    void RateLimitDeferred(
        ProviderKind provider,
        Guid accountId,
        DateTimeOffset retryAtUtc);

    void StorageFailed(string stage, Guid? accountId);

    void UnexpectedFailed(string stage, ProviderKind? provider, Guid? accountId);
}

public sealed class NullSyncEventLogger : ISyncEventLogger
{
    public void AccountStarted(ProviderKind provider, Guid accountId, SyncTriggerReason reason)
    {
    }

    public void AccountCompleted(
        ProviderKind provider,
        Guid accountId,
        SyncAccountResultKind result,
        int count,
        long elapsedMilliseconds)
    {
    }

    public void CalendarCompleted(
        ProviderKind provider,
        Guid accountId,
        string calendarId,
        int count,
        SyncStatus result,
        SyncErrorCategory errorCategory,
        long elapsedMilliseconds)
    {
    }

    public void RetryScheduled(
        ProviderKind provider,
        Guid accountId,
        string? calendarId,
        int attempt,
        TimeSpan delay,
        SyncErrorCategory errorCategory)
    {
    }

    public void RateLimitDeferred(
        ProviderKind provider,
        Guid accountId,
        DateTimeOffset retryAtUtc)
    {
    }

    public void StorageFailed(string stage, Guid? accountId)
    {
    }

    public void UnexpectedFailed(string stage, ProviderKind? provider, Guid? accountId)
    {
    }
}

public enum InternalRefreshReason
{
    MinuteBoundary,
    Resume,
    TimeZoneChanged,
}

public interface IInternalRefreshRequester
{
    ValueTask RequestRefreshAsync(
        InternalRefreshReason reason,
        CancellationToken cancellationToken = default);
}

public sealed class InternalRefreshSignal : IInternalRefreshRequester
{
    public event EventHandler<InternalRefreshRequestedEventArgs>? RefreshRequested;

    public ValueTask RequestRefreshAsync(
        InternalRefreshReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        RefreshRequested?.Invoke(this, new InternalRefreshRequestedEventArgs(reason));
        return ValueTask.CompletedTask;
    }
}

public sealed class InternalRefreshRequestedEventArgs : EventArgs
{
    public InternalRefreshRequestedEventArgs(InternalRefreshReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        Reason = reason;
    }

    public InternalRefreshReason Reason { get; }
}
