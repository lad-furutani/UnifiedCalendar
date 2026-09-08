using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Providers.Google;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class GoogleProviderBoundaryTests
{
    [Fact]
    public void CoreAndAppPublicApis_DoNotExposeGoogleSdkTypes()
    {
        var assemblies = new[]
        {
            typeof(CalendarEvent).Assembly,
            typeof(UnifiedCalendar.App.ServiceCollectionExtensions).Assembly,
        };

        Assert.DoesNotContain(
            typeof(CalendarEvent).Assembly.GetReferencedAssemblies(),
            reference => reference.Name?.StartsWith("Google.", StringComparison.Ordinal) == true);
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                Assert.False(IsGoogleType(type));
                foreach (var method in type.GetMethods())
                {
                    Assert.False(IsGoogleType(method.ReturnType));
                    Assert.DoesNotContain(method.GetParameters(), parameter => IsGoogleType(parameter.ParameterType));
                }

                foreach (var property in type.GetProperties())
                {
                    Assert.False(IsGoogleType(property.PropertyType));
                }
            }
        }
    }

    [Fact]
    public void GoogleProviderPublicApi_DoesNotExposeGoogleSdkTypes()
    {
        foreach (var type in typeof(GoogleCalendarProvider).Assembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods())
            {
                Assert.False(IsGoogleType(method.ReturnType));
                Assert.DoesNotContain(method.GetParameters(), parameter => IsGoogleType(parameter.ParameterType));
            }
        }
    }

    [Fact]
    public void ApplicationComposition_RegistersGoogleOnlyWhenCredentialPairIsResolved()
    {
        var withoutConfiguration = new ServiceCollection();
        withoutConfiguration.AddUnifiedCalendarApplication(new ClientCredentialResolution(
            null,
            ClientCredentialSource.NotConfigured,
            null,
            ClientCredentialSource.NotConfigured));
        using var withoutProvider = withoutConfiguration.BuildServiceProvider();
        Assert.Empty(withoutProvider.GetServices<ICalendarProvider>());

        var google = new GoogleClientCredentials(
            "fixture-client-id.apps.example.test",
            "fixture-public-client-metadata");
        var configured = new ServiceCollection();
        configured.AddUnifiedCalendarApplication(new ClientCredentialResolution(
            google,
            ClientCredentialSource.Configuration,
            null,
            ClientCredentialSource.NotConfigured));
        using var configuredProvider = configured.BuildServiceProvider();

        var provider = Assert.Single(configuredProvider.GetServices<ICalendarProvider>());
        Assert.IsType<GoogleCalendarProvider>(provider);
        var options = configuredProvider.GetRequiredService<GoogleProviderOptions>();
        Assert.Equal(google.ClientId, options.ClientId);
        Assert.Equal(google.ClientSecret, options.ClientSecret);
        Assert.Equal(GoogleProviderOptions.CalendarReadOnlyScope,
            "https://www.googleapis.com/auth/calendar.readonly");
    }

    [Fact]
    public async Task ProviderLogs_ContainOnlySafeIdentifiersAndMetadata()
    {
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var handler = new SequenceHttpMessageHandler();
        handler.EnqueueJson("""
            {"items":[{
              "id":"sensitive-event-id-fixture",
              "summary":"sensitive-title-fixture",
              "description":"sensitive-description-fixture",
              "location":"sensitive-location-fixture",
              "htmlLink":"https://sensitive.example.test/event",
              "status":"confirmed",
              "start":{"dateTime":"2026-08-28T10:00:00Z"},
              "end":{"dateTime":"2026-08-28T11:00:00Z"}
            },{
              "id":"sensitive-invalid-event-id-fixture",
              "summary":"sensitive-invalid-title-fixture",
              "description":"sensitive-invalid-description-fixture",
              "location":"sensitive-invalid-location-fixture",
              "htmlLink":"https://sensitive-invalid.example.test/event",
              "status":"confirmed",
              "start":{"dateTime":"2026-08-28T12:00:00Z"},
              "end":{"dateTime":"2026-08-28T11:00:00Z"}
            }]}
            """);
        var provider = new GoogleCalendarProvider(
            new FixedGoogleClientFactory(() => GoogleProviderSamples.CreateHttpClient(handler)),
            Substitute.For<ITokenStore>(),
            new GoogleProviderLogger(logger));

        var result = await provider.GetEventsAsync(
            GoogleProviderSamples.Account,
            GoogleProviderSamples.Calendar,
            GoogleProviderSamples.Range,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Events);
        var rendered = string.Join("\n", sink.Events.Select(logEvent => logEvent.RenderMessage()));
        Assert.DoesNotContain(GoogleProviderSamples.Calendar.CalendarId, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-event-id-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-title-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-description-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-location-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("https://sensitive.example.test/event", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-invalid-event-id-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-invalid-title-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-invalid-description-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-invalid-location-fixture", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("https://sensitive-invalid.example.test/event", rendered, StringComparison.Ordinal);
        Assert.Contains(GoogleProviderSamples.AccountId.ToString(), rendered, StringComparison.Ordinal);
        Assert.Contains(nameof(GoogleEventExclusionReason.InvalidTiming), rendered, StringComparison.Ordinal);
        var exclusionLog = Assert.Single(sink.Events, logEvent =>
            logEvent.Properties.ContainsKey("ExclusionReason"));
        Assert.Equal("1", exclusionLog.Properties["Count"].ToString());
    }

    private static bool IsGoogleType(Type type)
    {
        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(IsGoogleType);
        }

        if (type.IsArray)
        {
            return IsGoogleType(type.GetElementType()!);
        }

        return type.Namespace?.StartsWith("Google", StringComparison.Ordinal) == true;
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
