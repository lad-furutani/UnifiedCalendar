using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;

namespace UnifiedCalendar.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly TimelineViewportCoordinator _viewport;
    private readonly TimeColumnWidthCalculator _timeColumnWidthCalculator;
    private readonly MainWindowPlacementService _placementService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _placementSaveTimer;
    private EventKey? _detailsFocusOriginKey;
    private EventKey? _pendingMouseDetailsKey;
    private WindowPlacement? _savedPlacement;
    private HwndSource? _windowSource;
    private bool _placementInitialized;
    private bool _allowApplicationExit;
    private bool _stickyOverlayRightEdgeUpdatePending;

    public MainWindow(
        MainWindowViewModel viewModel,
        TimelineViewportCoordinator viewport,
        TimeColumnWidthCalculator timeColumnWidthCalculator,
        MainWindowPlacementService placementService)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        _timeColumnWidthCalculator = timeColumnWidthCalculator
            ?? throw new ArgumentNullException(nameof(timeColumnWidthCalculator));
        _placementService = placementService ?? throw new ArgumentNullException(nameof(placementService));
        InitializeComponent();
        DataContext = viewModel;
        _viewport.Attach(TimelineList);
        _viewport.Restored += OnViewportRestored;
        _viewModel.TimelineItems.CollectionChanged += OnTimelineItemsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
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
        try
        {
            _savedPlacement = await _placementService.LoadSavedPlacementAsync(cancellationToken);
            ApplyRestoreBounds(_placementService.CalculateRestoreBounds(_savedPlacement));
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
                "Initial",
                exception.GetType().Name);
        }
    }

    public void HideToTray()
    {
        CloseTransientPopups();
        SchedulePlacementSave();
        Hide();
    }

    public async Task PrepareForApplicationExitAsync(CancellationToken cancellationToken = default)
    {
        _allowApplicationExit = true;
        CloseTransientPopups();
        _placementSaveTimer.Stop();
        await SaveCurrentPlacementAsync(cancellationToken);
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!_allowApplicationExit)
        {
            eventArgs.Cancel = true;
            HideToTray();
            return;
        }

        _lifetimeCancellation.Cancel();
        WarningPopup.IsOpen = false;
        _viewModel.CloseDetails();
        base.OnClosing(eventArgs);
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        SourceInitialized -= OnSourceInitialized;
        _placementSaveTimer.Tick -= OnPlacementSaveTimerTick;
        _windowSource = null;
        _viewport.Restored -= OnViewportRestored;
        _viewModel.TimelineItems.CollectionChanged -= OnTimelineItemsChanged;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _lifetimeCancellation.Dispose();
        base.OnClosed(eventArgs);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        CloseTransientPopups();
        UpdateDpiDependentLayout(newDpi.PixelsPerDip);
        RepositionOpenPopups();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);

        if (_placementInitialized)
        {
            try
            {
                ApplyRestoreBounds(_placementService.CalculateFinalRestoreBounds(
                    _savedPlacement,
                    handle));
            }
            catch (Exception exception)
            {
                Log.Warning(
                    "WindowPlacementRestoreFailed {Stage} {ErrorCategory}",
                    "SourceInitialized",
                    exception.GetType().Name);
            }
        }

        UpdateDpiDependentLayout(VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private async void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        UpdateDpiDependentLayout(VisualTreeHelper.GetDpi(TimelineList).PixelsPerDip);
        try
        {
            await _viewModel.InitializeAsync(_lifetimeCancellation.Token);
            UpdateDpiDependentLayout(VisualTreeHelper.GetDpi(TimelineList).PixelsPerDip);
            UpdateStickyDate();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                "MainWindowInitializationFailed {Stage} {ErrorCategory}",
                "Initialization",
                exception.GetType().Name);
        }
    }

    private void TimelineItemMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is not ListBoxItem item || item.DataContext is not EventRowViewModel row)
        {
            ClearPendingMouseDetails();
            return;
        }

        if (FindAncestor<Button>(eventArgs.OriginalSource as DependencyObject) is not null)
        {
            ClearPendingMouseDetails();
            return;
        }

        if (eventArgs.ClickCount >= 2)
        {
            ClearPendingMouseDetails();
            CloseDetails();
            if (row.OpenSourceCommand.CanExecute(null))
            {
                row.OpenSourceCommand.Execute(null);
            }

            eventArgs.Handled = true;
            return;
        }

        _pendingMouseDetailsKey = row.Key;
        eventArgs.Handled = true;
    }

    private void TimelineItemMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!_pendingMouseDetailsKey.HasValue)
        {
            return;
        }

        var pendingKey = _pendingMouseDetailsKey.Value;
        ClearPendingMouseDetails();
        if (FindAncestor<Button>(eventArgs.OriginalSource as DependencyObject) is not null
            || sender is not ListBoxItem item
            || item.DataContext is not EventRowViewModel row
            || row.Key != pendingKey)
        {
            return;
        }

        OpenDetails(row, item);
        eventArgs.Handled = true;
    }

    private void WindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (!_pendingMouseDetailsKey.HasValue)
        {
            return;
        }

        var releasedItem = FindAncestor<ListBoxItem>(eventArgs.OriginalSource as DependencyObject);
        if (releasedItem?.DataContext is not EventRowViewModel row
            || row.Key != _pendingMouseDetailsKey.Value)
        {
            ClearPendingMouseDetails();
        }
    }

    private void TimelineListLostMouseCapture(object sender, MouseEventArgs eventArgs) =>
        ClearPendingMouseDetails();

    private void TimelinePreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Space)
        {
            eventArgs.Handled = true;
            return;
        }

        var focusedButton = FindAncestor<Button>(Keyboard.FocusedElement as DependencyObject);
        if (eventArgs.Key == Key.Enter && focusedButton is not null)
        {
            return;
        }

        var currentItem = FindAncestor<ListBoxItem>(Keyboard.FocusedElement as DependencyObject);
        if (eventArgs.Key == Key.Enter && currentItem?.DataContext is EventRowViewModel row)
        {
            OpenDetails(row, currentItem);
            eventArgs.Handled = true;
            return;
        }

        if (eventArgs.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        var rows = TimelineList.Items.OfType<EventRowViewModel>().ToArray();
        if (rows.Length == 0)
        {
            return;
        }

        var current = currentItem?.DataContext as EventRowViewModel;
        var currentIndex = current is null ? -1 : Array.IndexOf(rows, current);
        var nextIndex = eventArgs.Key == Key.Down
            ? Math.Min(rows.Length - 1, currentIndex + 1)
            : Math.Max(0, currentIndex <= 0 ? 0 : currentIndex - 1);
        FocusRow(rows[nextIndex]);
        eventArgs.Handled = true;
    }

    private void FocusRow(EventRowViewModel row)
    {
        TimelineList.ScrollIntoView(row);
        TimelineList.UpdateLayout();
        if (TimelineList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item)
        {
            EnsureRowBelowStickyOverlay(item);
            item.Focus();
        }
    }

    private void TimelineScrollChanged(object sender, ScrollChangedEventArgs eventArgs)
    {
        ScheduleStickyOverlayRightEdgeUpdate();
        if (eventArgs.VerticalChange == 0d || _viewport.IsRestoring)
        {
            return;
        }

        _viewport.NoteUserScroll();
        CloseTransientPopups();
        Dispatcher.BeginInvoke(UpdateStickyDate);
    }

    private void OnViewportRestored(object? sender, EventArgs eventArgs) => UpdateStickyDate();

    private void TimelineListSizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        ScheduleStickyOverlayRightEdgeUpdate();

    private void OnTimelineItemsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        ScheduleStickyOverlayRightEdgeUpdate();
        if (_pendingMouseDetailsKey.HasValue
            && !_viewModel.TimelineItems
                .OfType<EventRowViewModel>()
                .Any(row => row.Key == _pendingMouseDetailsKey.Value))
        {
            ClearPendingMouseDetails();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(MainWindowViewModel.FontSizeDip))
        {
            return;
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => UpdateTimeColumnWidth(
                VisualTreeHelper.GetDpi(TimelineList).PixelsPerDip)));
    }

    private void UpdateStickyDate()
    {
        var firstHeader = TimelineList.Items.OfType<DayHeaderItemViewModel>().FirstOrDefault();
        DateOnly? date = null;
        for (var index = 0; index < TimelineList.Items.Count; index++)
        {
            if (TimelineList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container)
            {
                continue;
            }

            var position = container.TranslatePoint(new Point(0d, 0d), TimelineList);
            if (position.Y + container.ActualHeight <= 0d)
            {
                continue;
            }

            date = TimelineList.Items[index] switch
            {
                DayHeaderItemViewModel header => header.Date,
                EventRowViewModel row => row.GroupDate,
                _ => null,
            };
            if (date.HasValue)
            {
                break;
            }
        }

        _viewModel.UpdateStickyDate(date ?? firstHeader?.Date);
    }

    private void EnsureRowBelowStickyOverlay(ListBoxItem item)
    {
        var overlayHeight = StickyDateOverlay.ActualHeight;
        var position = item.TranslatePoint(new Point(0d, 0d), TimelineList);
        if (overlayHeight <= 0d
            || position.Y >= overlayHeight
            || FindDescendant<ScrollViewer>(TimelineList) is not { } scrollViewer)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(Math.Max(
            0d,
            scrollViewer.VerticalOffset - (overlayHeight - position.Y)));
        TimelineList.UpdateLayout();
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Escape)
        {
            return;
        }

        ClearPendingMouseDetails();
        if (WarningPopup.IsOpen)
        {
            WarningPopup.IsOpen = false;
            eventArgs.Handled = true;
            return;
        }

        if (_viewModel.IsDetailsOpen)
        {
            CloseDetails();
            eventArgs.Handled = true;
        }
    }

    private void WindowLocationChanged(object? sender, EventArgs eventArgs)
    {
        CloseTransientPopups();
        SchedulePlacementSave();
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        CloseTransientPopups();
        SchedulePlacementSave();
    }

    private void WindowStateChanged(object? sender, EventArgs eventArgs)
    {
        if (WindowState == WindowState.Minimized)
        {
            CloseTransientPopups();
        }
        else
        {
            SchedulePlacementSave();
        }
    }

    private void DetailsPopupOpened(object? sender, EventArgs eventArgs) => Dispatcher.BeginInvoke(
        DispatcherPriority.Input,
        new Action(() => DetailsContent.FocusFirstInteractiveElement()));

    private void DetailsPopupClosed(object? sender, EventArgs eventArgs)
    {
        _viewModel.CloseDetails();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(RestoreDetailsFocus));
    }

    private void EventDetailsCloseRequested(object sender, RoutedEventArgs eventArgs) => CloseDetails();

    private void WarningButtonClick(object sender, RoutedEventArgs eventArgs)
    {
        WarningPopup.PlacementTarget = WarningButton;
        WarningPopup.IsOpen = true;
        eventArgs.Handled = true;
    }

    private void WarningPopupOpened(object? sender, EventArgs eventArgs) => Dispatcher.BeginInvoke(
        DispatcherPriority.Input,
        new Action(() => WarningContent.FocusFirstInteractiveElement()));

    private void WarningPopupClosed(object? sender, EventArgs eventArgs) => Dispatcher.BeginInvoke(
        DispatcherPriority.Input,
        new Action(() => WarningButton.Focus()));

    private void AccountWarningsCloseRequested(object sender, RoutedEventArgs eventArgs)
    {
        WarningPopup.IsOpen = false;
        eventArgs.Handled = true;
    }

    private CustomPopupPlacement[] PlaceDetailsPopup(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        const double gap = LayoutMetrics.PopupPlacementGap;
        return
        [
            new CustomPopupPlacement(new Point(targetSize.Width + gap, 0d), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(new Point(-popupSize.Width - gap, 0d), PopupPrimaryAxis.Vertical),
            new CustomPopupPlacement(new Point(0d, targetSize.Height + gap), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(0d, -popupSize.Height - gap), PopupPrimaryAxis.Horizontal),
        ];
    }

    private void CloseDetails()
    {
        if (_viewModel.IsDetailsOpen)
        {
            _viewModel.CloseDetails();
        }
    }

    private void CloseTransientPopups()
    {
        ClearPendingMouseDetails();
        WarningPopup.IsOpen = false;
        CloseDetails();
    }

    private void ClearPendingMouseDetails() => _pendingMouseDetailsKey = null;

    private void OpenDetails(EventRowViewModel row, ListBoxItem item)
    {
        _detailsFocusOriginKey = row.Key;
        DetailsPopup.PlacementTarget = item;
        item.Focus();
        _viewModel.OpenDetails(row.Key);
    }

    private void RestoreDetailsFocus()
    {
        var key = _detailsFocusOriginKey;
        _detailsFocusOriginKey = null;
        if (!key.HasValue)
        {
            return;
        }

        var row = TimelineList.Items
            .OfType<EventRowViewModel>()
            .FirstOrDefault(value => value.Key == key.Value);
        if (row is not null)
        {
            FocusRow(row);
        }
    }

    private void UpdateTimeColumnWidth(double pixelsPerDip)
    {
        _viewModel.SetTimeColumnWidth(_timeColumnWidthCalculator.Calculate(
            TimelineList.FontFamily,
            TimelineList.FontStyle,
            TimelineList.FontWeight,
            TimelineList.FontStretch,
            TimelineList.FontSize,
            pixelsPerDip));
    }

    private void UpdateDpiDependentLayout(double pixelsPerDip)
    {
        _viewModel.SetPixelsPerDip(pixelsPerDip);
        UpdateTimeColumnWidth(pixelsPerDip);
        ScheduleStickyOverlayRightEdgeUpdate();
    }

    private void ScheduleStickyOverlayRightEdgeUpdate()
    {
        if (_windowSource is null || _stickyOverlayRightEdgeUpdatePending)
        {
            return;
        }

        _stickyOverlayRightEdgeUpdatePending = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _stickyOverlayRightEdgeUpdatePending = false;
                if (_windowSource is not null)
                {
                    UpdateStickyOverlayRightMargin();
                }
            }));
    }

    private void UpdateStickyOverlayRightMargin()
    {
        if (FindDescendant<ScrollContentPresenter>(TimelineList) is not { } contentPresenter)
        {
            return;
        }

        var contentRight = contentPresenter.TranslatePoint(
            new Point(contentPresenter.ActualWidth, 0d),
            TimelineList).X;
        var rightMargin = Math.Max(0d, TimelineList.ActualWidth - contentRight);
        if (!double.IsFinite(rightMargin)
            || Math.Abs(StickyDateOverlay.Margin.Right - rightMargin) < 0.01d)
        {
            return;
        }

        var margin = StickyDateOverlay.Margin;
        StickyDateOverlay.Margin = new Thickness(
            margin.Left,
            margin.Top,
            rightMargin,
            margin.Bottom);
    }

    private void RepositionOpenPopups()
    {
        RefreshPopupPlacement(DetailsPopup);
        RefreshPopupPlacement(WarningPopup);
    }

    private static void RefreshPopupPlacement(Popup popup)
    {
        popup.Child?.InvalidateMeasure();
        if (!popup.IsOpen)
        {
            return;
        }

        var offset = popup.HorizontalOffset;
        popup.HorizontalOffset = offset + 0.1d;
        popup.HorizontalOffset = offset;
    }

    private void ApplyRestoreBounds(DipRect bounds)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowState = WindowState.Normal;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private void SchedulePlacementSave()
    {
        if (!_placementInitialized
            || _allowApplicationExit
            || _windowSource is null
            || WindowState != WindowState.Normal)
        {
            return;
        }

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private async void OnPlacementSaveTimerTick(object? sender, EventArgs eventArgs)
    {
        _placementSaveTimer.Stop();
        await SaveCurrentPlacementAsync(CancellationToken.None);
    }

    private async Task SaveCurrentPlacementAsync(CancellationToken cancellationToken)
    {
        if (!_placementInitialized || _windowSource is null)
        {
            return;
        }

        try
        {
            var placement = _placementService.Capture(this);
            await _placementService.SaveAsync(placement, cancellationToken);
            _savedPlacement = placement;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.Warning(
                "WindowPlacementSaveFailed {Stage} {ErrorCategory}",
                "Runtime",
                exception.GetType().Name);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
