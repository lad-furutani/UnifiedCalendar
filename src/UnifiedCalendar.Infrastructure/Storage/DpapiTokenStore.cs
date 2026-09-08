using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Infrastructure.Storage;

public sealed class DpapiTokenStore : ITokenStore
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentProtectionVersion = 1;

    // This identity is part of the DPAPI entropy contract and must remain stable across reinstalls.
    public static readonly Guid ApplicationEntropyId =
        Guid.Parse("a8f50292-b22d-45c4-bd9a-501f9a1b2bd4");

    private readonly AppPaths _paths;
    private readonly AtomicFileWriter _atomicFileWriter;
    private readonly QuarantineManager _quarantine;
    private readonly TimeProvider _timeProvider;
    private readonly StorageEventLogger _logger;

    public DpapiTokenStore(
        AppPaths paths,
        AtomicFileWriter atomicFileWriter,
        TimeProvider timeProvider,
        StorageEventLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _atomicFileWriter = atomicFileWriter ?? throw new ArgumentNullException(nameof(atomicFileWriter));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? new StorageEventLogger();
        _quarantine = new QuarantineManager(paths, timeProvider, _logger);
    }

    public async Task<byte[]?> ReadAsync(
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetTokenFile(provider, internalAccountId);
        if (!File.Exists(path))
        {
            _logger.Write(StorageFileKind.Token, StorageOperation.Load, StorageResult.Missing);
            return null;
        }

        JsonObject root;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            root = JsonNode.Parse(bytes) as JsonObject
                ?? throw new JsonException("The token root must be an object.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            _logger.Write(StorageFileKind.Token, StorageOperation.Load, StorageResult.Missing);
            return null;
        }
        catch (JsonException exception)
        {
            _logger.WriteFailure(StorageFileKind.Token, StorageOperation.Load, exception);
            _quarantine.Quarantine(path, "token", "corrupt", "corrupt");
            return null;
        }

        if (!TryReadInt32(root, "schemaVersion", out var schemaVersion))
        {
            _logger.Write(StorageFileKind.Token, StorageOperation.SchemaCheck, StorageResult.Invalid);
            _quarantine.Quarantine(path, "token", "corrupt", "corrupt");
            return null;
        }

        if (schemaVersion < 0)
        {
            _logger.Write(
                StorageFileKind.Token,
                StorageOperation.SchemaCheck,
                StorageResult.Invalid,
                schemaVersion);
            _quarantine.Quarantine(
                path,
                "token",
                "corrupt",
                "corrupt",
                schemaVersion);
            return null;
        }

        if (schemaVersion != CurrentSchemaVersion)
        {
            var group = schemaVersion < CurrentSchemaVersion ? "older" : "future";
            _logger.Write(
                StorageFileKind.Token,
                StorageOperation.SchemaCheck,
                schemaVersion < CurrentSchemaVersion ? StorageResult.Older : StorageResult.Future,
                schemaVersion);
            _quarantine.Quarantine(
                path,
                "token",
                group,
                $"{group}-v{schemaVersion}",
                schemaVersion);
            return null;
        }

        _logger.Write(
            StorageFileKind.Token,
            StorageOperation.SchemaCheck,
            StorageResult.Current,
            schemaVersion);
        if (!TryReadInt32(root, "protectionVersion", out var protectionVersion))
        {
            _logger.Write(
                StorageFileKind.Token,
                StorageOperation.SchemaCheck,
                StorageResult.Invalid,
                schemaVersion);
            _quarantine.Quarantine(
                path,
                "token",
                "corrupt",
                "corrupt",
                schemaVersion);
            return null;
        }

        if (protectionVersion != CurrentProtectionVersion)
        {
            _logger.Write(
                StorageFileKind.Token,
                StorageOperation.SchemaCheck,
                StorageResult.Invalid,
                schemaVersion);
            _quarantine.Quarantine(
                path,
                "token",
                "protection",
                $"protection-v{protectionVersion}",
                schemaVersion);
            return null;
        }

        try
        {
            var envelope = root.Deserialize<TokenEnvelopeJsonDocument>(StorageJson.Options)
                ?? throw new JsonException("The token envelope is empty.");
            ValidateEnvelope(envelope, provider, internalAccountId);
            var protectedPayload = Convert.FromBase64String(envelope.ProtectedPayload);
            var payload = ProtectedData.Unprotect(
                protectedPayload,
                CreateEntropy(provider, protectionVersion),
                DataProtectionScope.CurrentUser);
            _logger.Write(
                StorageFileKind.Token,
                StorageOperation.Load,
                StorageResult.Success,
                schemaVersion);
            return payload;
        }
        catch (Exception exception) when (IsInvalidTokenException(exception))
        {
            _logger.WriteFailure(
                StorageFileKind.Token,
                StorageOperation.Load,
                exception,
                schemaVersion);
            var kind = exception is CryptographicException ? "decrypt" : "corrupt";
            _quarantine.Quarantine(path, "token", kind, kind, schemaVersion);
            return null;
        }
    }

    public async Task WriteAsync(
        ProviderKind provider,
        Guid internalAccountId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var path = _paths.GetTokenFile(provider, internalAccountId);
            var protectedPayload = ProtectedData.Protect(
                payload.ToArray(),
                CreateEntropy(provider, CurrentProtectionVersion),
                DataProtectionScope.CurrentUser);
            var envelope = new TokenEnvelopeJsonDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                ProtectionVersion = CurrentProtectionVersion,
                ProtectionKind = TokenProtectionKind.DpapiCurrentUser,
                Provider = provider,
                InternalAccountId = internalAccountId,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                ProtectedPayload = Convert.ToBase64String(protectedPayload),
            };

            await _atomicFileWriter.WriteJsonAsync(
                path,
                envelope,
                StorageJson.Options,
                cancellationToken).ConfigureAwait(false);
            _logger.Write(
                StorageFileKind.Token,
                StorageOperation.Save,
                StorageResult.Success,
                CurrentSchemaVersion);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.WriteFailure(
                StorageFileKind.Token,
                StorageOperation.Save,
                exception,
                CurrentSchemaVersion);
            throw;
        }
    }

    public async Task RemoveAsync(
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        var path = _paths.GetTokenFile(provider, internalAccountId);
        try
        {
            await _atomicFileWriter.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
            _quarantine.RemoveForSource(path, "token");
            _logger.Write(StorageFileKind.Token, StorageOperation.Remove, StorageResult.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.WriteFailure(StorageFileKind.Token, StorageOperation.Remove, exception);
            throw;
        }
    }

    public async Task QuarantineAsync(
        ProviderKind provider,
        Guid internalAccountId,
        TokenQuarantineReason reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "The token quarantine reason is not defined.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = _paths.GetTokenFile(provider, internalAccountId);
        if (!File.Exists(path))
        {
            return;
        }

        var detail = reason switch
        {
            TokenQuarantineReason.MalformedProviderPayload => "malformed-provider-payload",
            TokenQuarantineReason.RefreshRejected => "refresh-rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "The token quarantine reason is not defined."),
        };
        if (_quarantine.Quarantine(
                path,
                "token",
                "provider",
                detail,
                CurrentSchemaVersion))
        {
            return;
        }

        // Quarantine retention is preferred, but an invalid credential must never become active again.
        await _atomicFileWriter.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateEntropy(ProviderKind provider, int protectionVersion)
    {
        var providerName = AppPaths.GetProviderName(provider);
        return Encoding.UTF8.GetBytes($"{ApplicationEntropyId:D}|{providerName}|{protectionVersion}");
    }

    private static void ValidateEnvelope(
        TokenEnvelopeJsonDocument envelope,
        ProviderKind expectedProvider,
        Guid expectedAccountId)
    {
        if (envelope.SchemaVersion != CurrentSchemaVersion
            || envelope.ProtectionVersion != CurrentProtectionVersion
            || envelope.ProtectionKind != TokenProtectionKind.DpapiCurrentUser
            || envelope.Provider != expectedProvider
            || envelope.InternalAccountId != expectedAccountId
            || string.IsNullOrWhiteSpace(envelope.ProtectedPayload))
        {
            throw new InvalidDataException("The token envelope is inconsistent with its storage location.");
        }
    }

    private static bool TryReadInt32(JsonObject root, string propertyName, out int value)
    {
        value = default;
        return root[propertyName] is JsonValue jsonValue && jsonValue.TryGetValue(out value);
    }

    private static bool IsInvalidTokenException(Exception exception) => exception is
        JsonException
        or InvalidDataException
        or FormatException
        or CryptographicException
        or ArgumentException;
}

internal enum TokenProtectionKind
{
    DpapiCurrentUser,
}

internal sealed class TokenEnvelopeJsonDocument
{
    public required int SchemaVersion { get; set; }

    public required int ProtectionVersion { get; set; }

    public required TokenProtectionKind ProtectionKind { get; set; }

    public required ProviderKind Provider { get; set; }

    public required Guid InternalAccountId { get; set; }

    public required DateTimeOffset UpdatedAtUtc { get; set; }

    public required string ProtectedPayload { get; set; }
}
