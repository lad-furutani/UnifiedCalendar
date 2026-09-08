using System.Text;
using NSubstitute;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class AtomicFileWriterTests
{
    [Fact]
    public async Task WriteFailureAfterPartialContent_LeavesOldTargetReadableAndDeletesTemp()
    {
        using var temporary = new TemporaryAppDirectory();
        var target = Path.Combine(temporary.Paths.SettingsDirectory, "atomic.json");
        await File.WriteAllTextAsync(
            target,
            "old-readable",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var writer = new AtomicFileWriter();

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(
            target,
            async (stream, _) =>
            {
                await stream.WriteAsync(
                    "partial-new"u8.ToArray(),
                    TestContext.Current.CancellationToken);
                throw new IOException("Injected during write.");
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "old-readable",
            await File.ReadAllTextAsync(target, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temporary.Paths.SettingsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task FailureAfterDiskFlushBeforeReplace_LeavesOldTargetReadable()
    {
        using var temporary = new TemporaryAppDirectory();
        var target = Path.Combine(temporary.Paths.SettingsDirectory, "atomic.json");
        await File.WriteAllTextAsync(
            target,
            "old-readable",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var faultInjector = Substitute.For<IAtomicWriteFaultInjector>();
        faultInjector
            .When(injector => injector.OnStage(AtomicWriteStage.FlushToDiskCompleted, target))
            .Do(_ => throw new IOException("Injected after durable flush."));
        var writer = new AtomicFileWriter(faultInjector);

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(
            target,
            (stream, _) => stream.WriteAsync(
                "new-value"u8.ToArray(),
                TestContext.Current.CancellationToken).AsTask(),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "old-readable",
            await File.ReadAllTextAsync(target, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temporary.Paths.SettingsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task ReplaceFailure_LeavesOldTargetReadable()
    {
        using var temporary = new TemporaryAppDirectory();
        var target = Path.Combine(temporary.Paths.SettingsDirectory, "atomic.json");
        await File.WriteAllTextAsync(
            target,
            "old-readable",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var writer = new ReplaceFailingAtomicFileWriter();

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(
            target,
            (stream, _) => stream.WriteAsync(
                "new-value"u8.ToArray(),
                TestContext.Current.CancellationToken).AsTask(),
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "old-readable",
            await File.ReadAllTextAsync(target, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temporary.Paths.SettingsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task NewFile_IsMovedAtomicallyOnSameDirectory()
    {
        using var temporary = new TemporaryAppDirectory();
        var target = Path.Combine(temporary.Paths.SettingsDirectory, "new.json");
        var writer = new AtomicFileWriter();

        await writer.WriteAsync(
            target,
            (stream, _) => stream.WriteAsync(
                "complete"u8.ToArray(),
                TestContext.Current.CancellationToken).AsTask(),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "complete",
            await File.ReadAllTextAsync(target, Encoding.UTF8, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temporary.Paths.SettingsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task WritesToTheSamePath_AreSerialized()
    {
        using var temporary = new TemporaryAppDirectory();
        var target = Path.Combine(temporary.Paths.SettingsDirectory, "serialized.json");
        var writer = new AtomicFileWriter();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = writer.WriteAsync(
            target,
            async (stream, _) =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(TestContext.Current.CancellationToken);
                await stream.WriteAsync("first"u8.ToArray(), TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);
        await firstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var second = writer.WriteAsync(
            target,
            async (stream, _) =>
            {
                secondEntered.SetResult();
                await stream.WriteAsync("second"u8.ToArray(), TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken);

        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
        Assert.Equal(
            "second",
            await File.ReadAllTextAsync(target, Encoding.UTF8, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Acc007_OldSettingsRemainReadableAfterInjectedFailure_AndBadSchemasContinueViaQuarantine()
    {
        using var temporary = new TemporaryAppDirectory();
        var initialStore = new SettingsJsonStore(
            temporary.Paths,
            new AtomicFileWriter(),
            new MutableTimeProvider(StorageSamples.Now));
        await initialStore.SaveAsync(
            StorageSamples.CreateSettings(),
            TestContext.Current.CancellationToken);
        var faultInjector = Substitute.For<IAtomicWriteFaultInjector>();
        faultInjector
            .When(injector => injector.OnStage(
                AtomicWriteStage.FlushToDiskCompleted,
                temporary.Paths.SettingsFile))
            .Do(_ => throw new IOException("ACC-007 injected failure."));
        var failingStore = new SettingsJsonStore(
            temporary.Paths,
            new AtomicFileWriter(faultInjector),
            new MutableTimeProvider(StorageSamples.Now));

        await Assert.ThrowsAsync<IOException>(() => failingStore.SaveAsync(
            new AppSettings(),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            14,
            (await initialStore.LoadAsync(TestContext.Current.CancellationToken)).Display.Days);

        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            "{corrupt",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            7,
            (await initialStore.LoadAsync(TestContext.Current.CancellationToken)).Display.Days);
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            "{\"schemaVersion\":77}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        Assert.Equal(
            7,
            (await initialStore.LoadAsync(TestContext.Current.CancellationToken)).Display.Days);
        Assert.Equal(2, Directory.GetFiles(temporary.Paths.RecoveryDirectory, "settings-*.json").Length);
    }

    [Fact]
    public void StartupCleanup_DeletesOnlyRecognizedResidualAtomicTemps()
    {
        using var temporary = new TemporaryAppDirectory();
        var recognized = Path.Combine(
            temporary.Paths.SettingsDirectory,
            $".settings.json.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(temporary.Paths.SettingsDirectory, "keep.tmp");
        File.WriteAllText(recognized, "residual", new UTF8Encoding(false));
        File.WriteAllText(unrelated, "unrelated", new UTF8Encoding(false));
        var writer = new AtomicFileWriter();

        var removed = writer.CleanupStaleTemporaryFiles(temporary.Paths.RootDirectory);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(recognized));
        Assert.True(File.Exists(unrelated));
    }

    private sealed class ReplaceFailingAtomicFileWriter : AtomicFileWriter
    {
        protected override void ReplaceFile(string temporaryPath, string targetPath) =>
            throw new IOException("Injected replacement failure.");
    }
}
