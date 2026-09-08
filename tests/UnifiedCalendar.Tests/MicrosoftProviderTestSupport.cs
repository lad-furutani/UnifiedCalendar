using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Graph;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Microsoft;

namespace UnifiedCalendar.Tests;

internal static class MicrosoftProviderSamples
{
    public static readonly Guid AccountId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static readonly CalendarAccount Account = new(
        AccountId,
        ProviderKind.Microsoft,
        "fixture-home-account",
        "Fixture Microsoft Account",
        "fixture-microsoft@example.test",
        true,
        $"microsoft/{AccountId:N}");

    public static readonly CalendarDescriptor Calendar = new(
        "fixture-primary-calendar",
        "fixture-primary-calendar",
        "Fixture Primary",
        true,
        true,
        RgbColor.Parse("#445566"));

    public static readonly TimeRangeUtc Range = new(
        DateTimeOffset.Parse("2026-08-28T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-09-04T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public static string LoadFixture(string fileName)
    {
        var resourceName = $"UnifiedCalendar.Tests.Fixtures.Microsoft.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture resource was not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static MicrosoftCalendarProvider CreateProvider(
        MicrosoftSequenceHttpMessageHandler handler,
        TimeProvider? timeProvider = null)
    {
        return new MicrosoftCalendarProvider(
            new FixedMicrosoftAuthenticationClient(),
            new FixedMicrosoftGraphClientFactory(handler, timeProvider ?? TimeProvider.System),
            new MemoryTokenStore(),
            timeProvider ?? TimeProvider.System);
    }
}

internal sealed class FixedMicrosoftAuthenticationClient : IMicrosoftAuthenticationClient
{
    private readonly MicrosoftAuthenticationResult _result;

    public FixedMicrosoftAuthenticationClient(MicrosoftAuthenticationResult? result = null)
    {
        _result = result ?? CreateDefaultResult();
    }

    public int InteractiveCallCount { get; private set; }

    public int SilentCallCount { get; private set; }

    public string? LastExpectedProviderSubjectId { get; private set; }

    public string? LastLoginHint { get; private set; }

    public Task<MicrosoftAuthenticationResult> AcquireInteractiveAsync(
        Guid internalAccountId,
        string? expectedProviderSubjectId,
        string? loginHint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InteractiveCallCount++;
        LastExpectedProviderSubjectId = expectedProviderSubjectId;
        LastLoginHint = loginHint;
        return Task.FromResult(_result);
    }

    public Task<MicrosoftAuthenticationResult> AcquireSilentAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SilentCallCount++;
        return Task.FromResult(_result);
    }

    private static MicrosoftAuthenticationResult CreateDefaultResult() => new(
        "fixture-access-token",
        MicrosoftProviderSamples.Account.ProviderSubjectId,
        MicrosoftProviderSamples.Account.DisplayName,
        MicrosoftProviderSamples.Account.Email);
}

internal sealed class FixedMicrosoftGraphClientFactory : IMicrosoftGraphApiClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly TimeProvider _timeProvider;

    public FixedMicrosoftGraphClientFactory(
        HttpMessageHandler handler,
        TimeProvider timeProvider)
    {
        _handler = handler;
        _timeProvider = timeProvider;
    }

    public IMicrosoftGraphApiClient Create(string accessToken)
    {
        var responseCapture = new MicrosoftGraphResponseCaptureHandler(_handler, _timeProvider);
        var httpClient = new HttpClient(responseCapture, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var graph = new GraphServiceClient(
            httpClient,
            new FixedAccessTokenAuthenticationProvider(accessToken),
            MicrosoftProviderOptions.GraphBaseUrl);
        return new MicrosoftGraphApiClient(
            graph,
            httpClient,
            TimeSpan.FromSeconds(30),
            _timeProvider,
            responseCapture);
    }
}

internal sealed class MicrosoftSequenceHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly List<MicrosoftRequestSnapshot> _requests = [];

    public IReadOnlyList<MicrosoftRequestSnapshot> Requests => _requests;

    public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
    }

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) =>
        _responses.Enqueue(response);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preferences = request.Headers.TryGetValues("Prefer", out var values)
            ? values.ToArray()
            : [];
        _requests.Add(new MicrosoftRequestSnapshot(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("Request URI was missing."),
            preferences,
            request.Headers.Authorization is not null));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No fake response remains.");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}

internal sealed record MicrosoftRequestSnapshot(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyList<string> Preferences,
    bool HasAuthorizationHeader);
