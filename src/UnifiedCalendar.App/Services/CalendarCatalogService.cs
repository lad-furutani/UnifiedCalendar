using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.App.Services;

public interface ICalendarCatalogService
{
    Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
        AccountSettings account,
        CancellationToken cancellationToken = default);

    Task<AccountCache?> LoadCacheAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default);
}

public sealed class CalendarCatalogService : ICalendarCatalogService
{
    private readonly IReadOnlyDictionary<ProviderKind, ICalendarProvider> _providers;
    private readonly ICacheStore _cacheStore;

    public CalendarCatalogService(
        IEnumerable<ICalendarProvider> providers,
        ICacheStore cacheStore)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
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

    public Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
        AccountSettings account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!_providers.TryGetValue(account.Provider, out var provider))
        {
            throw new InvalidOperationException("The account provider is not available.");
        }

        return provider.ListCalendarsAsync(account.ToCalendarAccount(), cancellationToken);
    }

    public Task<AccountCache?> LoadCacheAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The account ID cannot be empty.", nameof(internalAccountId));
        }

        return _cacheStore.LoadAccountAsync(internalAccountId, cancellationToken);
    }

}
