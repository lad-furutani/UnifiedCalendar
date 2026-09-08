using Microsoft.Graph.Models;
using Serilog;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using GraphCalendar = Microsoft.Graph.Models.Calendar;
using GraphEvent = Microsoft.Graph.Models.Event;
using RgbColor = UnifiedCalendar.Core.Models.RgbColor;

namespace UnifiedCalendar.Providers.Microsoft;

public sealed class MicrosoftCalendarProvider : ICalendarProvider
{
    private readonly IMicrosoftAuthenticationClient _authentication;
    private readonly IMicrosoftGraphApiClientFactory _clientFactory;
    private readonly ITokenStore _tokenStore;
    private readonly TimeProvider _timeProvider;
    private readonly MicrosoftProviderLogger _logger;

    public MicrosoftCalendarProvider(
        ITokenStore tokenStore,
        MicrosoftProviderOptions options,
        IHttpMessageHandlerFactory handlerFactory,
        TimeProvider timeProvider,
        ILogger? logger = null)
        : this(
            new MicrosoftAuthenticationClient(tokenStore, options, timeProvider),
            new MicrosoftGraphApiClientFactory(handlerFactory, options, timeProvider),
            tokenStore,
            timeProvider,
            new MicrosoftProviderLogger(logger))
    {
    }

    internal MicrosoftCalendarProvider(
        IMicrosoftAuthenticationClient authentication,
        IMicrosoftGraphApiClientFactory clientFactory,
        ITokenStore tokenStore,
        TimeProvider timeProvider,
        MicrosoftProviderLogger? logger = null)
    {
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? new MicrosoftProviderLogger();
    }

    public ProviderKind Provider => ProviderKind.Microsoft;

    public Task<AuthAccountResult> AuthenticateAsync(CancellationToken cancellationToken) =>
        AuthenticateCoreAsync(Guid.NewGuid(), null, null, cancellationToken);

    public async Task<AuthAccountResult> ReauthenticateAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateMicrosoftAccount(account);

