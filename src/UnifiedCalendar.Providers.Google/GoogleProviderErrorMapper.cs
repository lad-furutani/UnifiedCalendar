using System.Net;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.Providers.Google;

internal static class GoogleProviderErrorMapper
{
    public static ProviderError Map(Exception exception, CancellationToken callerCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ProviderException providerException)
        {
            return providerException.Error;
        }

        if (exception is GoogleIdentityMismatchException)
        {
            return new ProviderError(
                ProviderErrorCategory.AuthenticationRequired,
                reauthenticationRequired: true);
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

        if (exception is TokenResponseException tokenException)
        {
            var statusCode = tokenException.StatusCode.HasValue
                ? (int)tokenException.StatusCode.Value
                : (int?)null;
            if (tokenException.Error?.Error?.Equals("access_denied", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new ProviderError(ProviderErrorCategory.PermissionDenied, statusCode);
            }

            if (statusCode is 408 or 429 or >= 500 and <= 599)
            {
                return FromStatusCode(statusCode, false, GetRetryAfter(tokenException));
            }

            return new ProviderError(
                ProviderErrorCategory.AuthenticationRequired,
                statusCode,
                reauthenticationRequired: true);
        }

        if (exception is GoogleApiException googleException)
        {
            var statusCode = googleException.HttpStatusCode == 0
                ? (int?)null
                : (int)googleException.HttpStatusCode;
            return FromStatusCode(
                statusCode,
                IsRateLimitReason(googleException),
                GetRetryAfter(googleException));
        }

        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode.HasValue
                ? FromStatusCode((int)httpException.StatusCode.Value, false, null)
                : new ProviderError(ProviderErrorCategory.Network);
        }

        if (exception is IOException)
        {
            return new ProviderError(ProviderErrorCategory.Network);
        }

        if (exception is InvalidDataException
            or FormatException
            or System.Text.Json.JsonException
            || exception.GetType().Namespace?.StartsWith("Newtonsoft.Json", StringComparison.Ordinal) == true)
        {
            return new ProviderError(ProviderErrorCategory.MalformedResponse);
        }

        return new ProviderError(ProviderErrorCategory.Unexpected);
    }

    private static ProviderError FromStatusCode(
        int? statusCode,
        bool rateLimitReason,
        TimeSpan? retryAfter)
    {
        var category = statusCode switch
        {
            401 => ProviderErrorCategory.AuthenticationRequired,
            403 when rateLimitReason => ProviderErrorCategory.RateLimited,
            403 => ProviderErrorCategory.PermissionDenied,
            404 => ProviderErrorCategory.NotFound,
            408 => ProviderErrorCategory.Timeout,
            429 => ProviderErrorCategory.RateLimited,
            >= 500 and <= 599 => ProviderErrorCategory.ServerError,
            400 or 409 or 410 or 422 => ProviderErrorCategory.MalformedResponse,
            null => ProviderErrorCategory.Unexpected,
            _ => ProviderErrorCategory.Unexpected,
        };
        return new ProviderError(
            category,
            statusCode,
            retryAfter,
            reauthenticationRequired: category == ProviderErrorCategory.AuthenticationRequired);
    }

    private static bool IsRateLimitReason(GoogleApiException exception) =>
        exception.Error?.Errors?.Any(error => error.Reason?.ToLowerInvariant() is
            "ratelimitexceeded"
            or "userratelimitexceeded"
            or "quotaexceeded"
            or "calendarusagelimitsexceeded"
            or "dailylimitexceeded") == true;

    private static TimeSpan? GetRetryAfter(Exception exception)
    {
        if (exception.Data["RetryAfter"] is TimeSpan retryAfter && retryAfter >= TimeSpan.Zero)
        {
            return retryAfter;
        }

        if (exception.Data["RetryAfterSeconds"] is int seconds && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}
