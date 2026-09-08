using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace UnifiedCalendar.Infrastructure.Logging;

/// <summary>
/// Preserves CompactJsonFormatter output while rendering @t with the offset captured by LogEvent.
/// </summary>
public sealed class LocalTimestampCompactJsonFormatter : ITextFormatter
{
    private const string TimestampPrefix = "{\"@t\":\"";
    private readonly CompactJsonFormatter _inner = new();

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        var compactJson = buffer.ToString();
        if (!compactJson.StartsWith(TimestampPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CompactJsonFormatter output does not begin with the expected timestamp field.");
        }

        var timestampEnd = compactJson.IndexOf('"', TimestampPrefix.Length);
        if (timestampEnd < 0)
        {
            throw new InvalidOperationException(
                "CompactJsonFormatter output has an invalid timestamp field.");
        }

        output.Write(TimestampPrefix);
        output.Write(logEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        output.Write(compactJson.AsSpan(timestampEnd));
    }
}
