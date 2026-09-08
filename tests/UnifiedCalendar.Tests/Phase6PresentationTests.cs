using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using UnifiedCalendar.App.Presentation;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.App.ViewModels;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Presentation;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class Phase6PresentationTests
{
    [Fact]
    public void JapaneseResourcesContainEveryCoreAndAppResourceKey()
    {
        var service = new ResourceUiTextService();
        var keys = PublicStringConstants(typeof(UiTextResourceKeys))
            .Concat(PublicStringConstants(typeof(UiResourceKeys)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.True(service.Contains(key), $"Missing resource: {key}"));
    }

    [Fact]
    public void JapaneseTimedResourcesUseTwoDigitHoursWithoutPaddingMonthOrDay()
    {
        var service = new ResourceUiTextService();
        var date = new DateOnly(2026, 8, 27);
        var nextDate = date.AddDays(1);
        var one = new TimeOnly(1, 0);
        var eightThirty = new TimeOnly(8, 30);

        Assert.Equal("01:00", service.Get(UiTextResourceKeys.EventListInstant, one));
        Assert.Equal(
            "01:00–08:30",
            service.Get(UiTextResourceKeys.EventListTimedRange, one, eightThirty));
        Assert.Equal(
            "8/27(木) 01:00 ～ 08:30",
            service.Get(UiTextResourceKeys.EventDetailTimedSingleDay, date, one, eightThirty));
        Assert.Equal(
            "8/27(木) 01:00 ～ 8/28(金) 08:30",
            service.Get(
                UiTextResourceKeys.EventDetailTimedRange,
                date,
                one,
                nextDate,
                eightThirty));
        Assert.Equal(
            "01:00",
            service.Get(UiResourceKeys.CurrentTime, date.ToDateTime(one)));
        Assert.Equal(
            "最終更新 01:00",
            service.Get(UiResourceKeys.LastUpdateToday, date.ToDateTime(one)));
        Assert.Equal(
            "最終更新 8/27 01:00",
            service.Get(UiResourceKeys.LastUpdateOtherDay, date.ToDateTime(one)));

        var detail = service.Get(
            UiTextResourceKeys.EventDetailTimedSingleDay,
            date,
            one,
            eightThirty);
        var tooltip = service.Get(UiTextResourceKeys.EventTooltip, "Title", detail);
        Assert.Contains("01:00 ～ 08:30", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotDifferReusesEventRowsAndNeverRaisesReset()
    {
        var source = VisibleSnapshot(
            Phase6Data.Event("a", title: "Alpha"),
            Phase6Data.Event("b", title: "Beta", startUtc: Phase6Data.Now.AddHours(2), endUtc: Phase6Data.Now.AddHours(3)));
        var first = Phase6Data.Present(source);
        var target = new ObservableCollection<TimelineItemViewModel>();
        var actions = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => actions.Add(args.Action);
        var differ = new SnapshotDiffer();
        var text = new ResourceUiTextService();
        var launcher = new RecordingUriLauncher();
        var brushes = new BrushCache();

        differ.Apply(
            target,
            first,
            value => new EventRowViewModel(value, text, brushes, launcher, _ => { }),
            value => new DayHeaderItemViewModel(value.Date, text.Format(value.Header)));
        var original = target.OfType<EventRowViewModel>().Single(value => value.Key.SourceEventId == "a");
        var updatedEvent = Phase6Data.Event("a", title: "Changed");
        var updated = Phase6Data.Present(VisibleSnapshot(updatedEvent));
        differ.Apply(
            target,
            updated,
            value => new EventRowViewModel(value, text, brushes, launcher, _ => { }),
            value => new DayHeaderItemViewModel(value.Date, text.Format(value.Header)));

        Assert.Same(original, Assert.Single(target.OfType<EventRowViewModel>()));
        Assert.Equal("Changed", original.Title);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void ViewportPlannerRestoresNearestAnchorAndNextFocusWithoutInitialJumpAfterUserScroll()
    {
        var a = Key("a");
        var b = Key("b");
        var c = Key("c");
        var d = Key("d");
        var before = new TimelineViewportState(b, -7d, b, b, true);

        var result = ViewportRestorationPlanner.Create(
            before,
            [a, b, c],
            [a, c, d],
            scrollToUpcoming: true,
            upcomingKey: c);

        Assert.Equal(c, result.AnchorKey);
        Assert.Equal(-7d, result.AnchorPixelOffset);
        Assert.Equal(c, result.FocusedKey);
        Assert.True(result.CloseDetails);
        Assert.False(result.ScrollToUpcoming);
    }

    [Fact]
    public void ViewportPlannerUsesPreviousFocusWhenDeletedItemWasLast()
    {
        var a = Key("a");
        var b = Key("b");
        var result = ViewportRestorationPlanner.Create(
            new TimelineViewportState(b, 0d, b, null, false),
            [a, b],
            [a],
            false,
            null);

        Assert.Equal(a, result.FocusedKey);
    }

    [Fact]
    public void ViewportPlannerKeepsExistingAnchorAndClearsFocusWhenNoRowsRemain()
    {
        var a = Key("a");
        var retained = ViewportRestorationPlanner.Create(
            new TimelineViewportState(a, -4d, a, null, false),
            [a],
            [a],
            false,
            null);
        var removed = ViewportRestorationPlanner.Create(
            new TimelineViewportState(a, -4d, a, null, false),
            [a],
            [],
            false,
            null);

        Assert.Equal(a, retained.AnchorKey);
        Assert.Equal(a, retained.FocusedKey);
        Assert.Null(removed.AnchorKey);
        Assert.Null(removed.FocusedKey);
    }

    [Fact]
    public async Task EventRowsKeepMeetingAndSourceActionsSeparateFromDetails()
    {
        var meeting = new Uri("https://meet.example.invalid/room");
        var source = new Uri("https://calendar.example.invalid/event");
        var presented = Assert.Single(Phase6Data.Present(VisibleSnapshot(
            Phase6Data.Event("event", meetingUri: meeting, sourceDetailUri: source))).Events);
        var launcher = new RecordingUriLauncher();
        var detailsOpened = 0;
        var row = new EventRowViewModel(
            presented,
            new ResourceUiTextService(),
            new BrushCache(),
            launcher,
            _ => detailsOpened++);

        await row.OpenMeetingCommand.ExecuteAsync(null);
        await row.OpenSourceCommand.ExecuteAsync(null);

        Assert.Equal([meeting, source], launcher.OpenedUris);
        Assert.Equal(0, detailsOpened);
        Assert.True(row.IsFocusable);
    }

    [Theory]
    [InlineData(AttendeeResponse.Accepted, false)]
    [InlineData(AttendeeResponse.Tentative, true)]
    [InlineData(AttendeeResponse.NotResponded, true)]
    [InlineData(AttendeeResponse.Declined, true)]
    [InlineData(AttendeeResponse.Unknown, true)]
    public void EventRowsExposeAttentionTooltipOnlyForNonAcceptedResponses(
        AttendeeResponse response,
        bool expectedAttention)
    {
        var presented = Assert.Single(Phase6Data.Present(VisibleSnapshot(
            Phase6Data.Event("event", responseStatus: response))).Events);
        var row = new EventRowViewModel(
            presented,
            new ResourceUiTextService(),
            new BrushCache(),
            new RecordingUriLauncher(),
            _ => { });

        Assert.Equal(expectedAttention, row.AttentionText is not null);
        if (expectedAttention)
        {
            Assert.NotEmpty(row.AttentionText!);
        }
    }

    [Fact]
    public async Task EventDetailsUpdatesSameIdentityAndLaunchesDetectedUriCommand()
    {
        var first = Assert.Single(Phase6Data.Present(VisibleSnapshot(
            Phase6Data.Event("event", title: "First"))).Events);
        var changed = Assert.Single(Phase6Data.Present(VisibleSnapshot(
            Phase6Data.Event("event", title: "Changed"))).Events);
        var launcher = new RecordingUriLauncher();
        var details = new EventDetailsViewModel(first, new ResourceUiTextService(), launcher);
        var uri = new Uri("https://docs.example.invalid/item");

        details.UpdateFrom(changed);
        await details.OpenContentUriCommand.ExecuteAsync(uri);

        Assert.Equal("Changed", details.Title);
        Assert.Equal(uri, Assert.Single(launcher.OpenedUris));
    }

    [Fact]
    public void TimeColumnWidthIncludesLongestMeasuredSampleAndPadding()
    {
        var textService = new ResourceUiTextService();
        var calculator = new TimeColumnWidthCalculator(textService);
        var fontFamily = new FontFamily("Yu Gothic UI");

        var width100 = calculator.Calculate(
            fontFamily,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal,
            14d,
            1d);
        var width200 = calculator.Calculate(
            fontFamily,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal,
            14d,
            2d);

        Assert.True(width100 > LayoutMetrics.TimeColumnHorizontalPadding);
        Assert.True(width200 > LayoutMetrics.TimeColumnHorizontalPadding);
        var zeroPaddedSample = textService.Get(
            UiTextResourceKeys.EventListTimedRange,
            new TimeOnly(8, 30),
            new TimeOnly(9, 30));
        Assert.Equal("08:30–09:30", zeroPaddedSample);
        var measuredSample = new FormattedText(
            zeroPaddedSample,
            CultureInfo.GetCultureInfo("ja-JP"),
            FlowDirection.LeftToRight,
            new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            14d,
            Brushes.Black,
            1d).WidthIncludingTrailingWhitespace;
        Assert.True(width100 >= Math.Ceiling(
            measuredSample + LayoutMetrics.TimeColumnHorizontalPadding));
    }

    [Fact]
    public async Task UnavailableAccountInteractionsFailFastAndHonorCancellation()
    {
        var service = new UnavailableAccountInteractionService();

        Assert.False(service.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddAccountAsync(
                ProviderKind.Google,
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReauthenticateAsync(
                Phase6Data.GoogleAccountId,
                TestContext.Current.CancellationToken));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AddAccountAsync(ProviderKind.Google, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReauthenticateAsync(Phase6Data.GoogleAccountId, cancellation.Token));
    }

    [Theory]
    [InlineData("mailto:user@example.invalid")]
    [InlineData("relative/path")]
    public async Task ExternalUriLauncherRejectsNonWebUris(string value)
    {
        var launcher = new ExternalUriLauncher();

        await Assert.ThrowsAsync<ArgumentException>(() => launcher.OpenAsync(
            new Uri(value, UriKind.RelativeOrAbsolute),
            TestContext.Current.CancellationToken));
    }

    private static IEnumerable<string> PublicStringConstants(Type type) => type
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!);

    private static EventKey Key(string id) => new(
        ProviderKind.Google,
        Phase6Data.GoogleAccountId,
        "calendar",
        id);

    private static SyncSnapshot VisibleSnapshot(params CalendarEvent[] events)
    {
        var account = Phase6Data.Account(Phase6Data.GoogleAccountId, ProviderKind.Google);
        return Phase6Data.Snapshot(
            [account],
            [new CalendarSelection(account.InternalAccountId, "calendar", true)],
            events);
    }
}
