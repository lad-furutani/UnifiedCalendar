using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Infrastructure.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddUnifiedCalendarPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AppPaths>();
        services.TryAddSingleton<StorageEventLogger>();
        services.TryAddSingleton<AtomicFileWriter>(provider =>
        {
            var paths = provider.GetRequiredService<AppPaths>();
            var storageLogger = provider.GetRequiredService<StorageEventLogger>();
            paths.EnsureDirectories();
            var writer = new AtomicFileWriter(logger: storageLogger);
            writer.CleanupStaleTemporaryFiles(paths.RootDirectory);
            return writer;
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISettingsMigration, SettingsSchemaV0ToV1Migration>());
        services.TryAddSingleton<ISettingsStore, SettingsJsonStore>();
        services.TryAddSingleton<ICacheStore, AccountCacheJsonStore>();
        services.TryAddSingleton<ITokenStore, DpapiTokenStore>();
        return services;
    }
}
