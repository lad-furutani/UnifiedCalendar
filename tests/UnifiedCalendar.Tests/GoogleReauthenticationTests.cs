using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Google;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class GoogleReauthenticationTests
{
    [Fact]
    public void ReauthenticationAuthorizationUrl_RequiresConsentAndPreservesReadOnlyScope()
    {
        var store = new MemoryTokenStore();
        var factory = CreateProductionFactory(store, new SequenceHttpMessageHandler());
        var adapter = new GoogleTokenStoreDataStore(store, GoogleProviderSamples.AccountId);
        var initializer = factory.CreateFlowInitializer(
            adapter,
            GoogleInteractiveAuthorizationMode.Reauthentication);
        initializer.Scopes = [GoogleProviderOptions.CalendarReadOnlyScope];
        var flow = new PkceGoogleAuthorizationCodeFlow(initializer);

        var authorizationUri = flow
            .CreateAuthorizationCodeRequest("http://127.0.0.1:54321/authorize/")
            .Build();
        var query = ParseQuery(authorizationUri);

        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Contains(
            GoogleProviderOptions.CalendarReadOnlyScope,
            query["scope"].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("code", query["response_type"]);
        Assert.DoesNotContain("calendar.events", query["scope"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewAccountAuthorization_RequiresConsentAndOfflineAccess()
    {
        var store = new MemoryTokenStore();
        var factory = CreateProductionFactory(store, new SequenceHttpMessageHandler());
        var adapter = new GoogleTokenStoreDataStore(store, GoogleProviderSamples.AccountId);

        var initializer = factory.CreateFlowInitializer(
            adapter,
            GoogleInteractiveAuthorizationMode.NewAccount);
        initializer.Scopes = [GoogleProviderOptions.CalendarReadOnlyScope];
        var flow = new PkceGoogleAuthorizationCodeFlow(initializer);

        var authorizationUri = flow
            .CreateAuthorizationCodeRequest("http://127.0.0.1:54321/authorize/")
            .Build();
        var query = ParseQuery(authorizationUri);

        Assert.Equal("consent", query["prompt"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Contains(
            GoogleProviderOptions.CalendarReadOnlyScope,
            query["scope"].Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task SameIdentityWithReusableToken_SucceedsAndCommitsNewToken()
    {
        var store = await CreateStoreWithOldTokenAsync();
        var replacement = CreateToken("fixture-new-access-token", "fixture-new-refresh-token");
        var factory = new InteractiveGoogleClientFactory((accountId, _, cancellationToken) =>
            CreateSessionAsync(
                store,
                accountId,
                GoogleProviderSamples.Account.ProviderSubjectId,
                replacement,
                cancellationToken));
        var provider = new GoogleCalendarProvider(factory, store);

        var result = await provider.ReauthenticateAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(GoogleProviderSamples.Account.InternalAccountId, result.InternalAccountId);
        Assert.Equal(GoogleProviderSamples.Account.ProviderSubjectId, result.ProviderSubjectId);
        Assert.Equal(GoogleInteractiveAuthorizationMode.Reauthentication, factory.LastMode);
        Assert.Equal(1, factory.CommitCount);
        var committed = await ReadTokenAsync(store, GoogleProviderSamples.AccountId);
        Assert.NotNull(committed);
        Assert.Equal(replacement.AccessToken, committed.AccessToken);
        Assert.Equal(replacement.RefreshToken, committed.RefreshToken);
        Assert.Equal(replacement.ExpiresInSeconds, committed.ExpiresInSeconds);
        Assert.Equal(replacement.TokenType, committed.TokenType);
    }

    [Fact]
    public async Task SameIdentityWithoutRefreshToken_FailsPreservesOldTokenAndAllowsSilentReuse()
    {
        var store = await CreateStoreWithOldTokenAsync();
        var oldTokenPayload = store.Payload!.ToArray();
        var incomplete = CreateToken("fixture-short-lived-access-token", refreshToken: null);
        var factory = new InteractiveGoogleClientFactory((accountId, _, cancellationToken) =>
            CreateSessionAsync(
                store,
                accountId,
                GoogleProviderSamples.Account.ProviderSubjectId,
                incomplete,
                cancellationToken));
        var provider = new GoogleCalendarProvider(factory, store);

        var result = await provider.ReauthenticateAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.MalformedResponse, result.Error?.Category);
        Assert.Equal(0, factory.CommitCount);
        Assert.Equal(oldTokenPayload, store.Payload);
        var preserved = await ReadTokenAsync(store, GoogleProviderSamples.AccountId);
        Assert.Equal("fixture-old-refresh-token", preserved?.RefreshToken);

        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson("""
            {"items":[{
              "id":"fixture-subject",
              "summary":"Fixture Primary",
              "primary":true,
              "accessRole":"owner"
            }]}
            """);
        var silentProvider = new GoogleCalendarProvider(CreateProductionFactory(store, handler), store);
        var calendars = await silentProvider.ListCalendarsAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.Single(calendars);
        Assert.Single(handler.RequestedUris);
        Assert.True(handler.HadAuthorizationHeaders[0]);
    }

    [Fact]
    public async Task DifferentIdentityWithReusableToken_FailsAndPreservesAccountAndOldToken()
    {
        var store = await CreateStoreWithOldTokenAsync();
        var oldToken = store.Payload!.ToArray();
        var existingAccount = GoogleProviderSamples.Account;
        var factory = new InteractiveGoogleClientFactory((accountId, _, cancellationToken) =>
            CreateSessionAsync(
                store,
                accountId,
                "fixture-different-google-identity",
                CreateToken("fixture-other-access-token", "fixture-other-refresh-token"),
                cancellationToken));
        var provider = new GoogleCalendarProvider(factory, store);

        var result = await provider.ReauthenticateAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, result.Error?.Category);
        Assert.True(result.Error?.ReauthenticationRequired);
        Assert.Equal(GoogleProviderSamples.AccountId, existingAccount.InternalAccountId);
        Assert.Equal("fixture-subject", existingAccount.ProviderSubjectId);
        Assert.Equal(oldToken, store.Payload);
        Assert.Equal(0, factory.CommitCount);
    }

    [Fact]
    public async Task CancelledReauthentication_PreservesOldToken()
    {
        var store = await CreateStoreWithOldTokenAsync();
        var oldToken = store.Payload!.ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new InteractiveGoogleClientFactory((_, _, token) =>
            Task.FromCanceled<IGoogleInteractiveCalendarSession>(token));
        var provider = new GoogleCalendarProvider(factory, store);

        var result = await provider.ReauthenticateAsync(
            GoogleProviderSamples.Account,
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.Cancelled, result.Error?.Category);
        Assert.Equal(oldToken, store.Payload);
        Assert.Equal(0, factory.CommitCount);
    }

    [Fact]
    public async Task FailedReauthentication_PreservesOldToken()
    {
        var store = await CreateStoreWithOldTokenAsync();
        var oldToken = store.Payload!.ToArray();
        var factory = new InteractiveGoogleClientFactory((_, _, _) =>
            Task.FromException<IGoogleInteractiveCalendarSession>(
                new HttpRequestException("fixture OAuth network failure")));
        var provider = new GoogleCalendarProvider(factory, store);

        var result = await provider.ReauthenticateAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.Network, result.Error?.Category);
        Assert.Equal(oldToken, store.Payload);
        Assert.Equal(0, factory.CommitCount);
    }

    [Fact]
    public async Task NewAccountAuthentication_AllowsAnyIdentityAndCommitsReusableToken()
    {
        var store = new MemoryTokenStore();
        const string selectedIdentity = "fixture-new-google-identity";
        var factory = new InteractiveGoogleClientFactory((accountId, _, cancellationToken) =>
            CreateSessionAsync(
                store,
                accountId,
                selectedIdentity,
                CreateToken("fixture-account-access-token", "fixture-account-refresh-token"),
                cancellationToken));
        var provider = new GoogleCalendarProvider(factory, store);

        var result = await provider.AuthenticateAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(selectedIdentity, result.ProviderSubjectId);
        Assert.NotEqual(Guid.Empty, result.InternalAccountId);
        Assert.Equal(result.InternalAccountId, store.LastAccountId);
        Assert.Equal(GoogleInteractiveAuthorizationMode.NewAccount, factory.LastMode);
        Assert.Equal(1, factory.CommitCount);
        Assert.NotNull(await ReadTokenAsync(store, result.InternalAccountId));
    }

    [Fact]
    public async Task NewAccountWithoutRefreshToken_FailsAndDoesNotPersistIncompleteToken()
    {
        var store = new MemoryTokenStore();
        const string selectedIdentity = "fixture-consented-google-identity";
        var factory = new InteractiveGoogleClientFactory((accountId, _, cancellationToken) =>
            CreateSessionAsync(
                store,
                accountId,
                selectedIdentity,
                CreateToken("fixture-access-token-without-refresh", refreshToken: null),
                cancellationToken));
        var provider = new GoogleCalendarProvider(factory, store);

        var result = await provider.AuthenticateAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.MalformedResponse, result.Error?.Category);
        Assert.Equal(GoogleInteractiveAuthorizationMode.NewAccount, factory.LastMode);
        Assert.Equal(0, factory.CommitCount);
        Assert.Null(store.Payload);
    }

    [Fact]
    public async Task StagedTokenStore_ValidatesTokenResponseBeforeReplacingExistingToken()
    {
        var store = await CreateStoreWithOldTokenAsync();
        var oldToken = store.Payload!.ToArray();
        var replacement = CreateToken("fixture-staged-access-token", "fixture-staged-refresh-token");
        using var staged = new GoogleStagedTokenStore(store, GoogleProviderSamples.AccountId);
        var adapter = new GoogleTokenStoreDataStore(staged, GoogleProviderSamples.AccountId);

        await adapter.StoreAsync(GoogleProviderSamples.AccountId.ToString("N"), replacement);
        Assert.Equal(oldToken, store.Payload);

        await staged.CommitAsync(TestContext.Current.CancellationToken);

        var committed = await ReadTokenAsync(store, GoogleProviderSamples.AccountId);
        Assert.Equal(replacement.AccessToken, committed?.AccessToken);
        Assert.Equal(replacement.RefreshToken, committed?.RefreshToken);
        Assert.Equal(replacement.ExpiresInSeconds, committed?.ExpiresInSeconds);
        Assert.Equal(replacement.TokenType, committed?.TokenType);
    }

    private static async Task<IGoogleInteractiveCalendarSession> CreateSessionAsync(
        ITokenStore destination,
        Guid accountId,
        string primaryIdentity,
        TokenResponse token,
        CancellationToken cancellationToken)
    {
        var staged = new GoogleStagedTokenStore(destination, accountId);
        try
        {
            var adapter = new GoogleTokenStoreDataStore(staged, accountId);
            await adapter.StoreAsync(accountId.ToString("N"), token);
            cancellationToken.ThrowIfCancellationRequested();
            var handler = new SequenceHttpMessageHandler();
            handler.EnqueueJson($$"""
                {"items":[{
                  "id":"{{primaryIdentity}}",
                  "summary":"Fixture Primary",
                  "primary":true,
                  "accessRole":"owner"
                }]}
                """);
            return new GoogleInteractiveCalendarSession(
                GoogleProviderSamples.CreateHttpClient(handler),
                staged);
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    private static async Task<MemoryTokenStore> CreateStoreWithOldTokenAsync()
    {
        var store = new MemoryTokenStore();
        var adapter = new GoogleTokenStoreDataStore(store, GoogleProviderSamples.AccountId);
        await adapter.StoreAsync(
            GoogleProviderSamples.AccountId.ToString("N"),
            CreateToken("fixture-old-access-token", "fixture-old-refresh-token"));
        return store;
    }

    private static Task<TokenResponse?> ReadTokenAsync(ITokenStore store, Guid accountId) =>
        new GoogleTokenStoreDataStore(store, accountId)
            .GetAsync<TokenResponse>(accountId.ToString("N"));

    private static TokenResponse CreateToken(string accessToken, string? refreshToken) => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        ExpiresInSeconds = 3600,
        IssuedUtc = new DateTime(2099, 8, 29, 0, 0, 0, DateTimeKind.Utc),
        TokenType = "Bearer",
        Scope = GoogleProviderOptions.CalendarReadOnlyScope,
    };

    private static GoogleCalendarApiClientFactory CreateProductionFactory(
        ITokenStore tokenStore,
        SequenceHttpMessageHandler handler) => new(
        tokenStore,
        new GoogleProviderOptions(
            "fixture-client-id.apps.example.test",
            "fixture-public-client-metadata"),
        TimeProvider.System,
        GoogleProviderSamples.CreateHttpFactory(handler));

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri) => uri.Query
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => value.Split('=', 2))
        .ToDictionary(
            value => Uri.UnescapeDataString(value[0]),
            value => value.Length == 2 ? Uri.UnescapeDataString(value[1].Replace('+', ' ')) : string.Empty,
            StringComparer.Ordinal);

    private sealed class InteractiveGoogleClientFactory : IGoogleCalendarApiClientFactory
    {
        private readonly Func<
            Guid,
            GoogleInteractiveAuthorizationMode,
            CancellationToken,
            Task<IGoogleInteractiveCalendarSession>> _create;

        public InteractiveGoogleClientFactory(
            Func<
                Guid,
                GoogleInteractiveAuthorizationMode,
                CancellationToken,
                Task<IGoogleInteractiveCalendarSession>> create)
        {
            _create = create;
        }

        public int CommitCount { get; private set; }

        public GoogleInteractiveAuthorizationMode? LastMode { get; private set; }

        public Task<IGoogleCalendarApiClient> CreateStoredAsync(
            CalendarAccount account,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<IGoogleInteractiveCalendarSession> CreateInteractiveAsync(
            Guid internalAccountId,
            GoogleInteractiveAuthorizationMode mode,
            CancellationToken cancellationToken)
        {
            LastMode = mode;
            var session = await _create(internalAccountId, mode, cancellationToken);
            return new CommitCountingSession(session, () => CommitCount++);
        }
    }

    private sealed class CommitCountingSession : IGoogleInteractiveCalendarSession
    {
        private readonly IGoogleInteractiveCalendarSession _inner;
        private readonly Action _committed;

        public CommitCountingSession(IGoogleInteractiveCalendarSession inner, Action committed)
        {
            _inner = inner;
            _committed = committed;
        }

        public IGoogleCalendarApiClient Client => _inner.Client;

        public async Task CommitTokenAsync(CancellationToken cancellationToken)
        {
            await _inner.CommitTokenAsync(cancellationToken);
            _committed();
        }

        public void Dispose() => _inner.Dispose();
    }
}
