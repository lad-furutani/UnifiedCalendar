using UnifiedCalendar.Core;

namespace UnifiedCalendar.Providers.Microsoft;

public sealed record MicrosoftProviderOptions
{
    public const string CalendarsReadScope = "Calendars.Read";
    public const string MailboxSettingsReadScope = "MailboxSettings.Read";
    public const string Authority = "https://login.microsoftonline.com/common";
    public const string RedirectUri = "http://localhost";
    public const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    public const string HttpClientName = "UnifiedCalendar.Microsoft";

    internal static readonly string[] Scopes =
    [
        CalendarsReadScope,
        MailboxSettingsReadScope,
    ];

    public MicrosoftProviderOptions(
        string clientId,
        TimeSpan? apiTimeout = null,
        TimeSpan? authorizationTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ClientId = clientId;
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

    public TimeSpan ApiTimeout { get; }

    public TimeSpan AuthorizationTimeout { get; }
}
