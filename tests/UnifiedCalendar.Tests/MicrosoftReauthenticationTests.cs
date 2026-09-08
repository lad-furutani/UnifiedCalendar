using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Microsoft;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class MicrosoftReauthenticationTests
{
    [Fact]
    public async Task Reauthentication_WithSameIdentitySucceedsAndPassesExpectedIdentity()
    {
        var authentication = new FixedMicrosoftAuthenticationClient();
        var store = new MemoryTokenStore();
        var provider = CreateProvider(authentication, store);

        var result = await provider.ReauthenticateAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(MicrosoftProviderSamples.AccountId, result.InternalAccountId);
        Assert.Equal(MicrosoftProviderSamples.Account.ProviderSubjectId, result.ProviderSubjectId);
        Assert.Equal(MicrosoftProviderSamples.Account.ProviderSubjectId,
            authentication.LastExpectedProviderSubjectId);
        Assert.Equal(MicrosoftProviderSamples.Account.Email, authentication.LastLoginHint);
    }

    [Fact]
    public async Task Reauthentication_WithDifferentIdentityFailsWithoutReplacingExistingBinding()
    {
        var authentication = new FixedMicrosoftAuthenticationClient(new MicrosoftAuthenticationResult(
            "fixture-other-access-token",
            "fixture-other-home-account",
            "Fixture Other Account",
            "fixture-other@example.test"));
        var store = new MemoryTokenStore();
        byte[] existingCache = [7, 6, 5, 4];
        await store.WriteAsync(
            ProviderKind.Microsoft,
            MicrosoftProviderSamples.AccountId,
            existingCache,
            TestContext.Current.CancellationToken);
        var provider = CreateProvider(authentication, store);

        var result = await provider.ReauthenticateAsync(
            MicrosoftProviderSamples.Account,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, result.Error?.Category);
        Assert.True(result.Error?.ReauthenticationRequired);
        Assert.Equal(existingCache, store.Payload);
        Assert.Equal(MicrosoftProviderSamples.Account.ProviderSubjectId,
            authentication.LastExpectedProviderSubjectId);
    }

    [Fact]
    public async Task NewAccountAuthentication_AllowsAnySelectedIdentity()
    {
        var authentication = new FixedMicrosoftAuthenticationClient(new MicrosoftAuthenticationResult(
            "fixture-new-access-token",
            "fixture-new-home-account",
            "Fixture New Account",
            "fixture-new@example.test"));
        var provider = CreateProvider(authentication, new MemoryTokenStore());

        var result = await provider.AuthenticateAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.InternalAccountId);
        Assert.Equal("fixture-new-home-account", result.ProviderSubjectId);
        Assert.Null(authentication.LastExpectedProviderSubjectId);
        Assert.Null(authentication.LastLoginHint);
    }

    [Fact]
    public async Task TokenCachePolicy_RemovesEveryIdentityExceptSelectedIdentity()
    {
        var removed = new List<string>();
        string[] identities = ["fixture-target", "fixture-other-1", "fixture-other-2"];

        await MicrosoftIdentityCachePolicy.RemoveNonTargetAccountsAsync(
            identities,
            "fixture-target",
            identity => identity,
            (identity, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                removed.Add(identity);
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["fixture-other-1", "fixture-other-2"], removed);
        Assert.DoesNotContain("fixture-target", removed);
    }

    private static MicrosoftCalendarProvider CreateProvider(
        IMicrosoftAuthenticationClient authentication,
        MemoryTokenStore tokenStore) => new(
        authentication,
        new FixedMicrosoftGraphClientFactory(
            new MicrosoftSequenceHttpMessageHandler(),
            TimeProvider.System),
        tokenStore,
        TimeProvider.System);
}
