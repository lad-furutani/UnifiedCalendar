using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Infrastructure.Logging;
using UnifiedCalendar.Infrastructure.Storage;

namespace UnifiedCalendar.App;

public partial class App : Application
{
    private IHost? _host;
    private RunningApplicationMutex? _runningApplicationMutex;
    private SingleInstanceCoordinator? _singleInstance;
    private TrayIconService? _trayIcon;
    private MainWindow? _mainWindow;
    private ISettingsWindowLifetime? _settingsWindowLifetime;
    private bool _shutdownInProgress;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _runningApplicationMutex = new RunningApplicationMutex();
            _singleInstance = new SingleInstanceCoordinator(
                SingleInstanceNames.ForCurrentUser());
            if (!_singleInstance.TryAcquirePrimary())
            {
                _ = await _singleInstance.SignalPrimaryAsync();
                await _singleInstance.DisposeAsync();
                _singleInstance = null;
                Shutdown();
                return;
            }

            _host = Host.CreateDefaultBuilder(e.Args)
                .UseUnifiedCalendarSerilog()
                .ConfigureServices((context, services) =>
                    services.AddUnifiedCalendarApplication(context.Configuration))
                .Build();

            _host.Services
                .GetRequiredService<ClientCredentialResolution>()
                .LogSources(Log.Logger);

            var windowController = _host.Services.GetRequiredService<MainWindowController>();
            _singleInstance.StartListening(windowController.ShowAndActivateAsync);

            var themeService = _host.Services.GetRequiredService<StartupThemeService>();
            themeService.Apply(Resources);
            _host.Services.GetRequiredService<RuntimeThemeService>().Start(Resources);
            _host.Services.GetRequiredService<RuntimeTimeZoneService>().Start();
            _ = _host.Services.GetRequiredService<AtomicFileWriter>();
            await ApplyStartupRegistrationAsync(_host.Services);
            await _host.StartAsync();

            _mainWindow = _host.Services.GetRequiredService<MainWindow>();
            _settingsWindowLifetime = _host.Services.GetRequiredService<ISettingsWindowLifetime>();
            MainWindow = _mainWindow;
            await _mainWindow.InitializeShellAsync();
            windowController.Attach(_mainWindow);

            _trayIcon = _host.Services.GetRequiredService<TrayIconService>();
            _trayIcon.ExitRequested += OnTrayExitRequested;
            _trayIcon.Start();
            _mainWindow.Show();

            Log.Information("ApplicationStarted");
        }
        catch (Exception exception)
        {
            var safeException = ExceptionSanitizer.Sanitize(exception);
            Log.Fatal(
                "ApplicationStartupFailed {ExceptionType} {ErrorCategory}",
                safeException.ExceptionType,
                safeException.ErrorCategory);
            if (_mainWindow is not null)
            {
                try
                {
                    await _mainWindow.PrepareForApplicationExitAsync();
                }
                catch (Exception stopException)
                {
                    Log.Warning(
                        "ApplicationStopFailed {Stage} {ErrorCategory}",
                        "StartupFailure",
                        stopException.GetType().Name);
                }
            }

            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            await PrepareSettingsWindowForExitAsync("OnExit");
            if (_host is not null)
            {
                Log.Information("ApplicationStopping");
                try
                {
                    await _host.StopAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception exception)
                {
                    Log.Warning(
                        "ApplicationStopFailed {Stage} {ErrorCategory}",
                        "OnExit",
                        exception.GetType().Name);
                }
            }

            if (_mainWindow is not null)
            {
                try
                {
                    await _mainWindow.PrepareForApplicationExitAsync();
                }
                catch (Exception exception)
                {
                    Log.Warning(
                        "ApplicationStopFailed {Stage} {ErrorCategory}",
                        "WindowPlacement",
                        exception.GetType().Name);
                }
            }

            _host?.Dispose();
            _host = null;
            _settingsWindowLifetime = null;

            if (_trayIcon is not null)
            {
                _trayIcon.ExitRequested -= OnTrayExitRequested;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            if (_singleInstance is not null)
            {
                try
                {
                    await _singleInstance.DisposeAsync();
                }
                catch (Exception exception)
                {
                    Log.Warning(
                        "InstanceCoordinatorStopFailed {Stage} {ErrorCategory}",
                        "OnExit",
                        exception.GetType().Name);
                }

                _singleInstance = null;
            }
        }
        finally
        {
            Log.CloseAndFlush();
            try
            {
                base.OnExit(e);
            }
            finally
            {
                _runningApplicationMutex?.Dispose();
                _runningApplicationMutex = null;
            }
        }
    }

    internal static async Task ApplyStartupRegistrationAsync(IServiceProvider services)
    {
        try
        {
            var settings = await services
                .GetRequiredService<ISettingsStore>()
                .LoadAsync()
                .ConfigureAwait(true);
            var startupRegistration = services.GetRequiredService<IStartupRegistrationService>();
            if (startupRegistration.IsEnabled != settings.General.StartWithWindows)
            {
                startupRegistration.SetEnabled(settings.General.StartWithWindows);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(
                "StartupRegistrationFailed {Stage} {ErrorCategory}",
                "Startup",
                exception.GetType().Name);
        }
    }

    private async void OnTrayExitRequested(object? sender, EventArgs eventArgs)
    {
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        if (_trayIcon is not null)
        {
            foreach (System.Windows.Forms.ToolStripItem item in _trayIcon.ContextMenu.Items)
            {
                item.Enabled = false;
            }
        }

        await PrepareSettingsWindowForExitAsync("TrayExit");

        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                Log.Warning(
                    "ApplicationStopFailed {Stage} {ErrorCategory}",
                    "Host",
                    exception.GetType().Name);
            }
        }

        if (_mainWindow is not null)
        {
            try
            {
                await _mainWindow.PrepareForApplicationExitAsync();
            }
            catch (Exception exception)
            {
                Log.Warning(
                    "ApplicationStopFailed {Stage} {ErrorCategory}",
                    "WindowPlacement",
                    exception.GetType().Name);
            }
        }

        if (_trayIcon is not null)
        {
            _trayIcon.ExitRequested -= OnTrayExitRequested;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_singleInstance is not null)
        {
            try
            {
                await _singleInstance.DisposeAsync();
            }
            catch (Exception exception)
            {
                Log.Warning(
                    "InstanceCoordinatorStopFailed {Stage} {ErrorCategory}",
                    "TrayExit",
                    exception.GetType().Name);
            }

            _singleInstance = null;
        }

        _host?.Dispose();
        _host = null;
        _settingsWindowLifetime = null;
        Shutdown();
    }

    private async Task PrepareSettingsWindowForExitAsync(string stage)
    {
        if (_settingsWindowLifetime is null)
        {
            return;
        }

        try
        {
            await _settingsWindowLifetime.PrepareForApplicationExitAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "ApplicationStopFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }
}
