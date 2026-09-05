using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Jobs.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The list of releases Deluno will not use again.
///
/// <para>DESIGN-007 decision 2: refusals are permanent until somebody clears
/// them. James chose that over an expiry, and it is only a safe choice because
/// the list is visible and reversible — so what is asserted here is that it
/// can be read back and undone, and that it does not grow with repetition.</para>
/// </summary>
public sealed class TheBlocklistTests
{
    [Fact]
    public async Task A_blocked_release_can_be_read_back_with_its_reason()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", "noVideoStream"), CancellationToken.None);

        var listed = Assert.Single(await blocklist.ListAsync(CancellationToken.None));
        Assert.Equal("Arrival.2016.2160p", listed.ReleaseName);
        Assert.Equal("noVideoStream", listed.ReasonCode);
        Assert.Equal("Arrival", listed.Title);
    }

    /// <summary>
    /// The same release failing twice is one entry. A second row would say
    /// nothing the first did not, and would make the list grow with repetition
    /// rather than with problems — which is how a blocklist becomes a chore.
    /// </summary>
    [Fact]
    public async Task Blocking_the_same_release_twice_keeps_one_entry_and_the_first_reason()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", "noVideoStream"), CancellationToken.None);
        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", "importFailed"), CancellationToken.None);

        var listed = Assert.Single(await blocklist.ListAsync(CancellationToken.None));
        Assert.Equal("noVideoStream", listed.ReasonCode);
    }

    /// <summary>
    /// The same name from another indexer is a different offer, and Deluno has
    /// said nothing about it.
    /// </summary>
    [Fact]
    public async Task The_same_name_from_another_indexer_is_a_separate_entry()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", "likelySample"), CancellationToken.None);
        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Orbit", "likelySample"), CancellationToken.None);

        Assert.Equal(2, (await blocklist.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task The_keys_a_search_skips_are_the_ones_that_were_blocked()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", "likelySample"), CancellationToken.None);

        var keys = await blocklist.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.For("Arrival.2016.2160p", "Nebula"), keys);
        Assert.DoesNotContain(BlockedReleaseKeys.For("Arrival.2016.2160p", "Orbit"), keys);
    }

    /// <summary>
    /// Permanence is only safe if it can be undone. This is that.
    /// </summary>
    [Fact]
    public async Task Unblocking_removes_it_and_the_search_stops_skipping_it()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        var blocked = await blocklist.BlockAsync(
            Release("Arrival.2016.2160p", "Nebula", "noVideoStream"), CancellationToken.None);

        Assert.True(await blocklist.UnblockAsync(blocked.Id, CancellationToken.None));
        Assert.Empty(await blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await blocklist.ListKeysAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unblocking_something_that_is_not_there_says_so()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        Assert.False(await blocklist.UnblockAsync("never-blocked", CancellationToken.None));
    }

    // ------------------------------------------------------------------ helpers

    private static readonly FixedTimeProvider Clock = new(DateTimeOffset.Parse("2026-09-05T12:00:00Z"));

    private static async Task<TestStorage> StorageAsync()
    {
        var storage = TestStorage.Create();
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, Clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return storage;
    }

    private static BlockedRelease Release(string releaseName, string indexerName, string reasonCode)
        => new(
            Guid.NewGuid().ToString("n"),
            BlockedReleaseKeys.For(releaseName, indexerName),
            releaseName,
            indexerName,
            "movies",
            "movie-1",
            "Arrival",
            reasonCode,
            "The file had no video stream.",
            "hash-1",
            "qbittorrent-main",
            "qBittorrent",
            Clock.GetUtcNow());
}
