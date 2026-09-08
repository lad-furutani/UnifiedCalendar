using Microsoft.Identity.Client;
using NSubstitute;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Microsoft;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class MicrosoftTokenCacheAdapterTests
{
    [Fact]
    public async Task ProductionSilentPath_WithNoSavedAccountRequiresInteractionWithoutQuarantine()
    {
        var store = new MemoryTokenStore();
        var authentication = new MicrosoftAuthenticationClient(
            store,
            new MicrosoftProviderOptions("fixture-client-id"),
            TimeProvider.System);

        await Assert.ThrowsAsync<MsalUiRequiredException>(() => authentication.AcquireSilentAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken));

        Assert.Null(store.LastQuarantineReason);
        Assert.Null(store.Payload);
    }

    [Fact]
    public void AdditionalConsentRequired_IsNotQuarantinedButInvalidGrantIs()
    {
        Assert.False(MicrosoftAuthenticationClient.ShouldQuarantineSilentFailure(
            new MsalUiRequiredException(
                "consent_required",
                "fixture",
                null,
                UiRequiredExceptionClassification.ConsentRequired)));
        Assert.False(MicrosoftAuthenticationClient.ShouldQuarantineSilentFailure(
            new MsalUiRequiredException(
                "invalid_grant",
                "fixture consent classification takes precedence",
                null,
                UiRequiredExceptionClassification.ConsentRequired)));
        Assert.False(MicrosoftAuthenticationClient.ShouldQuarantineSilentFailure(
            new MsalServiceException("interaction_required", "fixture")));
        Assert.True(MicrosoftAuthenticationClient.ShouldQuarantineSilentFailure(
            new MsalUiRequiredException(
                "invalid_grant",
                "fixture",
                null,
                UiRequiredExceptionClassification.AcquireTokenSilentFailed)));
        Assert.True(MicrosoftAuthenticationClient.ShouldQuarantineSilentFailure(
            new MsalServiceException("invalid_grant", "fixture")));
    }

    [Fact]
    public async Task SilentUiRequiredInvalidGrant_QuarantinesAndNextAttemptCannotReusePayload()
    {
        var store = new MemoryTokenStore();
        byte[] existing = [7, 6, 5, 4];
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            existing,
            TestContext.Current.CancellationToken);
        var observedPayloads = new List<byte[]?>();
        var authentication = new MicrosoftAuthenticationClient(
            store,
            new MicrosoftProviderOptions("fixture-client-id"),
            TimeProvider.System,
            (_, _) =>
            {
                observedPayloads.Add(store.Payload?.ToArray());
                return Task.FromException<MicrosoftAuthenticationResult>(new MsalUiRequiredException(
                    "invalid_grant",
                    "fixture revoked refresh token",
                    null,
                    UiRequiredExceptionClassification.AcquireTokenSilentFailed));
            });
        var provider = new MicrosoftCalendarProvider(
            authentication,
            new FixedMicrosoftGraphClientFactory(
                new MicrosoftSequenceHttpMessageHandler(),
                TimeProvider.System),
            store,
            TimeProvider.System);

        var first = await Assert.ThrowsAsync<ProviderException>(() => provider.ListCalendarsAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken));
        var second = await Assert.ThrowsAsync<ProviderException>(() => provider.ListCalendarsAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, first.Error.Category);
        Assert.True(first.Error.ReauthenticationRequired);
        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, second.Error.Category);
        Assert.Equal(TokenQuarantineReason.RefreshRejected, store.LastQuarantineReason);
        Assert.Equal(existing, observedPayloads[0]);
        Assert.Null(observedPayloads[1]);
        Assert.Null(store.Payload);
    }

    [Theory]
    [InlineData("consent_required", UiRequiredExceptionClassification.ConsentRequired)]
    [InlineData("invalid_grant", UiRequiredExceptionClassification.ConsentRequired)]
    [InlineData("consent_required", UiRequiredExceptionClassification.None)]
    [InlineData("interaction_required", UiRequiredExceptionClassification.None)]
    [InlineData("login_required", UiRequiredExceptionClassification.AcquireTokenSilentFailed)]
    public async Task SilentUiRequiredInteractionOnly_PreservesPayloadAndRequiresInteraction(
        string errorCode,
        UiRequiredExceptionClassification classification)
    {
        var store = new MemoryTokenStore();
        byte[] existing = [3, 1, 4, 1];
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            existing,
            TestContext.Current.CancellationToken);
        var authentication = new MicrosoftAuthenticationClient(
            store,
            new MicrosoftProviderOptions("fixture-client-id"),
            TimeProvider.System,
            (_, _) => Task.FromException<MicrosoftAuthenticationResult>(new MsalUiRequiredException(
                errorCode,
                "fixture interaction required",
                null,
                classification)));

        var exception = await Assert.ThrowsAsync<MsalUiRequiredException>(() =>
            authentication.AcquireSilentAsync(
                MicrosoftProviderSamples.Account,
                TestContext.Current.CancellationToken));
        var error = MicrosoftProviderErrorMapper.Map(
            exception,
            TestContext.Current.CancellationToken,
            TimeProvider.System);

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, error.Category);
        Assert.True(error.ReauthenticationRequired);
        Assert.Equal(existing, store.Payload);
        Assert.Null(store.LastQuarantineReason);
    }

    [Fact]
    public async Task ProductionSilentPath_WithMalformedSavedCacheQuarantinesAndNeverUsesItAgain()
    {
        var store = new MemoryTokenStore();
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            new byte[] { 0, 1, 2, 3, 4 },
            TestContext.Current.CancellationToken);
        var authentication = new MicrosoftAuthenticationClient(
            store,
            new MicrosoftProviderOptions("fixture-client-id"),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<MicrosoftTokenCacheCorruptedException>(() => authentication.AcquireSilentAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken));

        var clientException = Assert.IsType<MsalClientException>(exception.InnerException);
        Assert.Equal("json_parse_failed", clientException.ErrorCode);
        Assert.Equal(TokenQuarantineReason.MalformedProviderPayload, store.LastQuarantineReason);
        Assert.Null(store.Payload);
    }

    [Fact]
    public async Task Provider_NormalizesMalformedSavedCacheToAuthenticationRequired()
    {
        var store = new MemoryTokenStore();
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            new byte[] { 0, 1, 2, 3, 4 },
            TestContext.Current.CancellationToken);
        var timeProvider = TimeProvider.System;
        var provider = new MicrosoftCalendarProvider(
            new MicrosoftAuthenticationClient(
                store,
                new MicrosoftProviderOptions("fixture-client-id"),
                timeProvider),
            new FixedMicrosoftGraphClientFactory(new MicrosoftSequenceHttpMessageHandler(), timeProvider),
            store,
            timeProvider);

        var exception = await Assert.ThrowsAsync<ProviderException>(() => provider.ListCalendarsAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken));

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, exception.Error.Category);
        Assert.True(exception.Error.ReauthenticationRequired);
        Assert.Equal(TokenQuarantineReason.MalformedProviderPayload, store.LastQuarantineReason);
    }

    [Fact]
    public async Task CacheCallbacks_RoundTripOpaqueBytesThroughTokenStore()
    {
        var store = new MemoryTokenStore();
        byte[] existing = [1, 2, 3, 4];
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            existing,
            TestContext.Current.CancellationToken);
        var serializer = Substitute.For<ITokenCacheSerializer>();
        byte[] updated = [9, 8, 7, 6];
        serializer.SerializeMsalV3().Returns(updated);
        var adapter = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await adapter.BeforeAccessAsync(CreateArgs(serializer, hasStateChanged: false));
        await adapter.AfterAccessAsync(CreateArgs(serializer, hasStateChanged: true));

        serializer.Received(1).DeserializeMsalV3(
            Arg.Is<byte[]>(value => value.SequenceEqual(existing)),
            true);
        Assert.Equal(updated, store.Payload);
        Assert.Equal(ProviderKind.Microsoft, store.LastProvider);
        Assert.Equal(MicrosoftProviderSamples.AccountId, store.LastAccountId);
    }

    [Fact]
    public async Task CacheCallbacks_ForSameAccountAreSerializedAcrossApplicationInstances()
    {
        var store = new MemoryTokenStore();
        var firstSerializer = Substitute.For<ITokenCacheSerializer>();
        var secondSerializer = Substitute.For<ITokenCacheSerializer>();
        var first = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);
        var second = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await first.BeforeAccessAsync(CreateArgs(firstSerializer, hasStateChanged: false));
        var secondBefore = second.BeforeAccessAsync(CreateArgs(secondSerializer, hasStateChanged: false));

        Assert.False(secondBefore.IsCompleted);
        await first.AfterAccessAsync(CreateArgs(firstSerializer, hasStateChanged: false));
        await secondBefore;
        await second.AfterAccessAsync(CreateArgs(secondSerializer, hasStateChanged: false));
    }

    [Fact]
    public async Task UnchangedCache_IsNotWritten()
    {
        var store = Substitute.For<ITokenStore>();
        var serializer = Substitute.For<ITokenCacheSerializer>();
        var adapter = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await adapter.BeforeAccessAsync(CreateArgs(serializer, hasStateChanged: false));
        await adapter.AfterAccessAsync(CreateArgs(serializer, hasStateChanged: false));

        await store.DidNotReceiveWithAnyArgs().WriteAsync(
            default,
            default,
            default,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MalformedInnerCache_IsQuarantinedAndNotReused()
    {
        var store = new MemoryTokenStore();
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            new byte[] { 99, 88, 77 },
            TestContext.Current.CancellationToken);
        var serializer = Substitute.For<ITokenCacheSerializer>();
        serializer.When(value => value.DeserializeMsalV3(Arg.Any<byte[]>(), true))
            .Do(_ => throw new System.Text.Json.JsonException("fixture malformed JSON"));
        var adapter = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await Assert.ThrowsAsync<MicrosoftTokenCacheCorruptedException>(() =>
            adapter.BeforeAccessAsync(CreateArgs(serializer, hasStateChanged: false)));

        Assert.Null(store.Payload);
        Assert.Equal(TokenQuarantineReason.MalformedProviderPayload, store.LastQuarantineReason);
    }

    [Fact]
    public async Task TokenStoreIOException_IsPropagatedWithoutQuarantine()
    {
        var store = Substitute.For<ITokenStore>();
        store.ReadAsync(
                ProviderKind.Microsoft,
                MicrosoftProviderSamples.AccountId,
                Arg.Any<CancellationToken>())
            .Returns<Task<byte[]?>>(_ => throw new IOException("fixture transient I/O"));
        var adapter = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await Assert.ThrowsAsync<IOException>(() => adapter.BeforeAccessAsync(
            CreateArgs(Substitute.For<ITokenCacheSerializer>(), hasStateChanged: false)));

        await store.DidNotReceiveWithAnyArgs().QuarantineAsync(
            default,
            default,
            default,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_IsPropagatedWithoutQuarantine()
    {
        var store = Substitute.For<ITokenStore>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var adapter = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.BeforeAccessAsync(
            CreateArgs(
                Substitute.For<ITokenCacheSerializer>(),
                hasStateChanged: false,
                cancellation.Token)));

        await store.DidNotReceiveWithAnyArgs().QuarantineAsync(
            default,
            default,
            default,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnexpectedDeserializeFailure_IsPropagatedWithoutQuarantine()
    {
        var store = new MemoryTokenStore();
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            new byte[] { 1, 2, 3 },
            TestContext.Current.CancellationToken);
        var serializer = Substitute.For<ITokenCacheSerializer>();
        serializer.When(value => value.DeserializeMsalV3(Arg.Any<byte[]>(), true))
            .Do(_ => throw new InvalidOperationException("fixture program failure"));
        var adapter = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.BeforeAccessAsync(
            CreateArgs(serializer, hasStateChanged: false)));

        Assert.NotNull(store.Payload);
        Assert.Null(store.LastQuarantineReason);
    }

    [Fact]
    public async Task UnexpectedMsalClientFailure_IsPropagatedWithoutQuarantine()
    {
        var store = new MemoryTokenStore();
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            new byte[] { 1, 2, 3 },
            TestContext.Current.CancellationToken);
        var serializer = Substitute.For<ITokenCacheSerializer>();
        serializer.When(value => value.DeserializeMsalV3(Arg.Any<byte[]>(), true))
            .Do(_ => throw new MsalClientException("unexpected_internal_failure", "fixture program failure"));
        var adapter = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await Assert.ThrowsAsync<MsalClientException>(() => adapter.BeforeAccessAsync(
            CreateArgs(serializer, hasStateChanged: false)));

        Assert.NotNull(store.Payload);
        Assert.Null(store.LastQuarantineReason);
    }

    [Fact]
    public async Task TransientReadFailure_LeavesExistingTokenAvailableForNextAttempt()
    {
        byte[] existing = [4, 3, 2, 1];
        var store = new TransientReadFailureTokenStore(existing);
        var serializer = Substitute.For<ITokenCacheSerializer>();
        var adapter = new MicrosoftTokenCacheAdapter(store, MicrosoftProviderSamples.AccountId);

        await Assert.ThrowsAsync<IOException>(() => adapter.BeforeAccessAsync(
            CreateArgs(serializer, hasStateChanged: false)));
        await adapter.BeforeAccessAsync(CreateArgs(serializer, hasStateChanged: false));
        await adapter.AfterAccessAsync(CreateArgs(serializer, hasStateChanged: false));

        serializer.Received(1).DeserializeMsalV3(
            Arg.Is<byte[]>(value => value.SequenceEqual(existing)),
            true);
        Assert.Equal(existing, store.Payload);
        Assert.Equal(0, store.QuarantineCount);
    }

    [Fact]
    public void ProductionCacheAdapterReferencesNoFileStorageApi()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "UnifiedCalendar.Providers.Microsoft",
            "MicrosoftTokenCacheAdapter.cs"));

        Assert.DoesNotContain("File.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileStream", source, StringComparison.Ordinal);
        Assert.Contains("ITokenStore", source, StringComparison.Ordinal);
        Assert.Contains("SerializeMsalV3", source, StringComparison.Ordinal);
    }

    private static TokenCacheNotificationArgs CreateArgs(
        ITokenCacheSerializer serializer,
        bool hasStateChanged,
        CancellationToken? cancellationToken = null) => new(
        serializer,
        "fixture-client-id",
        account: null,
        hasStateChanged,
        isApplicationCache: false,
        suggestedCacheKey: "fixture-cache",
        hasTokens: true,
        suggestedCacheExpiry: null,
        cancellationToken ?? TestContext.Current.CancellationToken);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UnifiedCalendar.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class TransientReadFailureTokenStore : ITokenStore
    {
        private bool _failNextRead = true;

        public TransientReadFailureTokenStore(byte[] payload)
        {
            Payload = payload.ToArray();
        }

        public byte[]? Payload { get; private set; }

        public int QuarantineCount { get; private set; }

        public Task<byte[]?> ReadAsync(
            ProviderKind provider,
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_failNextRead)
            {
                _failNextRead = false;
                throw new IOException("fixture transient I/O");
            }

            return Task.FromResult(Payload?.ToArray());
        }

        public Task WriteAsync(
            ProviderKind provider,
            Guid internalAccountId,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Payload = payload.ToArray();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            ProviderKind provider,
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Payload = null;
            return Task.CompletedTask;
        }

        public Task QuarantineAsync(
            ProviderKind provider,
            Guid internalAccountId,
            TokenQuarantineReason reason,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuarantineCount++;
            Payload = null;
            return Task.CompletedTask;
        }
    }
}
