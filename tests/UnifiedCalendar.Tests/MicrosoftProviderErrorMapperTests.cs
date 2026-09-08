using System.Net;
using Microsoft.Identity.Client;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Microsoft;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class MicrosoftProviderErrorMapperTests
{
    [Theory]
    [InlineData(401, ProviderErrorCategory.AuthenticationRequired, true)]
    [InlineData(403, ProviderErrorCategory.PermissionDenied, false)]
    [InlineData(404, ProviderErrorCategory.NotFound, false)]
    [InlineData(408, ProviderErrorCategory.Timeout, false)]
    [InlineData(429, ProviderErrorCategory.RateLimited, false)]
    [InlineData(500, ProviderErrorCategory.ServerError, false)]
    [InlineData(503, ProviderErrorCategory.ServerError, false)]
    public void HttpStatus_IsMappedWithoutGraphExceptionLeakage(
        int statusCode,
        ProviderErrorCategory expected,
        bool reauthenticationRequired)
    {
        var exception = new HttpRequestException(
            "fixture",
            inner: null,
            (HttpStatusCode)statusCode);

        var error = MicrosoftProviderErrorMapper.Map(exception, CancellationToken.None);

        Assert.Equal(expected, error.Category);
        Assert.Equal(statusCode, error.HttpStatusCode);
        Assert.Equal(reauthenticationRequired, error.ReauthenticationRequired);
    }

    [Fact]
    public void RetryAfter_DeltaSecondsAndHttpDateAreSupported()
    {
        var now = DateTimeOffset.Parse("2026-08-29T01:02:03Z");
        var timeProvider = new MutableTimeProvider(now);
        var delta = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Retry-After"] = ["17"],
        };
        var date = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Retry-After"] = [now.AddSeconds(41).ToString("R")],
        };

        Assert.Equal(TimeSpan.FromSeconds(17), MicrosoftProviderErrorMapper.GetRetryAfter(delta, timeProvider));
        Assert.Equal(TimeSpan.FromSeconds(41), MicrosoftProviderErrorMapper.GetRetryAfter(date, timeProvider));
    }

    [Fact]
    public void InteractionRequired_RequiresReauthentication()
    {
        var error = MicrosoftProviderErrorMapper.Map(
            new MsalUiRequiredException("interaction_required", "fixture"),
            CancellationToken.None);

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, error.Category);
        Assert.True(error.ReauthenticationRequired);
    }

    [Fact]
    public void InvalidGrant_RequiresReauthentication()
    {
        var error = MicrosoftProviderErrorMapper.Map(
            new MsalServiceException("invalid_grant", "fixture"),
            CancellationToken.None);

        Assert.Equal(ProviderErrorCategory.AuthenticationRequired, error.Category);
        Assert.True(error.ReauthenticationRequired);
    }

    [Fact]
    public void NetworkCallerCancellationAndTimeoutRemainDistinct()
    {
        Assert.Equal(
            ProviderErrorCategory.Network,
            MicrosoftProviderErrorMapper.Map(
                new HttpRequestException("fixture network"),
                CancellationToken.None).Category);
        Assert.Equal(
            ProviderErrorCategory.Timeout,
            MicrosoftProviderErrorMapper.Map(
                new TaskCanceledException("fixture timeout"),
                CancellationToken.None).Category);

        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.Equal(
            ProviderErrorCategory.Cancelled,
            MicrosoftProviderErrorMapper.Map(
                new OperationCanceledException(caller.Token),
                caller.Token).Category);
    }
}
