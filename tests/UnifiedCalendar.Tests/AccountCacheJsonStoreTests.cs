using System.Text;
using System.Text.Json.Nodes;
using UnifiedCalendar.Core.Models;
using UnifiedCalendar.Infrastructure.Storage;
using Xunit;

namespace UnifiedCalendar.Tests;

public sealed class AccountCacheJsonStoreTests
{
    [Fact]
    public async Task V1_RoundTripsAllCacheFieldsAndTimingKinds()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var expected = StorageSamples.CreateGoogleCache();

        await store.SaveAccountAsync(expected, TestContext.Current.CancellationToken);
        var actual = await store.LoadAccountAsync(
            expected.InternalAccountId,
            TestContext.Current.CancellationToken);

        Assert.NotNull(actual);
        Assert.Equal(expected.InternalAccountId, actual.InternalAccountId);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.GeneratedAtUtc, actual.GeneratedAtUtc);
        var expectedCalendar = Assert.Single(expected.Calendars);
        var actualCalendar = Assert.Single(actual.Calendars);
        Assert.Equal(expectedCalendar.CalendarId, actualCalendar.CalendarId);
        Assert.Equal(expectedCalendar.Name, actualCalendar.Name);
        Assert.Equal(expectedCalendar.SourceColor, actualCalendar.SourceColor);
        Assert.Equal(expectedCalendar.LastSuccessfulSyncUtc, actualCalendar.LastSuccessfulSyncUtc);
        Assert.Equal(2, actualCalendar.Events.Count);
        AssertEventEqual(expectedCalendar.Events[0], actualCalendar.Events[0]);
        AssertEventEqual(expectedCalendar.Events[1], actualCalendar.Events[1]);

        var json = await File.ReadAllTextAsync(
            temporary.Paths.GetAccountCacheFile(expected.InternalAccountId),
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        Assert.Contains("\"kind\": \"timed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"allDay\"", json, StringComparison.Ordinal);
        Assert.Contains("\"provider\": \"google\"", json, StringComparison.Ordinal);
        Assert.Contains(".0000000Z", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavesAndLoadsAccountsInIndependentFiles()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var first = StorageSamples.CreateGoogleCache(StorageSamples.GoogleAccountId);
        var second = StorageSamples.CreateGoogleCache(StorageSamples.MicrosoftAccountId);

        await store.SaveAccountAsync(first, TestContext.Current.CancellationToken);
        await store.SaveAccountAsync(second, TestContext.Current.CancellationToken);

        Assert.NotNull(await store.LoadAccountAsync(
            first.InternalAccountId,
            TestContext.Current.CancellationToken));
        Assert.NotNull(await store.LoadAccountAsync(
            second.InternalAccountId,
            TestContext.Current.CancellationToken));
        Assert.NotEqual(
            temporary.Paths.GetAccountCacheFile(first.InternalAccountId),
            temporary.Paths.GetAccountCacheFile(second.InternalAccountId));
        Assert.Equal(2, Directory.GetFiles(temporary.Paths.AccountCacheDirectory, "*.json").Length);
    }

    [Fact]
    public async Task CorruptCache_IsQuarantinedAndRequiresResynchronization()
    {
        using var temporary = new TemporaryAppDirectory();
        var path = temporary.Paths.GetAccountCacheFile(StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            path,
            "not-json",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = CreateStore(temporary);

        var actual = await store.LoadAccountAsync(
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "cache-*-corrupt-*.json"));
    }

    [Fact]
    public async Task OlderCache_IsQuarantinedAndRequiresResynchronization()
    {
        using var temporary = new TemporaryAppDirectory();
        var path = temporary.Paths.GetAccountCacheFile(StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":0}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = CreateStore(temporary);

        var actual = await store.LoadAccountAsync(
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "cache-*-older-*-older-v0-*.json"));
    }

    [Fact]
    public async Task FutureCache_IsQuarantinedAndRequiresResynchronization()
    {
        using var temporary = new TemporaryAppDirectory();
        var path = temporary.Paths.GetAccountCacheFile(StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":5}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = CreateStore(temporary);

        var actual = await store.LoadAccountAsync(
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "cache-*-future-*-future-v5-*.json"));
    }

    [Fact]
    public async Task InvalidCurrentCache_IsQuarantinedAndRequiresResynchronization()
    {
        using var temporary = new TemporaryAppDirectory();
        var path = temporary.Paths.GetAccountCacheFile(StorageSamples.GoogleAccountId);
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":1,\"internalAccountId\":\"00000000-0000-0000-0000-000000000000\",\"provider\":\"google\",\"generatedAtUtc\":\"2026-08-28T05:12:34.0000000Z\",\"calendars\":[]}",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var store = CreateStore(temporary);

        var actual = await store.LoadAccountAsync(
            StorageSamples.GoogleAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "cache-*-corrupt-*.json"));
    }

    [Theory]
    [InlineData("generatedAtUtc")]
    [InlineData("lastSuccessfulSyncUtc")]
    [InlineData("startUtc")]
    [InlineData("endUtc")]
    public async Task MissingRequiredScalarProperty_IsQuarantinedAsInvalid(string propertyName)
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var cache = StorageSamples.CreateGoogleCache();
        await store.SaveAccountAsync(cache, TestContext.Current.CancellationToken);
        var path = temporary.Paths.GetAccountCacheFile(cache.InternalAccountId);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(
            path,
            Encoding.UTF8,
            TestContext.Current.CancellationToken))!.AsObject();
        var owner = propertyName switch
        {
            "generatedAtUtc" => root,
            "lastSuccessfulSyncUtc" => root["calendars"]![0]!.AsObject(),
            _ => root["calendars"]![0]!["events"]![0]!["timing"]!.AsObject(),
        };
        Assert.True(owner.Remove(propertyName));
        await File.WriteAllTextAsync(
            path,
            root.ToJsonString(),
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);

        var actual = await store.LoadAccountAsync(
            cache.InternalAccountId,
            TestContext.Current.CancellationToken);

        Assert.Null(actual);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(temporary.Paths.RecoveryDirectory, "cache-*-corrupt-*.json"));
    }

    [Fact]
    public async Task RemoveAccountCache_OnlyRemovesRequestedAccount()
    {
        using var temporary = new TemporaryAppDirectory();
        var store = CreateStore(temporary);
        var first = StorageSamples.CreateGoogleCache(StorageSamples.GoogleAccountId);
        var second = StorageSamples.CreateGoogleCache(StorageSamples.MicrosoftAccountId);
        await store.SaveAccountAsync(first, TestContext.Current.CancellationToken);
        await store.SaveAccountAsync(second, TestContext.Current.CancellationToken);

        await store.RemoveAccountAsync(first.InternalAccountId, TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadAccountAsync(
            first.InternalAccountId,
            TestContext.Current.CancellationToken));
        Assert.NotNull(await store.LoadAccountAsync(
            second.InternalAccountId,
            TestContext.Current.CancellationToken));
    }

    private static AccountCacheJsonStore CreateStore(TemporaryAppDirectory temporary) => new(
        temporary.Paths,
        new AtomicFileWriter(),
        new MutableTimeProvider(StorageSamples.Now));

    private static void AssertEventEqual(CalendarEvent expected, CalendarEvent actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Timing, actual.Timing);
        Assert.Equal(expected.CalendarName, actual.CalendarName);
        Assert.Equal(expected.DescriptionPlainText, actual.DescriptionPlainText);
        Assert.Equal(expected.Location, actual.Location);
        Assert.Equal(expected.SourceTimeZoneId, actual.SourceTimeZoneId);
        Assert.Equal(expected.ResponseStatus, actual.ResponseStatus);
        Assert.Equal(expected.SourceEventColor, actual.SourceEventColor);
        Assert.Equal(expected.SourceCalendarColor, actual.SourceCalendarColor);
        Assert.Equal(expected.MeetingUri, actual.MeetingUri);
        Assert.Equal(expected.SourceDetailUri, actual.SourceDetailUri);
        Assert.Equal(expected.IsCancelled, actual.IsCancelled);
    }
}
