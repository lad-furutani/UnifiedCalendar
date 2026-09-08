using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using UnifiedCalendar.Infrastructure.Logging;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed partial class LocalTimestampCompactJsonFormatterTests
{
    [Fact]
    public void Format_ReplacesOnlyTimestampForAllSupportedCompactJsonShapes()
    {
        var sourceEvents = CreateSourceEvents();
        var offsets = new[]
        {
            TimeSpan.FromHours(9),
            TimeSpan.Zero,
            TimeSpan.FromHours(-4),
        };
        var standardFormatter = new CompactJsonFormatter();
        var subject = new LocalTimestampCompactJsonFormatter();

        for (var index = 0; index < sourceEvents.Count; index++)
        {
            var logEvent = WithTimestamp(
                sourceEvents[index],
                new DateTimeOffset(
                    2026,
                    9,
                    6,
                    12 + index,
                    34,
                    56,
                    789,
                    offsets[index]).AddTicks(1234));
            var standard = Format(standardFormatter, logEvent);
            var actual = Format(subject, logEvent);
            var expected = ReplaceTimestamp(standard, logEvent.Timestamp);

            Assert.Equal(expected, actual);
            using var document = JsonDocument.Parse(actual);
            var timestampText = Assert.IsType<string>(
                document.RootElement.GetProperty("@t").GetString());
            Assert.Equal(logEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture), timestampText);
            Assert.False(timestampText.EndsWith('Z'));
            Assert.Matches(ExplicitOffsetPattern(), timestampText);
        }
    }

    private static IReadOnlyList<LogEvent> CreateSourceEvents()
    {
        var sink = new CollectingLogSink();
        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("Message without properties");
        logger.Warning(
            "Scalar value {Scalar}",
            "quote \" slash \\ newline\n日本語");
        logger.Error(
            new InvalidOperationException("Safe exception with \"quotes\" and newline\ntext"),
            "Complex values {@Sequence} {@Dictionary} {@Structure}",
            new[] { 1, 2, 3 },
            new Dictionary<string, object?>
            {
                ["alpha"] = true,
                ["escaped"] = "line1\nline2\\end",
            },
            new { Name = "構造化", Enabled = true });

        return sink.Events.ToArray();
    }

    private static LogEvent WithTimestamp(LogEvent source, DateTimeOffset timestamp) => new(
        timestamp,
        source.Level,
        source.Exception,
        source.MessageTemplate,
        source.Properties.Select(property => new LogEventProperty(property.Key, property.Value)));

    private static string Format(ITextFormatter formatter, LogEvent logEvent)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        formatter.Format(logEvent, output);
        return output.ToString();
    }

    private static string ReplaceTimestamp(string compactJson, DateTimeOffset timestamp)
    {
        const string prefix = "{\"@t\":\"";
        Assert.StartsWith(prefix, compactJson, StringComparison.Ordinal);
        var timestampEnd = compactJson.IndexOf('"', prefix.Length);
        Assert.True(timestampEnd >= 0);
        return string.Concat(
            compactJson.AsSpan(0, prefix.Length),
            timestamp.ToString("O", CultureInfo.InvariantCulture),
            compactJson.AsSpan(timestampEnd));
    }

    [GeneratedRegex("[+-][0-9]{2}:[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffsetPattern();

    private sealed class CollectingLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
