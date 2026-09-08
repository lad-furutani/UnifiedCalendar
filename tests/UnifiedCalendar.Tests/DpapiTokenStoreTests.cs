using System.Text;
using System.Text.Json.Nodes;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class DpapiTokenStoreTests
{
    [Fact]
    public async Task CurrentUserDpapi_RoundTripsOpaqueProviderPayload()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        byte[] payload = [0, 255, 3, 128, 42, 17, 99];

        await store.WriteAsync(
            ProviderKind.Microsoft,
            StorageSamples.MicrosoftAccountId,
            payload,
            TestContext.Current.CancellationToken);
        var actual = await store.ReadAsync(
            ProviderKind.Microsoft,
            StorageSamples.MicrosoftAccountId,
            TestContext.Current.CancellationToken);

        Assert.Equal(payload, actual);
    }

    [Fact]
    public async Task TokenEnvelopeContainsProtectionMetadataButNoPlaintextTokens()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        const string accessToken = "access-token-plain-fixture-123";
        const string refreshToken = "refresh-token-plain-fixture-456";
        const string authorizationCode = "authorization-code-plain-fixture-789";
        var payload = $"{{\"accessToken\":\"{accessToken}\",\"refreshToken\":\"{refreshToken}\",\"authorizationCode\":\"{authorizationCode}\"}}";

        await store.WriteTextAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            payload,
            TestContext.Current.CancellationToken);

        var path = temporary.Paths.GetTokenFile(ProviderKind.Google, StorageSamples.GoogleAccountId);
        var envelopeText = await File.ReadAllTextAsync(
            path,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        Assert.Contains("\"schemaVersion\": 1", envelopeText, StringComparison.Ordinal);
        Assert.Contains("\"protectionVersion\": 1", envelopeText, StringComparison.Ordinal);
        Assert.Contains("\"protectionKind\": \"dpapiCurrentUser\"", envelopeText, StringComparison.Ordinal);
        Assert.Contains("\"provider\": \"google\"", envelopeText, StringComparison.Ordinal);
        var envelope = JsonNode.Parse(envelopeText)!;
        Assert.Equal("2026-08-28T05:12:34.0000000Z", envelope["updatedAtUtc"]!.GetValue<string>());
        Assert.DoesNotContain(accessToken, envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain(refreshToken, envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain(authorizationCode, envelopeText, StringComparison.Ordinal);
        Assert.Equal(
            payload,
            await store.ReadTextAsync(
                ProviderKind.Google,
                StorageSamples.GoogleAccountId,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidCiphertext_IsQuarantinedAndReturnsNoToken()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var path = temporary.Paths.GetTokenFile(ProviderKind.Google, StorageSamples.GoogleAccountId);
        await store.WriteTextAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            "secret",
            TestContext.Current.CancellationToken);
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(
            path,
            Encoding.UTF8,
            TestContext.Current.CancellationToken))!;
        envelope["protectedPayload"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        await File.WriteAllTextAsync(
            path,
            envelope.ToJsonString(),
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        var actual = await store.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "token-*-decrypt-*.json"));
    }

    [Fact]
    public async Task UnknownProtectionVersion_IsQuarantinedWithoutAttemptingDecryption()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var path = temporary.Paths.GetTokenFile(ProviderKind.Google, StorageSamples.GoogleAccountId);
        await store.WriteTextAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            "secret",
            TestContext.Current.CancellationToken);
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(
            path,
            Encoding.UTF8,
            TestContext.Current.CancellationToken))!;
        envelope["protectionVersion"] = 2;
        await File.WriteAllTextAsync(
            path,
            envelope.ToJsonString(),
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        var actual = await store.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "token-*-protection-*-protection-v2-*.json"));
    }

    [Fact]
    public async Task MalformedEnvelope_IsQuarantinedWithoutReturningData()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var path = temporary.Paths.GetTokenFile(ProviderKind.Google, StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":1,\"protectionVersion\":1,\"protectionKind\":\"other\"}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        Assert.Null(await store.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "token-*-corrupt-*.json"));
    }

    [Theory]
    [InlineData("protectionKind")]
    [InlineData("provider")]
    [InlineData("updatedAtUtc")]
    [InlineData("protectedPayload")]
    public async Task MissingRequiredEnvelopeProperty_IsQuarantinedAsInvalid(string propertyName)
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var path = temporary.Paths.GetTokenFile(ProviderKind.Google, StorageSamples.GoogleAccountId);
        await store.WriteTextAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            "opaque-provider-payload",
            TestContext.Current.CancellationToken);
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(
            path,
            Encoding.UTF8,
            TestContext.Current.CancellationToken))!.AsObject();
        Assert.True(envelope.Remove(propertyName));
        await File.WriteAllTextAsync(
            path,
            envelope.ToJsonString(),
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        var actual = await store.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "token-*-corrupt-*.json"));
    }

    [Fact]
    public async Task RemoveToken_DeletesEnvelopeAndAllRecoveryCopiesForThatAccountOnly()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var googlePath = temporary.Paths.GetTokenFile(ProviderKind.Google, StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            googlePath,
            "broken",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        Assert.Null(await store.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken));
        await store.WriteTextAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            "google-secret",
            TestContext.Current.CancellationToken);
        await store.WriteTextAsync(
            ProviderKind.Microsoft,
            StorageSamples.MicrosoftAccountId,
            "microsoft-secret",
            TestContext.Current.CancellationToken);

        await store.RemoveAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(await store.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken));
        Assert.Equal(
            "microsoft-secret",
            await store.ReadTextAsync(
                ProviderKind.Microsoft,
                StorageSamples.MicrosoftAccountId,
                TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "token-*.json"));
    }

    [Theory]
    [InlineData(TokenQuarantineReason.MalformedProviderPayload, "malformed-provider-payload")]
    [InlineData(TokenQuarantineReason.RefreshRejected, "refresh-rejected")]
    public async Task ProviderRejectedToken_IsQuarantinedAndCannotBeReadAgain(
        TokenQuarantineReason reason,
        string expectedDetail)
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        await store.WriteTextAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            "fixture-provider-token-payload",
            TestContext.Current.CancellationToken);

        await store.QuarantineAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            reason,
            TestContext.Current.CancellationToken);

        Assert.Null(await store.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            $"token-*-provider-*-{expectedDetail}-*.json"));
    }

    private static DpapiTokenStore CreateStore(TemporaryAppDirectory temporary) => new(
        temporary.Paths,
        new AtomicFileWriter(),
        new MutableTimeProvider(StorageSamples.Now));
}
