using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Sync;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Infrastructure.Logging;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class CompositionRootTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AccountInteractionRegistrationTracksEachConfiguredProvider(
        bool googleConfigured,
        bool microsoftConfigured)
    {
        var credentials = new ClientCredentialResolution(
            googleConfigured
                ? new GoogleClientCredentials(
                    "fixture-google-client-id",
                    "fixture-google-client-secret")
                : null,
            googleConfigured
                ? ClientCredentialSource.Configuration
                : ClientCredentialSource.NotConfigured,
            microsoftConfigured
                ? new MicrosoftClientCredentials("fixture-microsoft-client-id")
                : null,
            microsoftConfigured
                ? ClientCredentialSource.Configuration
                : ClientCredentialSource.NotConfigured);
        var services = new ServiceCollection();
        services.AddUnifiedCalendarApplication(credentials);
        using var provider = services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IAccountRegistrationService>();
        var reauthentication = provider.GetRequiredService<IAccountReauthenticationService>();

        Assert.Equal(googleConfigured || microsoftConfigured, registration.IsAvailable);
        Assert.Equal(googleConfigured, registration.IsProviderAvailable(ProviderKind.Google));
        Assert.Equal(microsoftConfigured, registration.IsProviderAvailable(ProviderKind.Microsoft));
        if (googleConfigured || microsoftConfigured)
        {
            Assert.IsType<AccountInteractionService>(registration);
            Assert.Same(registration, reauthentication);
        }
        else
        {
            Assert.IsType<UnavailableAccountInteractionService>(registration);
            Assert.Same(registration, reauthentication);
        }
    }

    [Fact]
    public void AddUnifiedCalendarApplication_RegistersSystemTimeProviderAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddUnifiedCalendarApplication();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        var first = provider.GetRequiredService<TimeProvider>();
        var second = provider.GetRequiredService<TimeProvider>();

        Assert.Same(TimeProvider.System, first);
        Assert.Same(first, second);
    }

    [Fact]
    public void MainWindowViewModel_CanBeResolvedFromCompositionRoot()
    {
        var services = new ServiceCollection();
        services.AddUnifiedCalendarApplication();
        using var serviceProvider = services.BuildServiceProvider();

        var viewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();

        Assert.IsType<MainWindowViewModel>(viewModel);
    }

    [Fact]
    public void AddUnifiedCalendarApplication_RegistersPresentationServiceAsSingleton()
    {
        var services = new ServiceCollection();

        services.AddUnifiedCalendarApplication();

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<CalendarPresentationService>(),
            provider.GetRequiredService<CalendarPresentationService>());
    }

    [Fact]
    public async Task AddUnifiedCalendarApplication_RegistersPhase5Services()
    {
        var services = new ServiceCollection();

        services.AddUnifiedCalendarApplication();

        await using var provider = services.BuildServiceProvider();
        Assert.Same(
            provider.GetRequiredService<CalendarSyncService>(),
            provider.GetRequiredService<ICalendarSyncService>());
        Assert.Same(
            provider.GetRequiredService<InternalRefreshSignal>(),
            provider.GetRequiredService<IInternalRefreshRequester>());
        Assert.IsType<WindowsSystemResumeSignal>(provider.GetRequiredService<ISystemResumeSignal>());
        Assert.IsType<SyncEventLogger>(provider.GetRequiredService<ISyncEventLogger>());
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Contains(hostedServices, value => value is SyncSchedulerHostedService);
        Assert.Contains(hostedServices, value => value is MinuteRefreshHostedService);
    }

    [Fact]
    public void PersistenceRegistrationDoesNotOwnSyncLogging()
    {
        var services = new ServiceCollection();

        services.AddUnifiedCalendarPersistence();

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<ISyncEventLogger>());
    }
}
