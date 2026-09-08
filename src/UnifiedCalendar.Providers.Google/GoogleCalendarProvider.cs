using Google.Apis.Calendar.v3.Data;
using Google.Apis.Auth.OAuth2.Responses;
using Serilog;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.Providers.Google;

public sealed class GoogleCalendarProvider : ICalendarProvider
{
    private readonly IGoogleCalendarApiClientFactory _clientFactory;
    private readonly ITokenStore _tokenStore;
    private readonly GoogleProviderLogger _logger;

    public GoogleCalendarProvider(
        ITokenStore tokenStore,
        GoogleProviderOptions options,
        TimeProvider timeProvider,
        ILogger? logger = null)
        : this(
            new GoogleCalendarApiClientFactory(tokenStore, options, timeProvider),
            tokenStore,
            new GoogleProviderLogger(logger))
    {
    }

    internal GoogleCalendarProvider(
        IGoogleCalendarApiClientFactory clientFactory,
        ITokenStore tokenStore,
        GoogleProviderLogger? logger = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _logger = logger ?? new GoogleProviderLogger();
    }

    public ProviderKind Provider => ProviderKind.Google;

    public async Task<AuthAccountResult> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var internalAccountId = Guid.NewGuid();
        var result = await AuthenticateCoreAsync(
            internalAccountId,
            expectedProviderSubjectId: null,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            try
            {
                await _tokenStore.RemoveAsync(
                    ProviderKind.Google,
                    internalAccountId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The original typed authentication failure remains authoritative.
            }
        }

        return result;
    }

    public Task<AuthAccountResult> ReauthenticateAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateGoogleAccount(account);

        return AuthenticateCoreAsync(
            account.InternalAccountId,
            account.ProviderSubjectId,
            cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateGoogleAccount(account);
        try
        {
            using var client = await _clientFactory.CreateStoredAsync(account, cancellationToken).ConfigureAwait(false);
            var source = await GetAllCalendarEntriesAsync(client, cancellationToken).ConfigureAwait(false);
            var calendars = source
                .Select(GoogleModelMapper.ToCalendarDescriptor)
                .Where(calendar => calendar is not null)
                .Cast<CalendarDescriptor>()
                .ToArray();
            _logger.OperationCompleted("ListCalendars", account.InternalAccountId, null, calendars.Length);
            return Array.AsReadOnly(calendars);
        }
        catch (Exception exception)
        {
            await QuarantineRejectedRefreshTokenAsync(
                exception,
                account.InternalAccountId).ConfigureAwait(false);
            var error = GoogleProviderErrorMapper.Map(exception, cancellationToken);
            _logger.OperationFailed("ListCalendars", account.InternalAccountId, null, error);
            throw new ProviderException(error);
        }
    }

