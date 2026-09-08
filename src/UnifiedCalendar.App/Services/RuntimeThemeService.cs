using System.Windows;
using Microsoft.Win32;
using Serilog;

namespace UnifiedCalendar.App.Services;

public interface IThemeChangeSignal : IDisposable
{
    event EventHandler? ThemeChanged;

    void Start();
}

public sealed class SystemThemeChangeSignal : IThemeChangeSignal
{
    private bool _started;

    public event EventHandler? ThemeChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _started = true;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _started = false;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs eventArgs) =>
        ThemeChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RuntimeThemeService : IDisposable
{
    private readonly StartupThemeService _themeService;
    private readonly IThemeChangeSignal _changeSignal;
    private readonly IUiDispatcher _dispatcher;
    private ResourceDictionary? _resources;
    private bool _lastDarkTheme;
    private bool _started;

    public RuntimeThemeService(
        StartupThemeService themeService,
        IThemeChangeSignal changeSignal,
        IUiDispatcher dispatcher)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _changeSignal = changeSignal ?? throw new ArgumentNullException(nameof(changeSignal));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (_started)
        {
            throw new InvalidOperationException("Runtime theme monitoring is already running.");
        }

        _resources = resources;
        _lastDarkTheme = _themeService.IsDarkTheme();
        _changeSignal.ThemeChanged += OnThemeChanged;
        _changeSignal.Start();
        _started = true;
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _changeSignal.ThemeChanged -= OnThemeChanged;
        _changeSignal.Dispose();
        _resources = null;
        _started = false;
    }

    private void OnThemeChanged(object? sender, EventArgs eventArgs) =>
        Observe(ApplyIfChangedAsync());

    private Task ApplyIfChangedAsync() => _dispatcher.InvokeAsync(() =>
    {
        var isDarkTheme = _themeService.IsDarkTheme();
        if (_resources is not null && isDarkTheme != _lastDarkTheme)
        {
            _themeService.Apply(_resources, isDarkTheme);
            _lastDarkTheme = isDarkTheme;
        }

        return Task.CompletedTask;
    });

    private static async void Observe(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "RuntimeThemeUpdateFailed {Stage} {ErrorCategory}",
                "Theme",
                exception.GetType().Name);
        }
    }
}
