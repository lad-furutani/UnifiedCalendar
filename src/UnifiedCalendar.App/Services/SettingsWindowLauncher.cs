using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using UnifiedCalendar.App.ViewModels;

namespace UnifiedCalendar.App.Services;

public interface ISettingsWindowLauncher
{
    bool IsAvailable { get; }

    void Show();
}

public sealed class UnavailableSettingsWindowLauncher : ISettingsWindowLauncher
{
    public bool IsAvailable => false;

    public void Show() => throw new InvalidOperationException("The settings window is not available.");
}

public interface ISettingsWindowLifetime
{
    Task PrepareForApplicationExitAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsWindowFactory
{
    SettingsWindow Create();
}

public sealed class SettingsWindowFactory : ISettingsWindowFactory
{
    private readonly IServiceProvider _services;

    public SettingsWindowFactory(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public SettingsWindow Create()
    {
        var viewModel = ActivatorUtilities.CreateInstance<SettingsWindowViewModel>(_services);
        var window = ActivatorUtilities.CreateInstance<SettingsWindow>(_services, viewModel);
        window.Owner = _services.GetRequiredService<MainWindow>();
        return window;
    }
}

public interface ISettingsWindowActivationService
{
    void Show(SettingsWindow window);

    void Activate(SettingsWindow window);
}

public sealed class SettingsWindowActivationService : ISettingsWindowActivationService
{
    public void Show(SettingsWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Show();
    }

    public void Activate(SettingsWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        _ = window.Activate();
    }
}

public sealed class SettingsWindowLauncher : ISettingsWindowLauncher, ISettingsWindowLifetime
{
    private readonly ISettingsWindowFactory _factory;
    private readonly ISettingsWindowActivationService _activationService;
    private readonly IUiDispatcher _dispatcher;
    private SettingsWindow? _window;
    private bool _initializing;
    private bool _activateWhenReady;

    public SettingsWindowLauncher(
        ISettingsWindowFactory factory,
        ISettingsWindowActivationService activationService,
        IUiDispatcher dispatcher)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _activationService = activationService
            ?? throw new ArgumentNullException(nameof(activationService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool IsAvailable => true;

    public void Show() => Observe(
        _dispatcher.InvokeAsync(ShowCoreAsync),
        "Show");

    public Task PrepareForApplicationExitAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(
            async () =>
            {
                if (_window is not null)
                {
                    await _window.PrepareForApplicationExitAsync(cancellationToken).ConfigureAwait(true);
                }
            },
            cancellationToken);

    private async Task ShowCoreAsync()
    {
        if (_window is not null)
        {
            if (_initializing)
            {
                _activateWhenReady = true;
            }
            else
            {
                _activationService.Activate(_window);
            }

            return;
        }

        var window = _factory.Create();
        _window = window;
        _initializing = true;
        window.Closed += OnWindowClosed;
        try
        {
            await window.InitializeShellAsync().ConfigureAwait(true);
            if (!ReferenceEquals(_window, window))
            {
                return;
            }

            _activationService.Show(window);
            if (_activateWhenReady)
            {
                _activationService.Activate(window);
            }
        }
        catch
        {
            window.Closed -= OnWindowClosed;
            _window = null;
            window.Close();
            throw;
        }
        finally
        {
            _initializing = false;
            _activateWhenReady = false;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is SettingsWindow window)
        {
            window.Closed -= OnWindowClosed;
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        }
    }

    private static async void Observe(Task task, string stage)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsWindowLaunchFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }
}
