using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsWindowViewModel _viewModel;
    private readonly SettingsWindowPlacementService _placementService;
    private readonly ISettingsConfirmationService _confirmationService;
    private readonly DispatcherTimer _placementSaveTimer;
    private WindowPlacement? _savedPlacement;
    private bool _placementInitialized;
    private bool _placementFlushedForExit;
    private bool _allowClose;
    private ColorRuleListItemViewModel? _draggedColorRule;
    private Point _colorRuleDragStart;

    public SettingsWindow(
        SettingsWindowViewModel viewModel,
        SettingsWindowPlacementService placementService,
        ISettingsConfirmationService confirmationService)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _placementService = placementService ?? throw new ArgumentNullException(nameof(placementService));
        _confirmationService = confirmationService
            ?? throw new ArgumentNullException(nameof(confirmationService));
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        _viewModel.CloseRequested += OnCloseRequested;
        _viewModel.CancelRequested += OnCancelRequested;
        _placementSaveTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = ShellTimingDefaults.WindowPlacementSaveDebounce,
        };
        _placementSaveTimer.Tick += OnPlacementSaveTimerTick;
    }

    public async Task InitializeShellAsync(CancellationToken cancellationToken = default)
    {
        if (Owner is null)
        {
            throw new InvalidOperationException("The settings window requires an owner.");
        }

        await _viewModel.InitializeAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            _savedPlacement = await _placementService
                .LoadSavedPlacementAsync(cancellationToken)
                .ConfigureAwait(true);
            ApplyRestoreBounds(_placementService.CalculateRestoreBounds(_savedPlacement, Owner));
            _placementInitialized = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(
                "WindowPlacementRestoreFailed {Stage} {ErrorCategory}",
                "SettingsInitial",
                exception.GetType().Name);
        }
    }

    public async Task PrepareForApplicationExitAsync(CancellationToken cancellationToken = default)
    {
        await _viewModel.FlushImmediateSettingsAsync().ConfigureAwait(true);
        if (_viewModel.IsDirty)
        {
            if (_confirmationService.ConfirmApplicationExit() == SettingsExitDecision.Apply)
            {
                await _viewModel.ApplyPendingChangesAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                _viewModel.Discard();
            }
        }

        _placementSaveTimer.Stop();
        await SaveCurrentPlacementAsync(cancellationToken).ConfigureAwait(true);
        _placementFlushedForExit = true;
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!_allowClose && _viewModel.IsDirty)
        {
            if (!_confirmationService.ConfirmDiscard())
            {
                eventArgs.Cancel = true;
                return;
            }

            _viewModel.Discard();
        }

        if (!eventArgs.Cancel)
        {
            _allowClose = true;
            _placementSaveTimer.Stop();
            if (!_placementFlushedForExit)
            {
                Observe(SaveCurrentPlacementAsync(CancellationToken.None), "SettingsClosePlacement");
            }
        }

        base.OnClosing(eventArgs);
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        SourceInitialized -= OnSourceInitialized;
        _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel.CancelRequested -= OnCancelRequested;
        _placementSaveTimer.Tick -= OnPlacementSaveTimerTick;
        _viewModel.Dispose();
        base.OnClosed(eventArgs);
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        if (!_placementInitialized || Owner is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        ApplyRestoreBounds(_placementService.CalculateFinalRestoreBounds(
            _savedPlacement,
            handle,
            Owner));
    }

    private void ApplyRestoreBounds(DipRect bounds)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void WindowLocationChanged(object sender, EventArgs eventArgs) => SchedulePlacementSave();

    private void WindowSizeChanged(object sender, SizeChangedEventArgs eventArgs) => SchedulePlacementSave();

    private void WindowStateChanged(object? sender, EventArgs eventArgs) => SchedulePlacementSave();

    private void SchedulePlacementSave()
    {
        if (!_placementInitialized || WindowState != WindowState.Normal || !IsVisible)
        {
            return;
        }

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void OnPlacementSaveTimerTick(object? sender, EventArgs eventArgs)
    {
        _placementSaveTimer.Stop();
        Observe(SaveCurrentPlacementAsync(CancellationToken.None), "SettingsDebouncedPlacement");
    }

    private async Task SaveCurrentPlacementAsync(CancellationToken cancellationToken)
    {
        if (!_placementInitialized || new WindowInteropHelper(this).Handle == 0)
        {
            return;
        }

        var placement = _placementService.Capture(this);
        await _placementService.SaveAsync(placement, cancellationToken).ConfigureAwait(false);
    }

    private void OnCloseRequested(object? sender, EventArgs eventArgs)
    {
        _allowClose = true;
        Close();
    }

    private void OnCancelRequested(object? sender, EventArgs eventArgs)
    {
        if (_viewModel.IsDirty && !_confirmationService.ConfirmDiscard())
        {
            return;
        }

        if (_viewModel.IsDirty)
        {
            _viewModel.Discard();
        }

        _allowClose = true;
        Close();
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
        }
    }

    private async void PickCustomColorClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await _viewModel.Display.PickCustomColorAsync(
                new WindowInteropHelper(this).Handle).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsWindowOperationFailed {Stage} {ErrorCategory}",
                "CustomColor",
                exception.GetType().Name);
        }
    }

    private void ColorRuleRowPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: ColorRuleListItemViewModel item }
            || IsWithinButton(eventArgs.OriginalSource as DependencyObject))
        {
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            _draggedColorRule = null;
            _viewModel.ColorRules.EditRuleCommand.Execute(item);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.ChangedButton == MouseButton.Left)
        {
            _draggedColorRule = item;
            _colorRuleDragStart = eventArgs.GetPosition(this);
        }
    }

    private void ColorRuleRowMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed)
        {
            _draggedColorRule = null;
            return;
        }

        if (_draggedColorRule is null
            || sender is not FrameworkElement { DataContext: ColorRuleListItemViewModel item }
            || !ReferenceEquals(_draggedColorRule, item))
        {
            return;
        }

        var current = eventArgs.GetPosition(this);
        if (Math.Abs(current.X - _colorRuleDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _colorRuleDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = _draggedColorRule;
        _draggedColorRule = null;
        _ = DragDrop.DoDragDrop(
            (DependencyObject)sender,
            dragged,
            DragDropEffects.Move);
    }

    private void ColorRuleRowDragOver(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = TryCreateColorRuleMoveRequest(sender, eventArgs.Data, out var request)
            && _viewModel.ColorRules.MoveRuleCommand.CanExecute(request)
                ? DragDropEffects.Move
                : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private async void ColorRuleRowDrop(object sender, DragEventArgs eventArgs)
    {
        try
        {
            if (!TryCreateColorRuleMoveRequest(sender, eventArgs.Data, out var request)
                || !_viewModel.ColorRules.MoveRuleCommand.CanExecute(request))
            {
                eventArgs.Effects = DragDropEffects.None;
                return;
            }

            eventArgs.Effects = DragDropEffects.Move;
            await _viewModel.ColorRules.MoveRuleCommand
                .ExecuteAsync(request)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsWindowOperationFailed {Stage} {ErrorCategory}",
                "ColorRuleMove",
                exception.GetType().Name);
        }
        finally
        {
            _draggedColorRule = null;
            eventArgs.Handled = true;
        }
    }

    private async void PickRuleCustomColorClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            await _viewModel.ColorRules.PickCustomColorAsync(
                new WindowInteropHelper(this).Handle).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsWindowOperationFailed {Stage} {ErrorCategory}",
                "ColorRuleCustomColor",
                exception.GetType().Name);
        }
    }

    private static async void Observe(Task task, string stage)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                "SettingsWindowOperationFailed {Stage} {ErrorCategory}",
                stage,
                exception.GetType().Name);
        }
    }

    private static bool TryCreateColorRuleMoveRequest(
        object sender,
        IDataObject data,
        out ColorRuleMoveRequest? request)
    {
        request = null;
        if (sender is not FrameworkElement { DataContext: ColorRuleListItemViewModel target }
            || data.GetData(typeof(ColorRuleListItemViewModel)) is not ColorRuleListItemViewModel source)
        {
            return false;
        }

        request = new ColorRuleMoveRequest(source.Index, target.Index);
        return source.Index != target.Index;
    }

    private static bool IsWithinButton(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }
}
