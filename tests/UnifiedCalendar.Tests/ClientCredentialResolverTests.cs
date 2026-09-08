using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.App.Services;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class ClientCredentialResolverTests
{
    private const string GoogleClientId = "fixture-google-client-id";
    private const string GoogleClientSecret = "fixture-google-client-secret";
    private const string MicrosoftClientId = "fixture-microsoft-client-id";

    [Fact]
    public void ResolveGoogle_UsesConfigurationThenEmbeddedThenNotConfigured()
    {
        var configurationOnly = Resolve(
            Configuration(
                ("UnifiedCalendar:Google:ClientId", GoogleClientId),
                ("UnifiedCalendar:Google:ClientSecret", GoogleClientSecret)));
        var embeddedOnly = Resolve(
            null,
            (ClientCredentialResolver.GoogleClientIdMetadataKey, GoogleClientId),
            (ClientCredentialResolver.GoogleClientSecretMetadataKey, GoogleClientSecret));
        var both = Resolve(
            Configuration(
                ("UnifiedCalendar:Google:ClientId", "configured-google-id"),
                ("UnifiedCalendar:Google:ClientSecret", "configured-google-secret")),
            (ClientCredentialResolver.GoogleClientIdMetadataKey, "embedded-google-id"),
            (ClientCredentialResolver.GoogleClientSecretMetadataKey, "embedded-google-secret"));
        var neither = Resolve(null);

        Assert.Equal(ClientCredentialSource.Configuration, configurationOnly.GoogleSource);
        Assert.Equal(GoogleClientId, configurationOnly.Google!.ClientId);
        Assert.Equal(GoogleClientSecret, configurationOnly.Google.ClientSecret);
        Assert.Equal(ClientCredentialSource.Embedded, embeddedOnly.GoogleSource);
        Assert.Equal(GoogleClientId, embeddedOnly.Google!.ClientId);
        Assert.Equal(GoogleClientSecret, embeddedOnly.Google.ClientSecret);
        Assert.Equal(ClientCredentialSource.Configuration, both.GoogleSource);
        Assert.Equal("configured-google-id", both.Google!.ClientId);
        Assert.Equal("configured-google-secret", both.Google.ClientSecret);
        Assert.Equal(ClientCredentialSource.NotConfigured, neither.GoogleSource);
        Assert.Null(neither.Google);
    }

    [Fact]
    public void ResolveMicrosoft_UsesConfigurationThenEmbeddedThenNotConfigured()
    {
        var configurationOnly = Resolve(
            Configuration(("UnifiedCalendar:Microsoft:ClientId", MicrosoftClientId)));
        var embeddedOnly = Resolve(
            null,
            (ClientCredentialResolver.MicrosoftClientIdMetadataKey, MicrosoftClientId));
        var both = Resolve(
            Configuration(("UnifiedCalendar:Microsoft:ClientId", "configured-microsoft-id")),
            (ClientCredentialResolver.MicrosoftClientIdMetadataKey, "embedded-microsoft-id"));
        var neither = Resolve(null);

        Assert.Equal(ClientCredentialSource.Configuration, configurationOnly.MicrosoftSource);
        Assert.Equal(MicrosoftClientId, configurationOnly.Microsoft!.ClientId);
        Assert.Equal(ClientCredentialSource.Embedded, embeddedOnly.MicrosoftSource);
        Assert.Equal(MicrosoftClientId, embeddedOnly.Microsoft!.ClientId);
        Assert.Equal(ClientCredentialSource.Configuration, both.MicrosoftSource);
        Assert.Equal("configured-microsoft-id", both.Microsoft!.ClientId);
        Assert.Equal(ClientCredentialSource.NotConfigured, neither.MicrosoftSource);
        Assert.Null(neither.Microsoft);
    }

    [Fact]
    public void ResolveGoogle_DoesNotMixConfigurationAndEmbeddedPairs()
    {
        var clientIdOnlyInConfiguration = Resolve(
            Configuration(("UnifiedCalendar:Google:ClientId", "configured-google-id")),
            (ClientCredentialResolver.GoogleClientIdMetadataKey, "embedded-google-id"),
            (ClientCredentialResolver.GoogleClientSecretMetadataKey, "embedded-google-secret"));
        var clientSecretOnlyInConfiguration = Resolve(
            Configuration(("UnifiedCalendar:Google:ClientSecret", "configured-google-secret")),
            (ClientCredentialResolver.GoogleClientIdMetadataKey, "embedded-google-id"),
            (ClientCredentialResolver.GoogleClientSecretMetadataKey, "embedded-google-secret"));

        Assert.Equal(ClientCredentialSource.NotConfigured, clientIdOnlyInConfiguration.GoogleSource);
        Assert.Null(clientIdOnlyInConfiguration.Google);
        Assert.Equal(ClientCredentialSource.NotConfigured, clientSecretOnlyInConfiguration.GoogleSource);
        Assert.Null(clientSecretOnlyInConfiguration.Google);
    }

    [Fact]
    public void ResolveGoogle_RequiresCompletePairFromEmbeddedMetadata()
    {
        var clientIdOnly = Resolve(
            null,
            (ClientCredentialResolver.GoogleClientIdMetadataKey, GoogleClientId));
        var clientSecretOnly = Resolve(
            null,
            (ClientCredentialResolver.GoogleClientSecretMetadataKey, GoogleClientSecret));
        var completePair = Resolve(
            null,
            (ClientCredentialResolver.GoogleClientIdMetadataKey, GoogleClientId),
            (ClientCredentialResolver.GoogleClientSecretMetadataKey, GoogleClientSecret));

        Assert.Equal(ClientCredentialSource.NotConfigured, clientIdOnly.GoogleSource);
        Assert.Null(clientIdOnly.Google);
        Assert.Equal(ClientCredentialSource.NotConfigured, clientSecretOnly.GoogleSource);
        Assert.Null(clientSecretOnly.Google);
        Assert.Equal(ClientCredentialSource.Embedded, completePair.GoogleSource);
        Assert.Equal(GoogleClientId, completePair.Google!.ClientId);
        Assert.Equal(GoogleClientSecret, completePair.Google.ClientSecret);
    }

    [Fact]
    public void LogSources_LogsEverySourceWithoutCredentialValues()
    {
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var configuredAndEmbedded = Resolve(
            Configuration(
                ("UnifiedCalendar:Google:ClientId", GoogleClientId),
                ("UnifiedCalendar:Google:ClientSecret", GoogleClientSecret)),
            (ClientCredentialResolver.MicrosoftClientIdMetadataKey, MicrosoftClientId));
        var notConfigured = Resolve(null);

        configuredAndEmbedded.LogSources(logger);
        notConfigured.LogSources(logger);

        Assert.Equal(4, sink.Events.Count);
        Assert.Contains(sink.Events, value =>
            PropertyValue(value, "Provider") == "Google"
            && PropertyValue(value, "Source") == nameof(ClientCredentialSource.Configuration));
        Assert.Contains(sink.Events, value =>
            PropertyValue(value, "Provider") == "Microsoft"
            && PropertyValue(value, "Source") == nameof(ClientCredentialSource.Embedded));
        Assert.Equal(
            2,
            sink.Events.Count(value =>
                PropertyValue(value, "Source") == nameof(ClientCredentialSource.NotConfigured)));

        var rendered = string.Join('\n', sink.Events.Select(value => value.RenderMessage()));
        Assert.DoesNotContain(GoogleClientId, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(GoogleClientSecret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(MicrosoftClientId, rendered, StringComparison.Ordinal);
        Assert.All(
            sink.Events,
            value => Assert.Equal(
                ["Provider", "Source"],
                value.Properties.Keys.Order(StringComparer.Ordinal).ToArray()));
    }

    [Fact]
    public void CredentialRecords_DoNotExposeValuesWhenStringifiedOrDestructured()
    {
        var google = new GoogleClientCredentials(GoogleClientId, GoogleClientSecret);
        var microsoft = new MicrosoftClientCredentials(MicrosoftClientId);
        var resolution = new ClientCredentialResolution(
            google,
            ClientCredentialSource.Configuration,
            microsoft,
            ClientCredentialSource.Embedded);
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        var rendered = string.Join(
            '\n',
            new[] { google.ToString(), microsoft.ToString(), resolution.ToString() });
        logger.Information("CredentialSafetyProbe {@Credentials}", resolution);
        rendered += '\n' + Assert.Single(sink.Events).RenderMessage();

        Assert.DoesNotContain(GoogleClientId, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(GoogleClientSecret, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(MicrosoftClientId, rendered, StringComparison.Ordinal);
    }

    private static ClientCredentialResolution Resolve(
        IConfiguration? configuration,
        params (string Key, string Value)[] embeddedValues) =>
        new ClientCredentialResolver(
            configuration,
            CreateAssembly(embeddedValues))
        .Resolve();

    private static IConfiguration Configuration(
        params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                value => value.Key,
                value => (string?)value.Value,
                StringComparer.Ordinal))
            .Build();

    private static Assembly CreateAssembly(
        IEnumerable<(string Key, string Value)> embeddedValues)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"ClientCredentialFixture_{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyMetadataAttribute).GetConstructor(
            [typeof(string), typeof(string)])
            ?? throw new InvalidOperationException("AssemblyMetadataAttribute constructor was not found.");

        foreach (var (key, value) in embeddedValues)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(constructor, [key, value]));
        }

        return assembly;
    }

    private static string? PropertyValue(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value)
            ? value.ToString().Trim('"')
            : null;

    private sealed class CollectingLogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
