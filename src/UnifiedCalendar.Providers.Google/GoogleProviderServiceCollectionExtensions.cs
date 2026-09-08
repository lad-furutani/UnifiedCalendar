using System.Net;
using Google.Apis.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.Providers.Google;

public static class GoogleProviderServiceCollectionExtensions
{
    public static IServiceCollection AddUnifiedCalendarGoogleProvider(
        this IServiceCollection services,
        GoogleProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddHttpClient(GoogleProviderOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            });
        services.TryAddSingleton(options);
        services.TryAddSingleton<global::Google.Apis.Http.IHttpClientFactory>(provider =>
        {
            var handlerFactory = provider.GetRequiredService<System.Net.Http.IHttpMessageHandlerFactory>();
            return new HttpClientFromMessageHandlerFactory(_ =>
                new HttpClientFromMessageHandlerFactory.ConfiguredHttpMessageHandler(
                    handlerFactory.CreateHandler(GoogleProviderOptions.HttpClientName),
                    false,
                    false));
        });
        services.TryAddSingleton<IGoogleCalendarApiClientFactory>(provider =>
            new GoogleCalendarApiClientFactory(
                provider.GetRequiredService<ITokenStore>(),
                provider.GetRequiredService<GoogleProviderOptions>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<global::Google.Apis.Http.IHttpClientFactory>()));
        services.TryAddSingleton<GoogleCalendarProvider>(provider =>
            new GoogleCalendarProvider(
                provider.GetRequiredService<IGoogleCalendarApiClientFactory>(),
                provider.GetRequiredService<ITokenStore>()));
        services.AddSingleton<ICalendarProvider>(provider =>
            provider.GetRequiredService<GoogleCalendarProvider>());
        return services;
    }
}
