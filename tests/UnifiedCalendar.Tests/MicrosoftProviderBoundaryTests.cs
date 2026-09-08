using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Microsoft;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class MicrosoftProviderBoundaryTests
{
    [Fact]
    public void CoreAppAndProviderPublicApis_DoNotExposeGraphOrMsalTypes()
    {
        var assemblies = new[]
        {
            typeof(CalendarEvent).Assembly,
            typeof(UnifiedCalendar.App.ServiceCollectionExtensions).Assembly,
            typeof(MicrosoftCalendarProvider).Assembly,
        };

        Assert.DoesNotContain(
            typeof(CalendarEvent).Assembly.GetReferencedAssemblies(),
            IsMicrosoftSdkAssembly);
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                Assert.False(IsMicrosoftSdkType(type));
                foreach (var method in type.GetMethods())
                {
                    Assert.False(IsMicrosoftSdkType(method.ReturnType));
                    Assert.DoesNotContain(method.GetParameters(), parameter => IsMicrosoftSdkType(parameter.ParameterType));
                }

                foreach (var property in type.GetProperties())
                {
                    Assert.False(IsMicrosoftSdkType(property.PropertyType));
                }
            }
        }
    }

    [Fact]
    public void ApplicationComposition_RegistersMicrosoftOnlyWhenClientIdIsConfigured()
    {
        var withoutConfiguration = new ServiceCollection();
        withoutConfiguration.AddUnifiedCalendarApplication(new ClientCredentialResolution(
            null,
            ClientCredentialSource.NotConfigured,
            null,
            ClientCredentialSource.NotConfigured));
        using var withoutProvider = withoutConfiguration.BuildServiceProvider();
        Assert.Empty(withoutProvider.GetServices<ICalendarProvider>());

        var microsoft = new MicrosoftClientCredentials("fixture-microsoft-client-id");
        var configured = new ServiceCollection();
        configured.AddUnifiedCalendarApplication(new ClientCredentialResolution(
            null,
            ClientCredentialSource.NotConfigured,
            microsoft,
            ClientCredentialSource.Configuration));
        using var provider = configured.BuildServiceProvider();

        Assert.IsType<MicrosoftCalendarProvider>(Assert.Single(provider.GetServices<ICalendarProvider>()));
        var options = provider.GetRequiredService<MicrosoftProviderOptions>();
        Assert.Equal("fixture-microsoft-client-id", options.ClientId);
        Assert.Equal("https://login.microsoftonline.com/common", MicrosoftProviderOptions.Authority);
        Assert.Equal("http://localhost", MicrosoftProviderOptions.RedirectUri);
        Assert.Equal("Calendars.Read", MicrosoftProviderOptions.CalendarsReadScope);
        Assert.Equal("MailboxSettings.Read", MicrosoftProviderOptions.MailboxSettingsReadScope);
        Assert.Equal(
            ["Calendars.Read", "MailboxSettings.Read"],
            MicrosoftProviderOptions.Scopes);
        Assert.DoesNotContain("User.Read", MicrosoftProviderOptions.Scopes);
        Assert.DoesNotContain(".default", MicrosoftProviderOptions.Scopes);
        Assert.Equal(TimeSpan.FromMinutes(10), options.AuthorizationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ApiTimeout);
    }

    [Fact]
    public void ProviderSourceContainsNoBetaEndpointOrWriteScopeOrWriteRequest()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "UnifiedCalendar.Providers.Microsoft");
        var source = string.Join('\n', Directory.GetFiles(directory, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("/beta", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Calendars.ReadWrite", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MailboxSettings.ReadWrite", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".PostAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".PatchAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".DeleteAsync(", source, StringComparison.Ordinal);
        Assert.Contains("Calendars.Read", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Calendars.Read.Shared", source, StringComparison.Ordinal);
        Assert.DoesNotContain("User.Read", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".default", source, StringComparison.Ordinal);
        Assert.Contains("MailboxSettings.Read", source, StringComparison.Ordinal);
        Assert.Contains("graph.microsoft.com/v1.0", source, StringComparison.Ordinal);
        Assert.Contains("login.microsoftonline.com/common", source, StringComparison.Ordinal);
        Assert.Contains("http://localhost", source, StringComparison.Ordinal);
        Assert.Contains("WithUseEmbeddedWebView(false)", source, StringComparison.Ordinal);
        Assert.Contains("AcquireTokenSilent", source, StringComparison.Ordinal);
        Assert.Contains("WithAccount", source, StringComparison.Ordinal);
        Assert.Contains("WithLoginHint", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Prompt.SelectAccount", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalSynchronization_UsesSilentAuthenticationOnly()
    {
        var authentication = new FixedMicrosoftAuthenticationClient();
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson("{\"value\":[]}");
        var provider = new MicrosoftCalendarProvider(
            authentication,
            new FixedMicrosoftGraphClientFactory(handler, TimeProvider.System),
            new MemoryTokenStore(),
            TimeProvider.System);

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, authentication.SilentCallCount);
        Assert.Equal(0, authentication.InteractiveCallCount);
    }

    [Fact]
    public async Task ApiRequestTimesOutAtThirtySecondsUsingInjectedTimeProvider()
    {
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero));
        var handler = new BlockingMicrosoftHttpMessageHandler();
        var provider = new MicrosoftCalendarProvider(
            new FixedMicrosoftAuthenticationClient(),
            new FixedMicrosoftGraphClientFactory(handler, timeProvider),
            new MemoryTokenStore(),
            timeProvider);

        var request = provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);
        await handler.Started;

        timeProvider.Advance(TimeSpan.FromMilliseconds(29_999));
        Assert.False(request.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        var result = await request;

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorCategory.Timeout, result.Error!.Category);
    }

    [Fact]
    public async Task ProviderLogs_ExcludePiiSecretsAndRawIdentifiersButIncludeSafeReasonCounts()
    {
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var handler = new MicrosoftSequenceHttpMessageHandler();
        handler.EnqueueJson("""
            {"value":[{
              "id":"sensitive-event-id-fixture",
              "subject":"sensitive-title-fixture",
              "body":{"contentType":"text","content":"sensitive-description-fixture"},
              "location":{"displayName":"sensitive-location-fixture"},
              "webLink":"https://sensitive.example.test/event",
              "start":{"dateTime":"2026-08-28T10:00:00Z","timeZone":"UTC"},
              "end":{"dateTime":"2026-08-28T11:00:00Z","timeZone":"UTC"},
              "isAllDay":false,"isCancelled":false
            },{
              "id":"sensitive-invalid-event-id-fixture",
              "subject":"sensitive-invalid-title-fixture",
              "body":{"contentType":"text","content":"sensitive-invalid-description-fixture"},
              "location":{"displayName":"sensitive-invalid-location-fixture"},
              "start":{"dateTime":"2026-08-28T12:00:00Z","timeZone":"UTC"},
              "end":{"dateTime":"2026-08-28T11:00:00Z","timeZone":"UTC"},
              "isAllDay":false,"isCancelled":false
            }]}
            """);
        var provider = new MicrosoftCalendarProvider(
            new FixedMicrosoftAuthenticationClient(),
            new FixedMicrosoftGraphClientFactory(handler, TimeProvider.System),
            new MemoryTokenStore(),
            TimeProvider.System,
            new MicrosoftProviderLogger(logger));

        var result = await provider.GetEventsAsync(
            MicrosoftProviderSamples.Account,
            MicrosoftProviderSamples.Calendar,
            MicrosoftProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Events);
        var rendered = string.Join('\n', sink.Events.Select(value => value.RenderMessage()));
        Assert.DoesNotContain(MicrosoftProviderSamples.Calendar.CalendarId, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-event-id-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-title-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-description-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-location-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("https://sensitive.example.test/event", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-access-token", rendered, StringComparison.Ordinal);
        Assert.Contains(MicrosoftProviderSamples.AccountId.ToString(), rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(MicrosoftEventExclusionReason.InvalidTiming), rendered, StringComparison.Ordinal);
        var exclusion = Assert.Single(sink.Events, value => value.Properties.ContainsKey("ExclusionReason"));
        Assert.Equal("1", exclusion.Properties["Count"].ToString());
    }

    private static bool IsMicrosoftSdkAssembly(System.Reflection.AssemblyName reference) =>
        reference.Name?.Equals("Microsoft.Graph", StringComparison.Ordinal) == true
        || reference.Name?.Equals("Microsoft.Identity.Client", StringComparison.Ordinal) == true;

    private static bool IsMicrosoftSdkType(Type type)
    {
        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(IsMicrosoftSdkType);
        }

        if (type.IsArray)
        {
            return IsMicrosoftSdkType(type.GetElementType()!);
        }

        return type.Namespace?.StartsWith("Microsoft.Graph", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("Microsoft.Identity.Client", StringComparison.Ordinal) == true;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UnifiedCalendar.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class BlockingMicrosoftHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The fake request should end through cancellation.");
        }
    }
}