    public async Task<ProviderCalendarResult> GetEventsAsync(
        CalendarAccount account,
        CalendarDescriptor calendar,
        TimeRangeUtc range,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(calendar);
        ValidateGoogleAccount(account);
        if (!calendar.CanReadEvents)
        {
            return ProviderCalendarResult.Success([]);
        }

        try
        {
            using var client = await _clientFactory.CreateStoredAsync(account, cancellationToken).ConfigureAwait(false);
            var sourceEvents = await GetAllEventsAsync(
                client,
                calendar.CalendarId,
                range,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, RgbColor> colors = new Dictionary<string, RgbColor>(StringComparer.Ordinal);
            if (sourceEvents.Any(source => !string.IsNullOrWhiteSpace(source.ColorId)))
            {
                colors = GoogleModelMapper.ToEventColorMap(
                    await client.GetColorsAsync(cancellationToken).ConfigureAwait(false));
            }

            var mappings = sourceEvents
                .Select(source => GoogleModelMapper.ToCalendarEvent(source, account, calendar, colors))
                .ToArray();
            _logger.EventsExcluded(
                account.InternalAccountId,
                calendar.CalendarId,
                mappings
                    .Where(mapping => !mapping.IsMapped && mapping.ExclusionReason.HasValue)
                    .GroupBy(mapping => mapping.ExclusionReason!.Value));
            var events = mappings
                .Where(mapping => mapping.IsMapped)
                .Select(mapping => mapping.Event!)
                .ToArray();
            _logger.OperationCompleted(
                "GetEvents",
                account.InternalAccountId,
                calendar.CalendarId,
                events.Length);
            return ProviderCalendarResult.Success(events);
        }
        catch (Exception exception)
        {
            await QuarantineRejectedRefreshTokenAsync(
                exception,
                account.InternalAccountId).ConfigureAwait(false);
            var error = GoogleProviderErrorMapper.Map(exception, cancellationToken);
            _logger.OperationFailed(
                "GetEvents",
                account.InternalAccountId,
                calendar.CalendarId,
                error);
            return ProviderCalendarResult.Failure(error);
        }
    }

    private async Task<AuthAccountResult> AuthenticateCoreAsync(
        Guid internalAccountId,
        string? expectedProviderSubjectId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var session = await _clientFactory.CreateInteractiveAsync(
                internalAccountId,
                expectedProviderSubjectId is null
                    ? GoogleInteractiveAuthorizationMode.NewAccount
                    : GoogleInteractiveAuthorizationMode.Reauthentication,
                cancellationToken).ConfigureAwait(false);
            var calendars = await GetAllCalendarEntriesAsync(
                session.Client,
                cancellationToken).ConfigureAwait(false);
            var primary = calendars.FirstOrDefault(calendar => calendar.Primary == true);
            if (primary is null || string.IsNullOrWhiteSpace(primary.Id))
            {
                throw new InvalidDataException("The Google account has no readable primary calendar identity.");
            }

            if (expectedProviderSubjectId is not null
                && !expectedProviderSubjectId.Equals(primary.Id, StringComparison.Ordinal))
            {
                throw new GoogleIdentityMismatchException();
            }

            var name = !string.IsNullOrWhiteSpace(primary.SummaryOverride)
                ? primary.SummaryOverride
                : !string.IsNullOrWhiteSpace(primary.Summary)
                    ? primary.Summary
                    : "Google Calendar";
            await session.CommitTokenAsync(cancellationToken).ConfigureAwait(false);
            _logger.OperationCompleted("Authenticate", internalAccountId, null, 1);
            return AuthAccountResult.Success(internalAccountId, primary.Id, name, primary.Id);
        }
        catch (Exception exception)
        {
            var error = GoogleProviderErrorMapper.Map(exception, cancellationToken);
            _logger.OperationFailed("Authenticate", internalAccountId, null, error);
            return AuthAccountResult.Failure(error);
        }
    }

    private static async Task<IReadOnlyList<CalendarListEntry>> GetAllCalendarEntriesAsync(
        IGoogleCalendarApiClient client,
        CancellationToken cancellationToken)
    {
        var results = new List<CalendarListEntry>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await client.GetCalendarListPageAsync(pageToken, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Google CalendarList response was empty.");
            if (page.Items is not null)
            {
                results.AddRange(page.Items.Where(item => item is not null));
            }

            pageToken = NullIfWhiteSpace(page.NextPageToken);
            if (pageToken is not null && !seenTokens.Add(pageToken))
            {
                throw new InvalidDataException("The Google CalendarList paging token repeated.");
            }
        }
        while (pageToken is not null);

        return results;
    }

    private static async Task<IReadOnlyList<Event>> GetAllEventsAsync(
        IGoogleCalendarApiClient client,
        string calendarId,
        TimeRangeUtc range,
        CancellationToken cancellationToken)
    {
        var results = new List<Event>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await client.GetEventsPageAsync(
                calendarId,
                range,
                pageToken,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Google Events response was empty.");
            if (page.Items is not null)
            {
                results.AddRange(page.Items.Where(item => item is not null));
            }

            pageToken = NullIfWhiteSpace(page.NextPageToken);
            if (pageToken is not null && !seenTokens.Add(pageToken))
            {
                throw new InvalidDataException("The Google Events paging token repeated.");
            }
        }
        while (pageToken is not null);

        return results;
    }

    private static void ValidateGoogleAccount(CalendarAccount account)
    {
        if (account.Provider != ProviderKind.Google)
        {
            throw new ArgumentException("The account is not a Google account.", nameof(account));
        }
    }

    private async Task QuarantineRejectedRefreshTokenAsync(
        Exception exception,
        Guid internalAccountId)
    {
        if (exception is not TokenResponseException tokenException
            || tokenException.Error?.Error?.Equals(
                "invalid_grant",
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        await _tokenStore.QuarantineAsync(
            ProviderKind.Google,
            internalAccountId,
            TokenQuarantineReason.RefreshRejected,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

internal sealed class GoogleIdentityMismatchException : Exception
{
    public GoogleIdentityMismatchException()
        : base("The selected Google identity does not match the existing account.")
    {
    }
}
