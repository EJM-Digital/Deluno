using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Jobs.Migrations;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
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
    /// The same name from another indexer is its own entry, with its own reason
    /// and its own download to clear up. Whether a search skips it is decided
    /// from the reason, below.
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
    /// #802, #874: the hash recorded at grab time is its own key, so the same
    /// content is refused wherever it turns up next, not only under the name
    /// and indexer it was first blocked from.
    /// </summary>
    [Fact]
    public async Task The_hash_recorded_at_grab_time_is_its_own_key()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", "likelySample") with { TorrentHashOrItemId = "ABCDEF0123456789ABCDEF0123456789ABCDEF01" }, CancellationToken.None);

        var keys = await blocklist.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.ForHash("ABCDEF0123456789ABCDEF0123456789ABCDEF01"), keys);
        // Case and surrounding space fall out the same way the name key's do.
        Assert.Contains(BlockedReleaseKeys.ForHash("  abcdef0123456789abcdef0123456789abcdef01  "), keys);
    }

    /// <summary>
    /// The identity a real client gives a download is what a block is keyed on: qBittorrent 5.2.1's own torrent list (tests/fixtures/recorded)
    /// names the torrent and its hash, and a release blocked with exactly those is skipped whichever indexer it is offered by, by hash and
    /// by the name the client knows it under.
    /// </summary>
    [Fact]
    public async Task A_release_blocked_with_the_hash_and_name_a_real_client_listed_is_skipped_from_any_indexer()
    {
        var torrent = RecordedReplies.Qbittorrent("downloading").Find("torrents/info").Reply.Json().AsArray()[0]!;
        var hash = (string)torrent["hash"]!;
        var name = (string)torrent["name"]!;
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release(name, "Nebula", "likelySample") with { TorrentHashOrItemId = hash }, CancellationToken.None);

        var keys = await blocklist.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.ForHash(hash), keys);
        Assert.Contains(BlockedReleaseKeys.ForHash(hash.ToUpperInvariant()), keys);
        Assert.Contains(BlockedReleaseKeys.For(name, "Nebula"), keys);
        Assert.Single(await blocklist.ListAsync(CancellationToken.None));
    }

    /// <summary>
    /// A block made before #874 shipped either predates the hash column or
    /// was made from a source that never had one. Reading it back must not
    /// throw, and it must keep matching by name+indexer exactly as before —
    /// nothing here is a migration that could drop a row's ability to match.
    /// </summary>
    [Fact]
    public async Task A_release_blocked_with_no_known_hash_still_matches_by_name()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", "likelySample") with { TorrentHashOrItemId = null }, CancellationToken.None);

        var keys = await blocklist.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.For("Arrival.2016.2160p", "Nebula"), keys);
    }

    /// <summary>
    /// #874: James decided the blocklist key is the release's identity across
    /// every indexer for every refusal reason, not only the ones about the
    /// file itself. A stall on one indexer is exactly as disqualifying on
    /// another, so every reason code — including the ones Deluno cannot place
    /// and the ones that are only about this attempt — adds the every-indexer
    /// key.
    /// </summary>
    [Theory]
    [InlineData(ImportFailurePolicy.ProcessorRejected)]
    [InlineData(ImportFailurePolicy.NoVideoStream)]
    [InlineData(ImportFailurePolicy.LikelySample)]
    [InlineData(ImportFailurePolicy.MediaProbeRejected)]
    [InlineData(ImportFailurePolicy.ReplacementRejected)]
    [InlineData(ImportFailurePolicy.DownloadStalled)]
    [InlineData(ImportFailurePolicy.ImportFailed)]
    [InlineData(ImportFailurePolicy.MediaProbeUnreadable)]
    public async Task Every_refusal_holds_the_release_from_every_indexer(string reasonCode)
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", reasonCode), CancellationToken.None);

        var keys = await blocklist.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.For("Arrival.2016.2160p", "Nebula"), keys);
        Assert.Contains(BlockedReleaseKeys.ForEveryIndexer("Arrival.2016.2160p"), keys);
    }

    /// <summary>
    /// The exact scenario #874 was raised over: a release refused for
    /// stalling on one indexer, with no hash recorded, must not be free to
    /// grab under the same name from a different indexer.
    /// </summary>
    [Fact]
    public async Task A_stall_with_no_hash_still_blocks_the_same_name_from_another_indexer()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(
            Release("Arrival.2016.2160p", "Nebula", ImportFailurePolicy.DownloadStalled) with { TorrentHashOrItemId = null },
            CancellationToken.None);

        var keys = await blocklist.ListKeysAsync(CancellationToken.None);
        Assert.Contains(BlockedReleaseKeys.ForEveryIndexer("Arrival.2016.2160p"), keys);
        // An unrelated name is not swept up by the every-indexer key.
        Assert.DoesNotContain(BlockedReleaseKeys.ForEveryIndexer("Some.Other.Movie.2020.1080p"), keys);
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

    /// <summary>
    /// The one that makes "ask me" worth offering. A proposal is recorded with
    /// its reason and changes nothing — a search that quietly skipped an
    /// undecided release would be making the decision by omission, which is
    /// the exact thing the option exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_proposed_refusal_is_listed_but_no_search_acts_on_it()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Proposal("Arrival.2016.2160p", "Nebula"), CancellationToken.None);

        var listed = Assert.Single(await blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(BlockedReleaseStates.Proposed, listed.State);
        Assert.Empty(await blocklist.ListKeysAsync(CancellationToken.None));
    }

    /// <summary>
    /// And its copy is left where it is. Destroying the evidence before the
    /// question is answered would make "allow it" a lie.
    /// </summary>
    [Fact]
    public async Task A_proposed_refusal_does_not_have_its_download_cleared_up()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Proposal("Arrival.2016.2160p", "Nebula"), CancellationToken.None);

        Assert.Empty(await blocklist.ListAwaitingCleanupAsync(CancellationToken.None));
    }

    /// <summary>
    /// #887: a caller's own "shouldDeletePayload" answers instead of the
    /// shipped table, so a person's "remove and delete packed archives" choice
    /// can fold a reason in without this repository knowing about settings.
    /// </summary>
    [Fact]
    public async Task A_caller_supplied_delete_rule_overrides_the_shipped_table()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);
        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "Nebula", ImportFailurePolicy.UnsupportedFile), CancellationToken.None);

        // The shipped table says no for a packed archive.
        Assert.Empty(await blocklist.ListAwaitingCleanupAsync(null, CancellationToken.None));

        // The caller's own rule says yes.
        Assert.Single(await blocklist.ListAwaitingCleanupAsync(
            reason => reason == ImportFailurePolicy.UnsupportedFile,
            CancellationToken.None));
    }

    [Fact]
    public async Task Answering_a_proposal_with_yes_makes_searches_skip_it()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);
        var proposed = await blocklist.BlockAsync(Proposal("Arrival.2016.2160p", "Nebula"), CancellationToken.None);

        Assert.True(await blocklist.RefuseAsync(proposed.Id, CancellationToken.None));

        Assert.Contains(BlockedReleaseKeys.For("Arrival.2016.2160p", "Nebula"), await blocklist.ListKeysAsync(CancellationToken.None));
        Assert.Single(await blocklist.ListAwaitingCleanupAsync(CancellationToken.None));
    }

    /// <summary>
    /// #882/#883/#885: a release the client itself reported as failed
    /// (missing articles, an encrypted archive, a damaged unpack) is eligible
    /// for cleanup too, alongside the reasons Deluno judged for itself —
    /// RefusedDownloadCleanupService is the one that decides whether the
    /// user's own cleanup setting actually lets it run.
    /// </summary>
    [Fact]
    public async Task A_client_reported_failure_is_eligible_for_cleanup_too()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);

        await blocklist.BlockAsync(Release("Arrival.2016.2160p", "NZBGeek", ImportFailurePolicy.ClientReportedFailure), CancellationToken.None);

        var eligible = Assert.Single(await blocklist.ListAwaitingCleanupAsync(CancellationToken.None));
        Assert.Equal(ImportFailurePolicy.ClientReportedFailure, eligible.ReasonCode);
    }

    /// <summary>
    /// A second click on an entry that is already refused must not move the
    /// date it happened, which is the only record of when Deluno stopped
    /// trying.
    /// </summary>
    [Fact]
    public async Task Refusing_something_already_refused_changes_nothing()
    {
        using var storage = await StorageAsync();
        var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, Clock);
        var refused = await blocklist.BlockAsync(
            Release("Arrival.2016.2160p", "Nebula", "likelySample"), CancellationToken.None);

        Assert.False(await blocklist.RefuseAsync(refused.Id, CancellationToken.None));
        Assert.Equal(BlockedReleaseStates.Refused, Assert.Single(await blocklist.ListAsync(CancellationToken.None)).State);
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

    private static BlockedRelease Proposal(string releaseName, string indexerName)
        => Release(releaseName, indexerName, ImportFailurePolicy.LikelySample) with
        {
            State = BlockedReleaseStates.Proposed
        };
}
