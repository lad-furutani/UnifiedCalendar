using System.Collections.ObjectModel;
using System.Diagnostics;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class Phase6PerformanceTests
{
    private const int RegressionLimitMilliseconds = 1_000;
    private readonly ITestOutputHelper _output;

    public Phase6PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ThousandItemProjectionAndDiffReportTimingWithoutTreatingTheInitialGoalAsAcceptanceLimit()
    {
        var source = CreateThousandItemSnapshot(changeTitles: false);
        var changed = CreateThousandItemSnapshot(changeTitles: true);
        var service = new CalendarPresentationService(
            new MutableTimeProvider(Phase6Data.Now),
            ColorMetrics.ProgressLightnessDelta);
        var display = new DisplaySettings();
        _ = service.BuildSnapshot(source, display, [], TimeZoneInfo.Utc);

        var buildTimer = Stopwatch.StartNew();
        var initial = service.BuildSnapshot(source, display, [], TimeZoneInfo.Utc);
        buildTimer.Stop();

        var target = new ObservableCollection<TimelineItemViewModel>();
        var differ = new SnapshotDiffer();
        var text = new ResourceUiTextService();
        var brushes = new BrushCache();
        var launcher = new RecordingUriLauncher();
        var applyTimer = Stopwatch.StartNew();
        differ.Apply(
            target,
            initial,
            value => new EventRowViewModel(value, text, brushes, launcher, _ => { }),
            value => new DayHeaderItemViewModel(value.Date, text.Format(value.Header)));
        applyTimer.Stop();

        var changedPresentation = service.BuildSnapshot(changed, display, [], TimeZoneInfo.Utc);
        var updateTimer = Stopwatch.StartNew();
        differ.Apply(
            target,
            changedPresentation,
            value => new EventRowViewModel(value, text, brushes, launcher, _ => { }),
            value => new DayHeaderItemViewModel(value.Date, text.Format(value.Header)));
        updateTimer.Stop();

        _output.WriteLine(
            "Phase 6 pure-logic timing (excludes WPF layout/render): BuildSnapshot={0:F1} ms, initial diff={1:F1} ms, update diff={2:F1} ms",
            buildTimer.Elapsed.TotalMilliseconds,
            applyTimer.Elapsed.TotalMilliseconds,
            updateTimer.Elapsed.TotalMilliseconds);
        Assert.Equal(1_000, initial.Events.Count);
        Assert.Equal(1_001, target.Count);
        Assert.True(
            buildTimer.ElapsedMilliseconds < RegressionLimitMilliseconds,
            $"BuildSnapshot exceeded the gross-regression limit: {buildTimer.ElapsedMilliseconds} ms");
        Assert.True(
            applyTimer.ElapsedMilliseconds < RegressionLimitMilliseconds,
            $"Initial diff exceeded the gross-regression limit: {applyTimer.ElapsedMilliseconds} ms");
        Assert.True(
            updateTimer.ElapsedMilliseconds < RegressionLimitMilliseconds,
            $"Update diff exceeded the gross-regression limit: {updateTimer.ElapsedMilliseconds} ms");
    }

    private static SyncSnapshot CreateThousandItemSnapshot(bool changeTitles)
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        var events = Enumerable.Range(0, 1_000)
            .Select(index => Phase6Data.Event(
                $"event-{index:D4}",
                title: $"Event {index:D4}{(changeTitles ? " changed" : string.Empty)}",
                startUtc: Phase6Data.Now.AddMinutes(1 + index % 300),
                endUtc: Phase6Data.Now.AddHours(8).AddMinutes(index % 300)))
            .ToArray();
        return Phase6Data.Snapshot(
            [account],
            [new CalendarSelection(account.InternalAccountId, "calendar", true)],
            events);
    }
}
