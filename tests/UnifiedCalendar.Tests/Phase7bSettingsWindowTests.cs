using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using UnifiedCalendar.App.Controls;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.Shell;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Models;
using Xunit;

namespace UnifiedCalendar.Tests;

[Collection(WpfApplicationCollection.Name)]
public sealed class Phase7bSettingsWindowTests
{
    private static readonly DisplayMonitor PrimaryMonitor = new(
        @"\\.\DISPLAY1",
        new PixelRect(0, 0, 1920, 1080),
        96d,
        96d,
        true);
    private static readonly DisplayMonitor SecondaryMonitor = new(
        @"\\.\DISPLAY2",
        new PixelRect(1920, 0, 3840, 1080),
        96d,
        96d,
        false);

    private readonly WpfApplicationFixture _fixture;

    public Phase7bSettingsWindowTests(WpfApplicationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void SettingsPlacementRestoresSavedBoundsAndClampsOffScreenBounds()
    {
        var fallback = new DipRect(580d, 260d, 760d, 560d);
        var saved = new WindowPlacement(200d, 120d, 720d, 500d, PrimaryMonitor.DeviceName, 96d, 96d);

        var restored = WindowPlacementCalculator.RestoreWindow(
            saved,
            [PrimaryMonitor],
            PrimaryMonitor.DeviceName,
            fallback,
            LayoutMetrics.MinimumSettingsWidth,
            LayoutMetrics.MinimumSettingsHeight);
        var clamped = WindowPlacementCalculator.RestoreWindow(
            new WindowPlacement(5000d, 4000d, 720d, 500d, PrimaryMonitor.DeviceName),
            [PrimaryMonitor],
            PrimaryMonitor.DeviceName,
            fallback,
            LayoutMetrics.MinimumSettingsWidth,
            LayoutMetrics.MinimumSettingsHeight);

        Assert.Equal(new DipRect(200d, 120d, 720d, 500d), restored);
        Assert.Equal(PrimaryMonitor.WorkAreaDip.Right, clamped.Right);
        Assert.Equal(PrimaryMonitor.WorkAreaDip.Bottom, clamped.Bottom);
    }

    [Fact]
    public void MissingMonitorUsesVisibleOwnerCenterAndHiddenOwnerUsesPrimaryCenter()
    {
        _fixture.Invoke(() =>
        {
            var provider = new OwnerOnSecondaryMonitorProvider();
            var service = new SettingsWindowPlacementService(
                new ApplicationSettingsService(new TestSettingsStore()),
                provider);
            var missing = new WindowPlacement(10d, 10d, 700d, 500d, @"\\.\MISSING");
            var owner = new Window
            {
                Left = 2200d,
                Top = 100d,
                Width = 900d,
                Height = 700d,
                ShowInTaskbar = false,
            };
            owner.Show();

            var visibleOwnerBounds = service.CalculateRestoreBounds(missing, owner);

            Assert.Equal(
                owner.Left + ((owner.ActualWidth - LayoutMetrics.InitialSettingsWidth) / 2d),
                visibleOwnerBounds.Left,
                precision: 6);
            Assert.Equal(
                owner.Top + ((owner.ActualHeight - LayoutMetrics.InitialSettingsHeight) / 2d),
                visibleOwnerBounds.Top,
                precision: 6);
            Assert.True(visibleOwnerBounds.Left >= SecondaryMonitor.WorkAreaDip.Left);

            owner.Hide();
            var hiddenOwnerBounds = service.CalculateRestoreBounds(missing, owner);

            Assert.Equal(
                (PrimaryMonitor.WorkAreaDip.Width - LayoutMetrics.InitialSettingsWidth) / 2d,
                hiddenOwnerBounds.Left,
                precision: 6);
            Assert.Equal(
                (PrimaryMonitor.WorkAreaDip.Height - LayoutMetrics.InitialSettingsHeight) / 2d,
                hiddenOwnerBounds.Top,
                precision: 6);
            owner.Close();
        });
    }

    [Fact]
    public void LauncherKeepsOneWindowActivatesItAndCreatesAgainAfterClose()
    {
        _fixture.Invoke(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            var factory = new RecordingSettingsWindowFactory(owner);
            var activation = new RecordingSettingsWindowActivationService();
            var launcher = new SettingsWindowLauncher(factory, activation, new ImmediateUiDispatcher());

            launcher.Show();
            launcher.Show();

            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(1, activation.ShowCount);
            Assert.Equal(1, activation.ActivateCount);

            factory.Windows[0].Close();
            launcher.Show();

            Assert.Equal(2, factory.CreateCount);
            Assert.Equal(2, activation.ShowCount);
            factory.PendingChanges[1].SetDirty(true);
            factory.Confirmations[1].ExitDecision = SettingsExitDecision.Discard;
            launcher.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            Assert.Equal(1, factory.Confirmations[1].ExitPromptCount);
            Assert.Equal(1, factory.PendingChanges[1].DiscardCount);
            owner.Close();
        });
    }

