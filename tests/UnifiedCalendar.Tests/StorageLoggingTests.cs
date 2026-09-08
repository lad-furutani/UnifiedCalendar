using System.Collections.Concurrent;
using System.Text;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Infrastructure.Logging;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class StorageLoggingTests
{
    [Fact]
    public async Task StorageFailuresAndQuarantines_LogOnlyWhitelistedMetadata()
    {
        using var temporary = new TemporaryAppDirectory();
        var sink = new CollectingLogSink();
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var storageLogger = new StorageEventLogger(serilog);
        var timeProvider = new MutableTimeProvider(StorageSamples.Now);
        var writer = new AtomicFileWriter(logger: storageLogger);
        var settingsStore = new SettingsJsonStore(
            temporary.Paths,
            writer,
            timeProvider,
            logger: storageLogger);
        const string email = "private-user@example.invalid";
        const string displayName = "Private Display Name";
        const string title = "Private Planning Title";
        const string description = "Private description body";
        const string location = "Private Tokyo Office";
        const string url = "https://private.example.invalid/meeting";
        const string calendarId = "private-calendar-id";
        const string eventId = "private-event-id";
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            $"{{\"schemaVersion\":1,\"email\":\"{email}\",\"displayName\":\"{displayName}\",\"title\":\"{title}\",\"description\":\"{description}\",\"location\":\"{location}\",\"url\":\"{url}\",\"calendarId\":\"{calendarId}\",\"eventId\":\"{eventId}\"",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        _ = await settingsStore.LoadAsync(TestContext.Current.CancellationToken);

        const string accessToken = "access-token-forbidden-fixture";
        const string refreshToken = "refresh-token-forbidden-fixture";
        const string authorizationCode = "authorization-code-forbidden-fixture";
        var tokenPath = temporary.Paths.GetTokenFile(ProviderKind.Google, StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            tokenPath,
            $"{{\"schemaVersion\":1,\"protectionVersion\":1,\"protectionKind\":\"dpapiCurrentUser\",\"provider\":\"google\",\"internalAccountId\":\"{StorageSamples.GoogleAccountId:D}\",\"updatedAtUtc\":\"2026-08-28T05:12:34.0000000Z\",\"protectedPayload\":\"{accessToken}-{refreshToken}\"}}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var tokenStore = new DpapiTokenStore(
            temporary.Paths,
            writer,
            timeProvider,
            storageLogger);
        _ = await tokenStore.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        var faultInjector = Substitute.For<IAtomicWriteFaultInjector>();
        var atomicTarget = Path.Combine(temporary.Paths.CacheDirectory, "atomic-fixture.json");
        faultInjector
            .When(injector => injector.OnStage(AtomicWriteStage.FlushToDiskCompleted, atomicTarget))
            .Do(_ => throw new IOException(
                $"{authorizationCode} {temporary.RootPath} {Environment.UserName}"));
        var failingWriter = new AtomicFileWriter(faultInjector, storageLogger);
        await Assert.ThrowsAsync<IOException>(() => failingWriter.WriteAsync(
            atomicTarget,
            (stream, _) => stream.WriteAsync(
                "safe-ciphertext"u8.ToArray(),
                TestContext.Current.CancellationToken).AsTask(),
            TestContext.Current.CancellationToken));

        var renderedLog = string.Join(
            Environment.NewLine,
            sink.Events.Select(logEvent =>
                $"{logEvent.RenderMessage()} {string.Join(' ', logEvent.Properties.Values)}"));
        foreach (var forbidden in new[]
                 {
                     temporary.RootPath,
                     Environment.UserName,
                     email,
                     displayName,
                     title,
                     description,
                     location,
                     url,
                     calendarId,
                     eventId,
                     accessToken,
                     refreshToken,
                     authorizationCode,
                 }.Where(value => !string.IsNullOrEmpty(value)))
        {
            Assert.DoesNotContain(forbidden, renderedLog, StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(sink.Events, logEvent => Assert.Null(logEvent.Exception));
        Assert.Contains(sink.Events, HasOperation(StorageOperation.Quarantine));
        Assert.Contains(sink.Events, HasOperation(StorageOperation.AtomicWrite));
        Assert.Contains(sink.Events, HasFileKind(StorageFileKind.Token));
        Assert.Contains("IOException", renderedLog, StringComparison.Ordinal);
        Assert.Contains("Io", renderedLog, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceptionSanitizer_DiscardsMessageAndReturnsOnlySafeClassification()
    {
        var safe = ExceptionSanitizer.Sanitize(
            new IOException("private-user@example.invalid access-token-forbidden-fixture"));

        Assert.Equal("IOException", safe.ExceptionType);
        Assert.Equal(SafeErrorCategory.Io, safe.ErrorCategory);
        Assert.DoesNotContain("private", safe.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", safe.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsMigrationException_IsInvalidDataWithoutMessageDisclosure()
    {
        const string privateMessage =
            "private-user@example.invalid could not migrate private-calendar-id";
        var exception = new SettingsMigrationException(privateMessage);
        var safe = ExceptionSanitizer.Sanitize(exception);
        var sink = new CollectingLogSink();
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();

        new StorageEventLogger(serilog).WriteFailure(
            StorageFileKind.Settings,
            StorageOperation.Migration,
            exception,
            0);

        Assert.Equal(nameof(SettingsMigrationException), safe.ExceptionType);
        Assert.Equal(SafeErrorCategory.InvalidData, safe.ErrorCategory);
        Assert.DoesNotContain(privateMessage, safe.ToString(), StringComparison.Ordinal);
        var logEvent = Assert.Single(sink.Events);
        Assert.Null(logEvent.Exception);
        Assert.Contains("InvalidData", logEvent.RenderMessage(), StringComparison.Ordinal);
        Assert.DoesNotContain(privateMessage, logEvent.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeCacheSchema_IsLoggedAndQuarantinedAsInvalidNotOlder()
    {
        using var temporary = new TemporaryAppDirectory();
        var sink = new CollectingLogSink();
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var storageLogger = new StorageEventLogger(serilog);
        var path = temporary.Paths.GetAccountCacheFile(StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":-1}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = new AccountCacheJsonStore(
            temporary.Paths,
            new AtomicFileWriter(logger: storageLogger),
            new MutableTimeProvider(StorageSamples.Now),
            storageLogger);

        var result = await store.LoadAccountAsync(
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "cache-*-corrupt-*.json"));
        Assert.Empty(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "cache-*-older-*.json"));
        Assert.Contains(sink.Events, HasResult(StorageResult.Invalid));
        Assert.DoesNotContain(sink.Events, HasResult(StorageResult.Older));
    }

    [Fact]
    public async Task NegativeTokenSchema_IsLoggedAndQuarantinedAsInvalidNotOlder()
    {
        using var temporary = new TemporaryAppDirectory();
        var sink = new CollectingLogSink();
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var storageLogger = new StorageEventLogger(serilog);
        var path = temporary.Paths.GetTokenFile(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":-1}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = new DpapiTokenStore(
            temporary.Paths,
            new AtomicFileWriter(logger: storageLogger),
            new MutableTimeProvider(StorageSamples.Now),
            storageLogger);

        var result = await store.ReadAsync(
            ProviderKind.Google,
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "token-*-corrupt-*.json"));
        Assert.Empty(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "token-*-older-*.json"));
        Assert.Contains(sink.Events, HasResult(StorageResult.Invalid));
        Assert.DoesNotContain(sink.Events, HasResult(StorageResult.Older));
    }

    private static Predicate<LogEvent> HasOperation(StorageOperation operation) => logEvent =>
        logEvent.Properties.TryGetValue("Operation", out var value)
        && value.ToString().Contains(operation.ToString(), StringComparison.Ordinal);

    private static Predicate<LogEvent> HasFileKind(StorageFileKind fileKind) => logEvent =>
        logEvent.Properties.TryGetValue("FileKind", out var value)
        && value.ToString().Contains(fileKind.ToString(), StringComparison.Ordinal);

    private static Predicate<LogEvent> HasResult(StorageResult result) => logEvent =>
        logEvent.Properties.TryGetValue("Result", out var value)
        && value.ToString().Contains(result.ToString(), StringComparison.Ordinal);

    private sealed class CollectingLogSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
