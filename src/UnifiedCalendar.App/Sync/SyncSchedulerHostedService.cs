using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using UnifiedCalendar.App.Services;
using UnifiedCalendar.Core.Sync;

namespace UnifiedCalendar.App.Sync;

public sealed class SyncSchedulerHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly ICalendarSyncService _syncService;
    private readonly IApplicationSettingsService _settingsService;
    private readonly IInternalRefreshRequester _internalRefreshRequester;
    private readonly ISystemResumeSignal _resumeSignal;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<bool> _resumeRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Channel<DateTimeOffset> _intervalChanges = Channel.CreateBounded<DateTimeOffset>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public SyncSchedulerHostedService(
        ICalendarSyncService syncService,
        IApplicationSettingsService settingsService,
        IInternalRefreshRequester internalRefreshRequester,
        ISystemResumeSignal resumeSignal,
        TimeProvider timeProvider)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _internalRefreshRequester = internalRefreshRequester
            ?? throw new ArgumentNullException(nameof(internalRefreshRequester));
        _resumeSignal = resumeSignal ?? throw new ArgumentNullException(nameof(resumeSignal));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _resumeSignal.Resumed += OnResumed;
        _settingsService.SettingsChanged += OnSettingsChanged;
        try
        {
            await _syncService.InitializeAsync(stoppingToken).ConfigureAwait(false);
            var startup = await _syncService.RequestSyncAsync(
                SyncTriggerReason.Startup,
                stoppingToken).ConfigureAwait(false);
            var retryAccounts = startup.StartupRetryAccountIds;
            for (var attempt = 0;
                 attempt < SyncPolicy.StartupRetryCount && retryAccounts.Count > 0;
                 attempt++)
            {
                await Task.Delay(
                    SyncPolicy.StartupRetryDelay,
                    _timeProvider,
                    stoppingToken).ConfigureAwait(false);
                var retry = await _syncService.RequestSyncAsync(
                    SyncTriggerReason.StartupRetry,
                    retryAccounts,
                    stoppingToken).ConfigureAwait(false);
                retryAccounts = retry.StartupRetryAccountIds;
            }

            DateTimeOffset? intervalChangedAt = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = await GetIntervalAsync(stoppingToken).ConfigureAwait(false);
                var intervalStartedAt = intervalChangedAt ?? _timeProvider.GetUtcNow();
                intervalChangedAt = null;
                var remainingInterval = intervalStartedAt + interval - _timeProvider.GetUtcNow();
                using var iterationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);
                var intervalDelay = Task.Delay(
                    TimeSpan.FromTicks(Math.Max(0L, remainingInterval.Ticks)),
                    _timeProvider,
                    iterationCancellation.Token);
                var resumeWait = _resumeRequests.Reader
                    .ReadAsync(iterationCancellation.Token)
                    .AsTask();
                var intervalChangeWait = _intervalChanges.Reader
                    .ReadAsync(iterationCancellation.Token)
                    .AsTask();
                var completed = await Task.WhenAny(
                    intervalDelay,
                    resumeWait,
                    intervalChangeWait).ConfigureAwait(false);
                if (completed == intervalChangeWait)
                {
                    intervalChangedAt = await intervalChangeWait.ConfigureAwait(false);
                    while (_intervalChanges.Reader.TryRead(out var newerChange))
                    {
                        intervalChangedAt = newerChange;
                    }

                    iterationCancellation.Cancel();
                    await IgnoreIterationCancellationAsync(intervalDelay).ConfigureAwait(false);
                    await IgnoreIterationCancellationAsync(resumeWait).ConfigureAwait(false);
                    continue;
                }

                if (completed == resumeWait)
                {
                    await resumeWait.ConfigureAwait(false);
                    iterationCancellation.Cancel();
                    await IgnoreIterationCancellationAsync(intervalDelay).ConfigureAwait(false);
                    await IgnoreIterationCancellationAsync(intervalChangeWait).ConfigureAwait(false);
                    await _internalRefreshRequester.RequestRefreshAsync(
                        InternalRefreshReason.Resume,
                        stoppingToken).ConfigureAwait(false);
                    await _syncService.RequestSyncAsync(
                        SyncTriggerReason.Resume,
                        stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await intervalDelay.ConfigureAwait(false);
                iterationCancellation.Cancel();
                await IgnoreIterationCancellationAsync(resumeWait).ConfigureAwait(false);
                await IgnoreIterationCancellationAsync(intervalChangeWait).ConfigureAwait(false);
                await _syncService.RequestSyncAsync(
                    SyncTriggerReason.Scheduled,
                    stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _resumeSignal.Resumed -= OnResumed;
            _settingsService.SettingsChanged -= OnSettingsChanged;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _syncService.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TimeSpan> GetIntervalAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            return TimeSpan.FromMinutes(settings.Sync.IntervalMinutes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DefaultInterval;
        }
    }

    private void OnResumed(object? sender, EventArgs eventArgs)
    {
        _resumeRequests.Writer.TryWrite(true);
    }

    private void OnSettingsChanged(object? sender, ApplicationSettingsChangedEventArgs eventArgs)
    {
        if ((eventArgs.ChangedSections & SettingsSection.Sync) != 0)
        {
            _intervalChanges.Writer.TryWrite(_timeProvider.GetUtcNow());
        }
    }

    private static async Task IgnoreIterationCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
