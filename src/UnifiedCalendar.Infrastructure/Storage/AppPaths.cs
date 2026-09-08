using UnifiedCalendar.Core;
using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Infrastructure.Storage;

public sealed class AppPaths
{
    public const string ProductDirectoryName = AppIdentity.ProductDirectoryName;

    public AppPaths()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductDirectoryName))
    {
    }

    public AppPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        SettingsDirectory = Path.Combine(RootDirectory, "settings");
        SettingsFile = Path.Combine(SettingsDirectory, "settings.json");
        TokensDirectory = Path.Combine(RootDirectory, "tokens");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
        AccountCacheDirectory = Path.Combine(CacheDirectory, "accounts");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        RecoveryDirectory = Path.Combine(RootDirectory, "recovery");
    }

    public string RootDirectory { get; }

    public string SettingsDirectory { get; }

    public string SettingsFile { get; }

    public string TokensDirectory { get; }

    public string CacheDirectory { get; }

    public string AccountCacheDirectory { get; }

    public string LogsDirectory { get; }

    public string RecoveryDirectory { get; }

    public string GetTokenDirectory(ProviderKind provider) =>
        Path.Combine(TokensDirectory, GetProviderName(provider));

    public string GetTokenFile(ProviderKind provider, Guid internalAccountId)
    {
        ValidateAccountId(internalAccountId);
        return Path.Combine(GetTokenDirectory(provider), $"{internalAccountId:N}.json");
    }

    public string GetTokenReference(ProviderKind provider, Guid internalAccountId)
    {
        ValidateAccountId(internalAccountId);
        return $"{GetProviderName(provider)}/{internalAccountId:N}";
    }

    public string GetAccountCacheFile(Guid internalAccountId)
    {
        ValidateAccountId(internalAccountId);
        return Path.Combine(AccountCacheDirectory, $"{internalAccountId:N}.json");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(GetTokenDirectory(ProviderKind.Google));
        Directory.CreateDirectory(GetTokenDirectory(ProviderKind.Microsoft));
        Directory.CreateDirectory(AccountCacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
    }

    public static string GetProviderName(ProviderKind provider) => provider switch
    {
        ProviderKind.Google => "google",
        ProviderKind.Microsoft => "microsoft",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "The provider is not defined."),
    };

    private static void ValidateAccountId(Guid internalAccountId)
    {
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The internal account ID cannot be empty.", nameof(internalAccountId));
        }
    }
}
