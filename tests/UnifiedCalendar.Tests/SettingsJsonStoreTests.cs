using System.Text;
using System.Text.Json.Nodes;
using NSubstitute;
using UnifiedCalendar.Core.Persistence;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class SettingsJsonStoreTests
{
    [Fact]
    public async Task LoadAsync_WhenFileIsAbsent_ReturnsDocumentedDefaults()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);

        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, settings.Display.Days);
        Assert.Equal(14, settings.Display.FontSizeDip);
        Assert.Equal(DisplayDensity.Standard, settings.Display.Density);
        Assert.Equal("#2F6FED", settings.Display.DefaultEventColor.ToHexString());
        Assert.Equal(5, settings.Sync.IntervalMinutes);
        Assert.True(settings.General.StartWithWindows);
        Assert.Null(settings.Windows.Main);
        Assert.Null(settings.Windows.Settings);
        Assert.Empty(settings.Accounts);
        Assert.Empty(settings.ColorRules);
    }

    [Fact]
    public async Task V1_RoundTripsCompleteSettings_AsBomlessCamelCaseJson()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var expected = StorageSamples.CreateSettings();

        await store.SaveAsync(expected, TestContext.Current.CancellationToken);
        var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        AssertSettingsEqual(expected, actual);
        var bytes = await File.ReadAllBytesAsync(
            temporary.Paths.SettingsFile,
            TestContext.Current.CancellationToken);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"density\": \"compact\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provider\": \"google\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SchemaVersion", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 14, 5)]
    [InlineData(91, 14, 5)]
    [InlineData(7, 9, 5)]
    [InlineData(7, 25, 5)]
    [InlineData(7, 14, 2)]
    public void SettingsValidation_RejectsValuesOutsideTheV1Contract(
        int days,
        int fontSizeDip,
        int intervalMinutes)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
        {
            _ = new AppSettings(
                new DisplayPreferences(days, fontSizeDip),
                new SyncPreferences(intervalMinutes));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    public void SettingsValidation_AcceptsEveryDocumentedSyncInterval(int intervalMinutes)
    {
        Assert.Equal(intervalMinutes, new SyncPreferences(intervalMinutes).IntervalMinutes);
    }

    [Fact]
    public async Task CorruptJson_IsQuarantined_AndDefaultsAreReturned()
    {
        using var temporary = new TemporaryAppDirectory();
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            "{not-json",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = CreateStore(temporary);

        var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, actual.Display.Days);
        Assert.False(File.Exists(temporary.Paths.SettingsFile));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "settings-*-corrupt-*.json"));
    }

    [Fact]
    public async Task InvalidCurrentSchema_IsQuarantined_AndDefaultsAreReturned()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        await store.SaveAsync(StorageSamples.CreateSettings(), TestContext.Current.CancellationToken);
        var invalid = (await File.ReadAllTextAsync(
                temporary.Paths.SettingsFile,
                Encoding.UTF8,
                TestContext.Current.CancellationToken))
            .Replace("\"days\": 14", "\"days\": 0", StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            invalid,
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, actual.Display.Days);
        Assert.False(File.Exists(temporary.Paths.SettingsFile));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "settings-*-corrupt-*.json"));
    }

    [Theory]
    [InlineData("density")]
    [InlineData("startWithWindows")]
    public async Task MissingRequiredScalarProperty_IsQuarantinedAsInvalid(string propertyName)
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        await store.SaveAsync(StorageSamples.CreateSettings(), TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(
            temporary.Paths.SettingsFile,
            Encoding.UTF8,
            TestContext.Current.CancellationToken))!.AsObject();
        var owner = propertyName == "density"
            ? root["display"]!.AsObject()
            : root["general"]!.AsObject();
        Assert.True(owner.Remove(propertyName));
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            root.ToJsonString(),
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, actual.Display.Days);
        Assert.False(File.Exists(temporary.Paths.SettingsFile));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "settings-*-corrupt-*.json"));
    }

    [Fact]
    public async Task FutureSchema_IsQuarantined_AndDefaultsAreReturned()
    {
        using var temporary = new TemporaryAppDirectory();
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            "{\"schemaVersion\":9,\"futureData\":true}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = CreateStore(temporary);

        var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, actual.Display.Days);
        Assert.False(File.Exists(temporary.Paths.SettingsFile));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "settings-*-future-*-future-v9-*.json"));
    }

    [Fact]
    public async Task OlderSchema_IsMigratedOneVersion_Validated_AndAtomicallySavedAsV1()
    {
        using var temporary = new TemporaryAppDirectory();
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            "{\"schemaVersion\":0,\"display\":{\"displayDays\":12}}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = CreateStore(temporary);

        var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, actual.Display.Days);
        Assert.Equal(14, actual.Display.FontSizeDip);
        Assert.Equal(5, actual.Sync.IntervalMinutes);
        var migrated = JsonNode.Parse(await File.ReadAllTextAsync(
            temporary.Paths.SettingsFile,
            Encoding.UTF8,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, migrated!["schemaVersion"]!.GetValue<int>());
        Assert.Null(migrated["display"]!["displayDays"]);
    }

    [Fact]
    public async Task MigrationTransformFailure_QuarantinesUnmodifiedSourceAndIsNotRetried()
    {
        using var temporary = new TemporaryAppDirectory();
        const string original = "{\"schemaVersion\":0,\"display\":{\"displayDays\":33}}";
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            original,
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var migration = Substitute.For<ISettingsMigration>();
        migration.FromVersion.Returns(0);
        migration.ToVersion.Returns(1);
        migration.Migrate(Arg.Any<JsonObject>()).Returns(_ =>
            throw new SettingsMigrationException("injected transform failure"));
        var store = CreateStore(temporary, migrations: [migration]);

        var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, actual.Display.Days);
        Assert.False(File.Exists(temporary.Paths.SettingsFile));
        var quarantined = Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "settings-*-migration-*-migration-v0-*.json"));
        Assert.Equal(
            original,
            await File.ReadAllTextAsync(
                quarantined,
                Encoding.UTF8,
                TestContext.Current.CancellationToken));

        _ = await store.LoadAsync(TestContext.Current.CancellationToken);
        migration.Received(1).Migrate(Arg.Any<JsonObject>());
    }

    [Fact]
    public async Task MigrationValidationFailure_QuarantinesUnmodifiedSource()
    {
        using var temporary = new TemporaryAppDirectory();
        const string original = "{\"schemaVersion\":0,\"display\":{\"displayDays\":0}}";
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            original,
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = CreateStore(temporary);

        var actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, actual.Display.Days);
        Assert.False(File.Exists(temporary.Paths.SettingsFile));
        var quarantined = Assert.Single(Directory.GetFiles(
            temporary.Paths.RecoveryDirectory,
            "settings-*-migration-*-migration-v0-*.json"));
        Assert.Equal(
            original,
            await File.ReadAllTextAsync(
                quarantined,
                Encoding.UTF8,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnexpectedMigrationImplementationFailure_PropagatesAndLeavesSourceActive()
    {
        using var temporary = new TemporaryAppDirectory();
        const string original = "{\"schemaVersion\":0,\"display\":{\"displayDays\":33}}";
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            original,
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var migration = Substitute.For<ISettingsMigration>();
        migration.FromVersion.Returns(0);
        migration.ToVersion.Returns(1);
        migration.Migrate(Arg.Any<JsonObject>()).Returns(_ =>
            throw new InvalidOperationException("implementation bug with user@example.invalid"));
        var store = CreateStore(temporary, migrations: [migration]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            original,
            await File.ReadAllTextAsync(
                temporary.Paths.SettingsFile,
                Encoding.UTF8,
                TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temporary.Paths.RecoveryDirectory));
    }

    [Fact]
    public async Task MigrationSaveFailure_AfterFlush_DoesNotModifyTheOriginalFile()
    {
        using var temporary = new TemporaryAppDirectory();
        const string original = "{\"schemaVersion\":0,\"display\":{\"displayDays\":33}}";
        await File.WriteAllTextAsync(
            temporary.Paths.SettingsFile,
            original,
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var faultInjector = Substitute.For<IAtomicWriteFaultInjector>();
        faultInjector
            .When(injector => injector.OnStage(
                AtomicWriteStage.FlushToDiskCompleted,
                temporary.Paths.SettingsFile))
            .Do(_ => throw new IOException("Injected migration commit failure."));
        var store = CreateStore(temporary, new AtomicFileWriter(faultInjector));

        await Assert.ThrowsAsync<IOException>(() =>
            store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            original,
            await File.ReadAllTextAsync(
                temporary.Paths.SettingsFile,
                Encoding.UTF8,
                TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(temporary.Paths.RecoveryDirectory));
    }

    [Fact]
    public async Task Quarantine_RetainsOnlyThreeNewestFilesForSameKindAndSource()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        for (var index = 0; index < 5; index++)
        {
            await File.WriteAllTextAsync(
                temporary.Paths.SettingsFile,
                $"{{broken-{index}",
                new UTF8Encoding(false),
                TestContext.Current.CancellationToken);
            _ = await store.LoadAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            3,
            Directory.GetFiles(temporary.Paths.RecoveryDirectory, "settings-*-corrupt-*.json").Length);
    }

    private static SettingsJsonStore CreateStore(
        TemporaryAppDirectory temporary,
        AtomicFileWriter? writer = null,
        IEnumerable<ISettingsMigration>? migrations = null) =>
        new(
            temporary.Paths,
            writer ?? new AtomicFileWriter(),
            new MutableTimeProvider(StorageSamples.Now),
            migrations);

    private static void AssertSettingsEqual(AppSettings expected, AppSettings actual)
    {
        Assert.Equal(expected.Display, actual.Display);
        Assert.Equal(expected.Sync, actual.Sync);
        Assert.Equal(expected.General, actual.General);
        Assert.Equal(expected.Windows, actual.Windows);
        var expectedAccount = Assert.Single(expected.Accounts);
        var actualAccount = Assert.Single(actual.Accounts);
        Assert.Equal(expectedAccount.InternalAccountId, actualAccount.InternalAccountId);
        Assert.Equal(expectedAccount.Provider, actualAccount.Provider);
        Assert.Equal(expectedAccount.ProviderSubjectId, actualAccount.ProviderSubjectId);
        Assert.Equal(expectedAccount.DisplayName, actualAccount.DisplayName);
        Assert.Equal(expectedAccount.Email, actualAccount.Email);
        Assert.Equal(expectedAccount.Enabled, actualAccount.Enabled);
        Assert.Equal(expectedAccount.TokenRef, actualAccount.TokenRef);
        Assert.Equal(expectedAccount.Calendars, actualAccount.Calendars);
        var expectedRule = Assert.Single(expected.ColorRules);
        var actualRule = Assert.Single(actual.ColorRules);
        Assert.Equal(expectedRule.Name, actualRule.Name);
        Assert.Equal(expectedRule.IsEnabled, actualRule.IsEnabled);
        Assert.Equal(expectedRule.Operator, actualRule.Operator);
        Assert.Equal(expectedRule.Color, actualRule.Color);
        Assert.Equal(expectedRule.Conditions, actualRule.Conditions);
    }
}
