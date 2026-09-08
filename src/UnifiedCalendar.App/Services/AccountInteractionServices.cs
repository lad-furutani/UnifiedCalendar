using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Infrastructure.Storage;
using Serilog;

namespace UnifiedCalendar.App.Services;

public interface IAccountRegistrationService
{
    bool IsAvailable { get; }

    bool IsProviderAvailable(ProviderKind provider);

    Task<AccountRegistrationResult?> AddAccountAsync(
        ProviderKind provider,
        CancellationToken cancellationToken = default);
}

public sealed class AccountRegistrationResult
{
    private readonly IReadOnlyList<CalendarDescriptor> _calendars;

    public AccountRegistrationResult(
        AccountSettings account,
        IEnumerable<CalendarDescriptor> calendars)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        ArgumentNullException.ThrowIfNull(calendars);
        var values = calendars.ToArray();
        if (values.Any(calendar => calendar is null))
        {
            throw new ArgumentException("Calendars cannot contain null elements.", nameof(calendars));
        }

        _calendars = Array.AsReadOnly(values);
    }

    public AccountSettings Account { get; }

    public IReadOnlyList<CalendarDescriptor> Calendars => _calendars;
}

public interface IAccountReauthenticationService
{
    bool IsAvailable { get; }

    Task ReauthenticateAsync(Guid internalAccountId, CancellationToken cancellationToken = default);
}

public enum AccountInteractionFailure
{
    ProviderUnavailable,
    AccountNotFound,
    DuplicateAccount,
    AuthenticationFailed,
    CalendarDiscoveryFailed,
    SettingsUpdateFailed,
}

public sealed class AccountInteractionException : Exception
{
    public AccountInteractionException(
        AccountInteractionFailure failure,
        ProviderErrorCategory? providerErrorCategory = null,
        Exception? innerException = null)
        : base($"Account interaction failed with category {failure}.", innerException)
    {
        Failure = failure;
        ProviderErrorCategory = providerErrorCategory;
    }

    public AccountInteractionFailure Failure { get; }

    public ProviderErrorCategory? ProviderErrorCategory { get; }
}

internal static class AccountInteractionUiText
{
    public static string GetFailureMessage(
        IUiTextService textService,
        AccountInteractionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(textService);
        return textService.Get(failure switch
        {
            AccountInteractionFailure.DuplicateAccount =>
                UiResourceKeys.SettingsAccountsDuplicate,
            AccountInteractionFailure.CalendarDiscoveryFailed =>
                UiResourceKeys.SettingsAccountsCalendarDiscoveryFailed,
            AccountInteractionFailure.ProviderUnavailable =>
                UiResourceKeys.SettingsAccountsProviderUnavailable,
            _ => UiResourceKeys.SettingsAccountsAuthenticationFailed,
        });
    }
}

