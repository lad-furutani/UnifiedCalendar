using Microsoft.Extensions.Hosting;
using UnifiedCalendar.Core.Sync;

namespace UnifiedCalendar.App.Sync;

public sealed class MinuteRefreshHostedService : BackgroundService
{
    private readonly IInternalRefreshRequester _refreshRequester;
    private readonly TimeProvider _timeProvider;

    public MinuteRefreshHostedService(
        IInternalRefreshRequester refreshRequester,
        TimeProvider timeProvider)
    {
        _refreshRequester = refreshRequester ?? throw new ArgumentNullException(nameof(refreshRequester));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var now = _timeProvider.GetLocalNow();
                var nextMinute = new DateTimeOffset(
                    now.Year,
                    now.Month,
                    now.Day,
                    now.Hour,
                    now.Minute,
                    0,
                    now.Offset).AddMinutes(1);
                await Task.Delay(
                    nextMinute - now,
                    _timeProvider,
                    stoppingToken).ConfigureAwait(false);
                await _refreshRequester.RequestRefreshAsync(
                    InternalRefreshReason.MinuteBoundary,
                    stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
