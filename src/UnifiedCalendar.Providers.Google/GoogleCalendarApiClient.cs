using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using System.Security.Cryptography;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.Providers.Google;

internal interface IGoogleCalendarApiClientFactory
{
    Task<IGoogleCalendarApiClient> CreateStoredAsync(
        CalendarAccount account,
        CancellationToken cancellationToken);

    Task<IGoogleInteractiveCalendarSession> CreateInteractiveAsync(
        Guid internalAccountId,
        GoogleInteractiveAuthorizationMode mode,
        CancellationToken cancellationToken);
}

internal enum GoogleInteractiveAuthorizationMode
{
    NewAccount,
    Reauthentication,
}

internal interface IGoogleInteractiveCalendarSession : IDisposable
{
    IGoogleCalendarApiClient Client { get; }

    Task CommitTokenAsync(CancellationToken cancellationToken);
}

internal interface IGoogleCalendarApiClient : IDisposable
{
    Task<CalendarList> GetCalendarListPageAsync(
        string? pageToken,
        CancellationToken cancellationToken);

    Task<Events> GetEventsPageAsync(
        string calendarId,
        TimeRangeUtc range,
        string? pageToken,
        CancellationToken cancellationToken);

    Task<Colors> GetColorsAsync(CancellationToken cancellationToken);
}

internal sealed class GoogleCalendarApiClientFactory : IGoogleCalendarApiClientFactory
{
    private readonly ITokenStore _tokenStore;
    private readonly GoogleProviderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly global::Google.Apis.Http.IHttpClientFactory _httpClientFactory;

    public GoogleCalendarApiClientFactory(
        ITokenStore tokenStore,
        GoogleProviderOptions options,
        TimeProvider timeProvider,
        global::Google.Apis.Http.IHttpClientFactory? httpClientFactory = null)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _httpClientFactory = httpClientFactory ?? new global::Google.Apis.Http.HttpClientFactory();
    }

    public async Task<IGoogleCalendarApiClient> CreateStoredAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ValidateGoogleAccount(account);
        cancellationToken.ThrowIfCancellationRequested();

        var store = new GoogleTokenStoreDataStore(_tokenStore, account.InternalAccountId);
        TokenResponse? token;
        try
        {
            token = await store.GetAsync<TokenResponse>(GetUserKey(account.InternalAccountId)).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new ProviderException(new ProviderError(
                ProviderErrorCategory.AuthenticationRequired,
                reauthenticationRequired: true));
        }

        if (token is null || string.IsNullOrWhiteSpace(token.RefreshToken) && string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new ProviderException(new ProviderError(
                ProviderErrorCategory.AuthenticationRequired,
                reauthenticationRequired: true));
        }

        var flow = new PkceGoogleAuthorizationCodeFlow(CreateFlowInitializer(store));
        var credential = new UserCredential(flow, GetUserKey(account.InternalAccountId), token);
        return CreateClient(credential);
    }

    public async Task<IGoogleInteractiveCalendarSession> CreateInteractiveAsync(
        Guid internalAccountId,
        GoogleInteractiveAuthorizationMode mode,
        CancellationToken cancellationToken)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The internal account ID cannot be empty.", nameof(internalAccountId));
        }

        var stagedTokenStore = new GoogleStagedTokenStore(_tokenStore, internalAccountId);
        var store = new GoogleTokenStoreDataStore(stagedTokenStore, internalAccountId);

        try
        {
            using var timeoutSource = new CancellationTokenSource(_options.AuthorizationTimeout, _timeProvider);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            var receiver = new LocalServerCodeReceiver(
                "<html><body>Authorization completed. You may close this window.</body></html>",
                LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp);
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                CreateFlowInitializer(store, mode),
                [GoogleProviderOptions.CalendarReadOnlyScope],
                GetUserKey(internalAccountId),
                usePkce: true,
                linkedSource.Token,
                store,
                receiver).ConfigureAwait(false);
            return new GoogleInteractiveCalendarSession(
                CreateClient(credential),
                stagedTokenStore);
        }
        catch
        {
            stagedTokenStore.Dispose();
            throw;
        }
    }

    internal GoogleAuthorizationCodeFlow.Initializer CreateFlowInitializer(
        GoogleTokenStoreDataStore store,
        GoogleInteractiveAuthorizationMode? mode = null) => new()
    {
        ClientSecrets = new ClientSecrets
        {
            ClientId = _options.ClientId,
            ClientSecret = _options.ClientSecret,
        },
        DataStore = store,
        HttpClientFactory = _httpClientFactory,
        Prompt = mode is GoogleInteractiveAuthorizationMode.NewAccount
            or GoogleInteractiveAuthorizationMode.Reauthentication
            ? "consent"
            : null,
    };

    private IGoogleCalendarApiClient CreateClient(UserCredential credential)
    {
        var service = new CalendarService(new BaseClientService.Initializer
        {
            ApplicationName = _options.ApplicationName,
            HttpClientInitializer = credential,
            HttpClientFactory = _httpClientFactory,
            HttpClientTimeout = _options.ApiTimeout,
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        });
        return new GoogleCalendarApiClient(service, _timeProvider);
    }

    private static string GetUserKey(Guid accountId) => accountId.ToString("N");

    private static void ValidateGoogleAccount(CalendarAccount account)
    {
        if (account.Provider != ProviderKind.Google)
        {
            throw new ArgumentException("The account is not a Google account.", nameof(account));
        }
    }
}

internal sealed class GoogleInteractiveCalendarSession : IGoogleInteractiveCalendarSession
{
    private readonly GoogleStagedTokenStore _stagedTokenStore;
    private bool _disposed;

