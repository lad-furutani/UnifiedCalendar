using Microsoft.Extensions.DependencyInjection;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class AppPathsAndPersistenceCompositionTests
{
    [Fact]
    public void AppPaths_AggregatesTheV1LocalStorageLayout()
    {
        using var temporary = new TemporaryAppDirectory();
        var paths = temporary.Paths;

        Assert.Equal("UnifiedCalendar", AppPaths.ProductDirectoryName);
        Assert.Equal(Path.Combine(temporary.RootPath, "settings", "settings.json"), paths.SettingsFile);
        Assert.Equal(
            Path.Combine(temporary.RootPath, "tokens", "google", $"{StorageSamples.GoogleAccountId:N}.json"),
            paths.GetTokenFile(ProviderKind.Google, StorageSamples.GoogleAccountId));
        Assert.Equal(
            Path.Combine(temporary.RootPath, "tokens", "microsoft", $"{StorageSamples.MicrosoftAccountId:N}.json"),
            paths.GetTokenFile(ProviderKind.Microsoft, StorageSamples.MicrosoftAccountId));
        Assert.Equal(
            Path.Combine(temporary.RootPath, "cache", "accounts", $"{StorageSamples.GoogleAccountId:N}.json"),
            paths.GetAccountCacheFile(StorageSamples.GoogleAccountId));
        Assert.Equal(Path.Combine(temporary.RootPath, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(temporary.RootPath, "recovery"), paths.RecoveryDirectory);
        Assert.Equal($"google/{StorageSamples.GoogleAccountId:N}", paths.GetTokenReference(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId));
    }

    [Fact]
    public void PersistenceComposition_RegistersTheThreeStoreBoundariesAsSingletons()
    {
        using var temporary = new TemporaryAppDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new MutableTimeProvider(StorageSamples.Now));
        services.AddSingleton(temporary.Paths);
        services.AddUnifiedCalendarPersistence();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<SettingsJsonStore>(provider.GetRequiredService<ISettingsStore>());
        Assert.IsType<AccountCacheJsonStore>(provider.GetRequiredService<ICacheStore>());
        Assert.IsType<DpapiTokenStore>(provider.GetRequiredService<ITokenStore>());
        Assert.Same(
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<ISettingsStore>());
    }
}
