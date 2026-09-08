using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Responses;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Infrastructure.Storage;
using UnifiedCalendar.Providers.Google;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class GoogleTokenStoreDataStoreTests
{
    [Fact]
    public async Task GoogleDataStore_RoundTripsThroughPhase2TokenStore()
    {
        var store = new MemoryTokenStore();
        var adapter = new GoogleTokenStoreDataStore(store, GoogleProviderSamples.AccountId);
        var expected = new TokenResponse
        {
            AccessToken = "fixture-access-token",
            RefreshToken = "fixture-refresh-token",
            TokenType = "Bearer",
        };

        await adapter.StoreAsync("fixture-user", expected);
        var actual = await adapter.GetAsync<TokenResponse>("fixture-user");

        Assert.Equal(expected.AccessToken, actual?.AccessToken);
        Assert.Equal(expected.RefreshToken, actual?.RefreshToken);
        Assert.Equal(ProviderKind.Google, store.LastProvider);
        Assert.Equal(GoogleProviderSamples.AccountId, store.LastAccountId);
        await adapter.DeleteAsync<TokenResponse>("fixture-user");
        Assert.Null(await adapter.GetAsync<TokenResponse>("fixture-user"));
    }

    [Fact]
    public async Task GoogleDataStore_WithDpapiCreatesOnlyProtectedTokenEnvelope()
    {
        using var temporary = new TemporaryAppDirectory();
        var tokenStore = new DpapiTokenStore(
            temporary.Paths,
            new AtomicFileWriter(),
            new MutableTimeProvider(StorageSamples.Now));
        var adapter = new GoogleTokenStoreDataStore(tokenStore, GoogleProviderSamples.AccountId);
        const string accessToken = "fixture-google-access-token-plain";
        const string refreshToken = "fixture-google-refresh-token-plain";

        await adapter.StoreAsync("fixture-user", new TokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
        });

        var tokenPath = temporary.Paths.GetTokenFile(ProviderKind.Google, GoogleProviderSamples.AccountId);
        var files = Directory.GetFiles(temporary.RootPath, "*", SearchOption.AllDirectories);
        var envelope = await File.ReadAllTextAsync(
            tokenPath,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        Assert.Single(files);
        Assert.Equal(tokenPath, files[0]);
        Assert.DoesNotContain(accessToken, envelope, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, envelope, StringComparison.Ordinal);
        Assert.Equal(
            refreshToken,
            (await adapter.GetAsync<TokenResponse>("fixture-user"))?.RefreshToken);
    }

    [Fact]
    public async Task Clear_RemovesProviderPayloadThroughTokenStore()
    {
        var store = new MemoryTokenStore();
        var adapter = new GoogleTokenStoreDataStore(store, GoogleProviderSamples.AccountId);
        await adapter.StoreAsync("fixture-user", new TokenResponse { AccessToken = "fixture" });

        await adapter.ClearAsync();

        Assert.Null(store.Payload);
        Assert.Null(await adapter.GetAsync<TokenResponse>("fixture-user"));
    }

    [Fact]
    public async Task ProductionFactory_RestoresSavedTokenWithoutInteractiveAuthorization()
    {
        var store = new MemoryTokenStore();
        var adapter = new GoogleTokenStoreDataStore(store, GoogleProviderSamples.AccountId);
        await adapter.StoreAsync(GoogleProviderSamples.AccountId.ToString("N"), new TokenResponse
        {
            AccessToken = "fixture-access-token",
            RefreshToken = "fixture-refresh-token",
            TokenType = "Bearer",
            ExpiresInSeconds = 315_360_000,
            IssuedUtc = StorageSamples.Now.UtcDateTime,
        });
        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson("{\"items\":[]}");
        var factory = CreateProductionFactory(store, handler);

        using var client = await factory.CreateStoredAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken);
        var calendars = await client.GetCalendarListPageAsync(
            pageToken: null,
            TestContext.Current.CancellationToken);

        Assert.Empty(calendars.Items ?? []);
        Assert.Single(handler.RequestedUris);
        Assert.True(handler.HadAuthorizationHeaders[0]);
        Assert.Contains("/calendar/v3/users/me/calendarList", handler.RequestedUris[0].AbsolutePath, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth", handler.RequestedUris[0].AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedInnerGoogleTokenJson_IsQuarantinedAndRequiresAuthentication()
    {
        using var temporary = new TemporaryAppDirectory();
        var tokenStore = CreateDpapiStore(temporary);
        var userKey = GoogleProviderSamples.AccountId.ToString("N");
        var malformedProviderPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            values = new Dictionary<string, string>
            {
                [$"Google.Apis.Auth.OAuth2.Responses.TokenResponse:{userKey}"] = "{not-json",
            },
        });
        await tokenStore.WriteAsync(
            ProviderKind.Google,
            GoogleProviderSamples.AccountId,
            malformedProviderPayload,
            TestContext.Current.CancellationToken);
        var handler = new SequenceHttpMessageHandler();
        var factory = CreateProductionFactory(tokenStore, handler);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => factory.CreateStoredAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Error.Category);
        Assert.True(exception.Error.ReauthenticationRequired);
        Assert.False(File.Exists(temporary.Paths.GetTokenFile(
            ProviderKind.Google,
            GoogleProviderSamples.AccountId)));
        Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "token-*-provider-*-malformed-provider-payload-*.json"));
        Assert.Empty(handler.RequestedUris);

        var repeated = await Assert.ThrowsAsync<ProviderException>(() => factory.CreateStoredAsync(
            GoogleProviderSamples.Account,
            TestContext.Current.CancellationToken));
        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, repeated.Error.Category);
        Assert.Empty(handler.RequestedUris);
    }

    [Fact]
    public async Task RefreshInvalidGrant_QuarantinesTokenAndPreservesSettingsAndCache()
    {
        using var temporary = new TemporaryAppDirectory();
        var tokenStore = CreateDpapiStore(temporary);
        var adapter = new GoogleTokenStoreDataStore(tokenStore, GoogleProviderSamples.AccountId);
        await adapter.StoreAsync(GoogleProviderSamples.AccountId.ToString("N"), new TokenResponse
        {
            RefreshToken = "fixture-rejected-refresh-token",
            TokenType = "Bearer",
        });
        const string settingsSentinel = "fixture-settings-sentinel";
        const string cacheSentinel = "fixture-cache-sentinel";
        var cachePath = temporary.Paths.GetAccountCacheFile(GoogleProviderSamples.AccountId);
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            settingsSentinel,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            cachePath,
            cacheSentinel,
            TestContext.Current.CancellationToken);
        var handler = new SequenceHttpMessageHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":\"invalid_grant\",\"error_description\":\"fixture rejected credential\"}",
                Encoding.UTF8,
                "application/json"),
        });
        var factory = CreateProductionFactory(tokenStore, handler);
        var provider = new GoogleCalendarProvider(factory, tokenStore);

        var result = await provider.GetEventsAsync(
            GoogleProviderSamples.Account,
            GoogleProviderSamples.Calendar,
            GoogleProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, result.Error?.Category);
        Assert.True(result.Error?.ReauthenticationRequired);
        Assert.Single(handler.RequestedUris);
        Assert.Contains("oauth", handler.RequestedUris[0].AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(temporary.Paths.GetTokenFile(
            ProviderKind.Google,
            GoogleProviderSamples.AccountId)));
        Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "token-*-provider-*-refresh-rejected-*.json"));
        Assert.Equal(settingsSentinel, await File.ReadAllTextAsync(
            temporary.Paths.SettingsFile,
            TestContext.Current.CancellationToken));
        Assert.Equal(cacheSentinel, await File.ReadAllTextAsync(
            cachePath,
            TestContext.Current.CancellationToken));

        var repeated = await provider.GetEventsAsync(
            GoogleProviderSamples.Account,
            GoogleProviderSamples.Calendar,
            GoogleProviderSamples.Range,
            TestContext.Current.CancellationToken);
        Assert.False(repeated.IsSuccess);
        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, repeated.Error?.Category);
        Assert.Single(handler.RequestedUris);
    }

    private static GoogleCalendarApiClientFactory CreateProductionFactory(
        ITokenStore tokenStore,
        SequenceHttpMessageHandler handler) => new(
        tokenStore,
        new GoogleProviderOptions(
            "fixture-client-id.apps.example.test",
            "fixture-public-client-metadata"),
        new MutableTimeProvider(StorageSamples.Now),
        GoogleProviderSamples.CreateHttpFactory(handler));

    private static DpapiTokenStore CreateDpapiStore(TemporaryAppDirectory temporary) => new(
        temporary.Paths,
        new AtomicFileWriter(),
        new MutableTimeProvider(StorageSamples.Now));

}
