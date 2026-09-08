using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Core.Providers;

public enum ProviderErrorCategory
{
    AuthenticationRequired,
    PermissionDenied,
    RateLimited,
    Network,
    Timeout,
    ServerError,
    MalformedResponse,
    Cancelled,
    NotFound,
    Unexpected,
}

public sealed record ProviderError
{
    public ProviderError(
        ProviderErrorCategory category,
        int? httpStatusCode = null,
        TimeSpan? retryAfter = null,
        bool reauthenticationRequired = false)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "The provider error category is not defined.");
        }

        if (httpStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(httpStatusCode), "HTTP status codes must be between 100 and 599.");
        }

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), "Retry-After cannot be negative.");
        }

        Category = category;
        HttpStatusCode = httpStatusCode;
        RetryAfter = retryAfter;
        ReauthenticationRequired = reauthenticationRequired;
    }

    public ProviderErrorCategory Category { get; }

    public int? HttpStatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public bool ReauthenticationRequired { get; }
}

public sealed class ProviderException : Exception
{
    public ProviderException(ProviderError error)
        : base($"Provider operation failed with category {error?.Category}.")
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public ProviderError Error { get; }
}

public readonly record struct TimeRangeUtc
{
    public TimeRangeUtc(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        StartUtc = startUtc.ToUniversalTime();
        EndUtc = endUtc.ToUniversalTime();
        if (EndUtc <= StartUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endUtc), "The range end must follow its start.");
        }
    }

    public DateTimeOffset StartUtc { get; }

    public DateTimeOffset EndUtc { get; }
}

public sealed record AuthAccountResult
{
    private AuthAccountResult(
        bool isSuccess,
        Guid internalAccountId,
        string? providerSubjectId,
        string? displayName,
        string? email,
        ProviderError? error)
    {
        IsSuccess = isSuccess;
        InternalAccountId = internalAccountId;
        ProviderSubjectId = providerSubjectId;
        DisplayName = displayName;
        Email = email;
        Error = error;
    }

    public bool IsSuccess { get; }

    public Guid InternalAccountId { get; }

    public string? ProviderSubjectId { get; }

    public string? DisplayName { get; }

    public string? Email { get; }

    public ProviderError? Error { get; }

    public static AuthAccountResult Success(
        Guid internalAccountId,
        string providerSubjectId,
        string displayName,
        string email)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The internal account ID cannot be empty.", nameof(internalAccountId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(providerSubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new AuthAccountResult(true, internalAccountId, providerSubjectId, displayName, email, null);
    }

    public static AuthAccountResult Failure(ProviderError error) =>
        new(false, Guid.Empty, null, null, null, error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed record ProviderCalendarResult
{
    private static readonly IReadOnlyList<CalendarEvent> NoEvents = Array.AsReadOnly(Array.Empty<CalendarEvent>());

    private ProviderCalendarResult(bool isSuccess, IReadOnlyList<CalendarEvent> events, ProviderError? error)
    {
        IsSuccess = isSuccess;
        Events = events;
        Error = error;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<CalendarEvent> Events { get; }

    public ProviderError? Error { get; }

    public static ProviderCalendarResult Success(IEnumerable<CalendarEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var values = events.ToArray();
        if (values.Any(calendarEvent => calendarEvent is null))
        {
            throw new ArgumentException("Provider results cannot contain null events.", nameof(events));
        }

        return new ProviderCalendarResult(true, Array.AsReadOnly(values), null);
    }

    public static ProviderCalendarResult Failure(ProviderError error) =>
        new(false, NoEvents, error ?? throw new ArgumentNullException(nameof(error)));
}

public interface ICalendarProvider
{
    ProviderKind Provider { get; }

    Task<AuthAccountResult> AuthenticateAsync(CancellationToken cancellationToken);

    Task<AuthAccountResult> ReauthenticateAsync(
        CalendarAccount account,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
        CalendarAccount account,
        CancellationToken cancellationToken);

    Task<ProviderCalendarResult> GetEventsAsync(
        CalendarAccount account,
        CalendarDescriptor calendar,
        TimeRangeUtc range,
        CancellationToken cancellationToken);
}
