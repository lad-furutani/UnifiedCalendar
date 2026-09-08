using System.Drawing;
using System.Windows.Forms;
using Serilog;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.ViewModels;

namespace UnifiedCalendar.App.Shell;

public interface ITrayIconAdapter : IDisposable
{
    event EventHandler? DoubleClick;

    ContextMenuStrip? ContextMenu { get; set; }

    bool Visible { get; set; }
}

public sealed class NotifyIconAdapter : ITrayIconAdapter
{
    private const string IconResourceUri =
        "/UnifiedCalendar.App;component/Resources/UnifiedCalendar.ico";

    private readonly Icon _icon;
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    public NotifyIconAdapter()
    {
        _icon = LoadSmallIcon();
        try
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = _icon,
                Text = UnifiedCalendar.Core.AppIdentity.ProductName,
            };
        }
        catch
        {
            _icon.Dispose();
            throw;
        }
    }

    public event EventHandler? DoubleClick
    {
        add => _notifyIcon.DoubleClick += value;
        remove => _notifyIcon.DoubleClick -= value;
    }

    public ContextMenuStrip? ContextMenu
    {
        get => _notifyIcon.ContextMenuStrip;
        set => _notifyIcon.ContextMenuStrip = value;
    }

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _notifyIcon.Dispose();
        }
        finally
        {
            _icon.Dispose();
        }
    }

    private static Icon LoadSmallIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri(IconResourceUri, UriKind.Relative))
            ?? throw new InvalidOperationException("The application icon resource is unavailable.");
        using var stream = resource.Stream;
        using var selectedFrame = new Icon(stream, SystemInformation.SmallIconSize);
        return (Icon)selectedFrame.Clone();
    }
}

public sealed class TrayIconService : IDisposable
{
    private readonly ITrayIconAdapter _trayIcon;
    private readonly IMainWindowController _windowController;
    private readonly MainWindowViewModel _viewModel;
    private readonly ISettingsWindowLauncher _settingsLauncher;
    private readonly IUiDispatcher _dispatcher;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _started;
    private bool _disposed;

    public TrayIconService(
        ITrayIconAdapter trayIcon,
        IMainWindowController windowController,
        MainWindowViewModel viewModel,
        ISettingsWindowLauncher settingsLauncher,
        IUiTextService textService,
        IUiDispatcher dispatcher)
    {
        _trayIcon = trayIcon ?? throw new ArgumentNullException(nameof(trayIcon));
        _windowController = windowController ?? throw new ArgumentNullException(nameof(windowController));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settingsLauncher = settingsLauncher ?? throw new ArgumentNullException(nameof(settingsLauncher));
        ArgumentNullException.ThrowIfNull(textService);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _toggleItem = new ToolStripMenuItem(textService.Get(UiResourceKeys.TrayShowHide));
        _refreshItem = new ToolStripMenuItem(textService.Get(UiResourceKeys.TrayRefresh));
        _settingsItem = new ToolStripMenuItem(textService.Get(UiResourceKeys.TraySettings))
        {
            Enabled = settingsLauncher.IsAvailable,
        };
        _exitItem = new ToolStripMenuItem(textService.Get(UiResourceKeys.TrayExit));
        ContextMenu = new ContextMenuStrip();
        ContextMenu.Items.AddRange([_toggleItem, _refreshItem, _settingsItem, _exitItem]);
        _trayIcon.ContextMenu = ContextMenu;
    }

    public event EventHandler? ExitRequested;

    public ContextMenuStrip ContextMenu { get; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _toggleItem.Click += OnToggleClick;
        _refreshItem.Click += OnRefreshClick;
        _settingsItem.Click += OnSettingsClick;
        _exitItem.Click += OnExitClick;
        _trayIcon.DoubleClick += OnDoubleClick;
        _viewModel.ManualRefreshCommand.CanExecuteChanged += OnManualRefreshCanExecuteChanged;
        UpdateRefreshAvailability();
        _trayIcon.Visible = true;
        _started = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            _toggleItem.Click -= OnToggleClick;
            _refreshItem.Click -= OnRefreshClick;
            _settingsItem.Click -= OnSettingsClick;
            _exitItem.Click -= OnExitClick;
            _trayIcon.DoubleClick -= OnDoubleClick;
            _viewModel.ManualRefreshCommand.CanExecuteChanged -= OnManualRefreshCanExecuteChanged;
            _trayIcon.Visible = false;
        }

        ContextMenu.Dispose();
        _trayIcon.Dispose();
    }

    private async void OnToggleClick(object? sender, EventArgs eventArgs) =>
        await ObserveAsync(_windowController.ToggleVisibilityAsync()).ConfigureAwait(false);

    private async void OnDoubleClick(object? sender, EventArgs eventArgs) =>
        await ObserveAsync(_windowController.ShowAndActivateAsync()).ConfigureAwait(false);

    private async void OnRefreshClick(object? sender, EventArgs eventArgs) =>
        await ObserveAsync(RefreshAsync()).ConfigureAwait(false);

    private async Task RefreshAsync()
    {
        if (!_viewModel.ManualRefreshCommand.CanExecute(null))
        {
            return;
        }

        await _viewModel.ManualRefreshCommand.ExecuteAsync(null).ConfigureAwait(false);
    }

    private void OnSettingsClick(object? sender, EventArgs eventArgs)
    {
        try
        {
            _settingsLauncher.Show();
        }
        catch (Exception exception)
        {
            Log.Warning(
                "TrayActionFailed {Stage} {ErrorCategory}",
                "Settings",
                exception.GetType().Name);
        }
    }

    private void OnExitClick(object? sender, EventArgs eventArgs) =>
        ExitRequested?.Invoke(this, EventArgs.Empty);

    private void OnManualRefreshCanExecuteChanged(object? sender, EventArgs eventArgs) =>
        _ = ObserveAsync(_dispatcher.InvokeAsync(() =>
        {
            UpdateRefreshAvailability();
            return Task.CompletedTask;
        }));

    private void UpdateRefreshAvailability() =>
        _refreshItem.Enabled = _viewModel.ManualRefreshCommand.CanExecute(null);

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "TrayActionFailed {Stage} {ErrorCategory}",
                "Tray",
                exception.GetType().Name);
        }
    }
}
