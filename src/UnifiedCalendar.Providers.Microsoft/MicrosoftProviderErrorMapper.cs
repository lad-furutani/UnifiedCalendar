using System.Globalization;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using Microsoft.Graph.Models.ODataErrors;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.Providers.Microsoft;

internal static class MicrosoftProviderErrorMapper
{
    public static ProviderError Map(
        Exception exception,
        CancellationToken callerCancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ProviderException providerException)
        {
            return providerException.Error;
        }

        if (exception is OperationCanceledException)
        {
            return new ProviderError(
                callerCancellationToken.IsCancellationRequested
                    ? ProviderErrorCategory.Cancelled
                    : ProviderErrorCategory.Timeout);
        }

        if (exception is TimeoutException)
        {
            return new ProviderError(ProviderErrorCategory.Timeout);
        }

        if (exception is MsalUiRequiredException)
        {
            return new ProviderError(
                ProviderErrorCategory.AuthenticationRequired,
                reauthenticationRequired: true);
        }

        if (exception is MicrosoftTokenCacheCorruptedException)
        {
            return new ProviderError(
                ProviderErrorCategory.AuthenticationRequired,
                reauthenticationRequired: true);
        }

        if (exception is MicrosoftIdentityMismatchException)
        {
            return new ProviderError(
                ProviderErrorCategory.AuthenticationRequired,
                reauthenticationRequired: true);
        }

        if (exception is MsalServiceException serviceException)
        {
            if (serviceException.ErrorCode.Equals("invalid_grant", StringComparison.OrdinalIgnoreCase)
                || serviceException.ErrorCode.Equals("interaction_required", StringComparison.OrdinalIgnoreCase))
            {
                return new ProviderError(
                    ProviderErrorCategory.AuthenticationRequired,
                    NormalizeStatusCode(serviceException.StatusCode),
                    reauthenticationRequired: true);
            }

            if (serviceException.ErrorCode.Equals("access_denied", StringComparison.OrdinalIgnoreCase))
            {
                return new ProviderError(
                    ProviderErrorCategory.PermissionDenied,
                    NormalizeStatusCode(serviceException.StatusCode));
            }

            var status = NormalizeStatusCode(serviceException.StatusCode);
            return status.HasValue
                ? FromStatusCode(status.Value, null)
                : new ProviderError(
                    ProviderErrorCategory.AuthenticationRequired,
                    reauthenticationRequired: true);
        }

        if (exception is MsalClientException clientException)
        {
            if (clientException.ErrorCode.Equals("authentication_canceled", StringComparison.OrdinalIgnoreCase))
            {
                return new ProviderError(ProviderErrorCategory.Cancelled);
            }

            return new ProviderError(ProviderErrorCategory.AuthenticationRequired, reauthenticationRequired: true);
        }

        if (exception is ODataError graphError)
        {
            return FromStatusCode(
                graphError.ResponseStatusCode,
                GetRetryAfter(graphError.ResponseHeaders, timeProvider ?? TimeProvider.System));
        }

        if (exception is ApiException apiException)
        {
            return FromStatusCode(
                apiException.ResponseStatusCode,
                GetRetryAfter(apiException.ResponseHeaders, timeProvider ?? TimeProvider.System));
        }

        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode.HasValue
                ? FromStatusCode((int)httpException.StatusCode.Value, null)
                : new ProviderError(ProviderErrorCategory.Network);
        }

        if (exception is IOException)
        {
            return new ProviderError(ProviderErrorCategory.Network);
        }

        if (exception is InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            return new ProviderError(ProviderErrorCategory.MalformedResponse);
        }

        return new ProviderError(ProviderErrorCategory.Unexpected);
    }

    internal static TimeSpan? GetRetryAfter(
        IDictionary<string, IEnumerable<string>>? headers,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (headers is null)
        {
            return null;
        }

        var values = headers.FirstOrDefault(pair =>
            pair.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value;
        var value = values?.FirstOrDefault();
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var retryAt))
        {
            var delay = retryAt - timeProvider.GetUtcNow();
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    internal static ProviderError FromStatusCode(int statusCode, TimeSpan? retryAfter)
    {
        var category = statusCode switch
        {
            401 => ProviderErrorCategory.AuthenticationRequired,
            403 => ProviderErrorCategory.PermissionDenied,
            404 => ProviderErrorCategory.NotFound,
            408 => ProviderErrorCategory.Timeout,
            429 => ProviderErrorCategory.RateLimited,
            >= 500 and <= 599 => ProviderErrorCategory.ServerError,
            400 or 409 or 410 or 422 => ProviderErrorCategory.MalformedResponse,
            _ => ProviderErrorCategory.Unexpected,
        };
        return new ProviderError(
            category,
            statusCode,
            retryAfter,
            reauthenticationRequired: category == ProviderErrorCategory.AuthenticationRequired);
    }

    private static int? NormalizeStatusCode(int statusCode) =>
        statusCode is >= 100 and <= 599 ? statusCode : null;
}
