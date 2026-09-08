using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using UnifiedCalendar.Infrastructure.Storage;

namespace UnifiedCalendar.Infrastructure.Logging;

public static class HostBuilderExtensions
{
    private const int RetainedLogFileCount = 30;

    public static IHostBuilder UseUnifiedCalendarSerilog(this IHostBuilder hostBuilder)
    {
        ArgumentNullException.ThrowIfNull(hostBuilder);

        return hostBuilder.UseSerilog((_, services, loggerConfiguration) =>
        {
            var paths = services.GetRequiredService<AppPaths>();
            Directory.CreateDirectory(paths.LogsDirectory);
            var logPath = Path.Combine(paths.LogsDirectory, "unifiedcalendar-.log");

            loggerConfiguration
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.File(
                    new LocalTimestampCompactJsonFormatter(),
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetainedLogFileCount,
                    shared: false);
        });
    }
}
