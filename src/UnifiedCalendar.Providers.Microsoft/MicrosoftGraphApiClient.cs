using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using UnifiedCalendar.Core.Providers;
using GraphCalendar = Microsoft.Graph.Models.Calendar;
using GraphEvent = Microsoft.Graph.Models.Event;

namespace UnifiedCalendar.Providers.Microsoft;

internal interface IMicrosoftGraphApiClient : IDisposable
{
    Task<CalendarCollectionResponse?> GetCalendarsPageAsync(string? nextLink, CancellationToken cancellationToken);

    Task<EventCollectionResponse?> GetCalendarViewPageAsync(
        string calendarId,
        TimeRangeUtc range,
        string? nextLink,
        CancellationToken cancellationToken);

    Task<OutlookCategoryCollectionResponse?> GetMasterCategoriesPageAsync(
        string? nextLink,
        CancellationToken cancellationToken);
}

internal interface IMicrosoftGraphApiClientFactory
{
    IMicrosoftGraphApiClient Create(string accessToken);
}

internal sealed class MicrosoftGraphApiClientFactory : IMicrosoftGraphApiClientFactory
{
    private readonly IHttpMessageHandlerFactory _handlerFactory;
    private readonly MicrosoftProviderOptions _options;
    private readonly TimeProvider _timeProvider;

    public MicrosoftGraphApiClientFactory(
        IHttpMessageHandlerFactory handlerFactory,
        MicrosoftProviderOptions options,
        TimeProvider timeProvider)
    {
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public IMicrosoftGraphApiClient Create(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var responseCapture = new MicrosoftGraphResponseCaptureHandler(
            _handlerFactory.CreateHandler(MicrosoftProviderOptions.HttpClientName),
            _timeProvider);
        var httpClient = new HttpClient(responseCapture, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var graph = new GraphServiceClient(
            httpClient,
            new FixedAccessTokenAuthenticationProvider(accessToken),
            MicrosoftProviderOptions.GraphBaseUrl);
        return new MicrosoftGraphApiClient(
            graph,
            httpClient,
            _options.ApiTimeout,
            _timeProvider,
            responseCapture);
    }
}

internal sealed class MicrosoftGraphApiClient : IMicrosoftGraphApiClient
{
    private const string ImmutableIdPreference = "IdType=\"ImmutableId\"";
    private const string CalendarViewPreferences =
        "IdType=\"ImmutableId\", outlook.timezone=\"UTC\", outlook.body-content-type=\"text\"";

    private readonly GraphServiceClient _client;
    private readonly HttpClient? _ownedHttpClient;
    private readonly TimeSpan _apiTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly MicrosoftGraphResponseCaptureHandler? _responseCapture;

    internal MicrosoftGraphApiClient(
        GraphServiceClient client,
        HttpClient? ownedHttpClient,
        TimeSpan apiTimeout,
        TimeProvider timeProvider,
        MicrosoftGraphResponseCaptureHandler? responseCapture = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownedHttpClient = ownedHttpClient;
        _apiTimeout = apiTimeout;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _responseCapture = responseCapture;
    }

    public Task<CalendarCollectionResponse?> GetCalendarsPageAsync(
        string? nextLink,
        CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        if (!string.IsNullOrWhiteSpace(nextLink))
        {
            return await _client.Me.Calendars.WithUrl(ValidateNextLink(nextLink)).GetAsync(
                configuration => configuration.Headers.Add("Prefer", ImmutableIdPreference),
                token).ConfigureAwait(false);
        }

        return await _client.Me.Calendars.GetAsync(configuration =>
        {
            configuration.Headers.Add("Prefer", ImmutableIdPreference);
            configuration.QueryParameters.Top = 100;
        }, token).ConfigureAwait(false);
    }, cancellationToken);

    public Task<EventCollectionResponse?> GetCalendarViewPageAsync(
        string calendarId,
        TimeRangeUtc range,
        string? nextLink,
        CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        if (!string.IsNullOrWhiteSpace(nextLink))
        {
            return await _client.Me.Calendars[calendarId].CalendarView.WithUrl(ValidateNextLink(nextLink)).GetAsync(
                configuration => configuration.Headers.Add("Prefer", CalendarViewPreferences),
                token).ConfigureAwait(false);
        }

        return await _client.Me.Calendars[calendarId].CalendarView.GetAsync(configuration =>
        {
            configuration.Headers.Add("Prefer", CalendarViewPreferences);
            configuration.QueryParameters.StartDateTime = range.StartUtc.ToString("O");
            configuration.QueryParameters.EndDateTime = range.EndUtc.ToString("O");
            configuration.QueryParameters.Top = 100;
        }, token).ConfigureAwait(false);
    }, cancellationToken);

    public Task<OutlookCategoryCollectionResponse?> GetMasterCategoriesPageAsync(
        string? nextLink,
        CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        if (!string.IsNullOrWhiteSpace(nextLink))
        {
            return await _client.Me.Outlook.MasterCategories.WithUrl(ValidateNextLink(nextLink)).GetAsync(cancellationToken: token)
                .ConfigureAwait(false);
        }

        return await _client.Me.Outlook.MasterCategories.GetAsync(
            configuration => configuration.QueryParameters.Top = 100,
            token).ConfigureAwait(false);
    }, cancellationToken);

    public void Dispose()
    {
        _ownedHttpClient?.Dispose();
    }

    private async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_apiTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await action(linked.Token).ConfigureAwait(false);
        }
        catch (Exception) when (_responseCapture?.LastStatusCode is >= 400 and <= 599)
        {
            var statusCode = _responseCapture.LastStatusCode!.Value;
            throw new ProviderException(MicrosoftProviderErrorMapper.FromStatusCode(
                statusCode,
                _responseCapture.RetryAfter));
        }
        catch (Exception exception) when (
            _responseCapture?.LastStatusCode is >= 200 and <= 299
            && exception is not OperationCanceledException
            && exception is not HttpRequestException)
        {
            throw new InvalidDataException("The Microsoft Graph response was malformed.", exception);
        }
    }

    private static string ValidateNextLink(string nextLink)
    {
        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Microsoft Graph next link was invalid.");
        }

        return uri.AbsoluteUri;
    }
}

internal sealed class MicrosoftGraphResponseCaptureHandler : DelegatingHandler
{
    private readonly TimeProvider _timeProvider;

    public MicrosoftGraphResponseCaptureHandler(HttpMessageHandler innerHandler, TimeProvider timeProvider)
        : base(innerHandler)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public int? LastStatusCode { get; private set; }

    public TimeSpan? RetryAfter { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastStatusCode = null;
        RetryAfter = null;
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        LastStatusCode = (int)response.StatusCode;
        var retry = response.Headers.RetryAfter;
        if (retry?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            RetryAfter = delta;
        }
        else if (retry?.Date is { } date)
        {
            var delay = date - _timeProvider.GetUtcNow();
            RetryAfter = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return response;
    }
}

internal sealed class FixedAccessTokenAuthenticationProvider : IAuthenticationProvider
{
    private readonly string _accessToken;

    public FixedAccessTokenAuthenticationProvider(string accessToken)
    {
        _accessToken = accessToken;
    }

    public Task AuthenticateRequestAsync(
        RequestInformation request,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request.Headers.Add("Authorization", $"Bearer {_accessToken}");
        return Task.CompletedTask;
    }
}
