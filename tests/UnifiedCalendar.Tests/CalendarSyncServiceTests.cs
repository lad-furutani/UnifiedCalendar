using System.Collections.Concurrent;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Core.Providers;
using UnifiedCalendar.Core.Sync;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class CalendarSyncServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Acc006_InitializeLoadsAvailableAccountCachesWithoutCallingProviders()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var cachedEvent = CreateEvent(account, "calendar-a", "cached-event", Now.AddHours(1));
        var cacheStore = new RecordingCacheStore();
        cacheStore.Caches[account.InternalAccountId] = CreateCache(
            account,
            ("calendar-a", new[] { cachedEvent }));
        var provider = new ScriptedProvider(ProviderKind.Google)
        {
            ListCalendars = (_, _) => throw new InvalidOperationException("The API must not be called while loading startup cache."),
        };
        await using var service = CreateService(account, cacheStore, provider);

        var result = await service.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.True(result.HasCache);
        Assert.Same(result.Snapshot, service.CurrentSnapshot);
        Assert.Equal([cachedEvent], result.Snapshot.Events);
        Assert.Equal(0, provider.ListCalendarCalls);
        Assert.Equal(SyncStatus.NotStarted, Assert.Single(result.AccountStates).Status);
    }

    [Fact]
    public async Task Acc006_InitializeWithoutCachePublishesEmptySnapshot()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        await using var service = CreateService(
            account,
            new RecordingCacheStore(),
            SuccessfulProvider(account));

        var result = await service.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.False(result.HasCache);
        Assert.Empty(result.Snapshot.Events);
        Assert.Single(result.Snapshot.Accounts);
        Assert.Single(result.Snapshot.CalendarSelections);
    }

    [Fact]
    public async Task Acc005_PartialCalendarSuccessMergesNewDataWithPreviousFailedCalendar()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a", "calendar-b");
        var oldA = CreateEvent(account, "calendar-a", "old-a", Now.AddHours(1));
        var oldB = CreateEvent(account, "calendar-b", "old-b", Now.AddHours(2));
        var newA = CreateEvent(account, "calendar-a", "new-a", Now.AddHours(3));
        var cacheStore = new RecordingCacheStore();
        cacheStore.Caches[account.InternalAccountId] = CreateCache(
            account,
            ("calendar-a", new[] { oldA }),
            ("calendar-b", new[] { oldB }));
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, calendar, _, _) => Task.FromResult(
            calendar.CalendarId == "calendar-a"
                ? ProviderCalendarResult.Success([newA])
                : ProviderCalendarResult.Failure(
                    new ProviderError(ProviderErrorCategory.PermissionDenied, 403)));
        await using var service = CreateService(account, cacheStore, provider);
        await service.InitializeAsync(TestContext.Current.CancellationToken);

        var run = await service.RequestSyncAsync(
            SyncTriggerReason.Manual,
            TestContext.Current.CancellationToken);

        var saved = Assert.Single(cacheStore.Saved);
        Assert.Equal([newA], Assert.Single(saved.Calendars, value => value.CalendarId == "calendar-a").Events);
        Assert.Equal([oldB], Assert.Single(saved.Calendars, value => value.CalendarId == "calendar-b").Events);
        Assert.Equal([newA, oldB], service.CurrentSnapshot.Events);
        var result = Assert.Single(run.Accounts);
        Assert.Equal(SyncAccountResultKind.PartiallySucceeded, result.Result);
        Assert.Equal(SyncStatus.PartiallySucceeded, result.State.Status);
        Assert.Equal(SyncStatus.Succeeded, result.State.CalendarStates[0].Status);
        Assert.Equal(SyncErrorCategory.PermissionDenied, result.State.CalendarStates[1].ErrorCategory);
        Assert.Equal(Now.AddMinutes(-5), result.State.CalendarStates[1].LastSuccessUtc);
        Assert.Equal(Now.AddMinutes(-5), result.State.LastFullySuccessfulSyncUtc);
    }

    [Fact]
    public async Task AllCalendarFailuresLeaveCacheAndSnapshotUnchanged()
    {
        var account = CreateAccount(ProviderKind.Microsoft, "calendar-a", "calendar-b");
        var previous = CreateEvent(account, "calendar-a", "previous", Now.AddHours(1));
        var cacheStore = new RecordingCacheStore();
        cacheStore.Caches[account.InternalAccountId] = CreateCache(
            account,
            ("calendar-a", new[] { previous }));
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, _, _, _) => Task.FromResult(
            ProviderCalendarResult.Failure(
                new ProviderError(ProviderErrorCategory.PermissionDenied, 403)));
        await using var service = CreateService(account, cacheStore, provider);
        await service.InitializeAsync(TestContext.Current.CancellationToken);

        var run = await service.RequestSyncAsync(
            SyncTriggerReason.Scheduled,
            TestContext.Current.CancellationToken);

        Assert.Empty(cacheStore.Saved);
        Assert.Equal([previous], service.CurrentSnapshot.Events);
        Assert.Equal(SyncAccountResultKind.Failed, Assert.Single(run.Accounts).Result);
    }

    [Fact]
    public async Task EmptySuccessfulCalendarIsCommittedAsAValidSnapshot()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var previous = CreateEvent(account, "calendar-a", "deleted-at-source", Now.AddHours(1));
        var cacheStore = new RecordingCacheStore();
        cacheStore.Caches[account.InternalAccountId] = CreateCache(
            account,
            ("calendar-a", new[] { previous }));
        var provider = SuccessfulProvider(account);
        await using var service = CreateService(account, cacheStore, provider);
        await service.InitializeAsync(TestContext.Current.CancellationToken);

        var result = await service.RequestSyncAsync(
            SyncTriggerReason.Scheduled,
            TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(cacheStore.Saved).Calendars[0].Events);
        Assert.Empty(service.CurrentSnapshot.Events);
        Assert.Equal(SyncAccountResultKind.Succeeded, Assert.Single(result.Accounts).Result);
    }

    [Fact]
    public async Task TransientFailuresRetryAfterTwoFiveAndTenSeconds()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var time = new ManualTimeProvider(Now);
        var calls = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, _, _, _) =>
        {
            var call = Interlocked.Increment(ref provider.GetEventCalls) - 1;
            calls[call].TrySetResult();
            return Task.FromResult(call < 3
                ? ProviderCalendarResult.Failure(new ProviderError(ProviderErrorCategory.Network))
                : ProviderCalendarResult.Success([]));
        };
        await using var service = CreateService(account, new RecordingCacheStore(), provider, time);

        var sync = service.RequestSyncAsync(SyncTriggerReason.Manual, TestContext.Current.CancellationToken);
        await calls[0].Task;
        await WaitForTimerAsync(time);
        time.Advance(TimeSpan.FromSeconds(2));
        await calls[1].Task;
        await WaitForTimerAsync(time);
        time.Advance(TimeSpan.FromSeconds(5));
        await calls[2].Task;
        await WaitForTimerAsync(time);
        time.Advance(TimeSpan.FromSeconds(10));
        await calls[3].Task;
        var result = await sync;

        Assert.Equal(4, provider.GetEventCalls);
        Assert.Equal(SyncAccountResultKind.Succeeded, Assert.Single(result.Accounts).Result);
    }

    [Fact]
    public async Task NonTransientFailureIsNotRetried()
    {
        var account = CreateAccount(ProviderKind.Microsoft, "calendar-a");
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, _, _, _) =>
        {
            Interlocked.Increment(ref provider.GetEventCalls);
            return Task.FromResult(ProviderCalendarResult.Failure(
                new ProviderError(
                    ProviderErrorCategory.AuthenticationRequired,
                    401,
                    reauthenticationRequired: true)));
        };
        await using var service = CreateService(account, new RecordingCacheStore(), provider);

        var result = await service.RequestSyncAsync(
            SyncTriggerReason.Manual,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, provider.GetEventCalls);
        Assert.Equal(SyncStatus.AuthenticationRequired, Assert.Single(result.Accounts).State.Status);
    }

    [Fact]
    public async Task AccountConcurrencyNeverExceedsFour()
    {
        var accounts = Enumerable.Range(0, 8)
            .Select(index => CreateAccount(ProviderKind.Google, $"calendar-{index}"))
            .ToArray();
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maximum = 0;
        var provider = new ScriptedProvider(ProviderKind.Google);
        provider.ListCalendars = async (account, cancellationToken) =>
        {
            var active = Interlocked.Increment(ref concurrent);
            SetMaximum(ref maximum, active);
            if (active == SyncPolicy.MaxConcurrentAccounts)
            {
                fourStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref concurrent);
            var calendarId = accounts
                .Single(value => value.InternalAccountId == account.InternalAccountId)
                .Calendars[0].CalendarId;
            return [CreateDescriptor(calendarId)];
        };
        provider.GetEvents = (_, _, _, _) => Task.FromResult(ProviderCalendarResult.Success([]));
        var settings = new StubSettingsStore(new AppSettings(accounts: accounts));
        await using var service = new CalendarSyncService(
            [provider],
            settings,
            new RecordingCacheStore(),
            new ManualTimeProvider(Now),
            localTimeZone: TimeZoneInfo.Utc);

        var sync = service.RequestSyncAsync(SyncTriggerReason.Startup, TestContext.Current.CancellationToken);
        await fourStarted.Task;
        Assert.Equal(4, maximum);
        release.TrySetResult();
        await sync;

        Assert.Equal(4, maximum);
        Assert.Equal(8, provider.ListCalendarCalls);
    }

    [Fact]
    public async Task CancellationDiscardsTheWholeAccountStageWithoutCacheWrite()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a", "calendar-b");
        var previous = CreateEvent(account, "calendar-a", "previous", Now.AddHours(1));
        var newEvent = CreateEvent(account, "calendar-a", "new", Now.AddHours(2));
        var cacheStore = new RecordingCacheStore();
        cacheStore.Caches[account.InternalAccountId] = CreateCache(
            account,
            ("calendar-a", new[] { previous }));
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = SuccessfulProvider(account);
        provider.GetEvents = async (_, calendar, _, cancellationToken) =>
        {
            if (calendar.CalendarId == "calendar-a")
            {
                return ProviderCalendarResult.Success([newEvent]);
            }

            secondStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ProviderCalendarResult.Success([]);
        };
        await using var service = CreateService(account, cacheStore, provider);
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var sync = service.RequestSyncAsync(SyncTriggerReason.Manual, cancellation.Token);
        await secondStarted.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        Assert.Empty(cacheStore.Saved);
        Assert.Equal([previous], service.CurrentSnapshot.Events);
        Assert.Equal(SyncStatus.Cancelled, Assert.Single(service.AccountStates).Status);
    }

    [Fact]
    public async Task CacheSaveFailureDoesNotPublishStagedProviderData()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var previous = CreateEvent(account, "calendar-a", "previous", Now.AddHours(1));
        var replacement = CreateEvent(account, "calendar-a", "replacement", Now.AddHours(2));
        var cacheStore = new RecordingCacheStore
        {
            SaveException = new IOException("transient cache write failure"),
        };
        cacheStore.Caches[account.InternalAccountId] = CreateCache(
            account,
            ("calendar-a", new[] { previous }));
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, _, _, _) => Task.FromResult(ProviderCalendarResult.Success([replacement]));
        await using var service = CreateService(account, cacheStore, provider);
        await service.InitializeAsync(TestContext.Current.CancellationToken);

        var result = await service.RequestSyncAsync(
            SyncTriggerReason.Manual,
            TestContext.Current.CancellationToken);

        Assert.Equal([previous], service.CurrentSnapshot.Events);
        Assert.Equal(SyncErrorCategory.Storage, Assert.Single(result.Accounts).State.CalendarStates[0].ErrorCategory);
    }

    [Fact]
    public async Task TransientCacheLoadFailureDoesNotCallProviderOrQuarantineOrWrite()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var cacheStore = new RecordingCacheStore
        {
            LoadException = new IOException("transient cache read failure"),
        };
        var provider = SuccessfulProvider(account);
        await using var service = CreateService(account, cacheStore, provider);

        var result = await service.RequestSyncAsync(
            SyncTriggerReason.Scheduled,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, provider.ListCalendarCalls);
        Assert.Empty(cacheStore.Saved);
        Assert.Equal(SyncErrorCategory.Storage, Assert.Single(result.Accounts).State.CalendarStates[0].ErrorCategory);
    }

    [Fact]
    public async Task SettingsLoadFailureReturnsTypedStorageFailureWithoutCallingProviders()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var provider = SuccessfulProvider(account);
        var settings = new StubSettingsStore(new AppSettings(accounts: [account]))
        {
            LoadException = new IOException("transient settings read failure"),
        };
        await using var service = new CalendarSyncService(
            [provider],
            settings,
            new RecordingCacheStore(),
            new ManualTimeProvider(Now),
            localTimeZone: TimeZoneInfo.Utc);

        var result = await service.RequestSyncAsync(
            SyncTriggerReason.Scheduled,
            TestContext.Current.CancellationToken);

        Assert.Equal(SyncErrorCategory.Storage, result.RequestError);
        Assert.Empty(result.Accounts);
        Assert.Equal(0, provider.ListCalendarCalls);
    }

    [Fact]
    public async Task StartupTransientCacheLoadFailureIsRetriedByTheFollowingSync()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var cacheStore = new RecordingCacheStore
        {
            LoadException = new IOException("transient startup cache read failure"),
        };
        var provider = SuccessfulProvider(account);
        await using var service = CreateService(account, cacheStore, provider);

        var startup = await service.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SyncErrorCategory.Storage, Assert.Single(startup.AccountStates).CalendarStates[0].ErrorCategory);
        cacheStore.LoadException = null;

        var result = await service.RequestSyncAsync(
            SyncTriggerReason.Startup,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, cacheStore.LoadCalls);
        Assert.Equal(1, provider.ListCalendarCalls);
        Assert.Equal(SyncAccountResultKind.Succeeded, Assert.Single(result.Accounts).Result);
    }

    [Fact]
    public async Task ConcurrentTriggersCoalesceToOnePendingRun()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = SuccessfulProvider(account);
        provider.GetEvents = async (_, _, _, cancellationToken) =>
        {
            var call = Interlocked.Increment(ref provider.GetEventCalls);
            if (call == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else if (call == 2)
            {
                secondStarted.TrySetResult();
            }

            return ProviderCalendarResult.Success([]);
        };
        var settings = new StubSettingsStore(new AppSettings(accounts: [account]));
        await using var service = new CalendarSyncService(
            [provider],
            settings,
            new RecordingCacheStore(),
            new ManualTimeProvider(Now),
            localTimeZone: TimeZoneInfo.Utc);

        var first = service.RequestSyncAsync(SyncTriggerReason.Scheduled, TestContext.Current.CancellationToken);
        await firstStarted.Task;
        var second = service.RequestSyncAsync(SyncTriggerReason.Resume, TestContext.Current.CancellationToken);
        var third = service.RequestSyncAsync(SyncTriggerReason.Scheduled, TestContext.Current.CancellationToken);
        await Phase6Data.WaitUntilAsync(() => settings.LoadCount >= 3);
        releaseFirst.TrySetResult();
        await secondStarted.Task;
        await Task.WhenAll(first, second, third);

        Assert.Equal(2, provider.GetEventCalls);
    }

    [Fact]
    public async Task ConcurrentAccountSnapshotsArePublishedInCurrentSnapshotOrder()
    {
        var firstAccount = CreateAccount(ProviderKind.Google, "calendar-a");
        var secondAccount = CreateAccount(ProviderKind.Google, "calendar-b");
        var firstEvent = CreateEvent(firstAccount, "calendar-a", "event-a", Now.AddHours(1));
        var secondEvent = CreateEvent(secondAccount, "calendar-b", "event-b", Now.AddHours(2));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstNotificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderKind.Google)
        {
            ListCalendars = (account, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>(
                [CreateDescriptor(account.InternalAccountId == firstAccount.InternalAccountId
                    ? "calendar-a"
                    : "calendar-b")]),
            GetEvents = async (account, _, _, cancellationToken) =>
            {
                if (account.InternalAccountId == firstAccount.InternalAccountId)
                {
                    firstStarted.TrySetResult();
                    await releaseFirstProvider.Task.WaitAsync(cancellationToken);
                    return ProviderCalendarResult.Success([firstEvent]);
                }

                secondStarted.TrySetResult();
                await releaseSecondProvider.Task.WaitAsync(cancellationToken);
                return ProviderCalendarResult.Success([secondEvent]);
            },
        };
        await using var service = new CalendarSyncService(
            [provider],
            new StubSettingsStore(new AppSettings(accounts: [firstAccount, secondAccount])),
            new RecordingCacheStore(),
            new ManualTimeProvider(Now),
            localTimeZone: TimeZoneInfo.Utc);
        await service.InitializeAsync(TestContext.Current.CancellationToken);
        var notifications = new List<string>();
        service.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot.Events.Count == 0)
            {
                return;
            }

            lock (notifications)
            {
                notifications.Add(string.Join(",", args.Snapshot.Events
                    .Select(calendarEvent => calendarEvent.Key.SourceEventId)
                    .Order(StringComparer.Ordinal)));
            }

            if (args.Snapshot.Events.Count == 1)
            {
                firstNotificationStarted.TrySetResult();
                releaseFirstNotification.Task.GetAwaiter().GetResult();
            }
        };

        var sync = service.RequestSyncAsync(
            SyncTriggerReason.Startup,
            TestContext.Current.CancellationToken);
        await Task.WhenAll(firstStarted.Task, secondStarted.Task);
        releaseFirstProvider.TrySetResult();
        await firstNotificationStarted.Task;
        releaseSecondProvider.TrySetResult();
        await Phase6Data.WaitUntilAsync(() => service.CurrentSnapshot.Events.Count == 2);

        lock (notifications)
        {
            Assert.Equal(["event-a"], notifications);
        }

        releaseFirstNotification.TrySetResult();
        await sync;

        lock (notifications)
        {
            Assert.Equal(["event-a", "event-a,event-b"], notifications);
        }
    }

    [Fact]
    public async Task FullySuccessfulTimestampIsPreservedAcrossFailureRateLimitAndCancellation()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var nextResult = ProviderCalendarResult.Success(Array.Empty<CalendarEvent>());
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, _, _, _) => Task.FromResult(nextResult);
        await using var service = CreateService(account, new RecordingCacheStore(), provider);

        var success = await service.RequestSyncAsync(
            SyncTriggerReason.Manual,
            TestContext.Current.CancellationToken);
        var fullySuccessfulAt = Assert.Single(success.Accounts).State.LastFullySuccessfulSyncUtc;
        Assert.Equal(Now, fullySuccessfulAt);

        foreach (var category in new[]
                 {
                     ProviderErrorCategory.PermissionDenied,
                     ProviderErrorCategory.RateLimited,
                     ProviderErrorCategory.Cancelled,
                 })
        {
            nextResult = ProviderCalendarResult.Failure(new ProviderError(category));
            var result = await service.RequestSyncAsync(
                SyncTriggerReason.Manual,
                TestContext.Current.CancellationToken);
            Assert.Equal(fullySuccessfulAt, Assert.Single(result.Accounts).State.LastFullySuccessfulSyncUtc);
        }
    }

    [Fact]
    public async Task StartupRetryFetchesOnlyFailedCalendarAndCompletesFullSuccessFromMergedState()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a", "calendar-b");
        var eventA = CreateEvent(account, "calendar-a", "event-a", Now.AddHours(1));
        var eventB = CreateEvent(account, "calendar-b", "event-b", Now.AddHours(2));
        var time = new ManualTimeProvider(Now);
        var callsA = 0;
        var callsB = 0;
        var retryCalls = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var calendarBSucceeds = false;
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, calendar, _, _) =>
        {
            if (calendar.CalendarId == "calendar-a")
            {
                Interlocked.Increment(ref callsA);
                return Task.FromResult(ProviderCalendarResult.Success([eventA]));
            }

            var call = Interlocked.Increment(ref callsB);
            retryCalls[Math.Min(call - 1, retryCalls.Length - 1)].TrySetResult();
            return Task.FromResult(calendarBSucceeds
                ? ProviderCalendarResult.Success([eventB])
                : ProviderCalendarResult.Failure(new ProviderError(ProviderErrorCategory.Network)));
        };
        var cacheStore = new RecordingCacheStore();
        await using var service = CreateService(account, cacheStore, provider, time);

        var initialTask = service.RequestSyncAsync(
            SyncTriggerReason.Startup,
            TestContext.Current.CancellationToken);
        await CompleteTransientRetriesAsync(time, retryCalls);
        var initial = await initialTask;
        Assert.Equal(SyncStatus.PartiallySucceeded, Assert.Single(initial.Accounts).State.Status);
        Assert.Null(Assert.Single(initial.Accounts).State.LastFullySuccessfulSyncUtc);

        calendarBSucceeds = true;
        var retry = await service.RequestSyncAsync(
            SyncTriggerReason.StartupRetry,
            [account.InternalAccountId],
            TestContext.Current.CancellationToken);

        var state = Assert.Single(retry.Accounts).State;
        Assert.Equal(SyncStatus.Succeeded, state.Status);
        Assert.Equal(Now, state.LastFullySuccessfulSyncUtc);
        Assert.Equal(1, callsA);
        Assert.Equal(5, callsB);
        Assert.Equal([eventA, eventB], service.CurrentSnapshot.Events);
        Assert.Equal(SyncStatus.Succeeded, state.CalendarStates[0].Status);

        var listCalls = provider.ListCalendarCalls;
        var noTargets = await service.RequestSyncAsync(
            SyncTriggerReason.StartupRetry,
            [account.InternalAccountId],
            TestContext.Current.CancellationToken);
        Assert.Empty(noTargets.Accounts);
        Assert.Equal(listCalls, provider.ListCalendarCalls);
        Assert.Equal(1, callsA);
        Assert.Equal(5, callsB);
    }

    [Fact]
    public async Task StartupRetryListFailureDoesNotWorsenNonTargetCalendarOrCache()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a", "calendar-b");
        var eventA = CreateEvent(account, "calendar-a", "event-a", Now.AddHours(1));
        var time = new ManualTimeProvider(Now);
        var callsB = 0;
        var retryCalls = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, calendar, _, _) =>
        {
            if (calendar.CalendarId == "calendar-a")
            {
                return Task.FromResult(ProviderCalendarResult.Success([eventA]));
            }

            var call = Interlocked.Increment(ref callsB);
            retryCalls[call - 1].TrySetResult();
            return Task.FromResult(ProviderCalendarResult.Failure(
                new ProviderError(ProviderErrorCategory.Timeout)));
        };
        await using var service = CreateService(account, new RecordingCacheStore(), provider, time);

        var initial = service.RequestSyncAsync(
            SyncTriggerReason.Startup,
            TestContext.Current.CancellationToken);
        await CompleteTransientRetriesAsync(time, retryCalls);
        await initial;
        var successfulBeforeRetry = Assert.Single(service.AccountStates)
            .CalendarStates.Single(state => state.CalendarId == "calendar-a");
        provider.ListCalendars = (_, _) => throw new ProviderException(
            new ProviderError(ProviderErrorCategory.PermissionDenied));

        var retry = await service.RequestSyncAsync(
            SyncTriggerReason.StartupRetry,
            [account.InternalAccountId],
            TestContext.Current.CancellationToken);

        var state = Assert.Single(retry.Accounts).State;
        var successfulAfterRetry = state.CalendarStates.Single(value => value.CalendarId == "calendar-a");
        Assert.Equal(successfulBeforeRetry, successfulAfterRetry);
        Assert.Equal(SyncErrorCategory.PermissionDenied,
            state.CalendarStates.Single(value => value.CalendarId == "calendar-b").ErrorCategory);
        Assert.Equal([eventA], service.CurrentSnapshot.Events);
    }

    [Fact]
    public async Task PendingFullRequestDominatesActiveCalendarSubsetRetry()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a", "calendar-b");
        var callsA = 0;
        var callsB = 0;
        var subsetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = SuccessfulProvider(account);
        provider.GetEvents = async (_, calendar, _, cancellationToken) =>
        {
            if (calendar.CalendarId == "calendar-a")
            {
                Interlocked.Increment(ref callsA);
                return ProviderCalendarResult.Success([]);
            }

            var call = Interlocked.Increment(ref callsB);
            if (call == 1)
            {
                return ProviderCalendarResult.Failure(
                    new ProviderError(ProviderErrorCategory.RateLimited));
            }

            if (call == 2)
            {
                subsetStarted.TrySetResult();
                await releaseSubset.Task.WaitAsync(cancellationToken);
            }

            return ProviderCalendarResult.Success([]);
        };
        var settings = new StubSettingsStore(new AppSettings(accounts: [account]));
        await using var service = new CalendarSyncService(
            [provider],
            settings,
            new RecordingCacheStore(),
            new ManualTimeProvider(Now),
            localTimeZone: TimeZoneInfo.Utc);
        await service.RequestSyncAsync(
            SyncTriggerReason.Startup,
            TestContext.Current.CancellationToken);

        var subset = service.RequestSyncAsync(
            SyncTriggerReason.RateLimitRetry,
            [account.InternalAccountId],
            TestContext.Current.CancellationToken);
        await subsetStarted.Task;
        var full = service.RequestSyncAsync(
            SyncTriggerReason.Manual,
            TestContext.Current.CancellationToken);
        await Phase6Data.WaitUntilAsync(() => settings.LoadCount >= 3);
        releaseSubset.TrySetResult();
        await Task.WhenAll(subset, full);

        Assert.Equal(2, callsA);
        Assert.Equal(3, callsB);
    }

    [Fact]
    public async Task PendingCalendarSubsetsAreMergedByUnionThroughTheCoalescer()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a", "calendar-b");
        var time = new ManualTimeProvider(Now);
        var networkAttempts = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var callsA = 0;
        var callsB = 0;
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, calendar, _, _) =>
        {
            if (calendar.CalendarId == "calendar-a")
            {
                var call = Interlocked.Increment(ref callsA);
                if (call <= networkAttempts.Length)
                {
                    networkAttempts[call - 1].TrySetResult();
                    return Task.FromResult(ProviderCalendarResult.Failure(
                        new ProviderError(ProviderErrorCategory.Network)));
                }

                return Task.FromResult(ProviderCalendarResult.Success([]));
            }

            var limitedCall = Interlocked.Increment(ref callsB);
            return Task.FromResult(limitedCall == 1
                ? ProviderCalendarResult.Failure(new ProviderError(
                    ProviderErrorCategory.RateLimited,
                    429,
                    TimeSpan.FromHours(1)))
                : ProviderCalendarResult.Success([]));
        };
        await using var service = CreateService(
            account,
            new RecordingCacheStore(),
            provider,
            time);

        var initial = service.RequestSyncAsync(
            SyncTriggerReason.Startup,
            TestContext.Current.CancellationToken);
        await CompleteTransientRetriesAsync(time, networkAttempts);
        await initial;
        Assert.Contains(Assert.Single(service.AccountStates).CalendarStates, state =>
            state.CalendarId == "calendar-a"
            && state.ErrorCategory == SyncErrorCategory.Network);
        Assert.Contains(Assert.Single(service.AccountStates).CalendarStates, state =>
            state.CalendarId == "calendar-b"
            && state.Status == SyncStatus.RateLimited);

        var previousContext = SynchronizationContext.Current;
        var queuedContext = new QueuedSynchronizationContext();
        Task<SyncRunResult> active;
        Task<SyncRunResult> pendingA;
        Task<SyncRunResult> pendingB;
        SynchronizationContext.SetSynchronizationContext(queuedContext);
        try
        {
            active = service.RequestSyncAsync(
                SyncTriggerReason.RateLimitRetry,
                [account.InternalAccountId],
                TestContext.Current.CancellationToken);
            pendingA = service.RequestSyncAsync(
                SyncTriggerReason.StartupRetry,
                [account.InternalAccountId],
                TestContext.Current.CancellationToken);
            pendingB = service.RequestSyncAsync(
                SyncTriggerReason.RateLimitRetry,
                [account.InternalAccountId],
                TestContext.Current.CancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        Assert.Equal(1, queuedContext.PendingCount);
        queuedContext.RunAll();
        await Task.WhenAll(active, pendingA, pendingB);

        Assert.Equal(5, callsA);
        Assert.Equal(3, callsB);
        Assert.All(Assert.Single(service.AccountStates).CalendarStates, state =>
            Assert.Equal(SyncStatus.Succeeded, state.Status));
    }

    [Fact]
    public async Task AccountWithNoVisibleCalendarsRemainsNotStarted()
    {
        var accountId = Guid.NewGuid();
        var account = new AccountSettings(
            accountId,
            ProviderKind.Google,
            $"subject-{accountId:N}",
            "Private Display Name",
            "private@example.invalid",
            true,
            $"google/{accountId:D}",
            [new CalendarSetting("hidden-calendar", false)]);
        var provider = SuccessfulProvider(account);
        await using var service = CreateService(account, new RecordingCacheStore(), provider);

        var startup = await service.InitializeAsync(TestContext.Current.CancellationToken);
        var state = Assert.Single(startup.AccountStates);

        Assert.Equal(SyncStatus.NotStarted, state.Status);
        Assert.Empty(state.CalendarStates);
        Assert.Equal(0, provider.ListCalendarCalls);
    }

    [Fact]
    public async Task RateLimitDefersWithoutBlockingOtherAccountsAndRequeuesAtProviderTime()
    {
        var limited = CreateAccount(ProviderKind.Google, "limited-calendar");
        var healthy = CreateAccount(ProviderKind.Google, "healthy-calendar");
        var settings = new StubSettingsStore(new AppSettings(accounts: [limited, healthy]));
        var cacheStore = new RecordingCacheStore();
        var time = new ManualTimeProvider(Now);
        var limitedCalls = 0;
        var deferredCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderKind.Google)
        {
            ListCalendars = (account, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>(
                [CreateDescriptor(account.InternalAccountId == limited.InternalAccountId
                    ? "limited-calendar"
                    : "healthy-calendar")]),
            GetEvents = (account, _, _, _) =>
            {
                if (account.InternalAccountId == healthy.InternalAccountId)
                {
                    return Task.FromResult(ProviderCalendarResult.Success([]));
                }

                var call = Interlocked.Increment(ref limitedCalls);
                if (call == 1)
                {
                    return Task.FromResult(ProviderCalendarResult.Failure(
                        new ProviderError(
                            ProviderErrorCategory.RateLimited,
                            429,
                            TimeSpan.FromSeconds(60))));
                }

                deferredCall.TrySetResult();
                return Task.FromResult(ProviderCalendarResult.Success([]));
            },
        };
        await using var service = new CalendarSyncService(
            [provider],
            settings,
            cacheStore,
            time,
            localTimeZone: TimeZoneInfo.Utc);

        var initial = await service.RequestSyncAsync(
            SyncTriggerReason.Startup,
            TestContext.Current.CancellationToken);

        Assert.Contains(initial.Accounts, result =>
            result.InternalAccountId == healthy.InternalAccountId
            && result.Result == SyncAccountResultKind.Succeeded);
        Assert.Contains(initial.Accounts, result =>
            result.InternalAccountId == limited.InternalAccountId
            && result.Result == SyncAccountResultKind.Deferred);
        Assert.Contains(cacheStore.Saved, cache => cache.InternalAccountId == healthy.InternalAccountId);

        await service.RequestSyncAsync(
            SyncTriggerReason.Manual,
            [limited.InternalAccountId],
            TestContext.Current.CancellationToken);
        Assert.Equal(1, limitedCalls);

        time.Advance(TimeSpan.FromSeconds(60));
        await deferredCall.Task;
        await Phase6Data.WaitUntilAsync(() => cacheStore.Saved.Any(cache =>
            cache.InternalAccountId == limited.InternalAccountId));
        Assert.Equal(2, limitedCalls);
    }

    [Fact]
    public async Task GoogleAndMicrosoftAccountsCommitIndependently()
    {
        var google = CreateAccount(ProviderKind.Google, "google-calendar");
        var microsoft = CreateAccount(ProviderKind.Microsoft, "microsoft-calendar");
        var settings = new StubSettingsStore(new AppSettings(accounts: [google, microsoft]));
        var cacheStore = new RecordingCacheStore();
        var googleProvider = SuccessfulProvider(google);
        var microsoftProvider = SuccessfulProvider(microsoft);
        microsoftProvider.GetEvents = (_, _, _, _) => Task.FromResult(
            ProviderCalendarResult.Failure(
                new ProviderError(ProviderErrorCategory.PermissionDenied, 403)));
        await using var service = new CalendarSyncService(
            [googleProvider, microsoftProvider],
            settings,
            cacheStore,
            new ManualTimeProvider(Now),
            localTimeZone: TimeZoneInfo.Utc);

        var result = await service.RequestSyncAsync(
            SyncTriggerReason.Startup,
            TestContext.Current.CancellationToken);

        Assert.Single(cacheStore.Saved);
        Assert.Equal(google.InternalAccountId, cacheStore.Saved[0].InternalAccountId);
        Assert.Contains(result.Accounts, value =>
            value.InternalAccountId == google.InternalAccountId
            && value.Result == SyncAccountResultKind.Succeeded);
        Assert.Contains(result.Accounts, value =>
            value.InternalAccountId == microsoft.InternalAccountId
            && value.Result == SyncAccountResultKind.Failed);
    }

    [Fact]
    public async Task SyncRangeIncludesCachedOngoingEventThatStartedBeforeToday()
    {
        var account = CreateAccount(ProviderKind.Google, "calendar-a");
        var ongoing = CreateEvent(
            account,
            "calendar-a",
            "ongoing",
            Now.AddDays(-1),
            Now.AddHours(1));
        var cacheStore = new RecordingCacheStore();
        cacheStore.Caches[account.InternalAccountId] = CreateCache(
            account,
            ("calendar-a", new[] { ongoing }));
        TimeRangeUtc? observedRange = null;
        var provider = SuccessfulProvider(account);
        provider.GetEvents = (_, _, range, _) =>
        {
            observedRange = range;
            return Task.FromResult(ProviderCalendarResult.Success([]));
        };
        await using var service = CreateService(account, cacheStore, provider);
        await service.InitializeAsync(TestContext.Current.CancellationToken);

        await service.RequestSyncAsync(
            SyncTriggerReason.Scheduled,
            TestContext.Current.CancellationToken);

        Assert.NotNull(observedRange);
        Assert.Equal("ongoing", ongoing.Key.SourceEventId);
        Assert.Equal(Now.AddDays(-1), observedRange.Value.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero), observedRange.Value.EndUtc);
    }

    private static CalendarSyncService CreateService(
        AccountSettings account,
        RecordingCacheStore cacheStore,
        ScriptedProvider provider,
        TimeProvider? timeProvider = null) =>
        new(
            [provider],
            new StubSettingsStore(new AppSettings(accounts: [account])),
            cacheStore,
            timeProvider ?? new ManualTimeProvider(Now),
            localTimeZone: TimeZoneInfo.Utc);

    private static ScriptedProvider SuccessfulProvider(AccountSettings account) =>
        new(account.Provider)
        {
            ListCalendars = (_, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>(
                account.Calendars.Select(calendar => CreateDescriptor(calendar.CalendarId)).ToArray()),
            GetEvents = (_, _, _, _) => Task.FromResult(ProviderCalendarResult.Success([])),
        };

    private static AccountSettings CreateAccount(
        ProviderKind provider,
        params string[] calendarIds)
    {
        var id = Guid.NewGuid();
        return new AccountSettings(
            id,
            provider,
            $"subject-{id:N}",
            "Private Display Name",
            "private@example.invalid",
            true,
            $"{provider.ToString().ToLowerInvariant()}/{id:D}",
            calendarIds.Select(calendarId => new CalendarSetting(calendarId, true)));
    }

    private static CalendarDescriptor CreateDescriptor(string calendarId) =>
        new(calendarId, calendarId, "Private Calendar Name", false, true, null);

    private static AccountCache CreateCache(
        AccountSettings account,
        params (string CalendarId, CalendarEvent[] Events)[] calendars) =>
        new(
            account.InternalAccountId,
            account.Provider,
            Now.AddMinutes(-5),
            calendars.Select(calendar => new CachedCalendar(
                calendar.CalendarId,
                "Private Calendar Name",
                null,
                Now.AddMinutes(-5),
                calendar.Events)));

    private static CalendarEvent CreateEvent(
        AccountSettings account,
        string calendarId,
        string eventId,
        DateTimeOffset startUtc) =>
        CreateEvent(account, calendarId, eventId, startUtc, startUtc.AddMinutes(30));

    private static CalendarEvent CreateEvent(
        AccountSettings account,
        string calendarId,
        string eventId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc) =>
        new(
            new EventKey(account.Provider, account.InternalAccountId, calendarId, eventId),
            "Private Event Title",
            new TimedEventTiming(startUtc, endUtc),
            "Private Calendar Name");

    private static void SetMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current
                || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static async Task CompleteTransientRetriesAsync(
        ManualTimeProvider timeProvider,
        IReadOnlyList<TaskCompletionSource> calls)
    {
        for (var index = 0; index < calls.Count; index++)
        {
            await calls[index].Task;
            if (index == calls.Count - 1)
            {
                return;
            }

            await WaitForTimerAsync(timeProvider);
            timeProvider.Advance(SyncPolicy.TransientRetryDelays[index]);
        }
    }

    private static async Task WaitForTimerAsync(ManualTimeProvider timeProvider)
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            if (timeProvider.PendingTimerCount > 0)
            {
                return;
            }

            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The expected virtual-time timer was not created.");
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public int PendingCount => _callbacks.Count;

        public override void Post(SendOrPostCallback callback, object? state) =>
            _callbacks.Enqueue((callback, state));

        public void RunAll()
        {
            while (_callbacks.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }
    }

    private sealed class StubSettingsStore : ISettingsStore
    {
        private readonly AppSettings _settings;

        public StubSettingsStore(AppSettings settings)
        {
            _settings = settings;
        }

        public int LoadCount { get; private set; }

        public Exception? LoadException { get; init; }

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            if (LoadException is not null)
            {
                throw LoadException;
            }

            return Task.FromResult(_settings);
        }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingCacheStore : ICacheStore
    {
        public ConcurrentDictionary<Guid, AccountCache> Caches { get; } = new();

        public List<AccountCache> Saved { get; } = [];

        public int LoadCalls { get; private set; }

        public Exception? LoadException { get; set; }

        public Exception? SaveException { get; init; }

        public Task<AccountCache?> LoadAccountAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCalls++;
            if (LoadException is not null)
            {
                throw LoadException;
            }

            return Task.FromResult(Caches.GetValueOrDefault(internalAccountId));
        }

        public Task SaveAccountAsync(
            AccountCache accountCache,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveException is not null)
            {
                throw SaveException;
            }

            lock (Saved)
            {
                Saved.Add(accountCache);
            }

            Caches[accountCache.InternalAccountId] = accountCache;
            return Task.CompletedTask;
        }

        public Task RemoveAccountAsync(
            Guid internalAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Caches.TryRemove(internalAccountId, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedProvider : ICalendarProvider
    {
        public ScriptedProvider(ProviderKind provider)
        {
            Provider = provider;
        }

        public ProviderKind Provider { get; }

        public int ListCalendarCalls;

        public int GetEventCalls;

        public Func<CalendarAccount, CancellationToken, Task<IReadOnlyList<CalendarDescriptor>>> ListCalendars
            { get; set; } = (_, _) => Task.FromResult<IReadOnlyList<CalendarDescriptor>>([]);

        public Func<CalendarAccount, CalendarDescriptor, TimeRangeUtc, CancellationToken, Task<ProviderCalendarResult>> GetEvents
            { get; set; } = (_, _, _, _) => Task.FromResult(ProviderCalendarResult.Success([]));

        public Task<AuthAccountResult> AuthenticateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuthAccountResult> ReauthenticateAsync(
            CalendarAccount account,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<CalendarDescriptor>> ListCalendarsAsync(
            CalendarAccount account,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ListCalendarCalls);
            return ListCalendars(account, cancellationToken);
        }

        public Task<ProviderCalendarResult> GetEventsAsync(
            CalendarAccount account,
            CalendarDescriptor calendar,
            TimeRangeUtc range,
            CancellationToken cancellationToken) =>
            GetEvents(account, calendar, range, cancellationToken);
    }
}
