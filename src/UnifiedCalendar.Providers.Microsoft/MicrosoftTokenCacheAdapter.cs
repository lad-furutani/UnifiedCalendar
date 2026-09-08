using Microsoft.Identity.Client;
using System.Collections.Concurrent;
using System.Text.Json;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Providers.Microsoft;

internal sealed class MicrosoftTokenCacheAdapter
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountGates = new();

    private readonly ITokenStore _tokenStore;
    private readonly Guid _internalAccountId;
    private readonly SemaphoreSlim _gate;
    private readonly HashSet<Guid> _activeCorrelations = [];

    public MicrosoftTokenCacheAdapter(ITokenStore tokenStore, Guid internalAccountId)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The internal account ID cannot be empty.", nameof(internalAccountId));
        }

        _internalAccountId = internalAccountId;
        _gate = AccountGates.GetOrAdd(internalAccountId, _ => new SemaphoreSlim(1, 1));
    }

    public void Attach(ITokenCache tokenCache)
    {
        ArgumentNullException.ThrowIfNull(tokenCache);
        tokenCache.SetBeforeAccessAsync(BeforeAccessAsync);
        tokenCache.SetAfterAccessAsync(AfterAccessAsync);
    }

    internal async Task BeforeAccessAsync(TokenCacheNotificationArgs args)
    {
        await _gate.WaitAsync(args.CancellationToken).ConfigureAwait(false);
        byte[]? payload;
        try
        {
            payload = await _tokenStore.ReadAsync(
                ProviderKind.Microsoft,
                _internalAccountId,
                args.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _gate.Release();
            throw;
        }

        if (payload is not null)
        {
            try
            {
                args.TokenCache.DeserializeMsalV3(payload, shouldClearExistingCache: true);
            }
            catch (Exception exception) when (IsMalformedPayloadException(exception))
            {
                _gate.Release();
                await _tokenStore.QuarantineAsync(
                    ProviderKind.Microsoft,
                    _internalAccountId,
                    TokenQuarantineReason.MalformedProviderPayload,
                    CancellationToken.None).ConfigureAwait(false);
                throw new MicrosoftTokenCacheCorruptedException(exception);
            }
            catch
            {
                _gate.Release();
                throw;
            }
        }

        if (!_activeCorrelations.Add(args.CorrelationId))
        {
            _gate.Release();
            throw new InvalidOperationException("The Microsoft token cache callback correlation is already active.");
        }
    }

    internal async Task AfterAccessAsync(TokenCacheNotificationArgs args)
    {
        if (!_activeCorrelations.Remove(args.CorrelationId))
        {
            return;
        }

        try
        {
            if (args.HasStateChanged)
            {
                await _tokenStore.WriteAsync(
                    ProviderKind.Microsoft,
                    _internalAccountId,
                    args.TokenCache.SerializeMsalV3(),
                    args.CancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsMalformedPayloadException(Exception exception) => exception switch
    {
        MsalClientException clientException => string.Equals(
            clientException.ErrorCode,
            "json_parse_failed",
            StringComparison.Ordinal),
        JsonException => true,
        FormatException => true,
        _ => false,
    };
}

internal sealed class MicrosoftTokenCacheCorruptedException : Exception
{
    public MicrosoftTokenCacheCorruptedException(Exception innerException)
        : base("The saved Microsoft token cache is malformed.", innerException)
    {
    }
}
