using System.Text;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class QuarantineRegressionTests
{
    [Fact]
    public async Task SameTimestampSourceAndKind_UsesGuidToAvoidFilenameCollisions()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateSettingsStore(temporary, new MutableTimeProvider(StorageSamples.Now));

        for (var index = 0; index < 4; index++)
        {
            await QuarantineCorruptSettingsAsync(temporary, store, $"collision-{index}");
        }

        var files = Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "settings-*-corrupt-*.json");
        Assert.Equal(3, files.Length);
        Assert.Equal(3, files.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Retention_IsIndependentForDifferentKindsOfTheSameSource()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateSettingsStore(temporary, new MutableTimeProvider(StorageSamples.Now));

        for (var index = 0; index < 4; index++)
        {
            await QuarantineCorruptSettingsAsync(temporary, store, $"corrupt-{index}");
            await File.WriteAllTextAsync(
                temporary.Paths.SettingsFile,
                $"{{\"schemaVersion\":{20 + index}}}",
                new UTF8Encoding(false),
                TestContext.Current.CancellationToken);
            _ = await store.LoadAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            3,
            Directory.GetFiles(temporary.Paths.RecoveryDirectory, "settings-*-corrupt-*.json").Length);
        Assert.Equal(
            3,
            Directory.GetFiles(temporary.Paths.RecoveryDirectory, "settings-*-future-*.json").Length);
    }

    [Fact]
    public async Task Retention_IsIndependentForDifferentSourceFiles()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = new AccountCacheJsonStore(
            temporary.Paths,
            new AtomicFileWriter(),
            new MutableTimeProvider(StorageSamples.Now));
        var accountIds = new[] { StorageSamples.GoogleAccountId, StorageSamples.MicrosoftAccountId };

        foreach (var accountId in accountIds)
        {
            var path = temporary.Paths.GetAccountCacheFile(accountId);
            for (var index = 0; index < 4; index++)
            {
                await File.WriteAllTextAsync(
                    path,
                    $"{{cache-corrupt-{index}",
                    new UTF8Encoding(false),
                    TestContext.Current.CancellationToken);
                _ = await store.LoadAccountAsync(accountId, TestContext.Current.CancellationToken);
            }
        }

        var groups = Directory.GetFiles(temporary.Paths.RecoveryDirectory, "cache-*-corrupt-*.json")
            .GroupBy(path => Path.GetFileName(path).Split('-')[1], StringComparer.Ordinal)
            .Select(group => group.Count())
            .Order()
            .ToArray();
        Assert.Equal([3, 3], groups);
    }

    [Fact]
    public async Task RecoveryFilename_DoesNotExposePiiFromQuarantinedContent()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateSettingsStore(temporary, new MutableTimeProvider(StorageSamples.Now));
        const string email = "pii-user@example.invalid";
        const string displayName = "SensitiveDisplayName";
        const string title = "SensitivePlanningTitle";
        const string location = "SensitiveTokyoOffice";
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            $"{{\"schemaVersion\":1,\"email\":\"{email}\",\"displayName\":\"{displayName}\",\"title\":\"{title}\",\"location\":\"{location}\"",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        _ = await store.LoadAsync(TestContext.Current.CancellationToken);

        var fileName = Path.GetFileName(Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "settings-*.json")));
        Assert.DoesNotContain(email, fileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(displayName, fileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(title, fileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(location, fileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MoreThanThreeGenerations_RetainsTheNewestThreeBodies()
    {
        using var temporary = new TemporaryAppDirectory();
        var timeProvider = new MutableTimeProvider(StorageSamples.Now);
        var store = CreateSettingsStore(temporary, timeProvider);

        for (var index = 0; index < 5; index++)
        {
            timeProvider.SetUtcNow(StorageSamples.Now.AddSeconds(index));
            await QuarantineCorruptSettingsAsync(temporary, store, $"generation-{index}");
        }

        var bodies = new List<string>();
        foreach (var path in Directory.GetFiles(
                     temporary.Paths.RecoveryDirectory,
                     "settings-*-corrupt-*.json"))
        {
            bodies.Add(await File.ReadAllTextAsync(
                path,
                Encoding.UTF8,
                TestContext.Current.CancellationToken));
        }

        Assert.Equal(3, bodies.Count);
        Assert.DoesNotContain(bodies, body => body.Contains("generation-0", StringComparison.Ordinal));
        Assert.DoesNotContain(bodies, body => body.Contains("generation-1", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("generation-2", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("generation-3", StringComparison.Ordinal));
        Assert.Contains(bodies, body => body.Contains("generation-4", StringComparison.Ordinal));
    }

    private static SettingsJsonStore CreateSettingsStore(
        TemporaryAppDirectory temporary,
        TimeProvider timeProvider) =>
        new(temporary.Paths, new AtomicFileWriter(), timeProvider);

    private static async Task QuarantineCorruptSettingsAsync(
        TemporaryAppDirectory temporary,
        SettingsJsonStore store,
        string marker)
    {
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            $"{{{marker}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        _ = await store.LoadAsync(TestContext.Current.CancellationToken);
    }
}
