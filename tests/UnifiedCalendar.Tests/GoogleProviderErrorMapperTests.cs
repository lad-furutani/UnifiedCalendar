using System.Net;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Requests;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Google;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class GoogleProviderErrorMapperTests
{
    [Theory]
    [InlineData(401, ProviderErrorCategory.AuthenticationRequired, true)]
    [InlineData(403, ProviderErrorCategory.PermissionDenied, false)]
    [InlineData(404, ProviderErrorCategory.NotFound, false)]
    [InlineData(408, ProviderErrorCategory.Timeout, false)]
    [InlineData(429, ProviderErrorCategory.RateLimited, false)]
    [InlineData(500, ProviderErrorCategory.ServerError, false)]
    [InlineData(503, ProviderErrorCategory.ServerError, false)]
    public void HttpStatus_IsMappedToProviderCategory(
        int statusCode,
        ProviderErrorCategory expected,
        bool reauthenticationRequired)
    {
        var exception = new GoogleApiException("calendar", "fixture")
        {
            HttpStatusCode = (HttpStatusCode)statusCode,
        };

        var error = GoogleProviderErrorMapper.Map(exception, CancellationToken.None);

        Assert.Equal(expected, error.Category);
        Assert.Equal(statusCode, error.HttpStatusCode);
        Assert.Equal(reauthenticationRequired, error.ReauthenticationRequired);
    }

    [Fact]
    public void GoogleRateLimitReasonOn403_IsRateLimitedAndCarriesRetryAfter()
    {
        var exception = new GoogleApiException("calendar", "fixture")
        {
            HttpStatusCode = HttpStatusCode.Forbidden,
            Error = new RequestError
            {
                Errors = [new SingleError { Reason = "rateLimitExceeded" }],
            },
        };
        exception.Data["RetryAfterSeconds"] = 17;

        var error = GoogleProviderErrorMapper.Map(exception, CancellationToken.None);

        Assert.Equal(ProviderErrorCategory.RateLimited, error.Category);
        Assert.Equal(TimeSpan.FromSeconds(17), error.RetryAfter);
    }

    [Fact]
    public void NetworkException_IsNetwork()
    {
        var error = GoogleProviderErrorMapper.Map(
            new HttpRequestException("fixture network"),
            CancellationToken.None);

        Assert.Equal(ProviderErrorCategory.Network, error.Category);
    }

    [Fact]
    public void TimeoutWithoutCallerCancellation_IsTimeout()
    {
        var error = GoogleProviderErrorMapper.Map(
            new TaskCanceledException("fixture timeout"),
            CancellationToken.None);

        Assert.Equal(ProviderErrorCategory.Timeout, error.Category);
    }

    [Fact]
    public void CallerCancellation_IsCancelled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        var error = GoogleProviderErrorMapper.Map(
            new OperationCanceledException(source.Token),
            source.Token);

        Assert.Equal(ProviderErrorCategory.Cancelled, error.Category);
    }

    [Fact]
    public void MalformedPayloadException_IsMalformedResponse()
    {
        var error = GoogleProviderErrorMapper.Map(
            new InvalidDataException("fixture malformed"),
            CancellationToken.None);

        Assert.Equal(ProviderErrorCategory.MalformedResponse, error.Category);
    }

    [Fact]
    public void RefreshTokenFailure_RequiresReauthenticationWithoutLeakingSdkException()
    {
        var exception = new TokenResponseException(
            new TokenErrorResponse { Error = "invalid_grant", ErrorDescription = "fixture secret detail" },
            HttpStatusCode.BadRequest);

        var error = GoogleProviderErrorMapper.Map(exception, CancellationToken.None);

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, error.Category);
        Assert.True(error.ReauthenticationRequired);
        Assert.Equal(400, error.HttpStatusCode);
    }
}
