using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.Core.Persistence;

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ICacheStore
{
    Task<AccountCache?> LoadAccountAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default);

    Task SaveAccountAsync(
        AccountCache accountCache,
        CancellationToken cancellationToken = default);

    Task RemoveAccountAsync(
        Guid internalAccountId,
        CancellationToken cancellationToken = default);
}

public interface ITokenStore
{
    Task<byte[]?> ReadAsync(
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        ProviderKind provider,
        Guid internalAccountId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default);

    Task QuarantineAsync(
        ProviderKind provider,
        Guid internalAccountId,
        TokenQuarantineReason reason,
        CancellationToken cancellationToken = default);
}

public enum TokenQuarantineReason
{
    MalformedProviderPayload,
    RefreshRejected,
}
