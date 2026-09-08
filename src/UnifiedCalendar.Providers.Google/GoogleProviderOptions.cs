using UnifiedCalendar.Core;

namespace UnifiedCalendar.Providers.Google;

public sealed record GoogleProviderOptions
{
    public const string CalendarReadOnlyScope =
        "https://www.googleapis.com/auth/calendar.readonly";

    public const string HttpClientName = "UnifiedCalendar.Google";

    public GoogleProviderOptions(
        string clientId,
        string? clientSecret = null,
        string applicationName = "UnifiedCalendar",
        TimeSpan? apiTimeout = null,
        TimeSpan? authorizationTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        ClientId = clientId;
        ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret;
        ApplicationName = applicationName;
        ApiTimeout = apiTimeout ?? ApplicationDefaults.ApiTimeout;
        AuthorizationTimeout = authorizationTimeout ?? TimeSpan.FromMinutes(10);

        if (ApiTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(apiTimeout), "The API timeout must be positive.");
        }

        if (AuthorizationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(authorizationTimeout), "The authorization timeout must be positive.");
        }
    }

    public string ClientId { get; }

    // A desktop OAuth client secret is public-client metadata, not a protectable application secret.
    public string? ClientSecret { get; }

    public string ApplicationName { get; }

    public TimeSpan ApiTimeout { get; }

    public TimeSpan AuthorizationTimeout { get; }
}