    public GoogleInteractiveCalendarSession(
        IGoogleCalendarApiClient client,
        GoogleStagedTokenStore stagedTokenStore)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _stagedTokenStore = stagedTokenStore ?? throw new ArgumentNullException(nameof(stagedTokenStore));
    }

    public IGoogleCalendarApiClient Client { get; }

    public Task CommitTokenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _stagedTokenStore.CommitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Client.Dispose();
        _stagedTokenStore.Dispose();
    }
}

internal sealed class GoogleStagedTokenStore : ITokenStore, IDisposable
{
    private readonly ITokenStore _destination;
    private readonly Guid _internalAccountId;
    private byte[]? _payload;
    private bool _disposed;

    public GoogleStagedTokenStore(ITokenStore destination, Guid internalAccountId)
    {
        _destination = destination ?? throw new ArgumentNullException(nameof(destination));
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The internal account ID cannot be empty.", nameof(internalAccountId));
        }

        _internalAccountId = internalAccountId;
    }

    public Task<byte[]?> ReadAsync(
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(provider, internalAccountId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(_payload?.ToArray());
    }

    public Task WriteAsync(
        ProviderKind provider,
        Guid internalAccountId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(provider, internalAccountId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReplacePayload(payload.Span);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(provider, internalAccountId);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClearPayload();
        return Task.CompletedTask;
    }

    public Task QuarantineAsync(
        ProviderKind provider,
        Guid internalAccountId,
        TokenQuarantineReason reason,
        CancellationToken cancellationToken = default) => RemoveAsync(
        provider,
        internalAccountId,
        cancellationToken);

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await ValidateTokenResponseAsync(cancellationToken).ConfigureAwait(false);
        var payload = _payload?.ToArray()
            ?? throw new InvalidDataException("Google OAuth did not produce a token payload.");
        try
        {
            await _destination.WriteAsync(
                ProviderKind.Google,
                _internalAccountId,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private async Task ValidateTokenResponseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var adapter = new GoogleTokenStoreDataStore(this, _internalAccountId);
        var token = await adapter.GetAsync<TokenResponse>(_internalAccountId.ToString("N")).ConfigureAwait(false)
            ?? throw new InvalidDataException("Google OAuth did not produce a token response.");
        if (string.IsNullOrWhiteSpace(token.AccessToken)
            || string.IsNullOrWhiteSpace(token.RefreshToken)
            || token.ExpiresInSeconds is null or <= 0
            || token.IssuedUtc == default
            || !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Google OAuth did not produce a reusable token response.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearPayload();
    }

    private void ReplacePayload(ReadOnlySpan<byte> payload)
    {
        ClearPayload();
        _payload = payload.ToArray();
    }

    private void ClearPayload()
    {
        if (_payload is not null)
        {
            CryptographicOperations.ZeroMemory(_payload);
            _payload = null;
        }
    }

    private void ValidateTarget(ProviderKind provider, Guid internalAccountId)
    {
        if (provider != ProviderKind.Google || internalAccountId != _internalAccountId)
        {
            throw new InvalidOperationException("The staged Google token target does not match its session.");
        }
    }
}

internal sealed class GoogleCalendarApiClient : IGoogleCalendarApiClient
{
    private readonly CalendarService _service;
    private readonly GoogleRetryAfterCaptureHandler _retryAfterCapture;

    public GoogleCalendarApiClient(CalendarService service, TimeProvider? timeProvider = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _retryAfterCapture = new GoogleRetryAfterCaptureHandler(timeProvider ?? TimeProvider.System);
        _service.HttpClient.MessageHandler.AddUnsuccessfulResponseHandler(_retryAfterCapture);
    }

    public Task<CalendarList> GetCalendarListPageAsync(
        string? pageToken,
        CancellationToken cancellationToken)
    {
        var request = _service.CalendarList.List();
        request.ShowHidden = true;
        request.ShowDeleted = false;
        request.PageToken = pageToken;
        return ExecuteAsync(() => request.ExecuteAsync(cancellationToken));
    }

    public Task<Events> GetEventsPageAsync(
        string calendarId,
        TimeRangeUtc range,
        string? pageToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        var request = _service.Events.List(calendarId);
        request.TimeMinDateTimeOffset = range.StartUtc;
        request.TimeMaxDateTimeOffset = range.EndUtc;
        request.SingleEvents = true;
        request.ShowDeleted = false;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
        request.PageToken = pageToken;
        return ExecuteAsync(() => request.ExecuteAsync(cancellationToken));
    }

    public Task<Colors> GetColorsAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => _service.Colors.Get().ExecuteAsync(cancellationToken));

    public void Dispose() => _service.Dispose();

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        _retryAfterCapture.Clear();
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_retryAfterCapture.Take() is { } retryAfter)
            {
                exception.Data["RetryAfter"] = retryAfter;
            }

            throw;
        }
    }
}

internal sealed class GoogleRetryAfterCaptureHandler : IHttpUnsuccessfulResponseHandler
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private TimeSpan? _retryAfter;

    public GoogleRetryAfterCaptureHandler(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var header = args.Response.Headers.RetryAfter;
        TimeSpan? retryAfter = header?.Delta;
        if (!retryAfter.HasValue && header?.Date is { } date)
        {
            retryAfter = date - _timeProvider.GetUtcNow();
        }

        if (retryAfter < TimeSpan.Zero)
        {
            retryAfter = TimeSpan.Zero;
        }

        lock (_gate)
        {
            _retryAfter = retryAfter;
        }

        return Task.FromResult(false);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _retryAfter = null;
        }
    }

    public TimeSpan? Take()
    {
        lock (_gate)
        {
            var value = _retryAfter;
            _retryAfter = null;
            return value;
        }
    }
}
