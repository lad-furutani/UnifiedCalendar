using System.Text.Json;
using Google.Apis.Json;
using Google.Apis.Util.Store;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.Providers.Google;

internal sealed class GoogleTokenStoreDataStore : IDataStore
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);
    private readonly ITokenStore _tokenStore;
    private readonly Guid _internalAccountId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GoogleTokenStoreDataStore(ITokenStore tokenStore, Guid internalAccountId)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        if (internalAccountId == Guid.Empty)
        {
            throw new ArgumentException("The internal account ID cannot be empty.", nameof(internalAccountId));
        }

        _internalAccountId = internalAccountId;
    }

    public Task StoreAsync<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        return MutateAsync(payload =>
        {
            payload.Values[CreateStorageKey<T>(key)] = NewtonsoftJsonSerializer.Instance.Serialize(value);
            return true;
        });
    }

    public Task DeleteAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return MutateAsync(payload => payload.Values.Remove(CreateStorageKey<T>(key)));
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var payload = await ReadValidatedPayloadAsync().ConfigureAwait(false);
            if (!payload.Values.TryGetValue(CreateStorageKey<T>(key), out var serialized))
            {
                return default;
            }

            try
            {
                return NewtonsoftJsonSerializer.Instance.Deserialize<T>(serialized);
            }
            catch (Exception exception) when (IsMalformedPayloadException(exception))
            {
                await QuarantineMalformedPayloadAsync().ConfigureAwait(false);
                throw new InvalidDataException("The Google token value is malformed.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _tokenStore.RemoveAsync(ProviderKind.Google, _internalAccountId).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task MutateAsync(Func<GoogleTokenPayload, bool> mutation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var payload = await ReadValidatedPayloadAsync().ConfigureAwait(false);
            if (!mutation(payload))
            {
                return;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadOptions);
            await _tokenStore.WriteAsync(ProviderKind.Google, _internalAccountId, bytes).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GoogleTokenPayload> ReadPayloadAsync()
    {
        var bytes = await _tokenStore.ReadAsync(ProviderKind.Google, _internalAccountId).ConfigureAwait(false);
        if (bytes is null)
        {
            return new GoogleTokenPayload();
        }

        try
        {
            return JsonSerializer.Deserialize<GoogleTokenPayload>(bytes, PayloadOptions)
                ?? throw new InvalidDataException("The Google token payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Google token payload is malformed.", exception);
        }
    }

    private async Task<GoogleTokenPayload> ReadValidatedPayloadAsync()
    {
        try
        {
            return await ReadPayloadAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsMalformedPayloadException(exception))
        {
            await QuarantineMalformedPayloadAsync().ConfigureAwait(false);
            throw exception is InvalidDataException
                ? exception
                : new InvalidDataException("The Google token payload is malformed.", exception);
        }
    }

    private Task QuarantineMalformedPayloadAsync() => _tokenStore.QuarantineAsync(
        ProviderKind.Google,
        _internalAccountId,
        TokenQuarantineReason.MalformedProviderPayload);

    private static bool IsMalformedPayloadException(Exception exception) => exception is
        InvalidDataException
        or JsonException
        or FormatException
        or ArgumentException
        || exception.GetType().Namespace?.StartsWith("Newtonsoft.Json", StringComparison.Ordinal) == true;

    private static string CreateStorageKey<T>(string key) => $"{typeof(T).FullName}:{key}";

    private sealed class GoogleTokenPayload
    {
        public Dictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);
    }
}
