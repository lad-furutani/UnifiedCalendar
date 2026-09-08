using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.App.Sync;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Core.Time;
using UnifiedCalendar.Infrastructure.Logging;
using UnifiedCalendar.Infrastructure.Storage;
using UnifiedCalendar.Providers.Google;
using UnifiedCalendar.Providers.Microsoft;

namespace UnifiedCalendar.App;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUnifiedCalendarApplication(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var credentials = new ClientCredentialResolver(configuration).Resolve();
        return services.AddUnifiedCalendarApplication(credentials);
    }

    internal static IServiceCollection AddUnifiedCalendarApplication(
        this IServiceCollection services,
        ClientCredentialResolution credentials)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(credentials);

        services.AddSingleton(credentials);
        services.AddSingleton(TimeProvider.System);
        services.AddUnifiedCalendarPersistence();
        services.AddSingleton<IApplicationSettingsService, ApplicationSettingsService>();
        services.AddSingleton<ISyncEventLogger, SyncEventLogger>();
        services.AddSingleton(provider => new CalendarPresentationService(
            provider.GetRequiredService<TimeProvider>(),
            ColorMetrics.ProgressLightnessDelta));
        services.AddSingleton(provider => new CalendarSyncService(
            provider.GetServices<ICalendarProvider>(),
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<ICacheStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILocalTimeZoneProvider>(),
            provider.GetRequiredService<ISyncEventLogger>()));
        services.AddSingleton<ICalendarSyncService>(provider =>
            provider.GetRequiredService<CalendarSyncService>());
        services.AddSingleton<InternalRefreshSignal>();
        services.AddSingleton<IInternalRefreshRequester>(provider =>
            provider.GetRequiredService<InternalRefreshSignal>());
        services.AddSingleton<ISystemResumeSignal, WindowsSystemResumeSignal>();
        services.AddHostedService<SyncSchedulerHostedService>();
        services.AddHostedService<MinuteRefreshHostedService>();
        services.AddSingleton<IUiTextService, ResourceUiTextService>();
        services.AddSingleton(provider => new WpfUiDispatcher(
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher));
        services.AddSingleton<IUiDispatcher>(provider =>
            provider.GetRequiredService<WpfUiDispatcher>());
        services.AddSingleton<UnavailableAccountInteractionService>();
        services.AddSingleton<AccountInteractionService>();
        services.AddSingleton<IAccountRegistrationService>(provider =>
            provider.GetServices<ICalendarProvider>().Any()
                ? provider.GetRequiredService<AccountInteractionService>()
                : provider.GetRequiredService<UnavailableAccountInteractionService>());
        services.AddSingleton<IAccountReauthenticationService>(provider =>
            provider.GetServices<ICalendarProvider>().Any()
                ? provider.GetRequiredService<AccountInteractionService>()
                : provider.GetRequiredService<UnavailableAccountInteractionService>());
        services.AddSingleton<ICalendarCatalogService, CalendarCatalogService>();
        services.AddSingleton<IExternalUriLauncher, ExternalUriLauncher>();
        services.AddSingleton<IApplicationInfoProvider, AssemblyApplicationInfoProvider>();
        services.AddSingleton<ILocalPathOpenAdapter, ShellLocalPathOpenAdapter>();
        services.AddSingleton<IAppLocalPathLauncher, AppLocalPathLauncher>();
        services.AddSingleton<IColorPickerService, WindowsColorPickerService>();
        services.AddSingleton<MessageBoxSettingsConfirmationService>();
        services.AddSingleton<ISettingsConfirmationService>(provider =>
            provider.GetRequiredService<MessageBoxSettingsConfirmationService>());
        services.AddSingleton<ISettingsResetConfirmationService>(provider =>
            provider.GetRequiredService<MessageBoxSettingsConfirmationService>());
        services.AddSingleton<IColorRuleDeleteConfirmationService>(provider =>
            provider.GetRequiredService<MessageBoxSettingsConfirmationService>());
        services.AddSingleton<IAccountDeleteConfirmationService>(provider =>
            provider.GetRequiredService<MessageBoxSettingsConfirmationService>());
        services.AddTransient<AccountSettingsViewModel>();
        services.AddTransient<CalendarSelectionSettingsViewModel>();
        services.AddSingleton<ColorRulesSettingsViewModel>();
        services.AddSingleton<IPendingSettingsChanges>(provider =>
            provider.GetRequiredService<ColorRulesSettingsViewModel>());
        services.AddSingleton<SettingsWindowPlacementService>();
        services.AddSingleton<ISettingsWindowFactory, SettingsWindowFactory>();
        services.AddSingleton<ISettingsWindowActivationService, SettingsWindowActivationService>();
        services.AddSingleton<SettingsWindowLauncher>();
        services.AddSingleton<ISettingsWindowLauncher>(provider =>
            provider.GetRequiredService<SettingsWindowLauncher>());
        services.AddSingleton<ISettingsWindowLifetime>(provider =>
            provider.GetRequiredService<SettingsWindowLauncher>());
        services.AddSingleton<IStartupRegistrationService, WindowsStartupRegistrationService>();
        services.AddSingleton<IThemePreferenceReader, WindowsThemePreferenceReader>();
        services.AddSingleton<StartupThemeService>();
        services.AddSingleton<IThemeChangeSignal, SystemThemeChangeSignal>();
        services.AddSingleton<RuntimeThemeService>();
        services.AddSingleton<WindowsLocalTimeZoneProvider>();
        services.AddSingleton<ILocalTimeZoneProvider>(provider =>
            provider.GetRequiredService<WindowsLocalTimeZoneProvider>());
        services.AddSingleton<IRefreshableLocalTimeZoneProvider>(provider =>
            provider.GetRequiredService<WindowsLocalTimeZoneProvider>());
        services.AddSingleton<ITimeZoneChangeSignal, SystemTimeZoneChangeSignal>();
        services.AddSingleton<RuntimeTimeZoneService>();
        services.AddSingleton<IMonitorProvider, WindowsMonitorProvider>();
        services.AddSingleton(provider => new MainWindowPlacementService(
            provider.GetRequiredService<IApplicationSettingsService>(),
            provider.GetRequiredService<IMonitorProvider>()));
        services.AddSingleton<MainWindowController>();
        services.AddSingleton<IMainWindowController>(provider =>
            provider.GetRequiredService<MainWindowController>());
        services.AddSingleton<ITrayIconAdapter, NotifyIconAdapter>();
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<BrushCache>();
        services.AddSingleton<SnapshotDiffer>();
        services.AddSingleton<TimeColumnWidthCalculator>();
        services.AddSingleton<TimelineViewportCoordinator>();
        services.AddSingleton<ITimelineViewport>(provider =>
            provider.GetRequiredService<TimelineViewportCoordinator>());
        services.AddSingleton(provider => new MainWindowViewModel(
            provider.GetRequiredService<ICalendarSyncService>(),
            provider.GetRequiredService<InternalRefreshSignal>(),
            provider.GetRequiredService<CalendarPresentationService>(),
            provider.GetRequiredService<IApplicationSettingsService>(),
            provider.GetRequiredService<IUiDispatcher>(),
            provider.GetRequiredService<IUiTextService>(),
            provider.GetRequiredService<IAccountRegistrationService>(),
            provider.GetRequiredService<IAccountReauthenticationService>(),
            provider.GetRequiredService<IExternalUriLauncher>(),
            provider.GetRequiredService<BrushCache>(),
            provider.GetRequiredService<SnapshotDiffer>(),
            provider.GetRequiredService<ITimelineViewport>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILocalTimeZoneProvider>(),
            provider.GetRequiredService<ISettingsWindowLauncher>()));
        services.AddSingleton<MainWindow>();

        if (credentials.Google is not null)
        {
            services.AddUnifiedCalendarGoogleProvider(new GoogleProviderOptions(
                credentials.Google.ClientId,
                credentials.Google.ClientSecret));
        }

        if (credentials.Microsoft is not null)
        {
            services.AddUnifiedCalendarMicrosoftProvider(
                new MicrosoftProviderOptions(credentials.Microsoft.ClientId));
        }

        return services;
    }
}
