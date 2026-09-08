using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnifiedCalendar.Infrastructure.Logging;
using Xunit;

namespace UnifiedCalendar.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostLoggingPathCollection
{
    public const string Name = "Host logging path tests";
}

[Collection(HostLoggingPathCollection.Name)]
public sealed partial class HostLoggingPathTests
{
    [Fact]
    public async Task SerilogFileSink_UsesInjectedPathAndWritesLocalOffsetTimestamp()
    {
        using var temporary = new TemporaryAppDirectory();
        using var host = Host.CreateDefaultBuilder()
            .UseUnifiedCalendarSerilog()
            .ConfigureServices(services => services.AddSingleton(temporary.Paths))
            .Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("LoggingPathProbe");
        var earliestUtc = TimeProvider.System.GetUtcNow();
        logger.LogDebug("LoggingPathDebugProbe");
        logger.LogInformation("LoggingPathProbe");
        var latestUtc = TimeProvider.System.GetUtcNow();
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        var logFile = Assert.Single(Directory.GetFiles(
            temporary.Paths.LogsDirectory,
            "unifiedcalendar-*.log",
            SearchOption.TopDirectoryOnly));
        var lines = await File.ReadAllLinesAsync(
            logFile,
            TestContext.Current.CancellationToken);
        var probeLines = lines
            .Where(line => line.Contains("LoggingPathProbe", StringComparison.Ordinal))
            .ToArray();
        var probeLine = Assert.Single(probeLines);
        Assert.DoesNotContain(lines, line =>
            line.Contains("LoggingPathDebugProbe", StringComparison.Ordinal));

        using var document = JsonDocument.Parse(probeLine);
        var timestampText = Assert.IsType<string>(
            document.RootElement.GetProperty("@t").GetString());
        Assert.False(timestampText.EndsWith('Z'));
        Assert.Matches(ExplicitOffsetPattern(), timestampText);
        var timestamp = DateTimeOffset.ParseExact(
            timestampText,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.InRange(timestamp.ToUniversalTime(), earliestUtc, latestUtc);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(timestamp.UtcDateTime), timestamp.Offset);
    }

    [GeneratedRegex("[+-][0-9]{2}:[0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffsetPattern();
}
