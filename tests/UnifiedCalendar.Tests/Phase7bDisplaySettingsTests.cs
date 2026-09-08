using Microsoft.Extensions.DependencyInjection;
using UnifiedCalendar.App;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Presentation;
using UnifiedCalendar.Core.Sync;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class Phase7bDisplaySettingsTests
{
    [Fact]
    public void PaletteAndSelectableIntervalsMatchApprovedOrder()
    {
        var (display, update, _, _, _, _) = CreateViewModels();

        Assert.Equal(
            ["#2F6FED", "#00696F", "#1F6B24", "#8A5200", "#B01F1F", "#96114B", "#5A2E9B", "#3A4B54"],
            display.ColorOptions.Select(option => option.Color.ToHexString()));
        Assert.Equal([1, 5, 10, 15, 30, 60], update.IntervalOptions.Select(option => option.Minutes));
        Assert.True(display.IsStandard);
        Assert.False(display.IsCompact);
        Assert.False(display.IsComfortable);
    }

    [Fact]
    public void PaletteColorsFlowThroughExistingForegroundAndProgressPipeline()
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var calendarEvent = new CalendarEvent(
            new EventKey(ProviderKind.Google, account.InternalAccountId, "calendar", "palette"),
            "palette",
            new TimedEventTiming(Phase6Data.Now.AddMinutes(-30), Phase6Data.Now.AddMinutes(30)),
            "calendar");
        var snapshot = Phase6Data.Snapshot(
            [account],
            [new CalendarSelection(account.InternalAccountId, "calendar", true)],
            [calendarEvent]);
        var presentation = new CalendarPresentationService(
            new MutableTimeProvider(Phase6Data.Now),
            ColorMetrics.ProgressLightnessDelta);

        foreach (var color in SettingsColorPalette.Colors)
        {
            var presented = Assert.Single(presentation.BuildSnapshot(
                snapshot,
                new DisplaySettings(defaultEventColor: color),
                [],
                TimeZoneInfo.Utc).Events);

            Assert.Equal(color, presented.BackgroundColor);
            Assert.Equal(RgbColor.Parse("#FFFFFF"), presented.ForegroundColor);
            Assert.Equal(
                color.AdjustLightness(-ColorMetrics.ProgressLightnessDelta),
                presented.ElapsedColor);
            Assert.Equal(
                color.AdjustLightness(ColorMetrics.ProgressLightnessDelta),
                presented.RemainingColor);
        }
    }

    [Fact]
    public async Task DaysSaveImmediatelyAndOnlyExpansionRequestsManualSync()
    {
        var (display, _, store, sync, _, _) = CreateViewModels();
        var reasons = new List<SyncTriggerReason>();
        sync.RequestHandler = (reason, _) =>
        {
            reasons.Add(reason);
            return Task.FromResult(new SyncRunResult(reason, []));
        };

        display.Days = 90;
        await display.WaitForPendingUpdatesAsync();

        Assert.Equal(90, store.Settings.Display.Days);
        Assert.Equal([SyncTriggerReason.Manual], reasons);

        display.Days = 1;
        await display.WaitForPendingUpdatesAsync();

        Assert.Equal(1, store.Settings.Display.Days);
        Assert.Equal([SyncTriggerReason.Manual], reasons);
        Assert.Throws<ArgumentOutOfRangeException>(() => display.Days = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => display.Days = 91);
    }

    [Fact]
    public async Task FontDensityPaletteAndCustomColorSaveThroughSettingsService()
    {
        var custom = RgbColor.Parse("#123456");
        var (display, _, store, _, picker, settingsService) = CreateViewModels(custom);
        var changedSections = SettingsSection.None;
        settingsService.SettingsChanged += (_, eventArgs) =>
            changedSections |= eventArgs.ChangedSections;

        display.FontSizeDip = 20;
        display.Density = DisplayDensity.Compact;
        await display.WaitForPendingUpdatesAsync();
        Assert.Equal(DisplayDensity.Compact, store.Settings.Display.Density);
        display.Density = DisplayDensity.Standard;
        await display.WaitForPendingUpdatesAsync();
        Assert.Equal(DisplayDensity.Standard, store.Settings.Display.Density);
        display.Density = DisplayDensity.Comfortable;
        display.SelectedColorOption = display.ColorOptions[2];
        await display.WaitForPendingUpdatesAsync();

        Assert.Equal(20, store.Settings.Display.FontSizeDip);
        Assert.Equal(DisplayDensity.Comfortable, store.Settings.Display.Density);
        Assert.Equal(RgbColor.Parse("#1F6B24"), store.Settings.Display.DefaultEventColor);
        Assert.Equal(SettingsSection.Display, changedSections);

        await display.PickCustomColorAsync((nint)123);

        Assert.Equal((nint)123, picker.OwnerWindowHandle);
        Assert.Equal(RgbColor.Parse("#1F6B24"), picker.InitialColor);
        Assert.Equal(custom, store.Settings.Display.DefaultEventColor);
        Assert.Equal(custom, display.CurrentColor);
        Assert.Null(display.SelectedColorOption);
        Assert.Throws<ArgumentOutOfRangeException>(() => display.FontSizeDip = 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => display.FontSizeDip = 25);
    }

    [Fact]
    public async Task UpdateIntervalSavesImmediatelyWithoutPendingApplyState()
    {
        var (display, update, store, _, _, _) = CreateViewModels();

        update.SelectedInterval = update.IntervalOptions.Single(option => option.Minutes == 30);
        await update.WaitForPendingUpdatesAsync();

        Assert.Equal(30, store.Settings.Sync.IntervalMinutes);
        Assert.True(display.IsStandard);
    }

    [Fact]
    public void SettingsWindowViewModelIsCreatedByFactoryInsteadOfRegisteredAsTransient()
    {
        var services = new ServiceCollection();

        services.AddUnifiedCalendarApplication();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(SettingsWindowViewModel));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(ISettingsWindowFactory)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    private static (
        DisplaySettingsViewModel Display,
        UpdateSettingsViewModel Update,
        TestSettingsStore Store,
        TestCalendarSyncService Sync,
        RecordingColorPicker Picker,
        IApplicationSettingsService SettingsService) CreateViewModels(RgbColor? pickedColor = null)
    {
        var store = new TestSettingsStore();
        var settings = new ApplicationSettingsService(store);
        var sync = new TestCalendarSyncService();
        var picker = new RecordingColorPicker(pickedColor);
        var text = new ResourceUiTextService();
        var display = new DisplaySettingsViewModel(
            settings,
            sync,
            picker,
            new BrushCache(),
            text);
        var update = new UpdateSettingsViewModel(settings, text);
        display.Initialize(store.Settings);
        update.Initialize(store.Settings);
        return (display, update, store, sync, picker, settings);
    }

    private sealed class RecordingColorPicker(RgbColor? result) : IColorPickerService
    {
        public nint OwnerWindowHandle { get; private set; }

        public RgbColor InitialColor { get; private set; }

        public RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor)
        {
            OwnerWindowHandle = ownerWindowHandle;
            InitialColor = initialColor;
            return result;
        }
    }

}