        return await AuthenticateCoreAsync(
            account.InternalAccountId,
            account.ProviderSubjectId,
            account.Email,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateMicrosoftAccount(account);
        try
        {
            var authentication = await _authentication.AcquireSilentAsync(account, cancellationToken)
                .ConfigureAwait(false);
            using var client = _clientFactory.Create(authentication.AccessToken);
            var calendars = await GetAllCalendarsAsync(client, cancellationToken).ConfigureAwait(false);
            var mapped = calendars
                .Select(MicrosoftModelMapper.ToCalendarDescriptor)
                .Where(value => value is not null)
                .Cast<CalendarDescriptor>()
                .ToArray();
            _logger.OperationCompleted("ListCalendars", account.InternalAccountId, null, mapped.Length);
            return Array.AsReadOnly(mapped);
        }
        catch (Exception exception)
        {
            var error = MicrosoftProviderErrorMapper.Map(exception, cancellationToken, _timeProvider);
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
        ValidateMicrosoftAccount(account);
        if (!calendar.CanReadEvents)
        {
            return ProviderCalendarResult.Success([]);
        }

        try
        {
            var authentication = await _authentication.AcquireSilentAsync(account, cancellationToken)
                .ConfigureAwait(false);
            using var client = _clientFactory.Create(authentication.AccessToken);
            var sourceEvents = await GetAllEventsAsync(
                client,
                calendar.ProviderLocator,
                range,
                cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, RgbColor> categoryColors =
                new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase);
            if (sourceEvents.Any(value => value.Categories?.Count > 0))
            {
                try
                {
                    categoryColors = MicrosoftModelMapper.ToCategoryColorMap(
                        await GetAllCategoriesAsync(client, cancellationToken).ConfigureAwait(false));
                }
                catch (Exception exception)
                {
                    var categoryError = MicrosoftProviderErrorMapper.Map(
                        exception,
                        cancellationToken,
                        _timeProvider);
                    if (!CanFallBackWithoutCategoryColors(categoryError))
                    {
                        throw;
                    }

                    _logger.CategoryColorsUnavailable(
                        account.InternalAccountId,
                        calendar.CalendarId,
                        categoryError);
                }
            }

            var mappings = sourceEvents
                .Select(value => MicrosoftModelMapper.ToCalendarEvent(value, account, calendar, categoryColors))
                .ToArray();
            _logger.EventsExcluded(
                account.InternalAccountId,
                calendar.CalendarId,
                mappings
                    .Where(value => !value.IsMapped && value.ExclusionReason.HasValue)
                    .GroupBy(value => value.ExclusionReason!.Value));
            var events = mappings
                .Where(value => value.IsMapped)
                .Select(value => value.Event!)
                .ToArray();
            _logger.OperationCompleted("GetEvents", account.InternalAccountId, calendar.CalendarId, events.Length);
            return ProviderCalendarResult.Success(events);
        }
        catch (Exception exception)
        {
            var error = MicrosoftProviderErrorMapper.Map(exception, cancellationToken, _timeProvider);
            _logger.OperationFailed("GetEvents", account.InternalAccountId, calendar.CalendarId, error);
            return ProviderCalendarResult.Failure(error);
        }
    }

    private async Task<AuthAccountResult> AuthenticateCoreAsync(
        Guid accountId,
        string? expectedProviderSubjectId,
        string? loginHint,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _authentication.AcquireInteractiveAsync(
                accountId,
                expectedProviderSubjectId,
                loginHint,
                cancellationToken).ConfigureAwait(false);
            MicrosoftIdentityCachePolicy.ValidateExpectedIdentity(
                expectedProviderSubjectId,
                result.ProviderSubjectId);
            _logger.OperationCompleted("Authenticate", accountId, null, 1);
            return AuthAccountResult.Success(
                accountId,
                result.ProviderSubjectId,
                result.DisplayName,
                result.Email);
        }
        catch (Exception exception)
        {
            if (expectedProviderSubjectId is null)
            {
                try
                {
                    await _tokenStore.RemoveAsync(
                        ProviderKind.Microsoft,
                        accountId,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The typed authentication error remains authoritative.
                }
            }

            var error = MicrosoftProviderErrorMapper.Map(exception, cancellationToken, _timeProvider);
            _logger.OperationFailed("Authenticate", accountId, null, error);
            return AuthAccountResult.Failure(error);
        }
    }

    private static async Task<IReadOnlyList<GraphCalendar>> GetAllCalendarsAsync(
        IMicrosoftGraphApiClient client,
        CancellationToken cancellationToken)
    {
        var calendars = new Dictionary<string, GraphCalendar>(StringComparer.Ordinal);
        await AddCalendarPagesAsync(
            next => client.GetCalendarsPageAsync(next, cancellationToken),
            calendars,
            cancellationToken).ConfigureAwait(false);

        return calendars.Values.ToArray();
    }

    private static async Task AddCalendarPagesAsync(
        Func<string?, Task<CalendarCollectionResponse?>> getPage,
        IDictionary<string, GraphCalendar> destination,
        CancellationToken cancellationToken)
    {
        var page = await GetAllPagesAsync(
            getPage,
            response => response.Value,
            response => response.OdataNextLink,
            "calendars",
            cancellationToken).ConfigureAwait(false);
        foreach (var calendar in page.Where(value => !string.IsNullOrWhiteSpace(value.Id)))
        {
            destination.TryAdd(calendar.Id!, calendar);
        }
    }

    private static Task<IReadOnlyList<GraphEvent>> GetAllEventsAsync(
        IMicrosoftGraphApiClient client,
        string locator,
        TimeRangeUtc range,
        CancellationToken cancellationToken) => GetAllPagesAsync(
            next => client.GetCalendarViewPageAsync(locator, range, next, cancellationToken),
            response => response.Value,
            response => response.OdataNextLink,
            "calendarView",
            cancellationToken);

    private static Task<IReadOnlyList<OutlookCategory>> GetAllCategoriesAsync(
        IMicrosoftGraphApiClient client,
        CancellationToken cancellationToken) => GetAllPagesAsync(
            next => client.GetMasterCategoriesPageAsync(next, cancellationToken),
            response => response.Value,
            response => response.OdataNextLink,
            "masterCategories",
            cancellationToken);

    private static async Task<IReadOnlyList<TItem>> GetAllPagesAsync<TResponse, TItem>(
        Func<string?, Task<TResponse?>> getPage,
        Func<TResponse, IList<TItem>?> getItems,
        Func<TResponse, string?> getNextLink,
        string responseName,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var results = new List<TItem>();
        var seenLinks = new HashSet<string>(StringComparer.Ordinal);
        string? nextLink = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await getPage(nextLink).ConfigureAwait(false)
                ?? throw new InvalidDataException($"The Microsoft {responseName} response was empty.");
            var items = getItems(response);
            if (items is not null)
            {
                results.AddRange(items.Where(value => value is not null)!);
            }

            nextLink = NullIfWhiteSpace(getNextLink(response));
            if (nextLink is not null && !seenLinks.Add(nextLink))
            {
                throw new InvalidDataException($"The Microsoft {responseName} next link repeated.");
            }
        }
        while (nextLink is not null);

        return results;
    }

    private static void ValidateMicrosoftAccount(CalendarAccount account)
    {
        if (account.Provider != ProviderKind.Microsoft)
        {
            throw new ArgumentException("The account is not a Microsoft account.", nameof(account));
        }
    }

    private static bool CanFallBackWithoutCategoryColors(ProviderError error) => error.Category is
        ProviderErrorCategory.PermissionDenied
        or ProviderErrorCategory.NotFound
        or ProviderErrorCategory.RateLimited
        or ProviderErrorCategory.Network
        or ProviderErrorCategory.Timeout
        or ProviderErrorCategory.ServerError
        or ProviderErrorCategory.MalformedResponse;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
