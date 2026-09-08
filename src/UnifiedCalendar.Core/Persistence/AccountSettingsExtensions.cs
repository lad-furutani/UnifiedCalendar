using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Core.Persistence;

public static class AccountSettingsExtensions
{
    public static CalendarAccount ToCalendarAccount(this AccountSettings account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new CalendarAccount(
            account.InternalAccountId,
            account.Provider,
            account.ProviderSubjectId,
            account.DisplayName,
            account.Email,
            account.Enabled,
            account.TokenRef);
    }
}