public sealed class AccountInteractionService :
    IAccountRegistrationService,
    IAccountReauthenticationService
{
    private readonly IReadOnlyDictionary<ProviderKind, ICalendarProvider> _providers;
    private readonly IApplicationSettingsService _settingsService;
    private readonly ICalendarSyncService _syncService;
    private readonly ITokenStore _tokenStore;
    private readonly AppPaths _paths;

    public AccountInteractionService(
        IEnumerable<ICalendarProvider> providers,
        IApplicationSettingsService settingsService,
        ICalendarSyncService syncService,
        ITokenStore tokenStore,
        AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        var values = providers.ToArray();
        if (values.Any(provider => provider is null))
        {
            throw new ArgumentException("Providers cannot contain null elements.", nameof(providers));
        }

        if (values.Select(provider => provider.Provider).Distinct().Count() != values.Length)
        {
            throw new ArgumentException("Only one provider can be registered for each provider kind.", nameof(providers));
        }

        _providers = values.ToDictionary(provider => provider.Provider);
    }

    public bool IsAvailable => _providers.Count > 0;

    public bool IsProviderAvailable(ProviderKind provider) => _providers.ContainsKey(provider);

    public async Task<AccountRegistrationResult?> AddAccountAsync(
        ProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(provider, out var calendarProvider))
        {
            throw new AccountInteractionException(AccountInteractionFailure.ProviderUnavailable);
        }

        AuthAccountResult authentication;
        try
        {
            authentication = await calendarProvider
                .AuthenticateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountAuthenticationFailed {Operation} {Provider} {ErrorCategory}",
                "Add",
                provider,
                ErrorCategory(exception));
            throw new AccountInteractionException(
                AccountInteractionFailure.AuthenticationFailed,
                exception is ProviderException providerException ? providerException.Error.Category : null,
                exception);
        }

        if (!authentication.IsSuccess)
        {
            if (authentication.Error?.Category == ProviderErrorCategory.Cancelled)
            {
                return null;
            }

            Log.Warning(
                "AccountAuthenticationFailed {Operation} {Provider} {ErrorCategory}",
                "Add",
                provider,
                authentication.Error?.Category.ToString() ?? "Unexpected");
            throw new AccountInteractionException(
                AccountInteractionFailure.AuthenticationFailed,
                authentication.Error?.Category);
        }

        return await CompleteRegistrationAsync(calendarProvider, authentication).ConfigureAwait(false);
    }

    public async Task ReauthenticateAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The account ID cannot be empty.", nameof(internalAccountId));
        }

        var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var accountSettings = settings.Accounts.FirstOrDefault(
            account => account.InternalAccountId == internalAccountId);
        if (accountSettings is null)
        {
            throw new AccountInteractionException(AccountInteractionFailure.AccountNotFound);
        }

        if (!_providers.TryGetValue(accountSettings.Provider, out var calendarProvider))
        {
            throw new AccountInteractionException(AccountInteractionFailure.ProviderUnavailable);
        }

        AuthAccountResult authentication;
        try
        {
            authentication = await calendarProvider.ReauthenticateAsync(
                accountSettings.ToCalendarAccount(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountAuthenticationFailed {Operation} {Provider} {InternalAccountId} {ErrorCategory}",
                "Reauthenticate",
                accountSettings.Provider,
                accountSettings.InternalAccountId,
                ErrorCategory(exception));
            throw new AccountInteractionException(
                AccountInteractionFailure.AuthenticationFailed,
                exception is ProviderException providerException ? providerException.Error.Category : null,
                exception);
        }

        if (!authentication.IsSuccess)
        {
            if (authentication.Error?.Category == ProviderErrorCategory.Cancelled)
            {
                return;
            }

            Log.Warning(
                "AccountAuthenticationFailed {Operation} {Provider} {InternalAccountId} {ErrorCategory}",
                "Reauthenticate",
                accountSettings.Provider,
                accountSettings.InternalAccountId,
                authentication.Error?.Category.ToString() ?? "Unexpected");
            throw new AccountInteractionException(
                AccountInteractionFailure.AuthenticationFailed,
                authentication.Error?.Category);
        }

        _ = ObserveSyncAsync(
            _syncService.RequestSyncAsync(
                SyncTriggerReason.Manual,
                [internalAccountId],
                CancellationToken.None),
            "ReauthenticateSync",
            accountSettings.Provider,
            internalAccountId);
    }

    private async Task<AccountRegistrationResult> CompleteRegistrationAsync(
        ICalendarProvider provider,
        AuthAccountResult authentication)
    {
        var internalAccountId = authentication.InternalAccountId;
        var providerKind = provider.Provider;
        var account = new CalendarAccount(
            internalAccountId,
            providerKind,
            authentication.ProviderSubjectId!,
            authentication.DisplayName!,
            authentication.Email!,
            true,
            _paths.GetTokenReference(providerKind, internalAccountId));
        IReadOnlyList<CalendarDescriptor> calendars;
        try
        {
            calendars = await provider
                .ListCalendarsAsync(account, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RemoveRegistrationTokenAsync(providerKind, internalAccountId).ConfigureAwait(false);
            Log.Warning(
                "AccountCalendarDiscoveryFailed {Provider} {InternalAccountId} {ErrorCategory}",
                providerKind,
                internalAccountId,
                ErrorCategory(exception));
            throw new AccountInteractionException(
                AccountInteractionFailure.CalendarDiscoveryFailed,
                exception is ProviderException providerException ? providerException.Error.Category : null,
                exception);
        }

        var calendarSettings = calendars
            .GroupBy(calendar => calendar.CalendarId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(calendar => new CalendarSetting(
                calendar.CalendarId,
                calendar.IsPrimary && calendar.CanReadEvents))
            .ToArray();
        var persistedAccount = new AccountSettings(
            internalAccountId,
            providerKind,
            authentication.ProviderSubjectId!,
            authentication.DisplayName!,
            authentication.Email!,
            true,
            account.TokenRef,
            calendarSettings);
        var settingsSaved = false;
        try
        {
            await _settingsService.UpdateAsync(current =>
            {
                if (current.Accounts.Any(existing =>
                    existing.Provider == providerKind
                    && string.Equals(
                        existing.ProviderSubjectId,
                        authentication.ProviderSubjectId,
                        StringComparison.Ordinal)))
                {
                    throw new AccountInteractionException(AccountInteractionFailure.DuplicateAccount);
                }

                return CopyWithAccounts(current, current.Accounts.Append(persistedAccount));
            }, CancellationToken.None).ConfigureAwait(false);
            settingsSaved = true;
            await _syncService.RefreshFromSettingsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (AccountInteractionException) when (!settingsSaved)
        {
            await RemoveRegistrationTokenUnlessExistingAsync(
                providerKind,
                internalAccountId).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            if (!settingsSaved)
            {
                await RemoveRegistrationTokenUnlessExistingAsync(
                    providerKind,
                    internalAccountId).ConfigureAwait(false);
            }

            Log.Warning(
                "AccountRegistrationFailed {Stage} {Provider} {InternalAccountId} {ErrorCategory}",
                settingsSaved ? "RefreshSettings" : "SaveSettings",
                providerKind,
                internalAccountId,
                ErrorCategory(exception));
            throw new AccountInteractionException(
                AccountInteractionFailure.SettingsUpdateFailed,
                innerException: exception);
        }

        _ = ObserveSyncAsync(
            _syncService.RequestSyncAsync(
                SyncTriggerReason.Manual,
                [internalAccountId],
                CancellationToken.None),
            "InitialSync",
            providerKind,
            internalAccountId);
        return new AccountRegistrationResult(persistedAccount, calendars);
    }

    private async Task RemoveRegistrationTokenUnlessExistingAsync(
        ProviderKind provider,
        Guid internalAccountId)
    {
        try
        {
            var current = await _settingsService.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (current.Accounts.Any(account => account.InternalAccountId == internalAccountId))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountRegistrationRollbackCheckFailed {Provider} {InternalAccountId} {ErrorCategory}",
                provider,
                internalAccountId,
                ErrorCategory(exception));
            return;
        }

        await RemoveRegistrationTokenAsync(provider, internalAccountId).ConfigureAwait(false);
    }

    private async Task RemoveRegistrationTokenAsync(ProviderKind provider, Guid internalAccountId)
    {
        try
        {
            await _tokenStore.RemoveAsync(
                provider,
                internalAccountId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountRegistrationRollbackFailed {Stage} {Provider} {InternalAccountId} {ErrorCategory}",
                "TokenRemove",
                provider,
                internalAccountId,
                ErrorCategory(exception));
        }
    }

    private static AppSettings CopyWithAccounts(
        AppSettings current,
        IEnumerable<AccountSettings> accounts) => new(
            current.Display,
            current.Sync,
            current.General,
            current.Windows,
            accounts,
            current.ColorRules);

    private static string ErrorCategory(Exception exception) => exception switch
    {
        ProviderException providerException => providerException.Error.Category.ToString(),
        _ => exception.GetType().Name,
    };

    private static async Task ObserveSyncAsync(
        Task task,
        string stage,
        ProviderKind provider,
        Guid internalAccountId)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "AccountSyncRequestFailed {Stage} {Provider} {InternalAccountId} {ErrorCategory}",
                stage,
                provider,
                internalAccountId,
                ErrorCategory(exception));
        }
    }
}

public sealed class UnavailableAccountInteractionService :
    IAccountRegistrationService,
    IAccountReauthenticationService
{
    public bool IsAvailable => false;

    public bool IsProviderAvailable(ProviderKind provider) => false;

    public Task<AccountRegistrationResult?> AddAccountAsync(
        ProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Account registration is not available.");
    }

    public Task ReauthenticateAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The account ID cannot be empty.", nameof(internalAccountId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Account reauthentication is not available.");
    }
}
