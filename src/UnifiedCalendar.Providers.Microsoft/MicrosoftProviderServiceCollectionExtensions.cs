using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;

namespace UnifiedCalendar.Providers.Microsoft;

public static class MicrosoftProviderServiceCollectionExtensions
{
    public static IServiceCollection AddUnifiedCalendarMicrosoftProvider(
        this IServiceCollection services,
        MicrosoftProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddHttpClient(MicrosoftProviderOptions.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
            });
        services.TryAddSingleton(options);
        services.TryAddSingleton<IMicrosoftAuthenticationClient>(provider =>
            new MicrosoftAuthenticationClient(
                provider.GetRequiredService<ITokenStore>(),
                provider.GetRequiredService<MicrosoftProviderOptions>(),
                provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IMicrosoftGraphApiClientFactory>(provider =>
            new MicrosoftGraphApiClientFactory(
                provider.GetRequiredService<IHttpMessageHandlerFactory>(),
                provider.GetRequiredService<MicrosoftProviderOptions>(),
                provider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<MicrosoftCalendarProvider>(provider =>
            new MicrosoftCalendarProvider(
                provider.GetRequiredService<IMicrosoftAuthenticationClient>(),
                provider.GetRequiredService<IMicrosoftGraphApiClientFactory>(),
                provider.GetRequiredService<ITokenStore>(),
                provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICalendarProvider>(provider =>
            provider.GetRequiredService<MicrosoftCalendarProvider>());
        return services;
    }
}
