using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.Core;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Sync;
using UnifiedCalendar.Infrastructure.Logging;
using UnifiedCalendar.Providers.Google;
using UnifiedCalendar.Providers.Microsoft;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class SyncPolicyAndLoggingTests
{
    [Fact]
    public void ProviderApiTimeoutsUseTheFixedThirtySecondPolicy()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ApplicationDefaults.ApiTimeout);
        Assert.Equal(ApplicationDefaults.ApiTimeout, new GoogleProviderOptions("client-id").ApiTimeout);
        Assert.Equal(ApplicationDefaults.ApiTimeout, new MicrosoftProviderOptions("client-id").ApiTimeout);
    }

    [Fact]
    public void SyncLoggerHashesCalendarIdentifiersAndUsesOnlySafeMetadata()
    {
        const string calendarId = "private-calendar-id@example.invalid";
        var sink = new CollectingLogSink();
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var logger = new SyncEventLogger(serilog);
        var accountId = Guid.NewGuid();

        logger.AccountStarted(ProviderKind.Google, accountId, SyncTriggerReason.Startup);
        logger.CalendarCompleted(
            ProviderKind.Google,
            accountId,
            calendarId,
            3,
            SyncStatus.Succeeded,
            SyncErrorCategory.None,
            12);
        logger.RetryScheduled(
            ProviderKind.Google,
            accountId,
            calendarId,
            1,
            TimeSpan.FromSeconds(2),
            SyncErrorCategory.Network);

        var rendered = string.Join(
            Environment.NewLine,
            sink.Events.Select(value =>
                $"{value.RenderMessage()} {string.Join(' ', value.Properties.Values)}"));
        Assert.DoesNotContain(calendarId, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("private-calendar-id", rendered, StringComparison.Ordinal);
        Assert.Contains(accountId.ToString(), rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CalendarKeyHash", string.Join(' ', sink.Events.Select(value => value.MessageTemplate.Text)));
    }

    private sealed class CollectingLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
