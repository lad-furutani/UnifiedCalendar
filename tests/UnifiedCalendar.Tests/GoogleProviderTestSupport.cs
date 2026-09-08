using System.Net;
using System.Reflection;
using System.Text;
using Google.Apis.Calendar.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Google;

namespace UnifiedCalendar.Tests;

internal static class GoogleProviderSamples
{
    public static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly CalendarAccount Account = new(
        AccountId,
        ProviderKind.Google,
        "fixture-subject",
        "Fixture Account",
        "fixture-account@example.test",
        true,
        $"google/{AccountId:N}");

    public static readonly CalendarDescriptor Calendar = new(
        "fixture-calendar@example.test",
        "fixture-calendar@example.test",
        "Fixture Calendar",
        false,
        true,
        RgbColor.Parse("#445566"));

    public static readonly TimeRangeUtc Range = new(
        DateTimeOffset.Parse("2026-08-28T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-09-04T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public static string LoadFixture(string fileName)
    {
        var resourceName = $"UnifiedCalendar.Tests.Fixtures.Google.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Fixture resource was not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static IGoogleCalendarApiClient CreateHttpClient(
        SequenceHttpMessageHandler handler,
        TimeProvider? timeProvider = null)
    {
        var httpFactory = CreateHttpFactory(handler);
        var service = new CalendarService(new BaseClientService.Initializer
        {
            ApplicationName = "UnifiedCalendar.ProviderContractTests",
            HttpClientFactory = httpFactory,
            HttpClientTimeout = TimeSpan.FromSeconds(30),
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None,
        });
        return new GoogleCalendarApiClient(service, timeProvider);
    }

    public static global::Google.Apis.Http.IHttpClientFactory CreateHttpFactory(
        SequenceHttpMessageHandler handler) =>
        new HttpClientFromMessageHandlerFactory(_ =>
            new HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(handler, false, false));
}

internal sealed class SequenceHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    private readonly List<Uri> _requestedUris = [];
    private readonly List<bool> _hadAuthorizationHeaders = [];

    public IReadOnlyList<Uri> RequestedUris => _requestedUris;

    public IReadOnlyList<bool> HadAuthorizationHeaders => _hadAuthorizationHeaders;

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
        _requestedUris.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI was missing."));
        _hadAuthorizationHeaders.Add(request.Headers.Authorization is not null);
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No fake response remains.");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}

internal sealed class MemoryTokenStore : ITokenStore
{
    public byte[]? Payload { get; private set; }

    public ProviderKind? LastProvider { get; private set; }

    public Guid? LastAccountId { get; private set; }

    public TokenQuarantineReason? LastQuarantineReason { get; private set; }

    public Task<byte[]?> ReadAsync(
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastProvider = provider;
        LastAccountId = internalAccountId;
        return Task.FromResult(Payload?.ToArray());
    }

    public Task WriteAsync(
        ProviderKind provider,
        Guid internalAccountId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastProvider = provider;
        LastAccountId = internalAccountId;
        Payload = payload.ToArray();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(
        ProviderKind provider,
        Guid internalAccountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastProvider = provider;
        LastAccountId = internalAccountId;
        Payload = null;
        return Task.CompletedTask;
    }

    public Task QuarantineAsync(
        ProviderKind provider,
        Guid internalAccountId,
        TokenQuarantineReason reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastProvider = provider;
        LastAccountId = internalAccountId;
        LastQuarantineReason = reason;
        Payload = null;
        return Task.CompletedTask;
    }
}

internal sealed class FixedGoogleClientFactory : IGoogleCalendarApiClientFactory
{
    private readonly Func<IGoogleCalendarApiClient> _create;

    public FixedGoogleClientFactory(Func<IGoogleCalendarApiClient> create)
    {
        _create = create;
    }

    public Task<IGoogleCalendarApiClient> CreateStoredAsync(
        CalendarAccount account,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_create());
    }

    public Task<IGoogleInteractiveCalendarSession> CreateInteractiveAsync(
        Guid internalAccountId,
        GoogleInteractiveAuthorizationMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IGoogleInteractiveCalendarSession>(
            new FixedGoogleInteractiveCalendarSession(_create()));
    }
}

internal sealed class FixedGoogleInteractiveCalendarSession : IGoogleInteractiveCalendarSession
{
    private readonly Func<CancellationToken, Task> _commit;

    public FixedGoogleInteractiveCalendarSession(
        IGoogleCalendarApiClient client,
        Func<CancellationToken, Task>? commit = null)
    {
        Client = client;
        _commit = commit ?? (_ => Task.CompletedTask);
    }

    public IGoogleCalendarApiClient Client { get; }

    public Task CommitTokenAsync(CancellationToken cancellationToken) => _commit(cancellationToken);

    public void Dispose() => Client.Dispose();
}
