using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace UnifiedCalendar.App.Services;

internal enum ClientCredentialSource
{
    Configuration,
    Embedded,
    NotConfigured,
}

internal sealed record GoogleClientCredentials
{
    internal GoogleClientCredentials(string clientId, string clientSecret)
    {
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    internal string ClientId { get; }

    internal string ClientSecret { get; }

    public override string ToString() => nameof(GoogleClientCredentials);
}

internal sealed record MicrosoftClientCredentials
{
    internal MicrosoftClientCredentials(string clientId)
    {
        ClientId = clientId;
    }

    internal string ClientId { get; }

    public override string ToString() => nameof(MicrosoftClientCredentials);
}

internal sealed record ClientCredentialResolution
{
    internal ClientCredentialResolution(
        GoogleClientCredentials? google,
        ClientCredentialSource googleSource,
        MicrosoftClientCredentials? microsoft,
        ClientCredentialSource microsoftSource)
    {
        Google = google;
        GoogleSource = googleSource;
        Microsoft = microsoft;
        MicrosoftSource = microsoftSource;
    }

    internal GoogleClientCredentials? Google { get; }

    internal ClientCredentialSource GoogleSource { get; }

    internal MicrosoftClientCredentials? Microsoft { get; }

    internal ClientCredentialSource MicrosoftSource { get; }

    public void LogSources(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        logger.Information(
            "OAuthClientCredentialsResolved {Provider} {Source}",
            "Google",
            GoogleSource.ToString());
        logger.Information(
            "OAuthClientCredentialsResolved {Provider} {Source}",
            "Microsoft",
            MicrosoftSource.ToString());
    }

    public override string ToString() =>
        $"{nameof(ClientCredentialResolution)} {{ "
        + $"GoogleSource = {GoogleSource}, MicrosoftSource = {MicrosoftSource} }}";
}

internal sealed class ClientCredentialResolver
{
    public const string GoogleClientIdMetadataKey = "GoogleClientId";
    public const string GoogleClientSecretMetadataKey = "GoogleClientSecret";
    public const string MicrosoftClientIdMetadataKey = "MicrosoftClientId";

    private const string GoogleClientIdConfigurationKey =
        "UnifiedCalendar:Google:ClientId";
    private const string GoogleClientSecretConfigurationKey =
        "UnifiedCalendar:Google:ClientSecret";
    private const string MicrosoftClientIdConfigurationKey =
        "UnifiedCalendar:Microsoft:ClientId";

    private readonly IConfiguration? _configuration;
    private readonly IReadOnlyDictionary<string, string?> _embeddedValues;

    public ClientCredentialResolver(IConfiguration? configuration)
        : this(configuration, typeof(ClientCredentialResolver).Assembly)
    {
    }

    internal ClientCredentialResolver(
        IConfiguration? configuration,
        Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        _configuration = configuration;
        _embeddedValues = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(attribute => attribute.Key is not null)
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value,
                StringComparer.Ordinal);
    }

    public ClientCredentialResolution Resolve()
    {
        var (google, googleSource) = ResolveGoogle();
        var (microsoft, microsoftSource) = ResolveMicrosoft();

        return new ClientCredentialResolution(
            google,
            googleSource,
            microsoft,
            microsoftSource);
    }

    private (GoogleClientCredentials? Credentials, ClientCredentialSource Source) ResolveGoogle()
    {
        var configuredClientId = _configuration?[GoogleClientIdConfigurationKey];
        var configuredClientSecret = _configuration?[GoogleClientSecretConfigurationKey];
        if (HasValue(configuredClientId) || HasValue(configuredClientSecret))
        {
            return HasValue(configuredClientId) && HasValue(configuredClientSecret)
                ? (new GoogleClientCredentials(configuredClientId, configuredClientSecret),
                    ClientCredentialSource.Configuration)
                : (null, ClientCredentialSource.NotConfigured);
        }

        var embeddedClientId = GetEmbeddedValue(GoogleClientIdMetadataKey);
        var embeddedClientSecret = GetEmbeddedValue(GoogleClientSecretMetadataKey);
        return HasValue(embeddedClientId) && HasValue(embeddedClientSecret)
            ? (new GoogleClientCredentials(embeddedClientId, embeddedClientSecret),
                ClientCredentialSource.Embedded)
            : (null, ClientCredentialSource.NotConfigured);
    }

    private (MicrosoftClientCredentials? Credentials, ClientCredentialSource Source) ResolveMicrosoft()
    {
        var configuredClientId = _configuration?[MicrosoftClientIdConfigurationKey];
        if (HasValue(configuredClientId))
        {
            return (new MicrosoftClientCredentials(configuredClientId),
                ClientCredentialSource.Configuration);
        }

        var embeddedClientId = GetEmbeddedValue(MicrosoftClientIdMetadataKey);
        return HasValue(embeddedClientId)
            ? (new MicrosoftClientCredentials(embeddedClientId),
                ClientCredentialSource.Embedded)
            : (null, ClientCredentialSource.NotConfigured);
    }

    private string? GetEmbeddedValue(string key) =>
        _embeddedValues.TryGetValue(key, out var value) ? value : null;

    private static bool HasValue([NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value);
}
