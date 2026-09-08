namespace UnifiedCalendar.Core.Models;

public sealed record CalendarAccount
{
    public CalendarAccount(
        Guid internalAccountId,
        ProviderKind provider,
        string providerSubjectId,
        string displayName,
        string email,
        bool enabled,
        string tokenRef)
    {
        ModelGuard.NotEmpty(internalAccountId, nameof(internalAccountId));
        ModelGuard.Defined(provider, nameof(provider));
        ModelGuard.NotBlank(providerSubjectId, nameof(providerSubjectId));
        ModelGuard.NotBlank(displayName, nameof(displayName));
        ModelGuard.NotBlank(email, nameof(email));
        ModelGuard.NotBlank(tokenRef, nameof(tokenRef));

        InternalAccountId = internalAccountId;
        Provider = provider;
        ProviderSubjectId = providerSubjectId;
        DisplayName = displayName;
        Email = email;
        Enabled = enabled;
        TokenRef = tokenRef;
    }

    public Guid InternalAccountId { get; }

    public ProviderKind Provider { get; }

    public string ProviderSubjectId { get; }

    public string DisplayName { get; }

    public string Email { get; }

    public bool Enabled { get; }

    public string TokenRef { get; }
}

public sealed record CalendarDescriptor
{
    public CalendarDescriptor(
        string calendarId,
        string providerLocator,
        string name,
        bool isPrimary,
        bool canReadEvents,
        RgbColor? sourceColor)
    {
        ModelGuard.NotBlank(calendarId, nameof(calendarId));
        ModelGuard.NotBlank(providerLocator, nameof(providerLocator));
        ModelGuard.NotBlank(name, nameof(name));

        CalendarId = calendarId;
        ProviderLocator = providerLocator;
        Name = name;
        IsPrimary = isPrimary;
        CanReadEvents = canReadEvents;
        SourceColor = sourceColor;
    }

    public string CalendarId { get; }

    public string ProviderLocator { get; }

    public string Name { get; }

    public bool IsPrimary { get; }

    public bool CanReadEvents { get; }

    public RgbColor? SourceColor { get; }
}

public sealed record CalendarSelection
{
    public CalendarSelection(Guid internalAccountId, string calendarId, bool isVisible)
    {
        ModelGuard.NotEmpty(internalAccountId, nameof(internalAccountId));
        ModelGuard.NotBlank(calendarId, nameof(calendarId));

        InternalAccountId = internalAccountId;
        CalendarId = calendarId;
        IsVisible = isVisible;
    }

    public Guid InternalAccountId { get; }

    public string CalendarId { get; }

    public bool IsVisible { get; }
}

public enum SyncStatus
{
    NotStarted,
    Syncing,
    Succeeded,
    PartiallySucceeded,
    Failed,
    AuthenticationRequired,
    RateLimited,
    Cancelled,
}

public enum SyncErrorCategory
{
    None,
    Network,
    Timeout,
    RateLimited,
    AuthenticationRequired,
    PermissionDenied,
    NotFound,
    InvalidData,
    Storage,
    Cancelled,
    Unexpected,
}

public sealed record CalendarSyncState
{
    public CalendarSyncState(
        string calendarId,
        SyncStatus status,
        DateTimeOffset? lastAttemptUtc,
        DateTimeOffset? lastSuccessUtc,
        DateTimeOffset? retryAtUtc,
        SyncErrorCategory errorCategory)
    {
        ModelGuard.NotBlank(calendarId, nameof(calendarId));
        ModelGuard.Defined(status, nameof(status));
        ModelGuard.Defined(errorCategory, nameof(errorCategory));

        CalendarId = calendarId;
        Status = status;
        LastAttemptUtc = lastAttemptUtc?.ToUniversalTime();
        LastSuccessUtc = lastSuccessUtc?.ToUniversalTime();
        RetryAtUtc = retryAtUtc?.ToUniversalTime();
        ErrorCategory = errorCategory;
    }

    public string CalendarId { get; }

    public SyncStatus Status { get; }

    public DateTimeOffset? LastAttemptUtc { get; }

    public DateTimeOffset? LastSuccessUtc { get; }

    public DateTimeOffset? RetryAtUtc { get; }

    public SyncErrorCategory ErrorCategory { get; }
}

public sealed class AccountSyncState
{
    private readonly IReadOnlyList<CalendarSyncState> _calendarStates;

    public AccountSyncState(
        Guid internalAccountId,
        SyncStatus status,
        DateTimeOffset? lastAttemptUtc,
        DateTimeOffset? lastFullySuccessfulSyncUtc,
        IEnumerable<CalendarSyncState> calendarStates)
    {
        ModelGuard.NotEmpty(internalAccountId, nameof(internalAccountId));
        ModelGuard.Defined(status, nameof(status));
        ArgumentNullException.ThrowIfNull(calendarStates);

        var calendarStateArray = calendarStates.ToArray();
        if (calendarStateArray.Any(state => state is null))
        {
            throw new ArgumentException("Calendar states cannot contain null elements.", nameof(calendarStates));
        }

        InternalAccountId = internalAccountId;
        Status = status;
        LastAttemptUtc = lastAttemptUtc?.ToUniversalTime();
        LastFullySuccessfulSyncUtc = lastFullySuccessfulSyncUtc?.ToUniversalTime();
        _calendarStates = Array.AsReadOnly(calendarStateArray);
    }

    public Guid InternalAccountId { get; }

    public SyncStatus Status { get; }

    public DateTimeOffset? LastAttemptUtc { get; }

    public DateTimeOffset? LastFullySuccessfulSyncUtc { get; }

    public IReadOnlyList<CalendarSyncState> CalendarStates => _calendarStates;

    public bool HasWarning => CalendarStates.Any(state =>
        state.Status is SyncStatus.Failed
            or SyncStatus.AuthenticationRequired
            or SyncStatus.RateLimited);
}

public sealed class SyncSnapshot
{
    private readonly IReadOnlyList<CalendarAccount> _accounts;
    private readonly IReadOnlyList<CalendarSelection> _calendarSelections;
    private readonly IReadOnlyList<CalendarEvent> _events;

    public SyncSnapshot(
        IEnumerable<CalendarAccount> accounts,
        IEnumerable<CalendarSelection> calendarSelections,
        IEnumerable<CalendarEvent> events,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(calendarSelections);
        ArgumentNullException.ThrowIfNull(events);

        var accountArray = accounts.ToArray();
        var selectionArray = calendarSelections.ToArray();
        var eventArray = events.ToArray();
        if (accountArray.Any(account => account is null))
        {
            throw new ArgumentException("Accounts cannot contain null elements.", nameof(accounts));
        }

        if (selectionArray.Any(selection => selection is null))
        {
            throw new ArgumentException("Calendar selections cannot contain null elements.", nameof(calendarSelections));
        }

        if (eventArray.Any(calendarEvent => calendarEvent is null))
        {
            throw new ArgumentException("Events cannot contain null elements.", nameof(events));
        }

        _accounts = Array.AsReadOnly(accountArray);
        _calendarSelections = Array.AsReadOnly(selectionArray);
        _events = Array.AsReadOnly(eventArray);
        CapturedAtUtc = capturedAtUtc.ToUniversalTime();
    }

    public IReadOnlyList<CalendarAccount> Accounts => _accounts;

    public IReadOnlyList<CalendarSelection> CalendarSelections => _calendarSelections;

    public IReadOnlyList<CalendarEvent> Events => _events;

    public DateTimeOffset CapturedAtUtc { get; }
}
