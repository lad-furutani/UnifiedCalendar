using Microsoft.Identity.Client;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Providers.Microsoft;

internal sealed record MicrosoftAuthenticationResult(
    string AccessToken,
    string ProviderSubjectId,
    string DisplayName,
    string Email);

internal interface IMicrosoftAuthenticationClient
{
    Task<MicrosoftAuthenticationResult> AcquireInteractiveAsync(
        Guid internalAccountId,
        string? expectedProviderSubjectId,
        string? loginHint,
        CancellationToken cancellationToken);

    Task<MicrosoftAuthenticationResult> AcquireSilentAsync(
        CalendarAccount account,
        CancellationToken cancellationToken);
}

internal sealed class MicrosoftAuthenticationClient : IMicrosoftAuthenticationClient
{
    private readonly ITokenStore _tokenStore;
    private readonly MicrosoftProviderOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CalendarAccount, CancellationToken, Task<MicrosoftAuthenticationResult>>? _silentAcquisition;

    public MicrosoftAuthenticationClient(
        ITokenStore tokenStore,
        MicrosoftProviderOptions options,
        TimeProvider timeProvider)
        : this(tokenStore, options, timeProvider, null)
    {
    }

    internal MicrosoftAuthenticationClient(
        ITokenStore tokenStore,
        MicrosoftProviderOptions options,
        TimeProvider timeProvider,
        Func<CalendarAccount, CancellationToken, Task<MicrosoftAuthenticationResult>>? silentAcquisition)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _silentAcquisition = silentAcquisition;
    }

    public async Task<MicrosoftAuthenticationResult> AcquireInteractiveAsync(
        Guid internalAccountId,
        string? expectedProviderSubjectId,
        string? loginHint,
        CancellationToken cancellationToken)
    {
        var application = CreateApplication(internalAccountId);
        using var timeout = new CancellationTokenSource(_options.AuthorizationTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var accounts = await application.GetAccountsAsync().ConfigureAwait(false);
        var expectedAccount = expectedProviderSubjectId is null
            ? null
            : accounts.FirstOrDefault(candidate => MicrosoftIdentityCachePolicy.IdentityEquals(
                candidate.HomeAccountId?.Identifier,
                expectedProviderSubjectId));
        var request = application
            .AcquireTokenInteractive(MicrosoftProviderOptions.Scopes)
            .WithUseEmbeddedWebView(false);
        if (expectedProviderSubjectId is not null)
        {
            request = expectedAccount is not null
                ? request.WithAccount(expectedAccount)
                : request.WithLoginHint(loginHint ?? expectedProviderSubjectId);
        }

        var result = await request.ExecuteAsync(linked.Token).ConfigureAwait(false);
        var authentication = ToAuthenticationResult(result);
        try
        {
            MicrosoftIdentityCachePolicy.ValidateExpectedIdentity(
                expectedProviderSubjectId,
                authentication.ProviderSubjectId);
            await MicrosoftIdentityCachePolicy.RemoveNonTargetAccountsAsync(
                await application.GetAccountsAsync().ConfigureAwait(false),
                authentication.ProviderSubjectId,
                (account, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return application.RemoveAsync(account);
                },
                linked.Token).ConfigureAwait(false);
        }
        catch
        {
            if (expectedProviderSubjectId is not null)
            {
                try
                {
                    await MicrosoftIdentityCachePolicy.RemoveNonTargetAccountsAsync(
                        await application.GetAccountsAsync().ConfigureAwait(false),
                        expectedProviderSubjectId,
                        (account, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            return application.RemoveAsync(account);
                        },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    await _tokenStore.RemoveAsync(
                        ProviderKind.Microsoft,
                        internalAccountId,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            throw;
        }

        return authentication;
    }

    public async Task<MicrosoftAuthenticationResult> AcquireSilentAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            return _silentAcquisition is null
                ? await AcquireSilentCoreAsync(account, cancellationToken).ConfigureAwait(false)
                : await _silentAcquisition(account, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldQuarantineSilentFailure(exception))
        {
            await _tokenStore.QuarantineAsync(
                ProviderKind.Microsoft,
                account.InternalAccountId,
                TokenQuarantineReason.RefreshRejected,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static bool ShouldQuarantineSilentFailure(Exception exception)
    {
        if (exception is not MsalServiceException serviceException)
        {
            return false;
        }

        if (exception is MsalUiRequiredException uiRequiredException
            && uiRequiredException.Classification == UiRequiredExceptionClassification.ConsentRequired)
        {
            return false;
        }

        if (IsInteractionOnlyErrorCode(serviceException.ErrorCode))
        {
            return false;
        }

        return IsRejectedCredentialErrorCode(serviceException.ErrorCode);
    }

    private async Task<MicrosoftAuthenticationResult> AcquireSilentCoreAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        var application = CreateApplication(account.InternalAccountId);
        var accounts = await application.GetAccountsAsync().ConfigureAwait(false);
        var matching = accounts.FirstOrDefault(candidate =>
            candidate.HomeAccountId?.Identifier?.Equals(
                account.ProviderSubjectId,
                StringComparison.Ordinal) == true);
        if (matching is null)
        {
            throw new MsalUiRequiredException(
                "account_not_found",
                "No saved Microsoft account is available for silent authentication.");
        }

        var result = await application
            .AcquireTokenSilent(MicrosoftProviderOptions.Scopes, matching)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToAuthenticationResult(result);
    }

    private static bool IsRejectedCredentialErrorCode(string? errorCode) =>
        errorCode?.ToLowerInvariant() is
            "invalid_grant"
            or "refresh_token_expired"
            or "refresh_token_revoked";

    private static bool IsInteractionOnlyErrorCode(string? errorCode) =>
        errorCode?.ToLowerInvariant() is
            "consent_required"
            or "interaction_required"
            or "login_required";

    private IPublicClientApplication CreateApplication(Guid internalAccountId)
    {
        var application = PublicClientApplicationBuilder
            .Create(_options.ClientId)
            .WithAuthority(MicrosoftProviderOptions.Authority)
            .WithRedirectUri(MicrosoftProviderOptions.RedirectUri)
            .Build();
        new MicrosoftTokenCacheAdapter(_tokenStore, internalAccountId).Attach(application.UserTokenCache);
        return application;
    }

    private static MicrosoftAuthenticationResult ToAuthenticationResult(AuthenticationResult result)
    {
        var subject = result.Account?.HomeAccountId?.Identifier;
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            throw new InvalidDataException("The Microsoft authentication response was incomplete.");
        }

        var account = result.Account
            ?? throw new InvalidDataException("The Microsoft authentication response has no account.");
        var email = account.Username;
        if (string.IsNullOrWhiteSpace(email))
        {
            email = subject;
        }

        var name = result.ClaimsPrincipal?.FindFirst("name")?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = email;
        }

        return new MicrosoftAuthenticationResult(result.AccessToken, subject, name, email);
    }
}

internal static class MicrosoftIdentityCachePolicy
{
    public static bool IdentityEquals(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first)
        && !string.IsNullOrWhiteSpace(second)
        && first.Equals(second, StringComparison.Ordinal);

    public static void ValidateExpectedIdentity(string? expectedIdentity, string actualIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actualIdentity);
        if (expectedIdentity is not null && !IdentityEquals(expectedIdentity, actualIdentity))
        {
            throw new MicrosoftIdentityMismatchException();
        }
    }

    public static async Task RemoveNonTargetAccountsAsync<TAccount>(
        IEnumerable<TAccount> accounts,
        string targetIdentity,
        Func<TAccount, string?> getIdentity,
        Func<TAccount, CancellationToken, Task> removeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentity);
        ArgumentNullException.ThrowIfNull(getIdentity);
        ArgumentNullException.ThrowIfNull(removeAsync);
        foreach (var account in accounts.Where(account =>
                     !IdentityEquals(getIdentity(account), targetIdentity)))
        {
            await removeAsync(account, cancellationToken).ConfigureAwait(false);
        }
    }

    public static Task RemoveNonTargetAccountsAsync(
        IEnumerable<IAccount> accounts,
        string targetIdentity,
        Func<IAccount, CancellationToken, Task> removeAsync,
        CancellationToken cancellationToken) => RemoveNonTargetAccountsAsync(
            accounts,
            targetIdentity,
            account => account.HomeAccountId?.Identifier,
            removeAsync,
            cancellationToken);
}

internal sealed class MicrosoftIdentityMismatchException : Exception
{
    public MicrosoftIdentityMismatchException()
        : base("The selected Microsoft identity does not match the existing account.")
    {
    }
}