    [Fact]
    public void LauncherActivatesOnceWhenShowIsRequestedDuringInitialization()
    {
        _fixture.Invoke(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            var store = new DeferredSettingsStore();
            var factory = new RecordingSettingsWindowFactory(owner, store);
            var activation = new RecordingSettingsWindowActivationService();
            var launcher = new SettingsWindowLauncher(factory, activation, new ImmediateUiDispatcher());

            launcher.Show();
            launcher.Show();

            Assert.Equal(1, factory.CreateCount);
            Assert.Equal(0, activation.ShowCount);
            Assert.Equal(0, activation.ActivateCount);

            store.Release();
            PumpDispatcherUntil(() => activation.ActivateCount == 1);

            Assert.Equal(1, activation.ShowCount);
            Assert.Equal(1, activation.ActivateCount);
            factory.Windows[0].Close();
            owner.Close();
        });
    }

    [Fact]
    public void HiddenOwnerStillAllowsWindowInteractionAndSettingsPlacementSave()
    {
        _fixture.Invoke(() =>
        {
            var saved = new WindowPlacement(100d, 120d, 700d, 500d, PrimaryMonitor.DeviceName, 96d, 96d);
            var store = new TestSettingsStore
            {
                Settings = new AppSettings(windows: new WindowPreferences(Settings: saved)),
            };
            var owner = new Window { Width = 820d, Height = 640d, ShowInTaskbar = false };
            var window = CreateWindow(owner, store, new RecordingPendingSettingsChanges(), new RecordingConfirmationService());
            window.InitializeShellAsync().GetAwaiter().GetResult();
            window.Show();
            Assert.False(owner.IsVisible);
            Assert.True(window.IsVisible);
            Assert.Equal(saved.LeftDip, window.Left, precision: 6);
            Assert.Equal(saved.TopDip, window.Top, precision: 6);
            Assert.Equal(saved.WidthDip, window.Width, precision: 6);
            Assert.Equal(saved.HeightDip, window.Height, precision: 6);

            var viewModel = Assert.IsType<SettingsWindowViewModel>(window.DataContext);
            viewModel.SelectedCategory = viewModel.Categories[2];
            window.Left += 40d;
            window.Width += 20d;
            var expectedWidth = saved.WidthDip + 20d;
            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();

            Assert.Equal("display", viewModel.SelectedCategory.Key);
            Assert.NotNull(store.Settings.Windows.Settings);
            Assert.Equal(expectedWidth, store.Settings.Windows.Settings.WidthDip, precision: 6);
            owner.Close();
        });
    }

    [Fact]
    public void CategoriesContentMinimumSizeEscapeAndTabOrderMatchContract()
    {
        _fixture.Invoke(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            var window = CreateWindow(owner, new TestSettingsStore(), new RecordingPendingSettingsChanges(), new RecordingConfirmationService());
            var viewModel = Assert.IsType<SettingsWindowViewModel>(window.DataContext);
            window.InitializeShellAsync().GetAwaiter().GetResult();
            window.Show();

            Assert.Equal(
                ["アカウント", "カレンダー選択", "表示", "色分けルール", "更新", "一般"],
                viewModel.Categories.Select(category => category.Title));
            Assert.IsType<DisplaySettingsViewModel>(viewModel.Categories[2].Content);
            Assert.IsType<ColorRulesSettingsViewModel>(viewModel.Categories[3].Content);
            Assert.IsType<UpdateSettingsViewModel>(viewModel.Categories[4].Content);
            Assert.IsType<GeneralSettingsViewModel>(viewModel.Categories[5].Content);
            Assert.All(
                viewModel.Categories.Where(category => category.Key is "account" or "calendar-selection"),
                category => Assert.False(string.IsNullOrWhiteSpace(
                    Assert.IsType<SettingsPlaceholderViewModel>(category.Content).Text)));
            Assert.Equal(LayoutMetrics.MinimumSettingsWidth, window.MinWidth);
            Assert.Equal(LayoutMetrics.MinimumSettingsHeight, window.MinHeight);

            var categoryList = Assert.IsType<ListBox>(window.FindName("CategoryList"));
            var content = Assert.IsType<ContentControl>(window.FindName("CategoryContent"));
            var ok = Assert.IsType<Button>(window.FindName("OkButton"));
            var apply = Assert.IsType<Button>(window.FindName("ApplyButton"));
            var cancel = Assert.IsType<Button>(window.FindName("CancelButton"));
            Assert.Equal(0, KeyboardNavigation.GetTabIndex(categoryList));
            Assert.Equal(1, KeyboardNavigation.GetTabIndex(content));
            Assert.Equal(2, KeyboardNavigation.GetTabIndex(ok));
            Assert.Equal(3, KeyboardNavigation.GetTabIndex(apply));
            Assert.Equal(4, KeyboardNavigation.GetTabIndex(cancel));
            Assert.Equal(14d, window.FontSize);

            viewModel.SelectedCategory = viewModel.Categories[2];
            PumpDispatcher();
            window.UpdateLayout();
            var spinners = FindDescendants<IntegerSpinner>(content).ToArray();
            Assert.Equal(2, spinners.Length);
            Assert.Equal((1, 90), (spinners[0].Minimum, spinners[0].Maximum));
            Assert.Equal((10, 24), (spinners[1].Minimum, spinners[1].Maximum));
            Assert.Equal(3, FindDescendants<RadioButton>(content).Count());

            viewModel.SelectedCategory = viewModel.Categories[4];
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Single(FindDescendants<ComboBox>(content));

            viewModel.SelectedCategory = viewModel.Categories[5];
            PumpDispatcher();
            window.UpdateLayout();
            Assert.Single(FindDescendants<CheckBox>(content));
            Assert.Equal(5, FindDescendants<Button>(content).Count());

            var keyEvent = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window),
                0,
                Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };
            window.RaiseEvent(keyEvent);
            Assert.True(keyEvent.Handled);
            Assert.True(window.IsVisible);

            window.Close();
            owner.Close();
        });
    }

    [Fact]
    public void CleanConfirmationOperationsCloseWithoutPromptAndApplyIsDisabled()
    {
        _fixture.Invoke(() =>
        {
            foreach (var action in new[] { "ok", "cancel", "close" })
            {
                var owner = new Window { ShowInTaskbar = false };
                var confirmation = new RecordingConfirmationService();
                var window = CreateWindow(
                    owner,
                    new TestSettingsStore(),
                    new RecordingPendingSettingsChanges(),
                    confirmation);
                var viewModel = Assert.IsType<SettingsWindowViewModel>(window.DataContext);
                window.InitializeShellAsync().GetAwaiter().GetResult();
                window.Show();
                Assert.False(viewModel.ApplyCommand.CanExecute(null));

                if (action == "ok")
                {
                    viewModel.OkCommand.ExecuteAsync(null).GetAwaiter().GetResult();
                }
                else if (action == "cancel")
                {
                    viewModel.CancelCommand.Execute(null);
                }
                else
                {
                    window.Close();
                }

                Assert.False(window.IsVisible);
                Assert.Equal(0, confirmation.DiscardPromptCount);
                owner.Close();
            }
        });
    }

    [Fact]
    public void DirtyApplyOkCancelCloseAndApplicationExitFollowContract()
    {
        _fixture.Invoke(() =>
        {
            VerifyDirtyApplyLeavesWindowOpen();
            VerifyDirtyOkAppliesAndCloses();
            VerifyDirtyCancelOrClosePrompts(isCancel: true);
            VerifyDirtyCancelOrClosePrompts(isCancel: false);
            VerifyDirtyApplicationExit(SettingsExitDecision.Apply);
            VerifyDirtyApplicationExit(SettingsExitDecision.Discard);
        });
    }

    [Fact]
    public void IntegerSpinnerRejectsOutOfRangeConfirmationAndAcceptsBoundaryValues()
    {
        _fixture.Invoke(() =>
        {
            var spinner = new IntegerSpinner
            {
                Minimum = 1,
                Maximum = 90,
                Value = 7,
                Width = LayoutMetrics.SettingsSpinnerWidth,
            };
            var host = new Window { Content = spinner, ShowInTaskbar = false };
            host.Show();
            var textBox = Assert.IsType<TextBox>(spinner.FindName("ValueTextBox"));

            ConfirmText(textBox, "90");
            Assert.Equal(90, spinner.Value);
            ConfirmText(textBox, "91");
            Assert.Equal(90, spinner.Value);
            Assert.Equal("90", textBox.Text);
            ConfirmText(textBox, "0");
            Assert.Equal(90, spinner.Value);
            Assert.Equal("90", textBox.Text);
            ConfirmText(textBox, "1");
            Assert.Equal(1, spinner.Value);

            host.Close();
        });
    }

    [Fact]
    public void ClosingWindowDoesNotCancelOrDisposeFlyingDebouncedPlacementSaveToken()
    {
        _fixture.Invoke(() =>
        {
            var owner = new Window { ShowInTaskbar = false };
            var store = new DelayedPlacementSaveStore();
            var window = CreateWindow(
                owner,
                store,
                new RecordingPendingSettingsChanges(),
                new RecordingConfirmationService());
            window.InitializeShellAsync().GetAwaiter().GetResult();
            window.Show();
            typeof(SettingsWindow)
                .GetMethod(
                    "OnPlacementSaveTimerTick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [null, EventArgs.Empty]);
            PumpDispatcherUntil(() => store.FirstSaveStarted.Task.IsCompleted);

            window.Close();

            Assert.False(store.FirstSaveStarted.Task.Result.CanBeCanceled);
            store.ReleaseFirstSave();
            PumpDispatcherUntil(() => store.FirstSaveFinished.Task.IsCompleted);
            owner.Close();
        });
    }

    [Fact]
    public void FontSettingRecalculatesRowAndTimeColumnAndFlowsToStatusAndDetails()
    {
        _fixture.Invoke(() =>
        {
            var store = new TestSettingsStore();
            var settings = new ApplicationSettingsService(store);
            var viewport = new RecordingTimelineViewport();
            var viewModel = Phase6Data.CreateViewModel(
                new TestCalendarSyncService(),
                new MutableTimeProvider(Phase6Data.Now),
                settings: store,
                dispatcher: new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
                viewport: viewport,
                settingsService: settings);
            var window = new MainWindow(
                viewModel,
                new TimelineViewportCoordinator(),
                new TimeColumnWidthCalculator(new ResourceUiTextService()),
                new MainWindowPlacementService(settings, new TestMonitorProvider()))
            {
                ShowInTaskbar = false,
            };
            window.Show();
            PumpDispatcherUntil(() => viewModel.ContentState != MainContentState.Loading);
            var initialWidth = viewModel.TimeColumnWidth;
            var initialHeight = viewModel.EventRowMinHeight;

            var update = settings.UpdateAsync(current => new AppSettings(
                new DisplayPreferences(
                    current.Display.Days,
                    DisplayPreferences.MaximumFontSizeDip,
                    current.Display.Density,
                    current.Display.DefaultEventColor),
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules));
            PumpDispatcherUntil(() =>
                update.IsCompleted
                && viewModel.FontSizeDip == DisplayPreferences.MaximumFontSizeDip
                && viewModel.TimeColumnWidth > initialWidth);
            update.GetAwaiter().GetResult();

            Assert.True(viewModel.EventRowMinHeight > initialHeight);
            Assert.Equal((double)DisplayPreferences.MaximumFontSizeDip, window.FontSize);
            Assert.Equal((double)DisplayPreferences.MaximumFontSizeDip, window.DetailsContent.FontSize);

            var fontAdjustedWidth = viewModel.TimeColumnWidth;
            var fontAdjustedHeight = viewModel.EventRowMinHeight;
            var projectionCount = viewport.Restorations.Count;
            var colorUpdate = settings.UpdateAsync(current => new AppSettings(
                new DisplayPreferences(
                    current.Display.Days,
                    current.Display.FontSizeDip,
                    current.Display.Density,
                    RgbColor.Parse("#1F6B24")),
                current.Sync,
                current.General,
                current.Windows,
                current.Accounts,
                current.ColorRules));
            PumpDispatcherUntil(() =>
                colorUpdate.IsCompleted
                && viewport.Restorations.Count > projectionCount);
            colorUpdate.GetAwaiter().GetResult();

            Assert.Equal(fontAdjustedWidth, viewModel.TimeColumnWidth);
            Assert.Equal(fontAdjustedHeight, viewModel.EventRowMinHeight);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }

    [Fact]
    public void PopupKeepsTitleBodyLabelHierarchyAcrossFontSizeRange()
    {
        _fixture.Invoke(() =>
        {
            var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
            var calendarEvent = Phase6Data.Event("popup-typography");
            var sync = new TestCalendarSyncService
            {
                CurrentSnapshot = Phase6Data.Snapshot(
                    [account],
                    [new CalendarSelection(account.InternalAccountId, "calendar", true)],
                    [calendarEvent]),
            };
            var store = new TestSettingsStore();
            var settings = new ApplicationSettingsService(store);
            var viewModel = Phase6Data.CreateViewModel(
                sync,
                new MutableTimeProvider(Phase6Data.Now),
                settings: store,
                dispatcher: new WpfUiDispatcher(Dispatcher.CurrentDispatcher),
                settingsService: settings);
            var window = new MainWindow(
                viewModel,
                new TimelineViewportCoordinator(),
                new TimeColumnWidthCalculator(new ResourceUiTextService()),
                new MainWindowPlacementService(settings, new TestMonitorProvider()))
            {
                ShowInTaskbar = false,
            };
            window.Show();
            PumpDispatcherUntil(() =>
                viewModel.TimelineItems.OfType<EventRowViewModel>().Any());
            viewModel.OpenDetails(calendarEvent.Key);
            PumpDispatcherUntil(() => viewModel.IsDetailsOpen);
            window.UpdateLayout();

            var title = FindDescendants<TextBox>(window.DetailsContent)
                .Single(textBox => textBox.FontWeight == FontWeights.SemiBold);
            var body = FindDescendants<TextBox>(window.DetailsContent)
                .First(textBox => textBox.FontWeight != FontWeights.SemiBold);
            var responseLabelText = new ResourceUiTextService().Get(UiResourceKeys.DetailResponse);
            var label = FindDescendants<TextBlock>(window.DetailsContent)
                .First(textBlock => textBlock.Text == responseLabelText);

            var minimumUpdate = UpdateFontSizeAsync(settings, DisplayPreferences.MinimumFontSizeDip);
            PumpDispatcherUntil(() =>
                minimumUpdate.IsCompleted
                && window.DetailsContent.FontSize == DisplayPreferences.MinimumFontSizeDip);
            minimumUpdate.GetAwaiter().GetResult();
            window.UpdateLayout();
            var minimumTitle = title.FontSize;
            var minimumBody = body.FontSize;
            var minimumLabel = label.FontSize;

            Assert.Equal((double)DisplayPreferences.MinimumFontSizeDip, minimumBody);
            Assert.True(minimumTitle > minimumBody);
            Assert.True(minimumBody > minimumLabel);

            var maximumUpdate = UpdateFontSizeAsync(settings, DisplayPreferences.MaximumFontSizeDip);
            PumpDispatcherUntil(() =>
                maximumUpdate.IsCompleted
                && window.DetailsContent.FontSize == DisplayPreferences.MaximumFontSizeDip);
            maximumUpdate.GetAwaiter().GetResult();
            window.UpdateLayout();

            Assert.True(title.FontSize > minimumTitle);
            Assert.True(body.FontSize > minimumBody);
            Assert.True(label.FontSize > minimumLabel);
            Assert.Equal((double)DisplayPreferences.MaximumFontSizeDip, body.FontSize);
            Assert.True(title.FontSize > body.FontSize);
            Assert.True(body.FontSize > label.FontSize);

            window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();
            window.Close();
            viewModel.Dispose();
        });
    }

    private static SettingsWindow CreateWindow(
        Window owner,
        ISettingsStore store,
        RecordingPendingSettingsChanges pending,
        RecordingConfirmationService confirmation)
    {
        if (!owner.IsLoaded)
        {
            owner.Show();
            owner.Hide();
        }

        var settings = new ApplicationSettingsService(store);
        var window = new SettingsWindow(
            new SettingsWindowViewModel(
                pending,
                new ResourceUiTextService(),
                settings,
                new TestCalendarSyncService(),
                new TestColorPickerService(),
                new BrushCache(),
                new RecordingStartupRegistrationService(),
                new TestApplicationInfoProvider(),
                new NoOpLocalPathLauncher(),
                confirmation),
            new SettingsWindowPlacementService(
                settings,
                new TestMonitorProvider()),
            confirmation)
        {
            Owner = owner,
            ShowInTaskbar = false,
        };
        return window;
    }

    private static void VerifyDirtyApplyLeavesWindowOpen()
    {
        var owner = new Window { ShowInTaskbar = false };
        var pending = new RecordingPendingSettingsChanges();
        var window = CreateWindow(owner, new TestSettingsStore(), pending, new RecordingConfirmationService());
        window.InitializeShellAsync().GetAwaiter().GetResult();
        window.Show();
        pending.SetDirty(true);
        var viewModel = Assert.IsType<SettingsWindowViewModel>(window.DataContext);

        Assert.True(viewModel.ApplyCommand.CanExecute(null));
        viewModel.ApplyCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        Assert.Equal(1, pending.ApplyCount);
        Assert.True(window.IsVisible);
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
        window.Close();
        owner.Close();
    }

    private static void VerifyDirtyOkAppliesAndCloses()
    {
        var owner = new Window { ShowInTaskbar = false };
        var pending = new RecordingPendingSettingsChanges();
        var window = CreateWindow(owner, new TestSettingsStore(), pending, new RecordingConfirmationService());
        window.InitializeShellAsync().GetAwaiter().GetResult();
        window.Show();
        pending.SetDirty(true);
        var viewModel = Assert.IsType<SettingsWindowViewModel>(window.DataContext);

        viewModel.OkCommand.ExecuteAsync(null).GetAwaiter().GetResult();

        Assert.Equal(1, pending.ApplyCount);
        Assert.False(window.IsVisible);
        owner.Close();
    }

    private static void VerifyDirtyCancelOrClosePrompts(bool isCancel)
    {
        var owner = new Window { ShowInTaskbar = false };
        var pending = new RecordingPendingSettingsChanges();
        var confirmation = new RecordingConfirmationService { ConfirmDiscardResult = false };
        var window = CreateWindow(owner, new TestSettingsStore(), pending, confirmation);
        window.InitializeShellAsync().GetAwaiter().GetResult();
        window.Show();
        pending.SetDirty(true);
        var viewModel = Assert.IsType<SettingsWindowViewModel>(window.DataContext);

        if (isCancel)
        {
            viewModel.CancelCommand.Execute(null);
        }
        else
        {
            window.Close();
        }

        Assert.True(window.IsVisible);
        Assert.Equal(1, confirmation.DiscardPromptCount);
        Assert.Equal(0, pending.DiscardCount);

        confirmation.ConfirmDiscardResult = true;
        if (isCancel)
        {
            viewModel.CancelCommand.Execute(null);
        }
        else
        {
            window.Close();
        }

        Assert.False(window.IsVisible);
        Assert.Equal(2, confirmation.DiscardPromptCount);
        Assert.Equal(1, pending.DiscardCount);
        owner.Close();
    }

    private static void VerifyDirtyApplicationExit(SettingsExitDecision decision)
    {
        var owner = new Window { ShowInTaskbar = false };
        var pending = new RecordingPendingSettingsChanges();
        var confirmation = new RecordingConfirmationService { ExitDecision = decision };
        var window = CreateWindow(owner, new TestSettingsStore(), pending, confirmation);
        window.InitializeShellAsync().GetAwaiter().GetResult();
        window.Show();
        pending.SetDirty(true);

        window.PrepareForApplicationExitAsync().GetAwaiter().GetResult();

        Assert.Equal(1, confirmation.ExitPromptCount);
        Assert.Equal(decision == SettingsExitDecision.Apply ? 1 : 0, pending.ApplyCount);
        Assert.Equal(decision == SettingsExitDecision.Discard ? 1 : 0, pending.DiscardCount);
        Assert.False(window.IsVisible);
        owner.Close();
    }

    private sealed class OwnerOnSecondaryMonitorProvider : IMonitorProvider
    {
        public IReadOnlyList<DisplayMonitor> GetMonitors() => [PrimaryMonitor, SecondaryMonitor];

        public DisplayMonitor GetMonitorForWindow(nint windowHandle) => SecondaryMonitor;
    }

    private sealed class RecordingSettingsWindowFactory : ISettingsWindowFactory
    {
        private readonly Window _owner;
        private readonly ISettingsStore _store;

        public RecordingSettingsWindowFactory(Window owner, ISettingsStore? store = null)
        {
            _owner = owner;
            _store = store ?? new TestSettingsStore();
        }

        public int CreateCount { get; private set; }

        public List<SettingsWindow> Windows { get; } = [];

        public List<RecordingPendingSettingsChanges> PendingChanges { get; } = [];

        public List<RecordingConfirmationService> Confirmations { get; } = [];

        public SettingsWindow Create()
        {
            CreateCount++;
            var pending = new RecordingPendingSettingsChanges();
            var confirmation = new RecordingConfirmationService();
            var window = CreateWindow(
                _owner,
                _store,
                pending,
                confirmation);
            Windows.Add(window);
            PendingChanges.Add(pending);
            Confirmations.Add(confirmation);
            return window;
        }
    }

    private sealed class DeferredSettingsStore : ISettingsStore
    {
        private readonly TaskCompletionSource<AppSettings> _loadCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            _loadCompletion.Task.WaitAsync(cancellationToken);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void Release() => _loadCompletion.TrySetResult(AppSettings.CreateDefault());
    }

    private sealed class DelayedPlacementSaveStore : ISettingsStore
    {
        private readonly TaskCompletionSource _firstSaveCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private AppSettings _settings = AppSettings.CreateDefault();

        public TaskCompletionSource<CancellationToken> FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstSaveFinished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int SaveCount { get; private set; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_settings);
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            var saveNumber = ++SaveCount;
            if (saveNumber == 1)
            {
                FirstSaveStarted.TrySetResult(cancellationToken);
                await _firstSaveCompletion.Task.WaitAsync(cancellationToken);
            }

            _settings = settings;
            if (saveNumber == 1)
            {
                FirstSaveFinished.TrySetResult();
            }
        }

        public void ReleaseFirstSave() => _firstSaveCompletion.TrySetResult();
    }

    private sealed class TestColorPickerService : IColorPickerService
    {
        public RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor) => null;
    }

    private sealed class RecordingSettingsWindowActivationService : ISettingsWindowActivationService
    {
        public int ShowCount { get; private set; }

        public int ActivateCount { get; private set; }

        public void Show(SettingsWindow window)
        {
            ShowCount++;
            window.Show();
        }

        public void Activate(SettingsWindow window)
        {
            ActivateCount++;
            _ = window.Activate();
        }
    }

    private sealed class RecordingPendingSettingsChanges : IPendingSettingsChanges
    {
        public event EventHandler? StateChanged;

        public bool IsDirty { get; private set; }

        public int ApplyCount { get; private set; }

        public int DiscardCount { get; private set; }

        public Task ApplyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCount++;
            SetDirty(false);
            return Task.CompletedTask;
        }

        public void Discard()
        {
            DiscardCount++;
            SetDirty(false);
        }

        public void SetDirty(bool value)
        {
            IsDirty = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class RecordingConfirmationService
        : ISettingsConfirmationService, ISettingsResetConfirmationService
    {
        public bool ConfirmDiscardResult { get; set; } = true;

        public SettingsExitDecision ExitDecision { get; set; } = SettingsExitDecision.Apply;

        public bool ConfirmResetResult { get; set; } = true;

        public int DiscardPromptCount { get; private set; }

        public int ExitPromptCount { get; private set; }

        public int ResetPromptCount { get; private set; }

        public bool ConfirmDiscard()
        {
            DiscardPromptCount++;
            return ConfirmDiscardResult;
        }

        public SettingsExitDecision ConfirmApplicationExit()
        {
            ExitPromptCount++;
            return ExitDecision;
        }

        public bool ConfirmReset()
        {
            ResetPromptCount++;
            return ConfirmResetResult;
        }
    }

    private sealed class RecordingStartupRegistrationService : IStartupRegistrationService
    {
        public bool IsEnabled { get; private set; }

        public void SetEnabled(bool enabled) => IsEnabled = enabled;
    }

    private sealed class TestApplicationInfoProvider : IApplicationInfoProvider
    {
        public ApplicationInfo Get() => new(
            "UnifiedCalendar",
            "1.0.0",
            new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero));
    }

    private sealed class NoOpLocalPathLauncher : IAppLocalPathLauncher
    {
        public Task OpenAsync(
            AppLocalPathTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static void ConfirmText(TextBox textBox, string text)
    {
        textBox.Text = text;
        var keyEvent = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(textBox),
            0,
            Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        textBox.RaiseEvent(keyEvent);
        Assert.True(keyEvent.Handled);
    }

    private static Task<AppSettings> UpdateFontSizeAsync(
        IApplicationSettingsService settings,
        int fontSizeDip) => settings.UpdateAsync(current => new AppSettings(
            new DisplayPreferences(
                current.Display.Days,
                fontSizeDip,
                current.Display.Density,
                current.Display.DefaultEventColor),
            current.Sync,
            current.General,
            current.Windows,
            current.Accounts,
            current.ColorRules));

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

}
