using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1240: when a strike rule reaches its count on a torrent from a private tracker (or one grouped with a seeding
/// tag) and the rule says so, the torrent is moved to the keep-seeding category instead of being removed. It keeps
/// running in the client, Deluno stops tracking it, a replacement search starts, and nothing is deleted on any path.
/// A public torrent is removed as before.
///
/// <para>Runs the real <see cref="DownloadClientTelemetryService.RunConfiguredHealthRemediationAsync"/> against real
/// SQLite repositories and a fake client that records every call, so the order, the refusals and the side effects are the
/// service's own.</para>
/// </summary>
public sealed class AStruckPrivateTorrentKeepsSeedingTests
{
    private const string Release = "Example.Movie.2026.1080p.WEB-DL-GROUP";

    /// <summary>A real data folder with the real databases in it: every test here runs on one, and its download folder holds a real file.</summary>
    private static TestStorage NewStorage() => TestStorage.Create();

    /// <summary>Reads the real database file on disk, so a test proves what was saved, not only what the repositories say.</summary>
    private static string OnDisk(World world, string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(world.DataRoot, database)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    public static IEnumerable<object[]> EveryRuleKind() =>
    [
        [DownloadHealthRuleKinds.NoProgress],
        [DownloadHealthRuleKinds.TooSlow],
        [DownloadHealthRuleKinds.LowPeers],
        [DownloadHealthRuleKinds.DeadTorrent],
        [DownloadHealthRuleKinds.MagnetMetadata],
        [DownloadHealthRuleKinds.ClientFailure]
    ];

    // ------------------------------------------------------------- each rule moves a private torrent at its count

    [Theory]
    [MemberData(nameof(EveryRuleKind))]
    public async Task Each_rule_moves_a_private_torrent_at_its_count_instead_of_removing_it(string kind)
    {
        using var world = await World.CreateAsync(rule: Rule(kind, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");

        var report = await world.RemediateAsync();

        Assert.Equal(1, report.TorrentsMoved);
        Assert.Equal(0, report.ClientEntriesRemoved);
        Assert.Equal(0, report.PayloadsPurged);
        var move = Assert.Single(world.Client.Moves);
        Assert.Equal(("queue-movie", "keep-seeding", "deluno-movies"), (move.ItemId, move.Category, move.CurrentCategory));
        // Nothing was removed, deleted, paused or forgotten, on any client call.
        Assert.Empty(world.Client.Actions);
        Assert.True(File.Exists(world.PayloadPath));
        // The release is blocked for the title and a replacement search is queued.
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        // And the blocked release is not left waiting for the refused-download cleanup, which would otherwise ask the
        // client to forget it with its files: a parked torrent is not a refused copy to clear.
        Assert.Empty(await world.Blocklist.ListAwaitingCleanupAsync(CancellationToken.None));
        Assert.Equal(1, report.ReplacementSearchesQueued);
        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
        // Deluno stopped tracking it.
        var dispatch = (await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None))!;
        Assert.Equal("archived", dispatch.Status);
        Assert.True(KeptSeedingDispatches.IsReleased(dispatch));
        Assert.False((await world.Service.CheckOwnershipAsync(world.ClientId, "queue-movie", CancellationToken.None)).Owned);
        // And the problem list is empty.
        Assert.Empty(await world.Store.ListProblemsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_activity_line_says_what_moved_where_why_and_that_it_is_looking_for_another_copy()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");

        await world.RemediateAsync();

        var entry = Assert.Single(
            await world.Jobs.ListActivityAsync(20, null, null, CancellationToken.None),
            item => item.Category == "download.health.remediated");
        Assert.Equal(
            $"Moved {Release} to keep-seeding on {world.ClientName} so it keeps seeding on {world.SiteName}, and is looking for another copy. Deluno blocked this release. It made no progress for 3 checks in a row.",
            entry.Message);
    }

    [Fact]
    public async Task A_public_torrent_is_removed_as_before_even_by_a_rule_that_moves_private_ones()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: false, clientCategory: "keep-seeding", clientSaysPrivate: false);

        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Equal(1, report.ClientEntriesRemoved);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        Assert.Empty(world.Client.Moves);
    }

    [Fact]
    public async Task A_torrent_the_client_flags_private_moves_even_when_its_site_is_not_marked_private()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: false, clientCategory: "keep-seeding", clientSaysPrivate: true);

        var report = await world.RemediateAsync();

        Assert.Equal(1, report.TorrentsMoved);
        Assert.Empty(world.Client.Actions);
    }

    [Fact]
    public async Task A_site_the_person_overrides_to_private_moves_whatever_its_definition_says()
    {
        using var world = await World.CreateAsync(
            rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: false, clientCategory: "keep-seeding",
            settings: s => s with { Indexers = new Dictionary<string, KeepSeedingIndexerSettings> { ["site"] = new(PrivateOverride: true) } });

        var report = await world.RemediateAsync();

        Assert.Equal(1, report.TorrentsMoved);
    }

    [Fact]
    public async Task A_site_the_person_overrides_to_not_private_is_removed_as_before_when_the_client_says_nothing()
    {
        using var world = await World.CreateAsync(
            rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding",
            settings: s => s with { Indexers = new Dictionary<string, KeepSeedingIndexerSettings> { ["site"] = new(PrivateOverride: false) } });

        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Equal(1, report.ClientEntriesRemoved);
    }

    [Fact]
    public async Task A_site_added_before_Deluno_kept_its_privacy_is_read_from_its_tracker_definition()
    {
        using var world = await World.CreateAsync(
            rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: null, clientCategory: "keep-seeding", definitionPrivacy: IndexerPrivacy.Private);

        var report = await world.RemediateAsync();

        Assert.Equal(1, report.TorrentsMoved);
    }

    [Fact]
    public async Task A_torrent_whose_privacy_nothing_says_is_handled_as_public_by_the_rule()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: null, clientCategory: "keep-seeding");

        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Equal(1, report.ClientEntriesRemoved);
    }

    [Fact]
    public async Task A_tagged_torrent_moves_even_when_nothing_says_it_is_private()
    {
        using var world = await World.CreateAsync(
            rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: null, clientCategory: "keep-seeding",
            settings: s => s with { Indexers = new Dictionary<string, KeepSeedingIndexerSettings> { ["site"] = new(Tags: ["ratio-sensitive"]) } });

        var report = await world.RemediateAsync();

        Assert.Equal(1, report.TorrentsMoved);
        Assert.Empty(world.Client.Actions);
    }

    // ------------------------------------------------------------- which category

    [Fact]
    public async Task The_tags_category_beats_the_sites_which_beats_the_clients()
    {
        using var world = await World.CreateAsync(
            rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "from-client",
            settings: s => s with
            {
                Indexers = new Dictionary<string, KeepSeedingIndexerSettings> { ["site"] = new(Tags: ["ratio-sensitive"], Category: "from-site") },
                TagCategories = new Dictionary<string, string> { ["ratio-sensitive"] = "from-tag" }
            });

        await world.RemediateAsync();

        Assert.Equal("from-tag", Assert.Single(world.Client.Moves).Category);
    }

    [Fact]
    public async Task The_sites_category_beats_the_clients_when_no_tag_has_one()
    {
        using var world = await World.CreateAsync(
            rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "from-client",
            settings: s => s with { Indexers = new Dictionary<string, KeepSeedingIndexerSettings> { ["site"] = new(Tags: ["no-category-tag"], Category: "from-site") } });

        await world.RemediateAsync();

        Assert.Equal("from-site", Assert.Single(world.Client.Moves).Category);
    }

    [Fact]
    public async Task The_clients_category_applies_when_neither_the_tag_nor_the_site_has_one()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "from-client");

        await world.RemediateAsync();

        Assert.Equal("from-client", Assert.Single(world.Client.Moves).Category);
    }

    // ------------------------------------------------------------- no category anywhere

    [Fact]
    public async Task A_private_torrent_with_no_category_anywhere_is_left_alone_and_the_rule_says_so()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: null);

        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Equal(0, report.ClientEntriesRemoved);
        Assert.Empty(world.Client.Moves);
        Assert.Empty(world.Client.Actions);
        Assert.True(File.Exists(world.PayloadPath));
        // No block, no search, no handled stamp: the torrent is exactly as it was.
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
        var dispatch = (await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None))!;
        Assert.NotEqual("archived", dispatch.Status);
        Assert.Null(dispatch.ImportFailureCode);
        var problem = Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));
        Assert.Equal(KeepSeedingProblemKinds.NeedsCategory, problem.Kind);
        Assert.Contains("no keep-seeding category is set for it", problem.Message, StringComparison.Ordinal);
        Assert.Contains($"Set one on its tag, its site or {world.ClientName}", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_a_category_later_moves_it_at_the_next_check_and_clears_the_problem()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: null);
        await world.RemediateAsync();
        Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));

        await world.Store.SaveAsync(new KeepSeedingSettings(ClientCategories: new Dictionary<string, string> { [world.ClientId] = "keep-seeding" }), CancellationToken.None);
        var report = await world.RemediateAsync();

        Assert.Equal(1, report.TorrentsMoved);
        Assert.Empty(await world.Store.ListProblemsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_category_Deluno_uses_for_its_own_downloads_is_not_a_place_to_keep_seeding()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "deluno-movies");

        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Empty(world.Client.Moves);
        Assert.Empty(world.Client.Actions);
        var problem = Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));
        Assert.Contains($"is the category Deluno uses for downloads on {world.ClientName}", problem.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- a client refusing the move

    [Fact]
    public async Task A_client_that_refuses_the_move_leaves_the_torrent_alone_and_shows_why()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        world.Client.RefuseWith = "qBittorrent does not have a category named keep-seeding.";

        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Equal(0, report.ClientEntriesRemoved);
        Assert.Empty(world.Client.Actions);
        Assert.True(File.Exists(world.PayloadPath));
        // Nothing else happens: no block, no search, no handled stamp, tracking is not stopped.
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.Jobs.ListAsync(10, CancellationToken.None));
        var dispatch = (await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None))!;
        Assert.NotEqual("archived", dispatch.Status);
        Assert.Null(dispatch.ImportFailureCode);
        var problem = Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));
        Assert.Equal(KeepSeedingProblemKinds.Refused, problem.Kind);
        Assert.Equal(
            $"Deluno could not move {Release} to keep-seeding in {world.ClientName}: qBittorrent does not have a category named keep-seeding. It left the download alone and will try again at the next check.",
            problem.Message);
    }

    [Fact]
    public async Task A_refused_move_is_tried_again_at_every_check_and_said_once_in_activity()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        world.Client.RefuseWith = "qBittorrent returned 503 when asked to set the category.";

        await world.RemediateAsync();
        await world.RemediateAsync();
        await world.RemediateAsync();

        Assert.Equal(3, world.Client.Moves.Count);
        var problem = Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));
        Assert.Equal(3, problem.Attempts);
        Assert.Single(
            await world.Jobs.ListActivityAsync(50, null, null, CancellationToken.None),
            item => item.Category == "download.health.keep-seeding-problem");
    }

    [Fact]
    public async Task A_retry_that_works_moves_it_and_clears_the_problem_in_the_same_pass()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        world.Client.RefuseWith = "qBittorrent is not answering.";
        await world.RemediateAsync();
        Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));

        world.Client.RefuseWith = null;
        var report = await world.RemediateAsync();

        Assert.Equal(1, report.TorrentsMoved);
        Assert.Empty(await world.Store.ListProblemsAsync(CancellationToken.None));
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(world.Client.Actions);
    }

    [Fact]
    public async Task A_client_that_throws_is_a_refusal_not_a_crash_and_nothing_is_deleted()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        world.Client.ThrowOnMove = new HttpRequestException("No connection could be made because the target machine actively refused it.");

        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Empty(world.Client.Actions);
        Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_problem_ends_when_the_torrent_recovers_or_leaves_the_client()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        world.Client.RefuseWith = "qBittorrent is not answering.";
        await world.RemediateAsync();
        Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));

        // The torrent recovered: its finding is gone, so the pass has nothing to move.
        await world.RemediateAsync(struck: false);

        Assert.Empty(await world.Store.ListProblemsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_client_that_did_not_answer_keeps_its_problems()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        world.Client.RefuseWith = "qBittorrent is not answering.";
        await world.RemediateAsync();

        await world.RemediateAsync(struck: false, clientHealth: "degraded");

        Assert.Single(await world.Store.ListProblemsAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------- timings and the other side of the rule

    [Fact]
    public async Task A_torrent_that_has_not_reached_the_count_is_never_moved()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");

        var report = await world.RemediateAsync(strikes: 2);

        Assert.Equal(0, report.Evaluated);
        Assert.Empty(world.Client.Moves);
        Assert.Empty(world.Client.Actions);
    }

    [Fact]
    public async Task Once_moved_it_is_not_struck_or_moved_again_at_the_next_check()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        await world.RemediateAsync();

        var again = await world.RemediateAsync();

        Assert.Equal(0, again.TorrentsMoved);
        Assert.Single(world.Client.Moves);
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Single(await world.Jobs.ListAsync(10, CancellationToken.None));
        // Still one blocklist entry and one search in the database files after the second check, and the payload is still on disk.
        Assert.Equal("1", OnDisk(world, "jobs.db", "SELECT COUNT(*) FROM blocked_releases"));
        Assert.Equal("1", OnDisk(world, "jobs.db", "SELECT COUNT(*) FROM job_queue"));
        Assert.True(File.Exists(world.PayloadPath), "a moved torrent keeps its files");
    }

    [Fact]
    public async Task A_rule_that_does_not_ask_to_move_removes_a_private_torrent_exactly_as_it_always_did()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: false), siteIsPrivate: true, clientCategory: "keep-seeding", clientSaysPrivate: true);

        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Equal(1, report.ClientEntriesRemoved);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
        Assert.Empty(world.Client.Moves);
    }

    [Fact]
    public async Task The_release_is_blocklisted_only_when_the_rule_says_so()
    {
        using var world = await World.CreateAsync(
            rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true) with { BlocklistWhenMoved = false }, siteIsPrivate: true, clientCategory: "keep-seeding");

        var report = await world.RemediateAsync();

        Assert.Equal(1, report.TorrentsMoved);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(1, report.ReplacementSearchesQueued);
        var entry = Assert.Single(
            await world.Jobs.ListActivityAsync(20, null, null, CancellationToken.None),
            item => item.Category == "download.health.remediated");
        Assert.DoesNotContain("blocked this release", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_says_what_it_would_move_and_touches_nothing()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding", dryRun: true);

        var report = await world.RemediateAsync();

        Assert.Empty(world.Client.Moves);
        Assert.Empty(world.Client.Actions);
        Assert.Equal(0, report.TorrentsMoved);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        var preview = Assert.Single(
            await world.Jobs.ListActivityAsync(20, null, null, CancellationToken.None),
            item => item.Category == "download.health.remediation-previewed");
        Assert.Contains($"would move it to keep-seeding on {world.ClientName}", preview.Message, StringComparison.Ordinal);
        var dispatch = (await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None))!;
        Assert.NotEqual("archived", dispatch.Status);
    }

    [Fact]
    public async Task A_usenet_download_is_never_moved_and_follows_the_rule_as_before()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding", protocol: "sabnzbd");

        var report = await world.RemediateAsync();

        Assert.Empty(world.Client.Moves);
        Assert.Equal(0, report.TorrentsMoved);
        Assert.Equal(1, report.ClientEntriesRemoved);
    }

    // ------------------------------------------------------------- after the move

    [Fact]
    public async Task After_the_move_the_torrent_is_out_of_Delunos_view_for_good_but_its_files_are_still_claimed()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        // The torrent's own folder inside Deluno's download folder, named after the release as a client names it.
        var downloads = Path.GetDirectoryName(world.PayloadPath)!;
        var folder = Path.Combine(downloads, Release);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Example.Movie.2026.mkv"), "payload");
        world.ClientListsTheTorrentIn("deluno-movies", folder);
        Assert.Single((await world.Service.GetOverviewAsync(CancellationToken.None)).Clients.Single().Queue);

        await world.RemediateAsync();
        // The client now lists it in the keep-seeding category, as the move left it.
        world.ClientListsTheTorrentIn("keep-seeding", folder);

        for (var poll = 0; poll < 3; poll++)
        {
            var overview = await world.Service.GetOverviewAsync(CancellationToken.None);
            // Gone from Transfers and the Queue, and counted nowhere.
            Assert.Empty(overview.Clients.Single().Queue);
            Assert.Equal(0, overview.Summary.ActiveCount + overview.Summary.QueuedCount + overview.Summary.StalledCount);
        }

        // It gets no more strikes: nothing is recorded for it.
        Assert.Empty(await world.Health.ListDownloadHealthRecordsAsync(50, CancellationToken.None));
        // Its files are still held by the client as far as the leftover, orphan and refused-payload cleaners can tell:
        // they read the client's outside items, which is where this torrent now is, so they are never offered or swept.
        var scan = Assert.Single(await world.Service.GetSnapshotsForLeftoverScanAsync(CancellationToken.None));
        Assert.Contains(folder, scan.OutsideSourcePaths);
        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(folder, "Example.Movie.2026.mkv")), "the moved torrent's file is still on disk");
        // The orphan scan, given what the client holds, finds nothing to offer, even for a release Deluno sent and
        // even long after the grace period: the parked torrent's files sit inside Deluno's own download folder.
        var entry = new DownloadFolderEntry(folder, true, 15, DateTimeOffset.UtcNow.AddDays(-30));
        var orphans = OrphanedDownloads.Find(
            [new DownloadFolderScan("lib", "Movies", downloads, [entry])],
            scan.OutsideSourcePaths,
            [Release],
            [],
            DateTimeOffset.UtcNow);
        Assert.Empty(orphans);
        // Control: with nothing claiming it, the same file would have been offered, so the claim is what protects it.
        Assert.Single(OrphanedDownloads.Find([new DownloadFolderScan("lib", "Movies", downloads, [entry])], [], [Release], [], DateTimeOffset.UtcNow));
        // And Deluno will not act on it from a screen or a rule either.
        var action = await world.Service.ExecuteActionAsync(world.ClientId, new DownloadClientActionRequest(DownloadClientActions.DeleteWithData, "queue-movie"), CancellationToken.None);
        Assert.False(action.Succeeded);
        Assert.Empty(world.Client.Actions);
    }

    // ------------------------------------------------------------- hand-offs and the processor

    [Theory]
    [InlineData(ProcessorHandoffStatuses.Failed)]
    [InlineData(ProcessorHandoffStatuses.Submitted)]
    public async Task A_moved_torrent_that_was_handed_to_the_processor_has_its_hand_off_closed_with_a_plain_reason(string status)
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        var handoff = await world.HandoffAsync(status);

        await world.RemediateAsync();

        var stored = (await world.Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, stored.Status);
        Assert.Equal(
            $"Deluno moved {Release} to keep-seeding on {world.ClientName} so it keeps seeding, so it will not import it.",
            stored.FailureMessage);
        Assert.False(ProcessorHandoffStatuses.NeedsPerson(stored.Status, stored.FailureMessage));
        var closed = Assert.Single(
            await world.Jobs.ListActivityAsync(50, null, null, CancellationToken.None),
            item => item.Category == "processing.dismissed");
        Assert.Contains("because Deluno moved the download to keep it seeding", closed.Message, StringComparison.Ordinal);
        Assert.Equal(ProcessorHandoffStatuses.Dismissed, OnDisk(world, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
    }

    [Fact]
    public async Task A_hand_off_that_already_finished_is_left_as_it_was()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        var handoff = await world.HandoffAsync(ProcessorHandoffStatuses.Completed);

        await world.RemediateAsync();

        Assert.Equal(ProcessorHandoffStatuses.Completed, (await world.Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Equal(ProcessorHandoffStatuses.Completed, OnDisk(world, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
    }

    [Fact]
    public async Task A_refused_move_leaves_the_hand_off_open()
    {
        using var world = await World.CreateAsync(rule: Rule(DownloadHealthRuleKinds.NoProgress, move: true), siteIsPrivate: true, clientCategory: "keep-seeding");
        var handoff = await world.HandoffAsync(ProcessorHandoffStatuses.Submitted);
        world.Client.RefuseWith = "qBittorrent is not answering.";

        await world.RemediateAsync();

        Assert.Equal(ProcessorHandoffStatuses.Submitted, (await world.Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!.Status);
        Assert.Equal(ProcessorHandoffStatuses.Submitted, OnDisk(world, "platform.db", $"SELECT status FROM processor_handoffs WHERE id = '{handoff.Id}'"));
    }

    // ------------------------------------------------------------- a fresh install and an upgraded one

    [Fact]
    public async Task A_fresh_install_has_nothing_set_and_no_rule_moves()
    {
        using var world = await World.CreateAsync(rule: null, siteIsPrivate: true, clientCategory: null);

        Assert.Equal(KeepSeedingSettings.Empty.Normalized().ClientCategoryMap, (await world.Store.GetAsync(CancellationToken.None)).ClientCategoryMap);
        Assert.Empty((await world.Store.GetAsync(CancellationToken.None)).IndexerMap);
        Assert.Empty((await world.Store.GetAsync(CancellationToken.None)).TagCategoryMap);
        Assert.Empty(await world.Store.ListProblemsAsync(CancellationToken.None));
        var rules = await world.Health.GetDownloadHealthRulesAsync(await world.Settings.GetAsync(CancellationToken.None), CancellationToken.None);
        Assert.All(rules, rule =>
        {
            Assert.Equal(DownloadHealthPrivateActions.Remove, rule.PrivateAction);
            Assert.Equal(DownloadHealthActionPresets.JustTellMe, rule.ActionPreset);
        });
    }

    [Fact]
    public async Task A_rule_saved_before_the_setting_existed_reads_as_remove_and_acts_as_it_did()
    {
        // A rule stored by an older build: JSON with none of the new fields.
        const string stored = """
            {"Id":"old-rule","Kind":"no-progress","Enabled":true,"StrikeLimit":3,"TimerMinutes":30,"AppliesTo":"both","MinCompletionPercent":0,
             "MaxCompletionPercent":100,"ResetStrikesOnProgress":true,"ActionPreset":"replace","CreatedUtc":"2026-09-01T00:00:00+00:00",
             "UpdatedUtc":"2026-09-01T00:00:00+00:00","Threshold":0,"PrivacyScope":"both"}
            """;
        var rule = JsonSerializer.Deserialize<DownloadHealthRule>(stored)!;
        Assert.Equal(DownloadHealthPrivateActions.Remove, rule.PrivateAction);

        using var world = await World.CreateAsync(rule: rule, siteIsPrivate: true, clientCategory: "keep-seeding", clientSaysPrivate: true);
        var report = await world.RemediateAsync();

        Assert.Equal(0, report.TorrentsMoved);
        Assert.Equal(1, report.ClientEntriesRemoved);
        Assert.Equal(DownloadClientActions.DeleteWithData, world.Client.LastAction);
    }

    [Fact]
    public async Task An_upgrade_from_the_last_release_keeps_every_rule_exactly_and_adds_nothing_to_the_keep_seeding_settings()
    {
        using var storage = NewStorage();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-11T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var settings = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
        var health = new SqliteDownloadHealthRepository(storage.Factory, time);
        var before = await health.SaveDownloadHealthRuleAsync(
            Rule(DownloadHealthRuleKinds.TooSlow, move: false) with { Id = "kept", PrivacyScope = DownloadHealthPrivacyScopes.Private },
            await settings.GetAsync(CancellationToken.None), CancellationToken.None);
        Assert.Null(before.Error);

        // The host starts again (every start runs the migrator): nothing about the rules or the keep-seeding settings changes.
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var after = await health.GetDownloadHealthRulesAsync(await settings.GetAsync(CancellationToken.None), CancellationToken.None);
        var kept = Assert.Single(after, rule => rule.Id == "kept");
        Assert.Equal(DownloadHealthPrivateActions.Remove, kept.PrivateAction);
        Assert.Equal(DownloadHealthPrivacyScopes.Private, kept.PrivacyScope);
        var store = new SqliteKeepSeedingStore(storage.Factory, time);
        Assert.Empty((await store.GetAsync(CancellationToken.None)).IndexerMap);
        Assert.Empty(await store.ListProblemsAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------- the rule form's own refusals

    [Theory]
    [InlineData(DownloadHealthActionPresets.RetryElsewhere, DownloadHealthAppliesTo.Both, DownloadHealthRuleKinds.NoProgress, "action must be Replace")]
    [InlineData(DownloadHealthActionPresets.JustTellMe, DownloadHealthAppliesTo.Both, DownloadHealthRuleKinds.NoProgress, "action must be Replace")]
    [InlineData(DownloadHealthActionPresets.Replace, DownloadHealthAppliesTo.Usenet, DownloadHealthRuleKinds.NoProgress, "must cover torrents")]
    [InlineData(DownloadHealthActionPresets.Replace, DownloadHealthAppliesTo.Both, DownloadHealthRuleKinds.DangerousFile, "always removed and deleted")]
    public void A_rule_that_cannot_keep_a_torrent_seeding_is_refused_with_the_reason(string preset, string appliesTo, string kind, string reason)
    {
        var rule = Rule(kind, move: true, validate: false) with { ActionPreset = preset, AppliesTo = appliesTo };

        var error = DownloadHealthRuleValidator.Validate(rule, []);

        Assert.NotNull(error);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DownloadHealthRuleKinds.NoProgress)]
    [InlineData(DownloadHealthRuleKinds.TooSlow)]
    [InlineData(DownloadHealthRuleKinds.LowPeers)]
    [InlineData(DownloadHealthRuleKinds.DeadTorrent)]
    [InlineData(DownloadHealthRuleKinds.MagnetMetadata)]
    [InlineData(DownloadHealthRuleKinds.ClientFailure)]
    public void Every_rule_kind_can_be_set_to_move_a_torrent(string kind)
        => Assert.Null(DownloadHealthRuleValidator.Validate(Rule(kind, move: true, validate: false), []));

    [Fact]
    public void An_unknown_action_is_refused()
        => Assert.NotNull(DownloadHealthRuleValidator.Validate(Rule(DownloadHealthRuleKinds.NoProgress, move: false) with { PrivateAction = "burn" }, []));

    // ------------------------------------------------------------- helpers

    private static DownloadHealthRule Rule(string kind, bool move, bool validate = true)
    {
        var rule = new DownloadHealthRule(
            Id: "rule-" + kind, Kind: kind, Enabled: true, StrikeLimit: 3, TimerMinutes: 5,
            AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
            ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue,
            Threshold: kind switch { DownloadHealthRuleKinds.TooSlow => 100, DownloadHealthRuleKinds.LowPeers => 3, _ => 0 },
            PrivateAction: move ? DownloadHealthPrivateActions.MoveAndReplace : DownloadHealthPrivateActions.Remove);
        if (validate) Assert.Null(DownloadHealthRuleValidator.Validate(rule, []));
        return rule;
    }

    /// <summary>Records every call asked of it, without touching any real client.</summary>
    private sealed class FakeClient(string protocol) : IDownloadClient
    {
        public string Protocol => protocol;

        public string? RefuseWith { get; set; }

        public Exception? ThrowOnMove { get; set; }

        public List<(string Action, string ItemId)> Actions { get; } = [];

        public List<(string ItemId, string Category, string? CurrentCategory)> Moves { get; } = [];

        public string? LastAction => Actions.LastOrDefault().Action;

        public DownloadClientTelemetryCapabilities Capabilities { get; } = new(true, true, true, true, true, true, protocol);

        public Task<DownloadClientGrabResult> GrabAsync(DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <summary>What the client lists when Deluno polls it; unset means the test never polls.</summary>
        public DownloadQueueItem? Listed { get; set; }

        public Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => Listed is null
                ? throw new NotSupportedException()
                : Task.FromResult<DownloadClientTelemetrySnapshot?>(new DownloadClientTelemetrySnapshot(
                    client.Id, client.Name, client.Protocol, null, "healthy", null, Capabilities,
                    new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [Listed], [], capturedUtc));

        public Task<DownloadClientActionResult> ExecuteActionAsync(DownloadClientItem client, string action, string queueItemId, CancellationToken cancellationToken)
        {
            Actions.Add((action, queueItemId));
            return Task.FromResult(new DownloadClientActionResult(client.Id, queueItemId, action, true, $"{action} sent to {client.Name}"));
        }

        public Task<DownloadClientActionResult> SetCategoryAsync(DownloadClientItem client, string queueItemId, string category, string? currentCategory, CancellationToken cancellationToken)
        {
            Moves.Add((queueItemId, category, currentCategory));
            if (ThrowOnMove is not null) throw ThrowOnMove;
            return Task.FromResult(RefuseWith is null
                ? new DownloadClientActionResult(client.Id, queueItemId, DownloadClientActions.SetCategory, true, "moved")
                : new DownloadClientActionResult(client.Id, queueItemId, DownloadClientActions.SetCategory, false, RefuseWith));
        }

        public Task<IReadOnlyList<DownloadClientHistoryItem>> GetHistoryAsync(DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DownloadClientCategoryCheckResult> CheckCategoryAsync(DownloadClientItem client, string category, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> GetFilesAsync(DownloadClientItem client, string queueItemId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>?>(null);

        public string NormalizeStatus(string? nativeStatus, double? progress, int? errorCode = null, string? errorMessage = null)
            => nativeStatus ?? "unknown";
    }

    private sealed class PassThroughResiliencePolicy : Deluno.Infrastructure.Resilience.IIntegrationResiliencePolicy
    {
        public async Task<Deluno.Infrastructure.Resilience.IntegrationResilienceResult<T>> ExecuteAsync<T>(
            Deluno.Infrastructure.Resilience.IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, Deluno.Infrastructure.Resilience.IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => new(await operation(cancellationToken), false, false, 1, null, null);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeDefinitionNames(string? privacy) : ITrackerDefinitionNames
    {
        public string? DisplayName(string definitionId) => "Example Tracker";

        public string? Privacy(string definitionId) => privacy;
    }

    private sealed class World : IDisposable
    {
        private readonly TestStorage _storage;
        private readonly FixedTimeProvider _time;
        private readonly DownloadClientItem _client;

        private World(
            TestStorage storage, FixedTimeProvider time, DownloadClientTelemetryService service, DownloadClientItem client,
            SqliteJobStore jobs, SqliteBlockedReleaseRepository blocklist, SqliteDownloadDispatchesRepository dispatches, string dispatchId,
            FakeClient fake, string payloadPath, SqliteKeepSeedingStore store, SqliteProcessorRepository processors,
            string libraryId, string mediaType, SqliteDownloadHealthRepository health, SqlitePlatformSettingsRepository settings)
        {
            _storage = storage;
            _time = time;
            Service = service;
            _client = client;
            Jobs = jobs;
            Blocklist = blocklist;
            Dispatches = dispatches;
            DispatchId = dispatchId;
            Client = fake;
            PayloadPath = payloadPath;
            Store = store;
            Processors = processors;
            LibraryId = libraryId;
            MediaType = mediaType;
            Health = health;
            Settings = settings;
        }

        public DownloadClientTelemetryService Service { get; }
        public SqliteJobStore Jobs { get; }
        public SqliteBlockedReleaseRepository Blocklist { get; }
        public SqliteDownloadDispatchesRepository Dispatches { get; }
        public string DispatchId { get; }
        public FakeClient Client { get; }
        public string PayloadPath { get; }
        public SqliteKeepSeedingStore Store { get; }
        public SqliteProcessorRepository Processors { get; }
        public string LibraryId { get; }
        public string MediaType { get; }
        public SqliteDownloadHealthRepository Health { get; }
        public SqlitePlatformSettingsRepository Settings { get; }
        public string ClientId => _client.Id;
        public string ClientName => _client.Name;
        public string SiteName { get; private set; } = string.Empty;

        public static async Task<World> CreateAsync(
            DownloadHealthRule? rule,
            bool? siteIsPrivate,
            string? clientCategory,
            bool? clientSaysPrivate = null,
            Func<KeepSeedingSettings, KeepSeedingSettings>? settings = null,
            string? definitionPrivacy = null,
            bool dryRun = false,
            string protocol = "qbittorrent")
        {
            var storage = NewStorage();
            var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-11T10:00:00Z"));
            var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
            await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            await new JobsSchemaInitializer(storage.Factory, migrator, NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
            var platform = new SqlitePlatformSettingsRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var names = new FakeDefinitionNames(definitionPrivacy);
            var connections = new SqliteConnectionsRepository(storage.Factory, time, TestSecretProtection.Create(storage), names);
            var libraries = new SqliteLibrariesRepository(storage.Factory, time);
            var downloadsPath = Path.Combine(storage.DataRoot, "downloads");
            var library = await libraries.CreateLibraryAsync(new CreateLibraryRequest(
                "Movies", "movies", "library", Path.Combine(storage.DataRoot, "movies"), downloadsPath, null,
                "direct-import", null, null, null, null, true, true, true, 12, 24, 25), CancellationToken.None);
            Directory.CreateDirectory(downloadsPath);
            var payloadPath = Path.Combine(downloadsPath, "Example.Movie.2026.mkv");
            await File.WriteAllTextAsync(payloadPath, "fixture payload");

            var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest(
                "External qBittorrent", protocol, "localhost", 8080, null, null, null, "deluno-movies", "deluno-tv", null, 1, true), CancellationToken.None);
            // The site: created with the privacy the test wants, so the dispatch records its id.
            var site = await connections.CreateIndexerAsync(new CreateIndexerRequest(
                "Example Tracker", "torznab", siteIsPrivate switch { true => "private", false => "public", _ => null },
                "http://tracker.test/", "key", 10, "2000", "", "movies", true,
                DefinitionId: definitionPrivacy is null ? null : "example-definition"), CancellationToken.None);

            if (definitionPrivacy is not null)
            {
                // A site added before Deluno kept the privacy: stored as "not set", with a definition that says otherwise.
                await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Platform, CancellationToken.None);
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE indexer_sources SET privacy = 'unknown' WHERE id = @id;";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = site.Id;
                command.Parameters.Add(parameter);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }

            var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, time);
            var jobs = new SqliteJobStore(storage.Factory, time, new NullRealtimeEventPublisher(), dispatches);
            var dispatchId = await jobs.RecordDownloadDispatchAsync(
                library.Id, "movies", "movie", "movie-example", Release, site.Name, client.Id, client.Name, "sent", null,
                cancellationToken: CancellationToken.None);
            await dispatches.RecordDetectionAsync(dispatchId, "queue-movie", 1024, completed: false, CancellationToken.None);

            var blocklist = new SqliteBlockedReleaseRepository(storage.Factory, time);
            var fake = new FakeClient(protocol);
            var health = new SqliteDownloadHealthRepository(storage.Factory, time);
            var processors = new SqliteProcessorRepository(storage.Factory, time, TestSecretProtection.Create(storage));
            var store = new SqliteKeepSeedingStore(storage.Factory, time);

            var current = await platform.GetAsync(CancellationToken.None);
            await platform.SaveAsync(
                PlatformSettingsPatchMerger.Apply(current, new PatchPlatformSettingsRequest(DownloadHealthDryRunEnabled: dryRun)),
                CancellationToken.None);
            if (rule is not null)
            {
                var saved = await health.SaveDownloadHealthRuleAsync(rule, await platform.GetAsync(CancellationToken.None), CancellationToken.None);
                Assert.Null(saved.Error);
            }

            var seed = new KeepSeedingSettings(
                ClientCategories: clientCategory is null ? null : new Dictionary<string, string> { [client.Id] = clientCategory });
            if (settings is not null)
            {
                seed = settings(seed);
                // Tests name the site as "site"; the real id is only known now.
                if (seed.Indexers is { } named && named.TryGetValue("site", out var siteSettings))
                {
                    seed = seed with { Indexers = new Dictionary<string, KeepSeedingIndexerSettings> { [site.Id] = siteSettings } };
                }
            }

            await store.SaveAsync(seed, CancellationToken.None);

            var connectivity = Deluno.Contracts.Connectivity.NullConnectivityMonitor.Instance;
            var service = new DownloadClientTelemetryService(
                platform, health, libraries, connections, jobs,
                new DownloadClientRegistry([fake]), time, new PassThroughResiliencePolicy(),
                jobs, dispatches, jobs, new Deluno.Recovery.Policies.DownloadHealthEvaluator(), processors, connectivity, blocklist,
                keepSeedingStore: store, trackerDefinitionNames: names);
            var world = new World(storage, time, service, client, jobs, blocklist, dispatches, dispatchId, fake, payloadPath, store, processors, library.Id, "movies", health, platform);
            world.SiteName = site.Name;
            world.ClientSaysPrivate = clientSaysPrivate;
            world.RuleId = rule?.Id;
            world.RuleKind = rule?.Kind;
            return world;
        }

        private bool? ClientSaysPrivate { get; set; }
        private string? RuleId { get; set; }
        private string? RuleKind { get; set; }

        public async Task<ProcessorHandoffItem> HandoffAsync(string status)
        {
            var handoff = await Processors.EnsureProcessorHandoffAsync(
                new CreateProcessorHandoffRequest(LibraryId, MediaType, ClientId, "queue-movie", Release, Path.Combine(Path.GetTempPath(), Release), "Weir"),
                CancellationToken.None);
            await Processors.UpdateProcessorHandoffAsync(
                handoff.Id, status, null, null,
                status == ProcessorHandoffStatuses.Failed ? "Deluno could not submit the hand-off." : null,
                CancellationToken.None);
            return (await Processors.GetProcessorHandoffAsync(handoff.Id, CancellationToken.None))!;
        }

        public async Task<DownloadHealthRemediationReport> RemediateAsync(bool struck = true, int strikes = 3, string clientHealth = "healthy")
        {
            var findings = struck && RuleId is not null && RuleKind is not null
                ? new[]
                {
                    new DownloadHealthFinding(
                        "warning", RuleKind, "Stalled", "No bytes arrived.", "Replace it.", true, true,
                        StrikeCount: strikes, RuleId: RuleId, RuleActionPreset: DownloadHealthActionPresets.Replace, RuleStrikeLimit: 3)
                }
                : Array.Empty<DownloadHealthFinding>();
            var item = new DownloadQueueItem(
                "queue-movie", _client.Id, _client.Name, _client.Protocol, "movies", "Example Movie", Release,
                "deluno-movies", DownloadQueueStatuses.Downloading, 40, 0, 60, 4_000_000_000, 1_600_000_000, 0, "Example Tracker", null,
                _time.GetUtcNow(), PayloadPath, HealthFindings: findings, IsPrivate: ClientSaysPrivate);
            var snapshot = new DownloadClientTelemetrySnapshot(_client.Id, _client.Name, _client.Protocol, null, clientHealth, null,
                new DownloadClientTelemetryCapabilities(true, true, true, true, true, true, "none"),
                new DownloadTelemetrySummary(0, 0, 0, 1, 0, 0, 0), [item], [], _time.GetUtcNow());
            return await Service.RunConfiguredHealthRemediationAsync(
                new DownloadTelemetryOverview(snapshot.Summary, [snapshot], _time.GetUtcNow()), CancellationToken.None);
        }

        /// <summary>Makes the client list the torrent, in this category, when Deluno polls it.</summary>
        public void ClientListsTheTorrentIn(string category, string? sourcePath = null)
            => Client.Listed = new DownloadQueueItem(
                "queue-movie", _client.Id, _client.Name, _client.Protocol, "movies", "Example Movie", Release,
                category, DownloadQueueStatuses.Downloading, 40, 0, 60, 4_000_000_000, 1_600_000_000, 0, "Example Tracker", null,
                _time.GetUtcNow(), sourcePath ?? PayloadPath, IsPrivate: ClientSaysPrivate);

        public string DataRoot => _storage.DataRoot;

        public void Dispose() => _storage.Dispose();
    }
}
