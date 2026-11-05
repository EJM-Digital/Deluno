using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;
using Deluno.Worker.Jobs;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1476, #1482, #1268: every strike rule is proven from what the client reports to the release being replaced, in milliseconds. The
/// real telemetry pass reads a scripted client, the real evaluator and the real download-health repository (on a temporary
/// database) count one strike per check while the test moves the clock, and at the limit the release is blocklisted, the client entry
/// removed with its files, the dispatch archived, Activity says why in plain words and a replacement search is queued for the very
/// dispatch it replaces. No test waits: a check is one call and the clock is moved by the test.
/// </summary>
public sealed class EveryStrikeRuleStrikesAndReplacesADownloadTests
{
    private const string Torrent = "qbittorrent";

    private static DownloadHealthRule Rule(
        string kind,
        double threshold = 0,
        int strikes = 3,
        int fromPercent = 0,
        int toPercent = 100,
        string preset = DownloadHealthActionPresets.Replace,
        int? maxTotalMinutes = null,
        long? ignoreAboveBytes = null,
        bool skipWhileAlternateSpeed = false,
        string appliesTo = DownloadHealthAppliesTo.Both)
        => new(
            Id: "strike-rule", Kind: kind, Enabled: true, StrikeLimit: strikes, TimerMinutes: 5, AppliesTo: appliesTo,
            MinCompletionPercent: fromPercent, MaxCompletionPercent: toPercent, ResetStrikesOnProgress: true, ActionPreset: preset,
            CreatedUtc: AStrikePass.Start, UpdatedUtc: AStrikePass.Start, Threshold: threshold,
            TooSlowMaxTotalMinutes: maxTotalMinutes, IgnoreAboveSizeBytes: ignoreAboveBytes, SkipWhileAlternateSpeedLimitOn: skipWhileAlternateSpeed);

    private static async Task<(AStrikePass World, ScriptedDownloadClient Client)> StartAsync(DownloadHealthRule rule, string protocol = Torrent)
    {
        var client = new ScriptedDownloadClient(protocol);
        return (await AStrikePass.CreateAsync(rule, client, protocol: protocol), client);
    }

    /// <summary>What the client lists at this check: the one download, with the speed, bytes and peers it reports.</summary>
    private static void Show(
        AStrikePass world, ScriptedDownloadClient client, double kilobytesPerSecond, long bytes, int peers = 10,
        string status = DownloadQueueStatuses.Downloading, double progress = 40, long size = 4_000_000_000,
        TimeSpan? age = null, bool fetchingMetadata = false)
    {
        client.Queue.Clear();
        client.Queue.Add(new DownloadQueueItem(
            AStrikePass.ItemId, "x", "x", client.Protocol, "movies", "Example Movie", AStrikePass.Release, "deluno-movies", status,
            progress, kilobytesPerSecond / 1000d, 60, size, bytes, peers, "Example indexer", null,
            AStrikePass.Start - (age ?? TimeSpan.FromHours(3)), world.PayloadPath, IsFetchingMetadata: fetchingMetadata));
    }

    /// <summary>Bytes that grow by 5 MB at every check, so a crawl really is arriving.</summary>
    private static long Growing(int check) => 1_000_000L + check * 5_000_000L;

    private static async Task AssertNothingTouchedAsync(AStrikePass world, ScriptedDownloadClient client)
    {
        Assert.Empty(client.Actions);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.True(File.Exists(world.PayloadPath));
        Assert.Empty(await world.JobsQueuedAsync());
        Assert.NotNull(await world.Dispatches.GetDispatchAsync(world.DispatchId, CancellationToken.None));
    }

    /// <summary>Everything that must have happened once a download reached its limit under a "replace it" rule.</summary>
    private static async Task AssertReplacedAsync(AStrikePass world, ScriptedDownloadClient client, string says)
    {
        // Removed from the client together with its data, once.
        Assert.Equal([(DownloadClientActions.DeleteWithData, AStrikePass.ItemId)], client.Actions);
        Assert.Empty(client.Queue);
        Assert.False(File.Exists(world.PayloadPath), "its files are deleted");

        // The release is refused, with the rule's own reason, so search never offers it again.
        var refused = Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Equal(BlockedReleaseStates.Refused, refused.State);
        Assert.Equal(AStrikePass.Release, refused.ReleaseName);
        Assert.Equal(AStrikePass.EntityId, refused.EntityId);
        Assert.StartsWith(says, refused.Reason, StringComparison.Ordinal);

        // The dispatch is closed, so the title no longer reads "downloading".
        var dispatch = await world.Dispatches.GetDispatchIncludingArchivedAsync(world.DispatchId, CancellationToken.None);
        Assert.Equal("archived", dispatch!.Status);
        Assert.Empty(await world.Jobs.ListDownloadsInFlightAsync(100, CancellationToken.None));

        // Activity says why, in plain words, and what Deluno did about it.
        var activity = Assert.Single(
            await world.Jobs.ListActivityAsync(20, null, null, CancellationToken.None),
            item => item.Category == "download.health.remediated");
        Assert.StartsWith(AStrikePass.Release, activity.Message, StringComparison.Ordinal);
        Assert.Contains(says, activity.Message, StringComparison.Ordinal);
        Assert.Contains($"Deluno blocked this release, is looking for another copy, removed it from {world.Client.Name} and deleted the leftover files.", activity.Message);

        await AssertReplacementQueuedAsync(world);
    }

    /// <summary>The replacement search is for this title, is linked to the dispatch it replaces, and ignores that dispatch (#1482).</summary>
    private static async Task AssertReplacementQueuedAsync(AStrikePass world)
    {
        var job = Assert.Single(await world.JobsQueuedAsync());
        Assert.Equal(LibrarySearchJobTypes.For("movies"), job.JobType);
        Assert.Equal("download_dispatch", job.RelatedEntityType);
        Assert.Equal(world.DispatchId, job.RelatedEntityId);
        Assert.Equal(world.DispatchId, SearchExecutionSupport.ReplacedDispatchId(job));
        using var payload = JsonDocument.Parse(job.PayloadJson!);
        Assert.Equal("health-replacement", payload.RootElement.GetProperty("triggeredBy").GetString());
        Assert.True(SearchExecutionSupport.IsReplacementSearch("health-replacement"));
        Assert.Equal(AStrikePass.EntityId, payload.RootElement.GetProperty("targetEntityId").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("maxItems").GetInt32());

        // When the search was queued the replaced download was still in the client and its dispatch still open: the search can
        // start before the removal and the archive (#1482). It must not count that download as on its way, or it would send
        // nothing; with the old behaviour (not ignoring it) it would have been held back by it.
        var inFlight = Assert.Single(world.Watcher.InFlightWhenQueued!);
        Assert.Equal(world.DispatchId, inFlight.DispatchId);
        Assert.Null(world.Watcher.HeldBackIgnoringTheReplacedDownload);
        Assert.NotNull(world.Watcher.HeldBackNotIgnoringIt);
    }

    // ---- Too slow --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_under_the_speed_limit_is_struck_once_per_check_and_at_the_limit_is_blocklisted_removed_archived_and_searched_again()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75));
        using var _ = world;

        for (var check = 1; check <= 2; check++)
        {
            Show(world, client, kilobytesPerSecond: 50, bytes: Growing(check));
            var struck = await world.CheckAsync();
            Assert.Equal(check, struck.Strikes(DownloadHealthStrikes.LowSpeed));
            Assert.Equal(0, struck.Report.ClientEntriesRemoved);
            await AssertNothingTouchedAsync(world, client);
        }

        Show(world, client, kilobytesPerSecond: 50, bytes: Growing(3));
        var last = await world.CheckAsync();

        Assert.Equal(3, last.Strikes(DownloadHealthStrikes.LowSpeed));
        Assert.Equal(1, last.Report.Evaluated);
        Assert.Equal(1, last.Report.ReleasesRefused);
        Assert.Equal(1, last.Report.ClientEntriesRemoved);
        Assert.Equal(1, last.Report.PayloadsPurged);
        Assert.Equal(1, last.Report.ReplacementSearchesQueued);
        await AssertReplacedAsync(world, client, "It stayed under 75 KB/s for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task One_strike_is_enough_when_the_rule_says_one()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, strikes: 1));
        using var _ = world;

        Show(world, client, kilobytesPerSecond: 50, bytes: Growing(1));
        var only = await world.CheckAsync();

        Assert.Equal(1, only.Strikes(DownloadHealthStrikes.LowSpeed));
        await AssertReplacedAsync(world, client, "It stayed under 75 KB/s for 1 check, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task Several_strikes_are_counted_one_per_check_up_to_the_limit_and_not_before()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, strikes: 6));
        using var _ = world;

        for (var check = 1; check <= 5; check++)
        {
            Show(world, client, kilobytesPerSecond: 50, bytes: Growing(check));
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
            await AssertNothingTouchedAsync(world, client);
        }

        Show(world, client, kilobytesPerSecond: 50, bytes: Growing(6));
        var sixth = await world.CheckAsync();

        Assert.Equal(6, sixth.Strikes(DownloadHealthStrikes.LowSpeed));
        await AssertReplacedAsync(world, client, "It stayed under 75 KB/s for 6 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task Two_checks_closer_together_than_the_check_interval_add_one_strike_not_two()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75));
        using var _ = world;

        Show(world, client, 50, Growing(1));
        Assert.Equal(1, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        Show(world, client, 50, Growing(2));
        Assert.Equal(1, (await world.CheckAsync(after: TimeSpan.FromMinutes(1))).Strikes(DownloadHealthStrikes.LowSpeed));
        Show(world, client, 50, Growing(3));
        Assert.Equal(2, (await world.CheckAsync(after: world.Window)).Strikes(DownloadHealthStrikes.LowSpeed));
    }

    [Fact]
    public async Task A_download_that_speeds_up_before_the_limit_clears_its_strikes_and_starts_again_from_one()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75));
        using var _ = world;

        Show(world, client, 50, Growing(1));
        await world.CheckAsync();
        Show(world, client, 50, Growing(2));
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));

        // Back over the limit: the problem is over and the strikes with it.
        Show(world, client, 500, Growing(3));
        var recovered = await world.CheckAsync();
        Assert.Equal(0, recovered.Strikes(DownloadHealthStrikes.LowSpeed));
        Assert.Null(await world.RecordOfAsync(DownloadHealthStrikes.LowSpeed));
        await AssertNothingTouchedAsync(world, client);

        // Slow again later: a new spell, so it takes the full count again.
        Show(world, client, 50, Growing(4));
        Assert.Equal(1, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        Show(world, client, 50, Growing(5));
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        await AssertNothingTouchedAsync(world, client);
    }

    [Fact]
    public async Task A_fast_download_is_never_struck_however_many_checks_pass()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75));
        using var _ = world;

        for (var check = 1; check <= 6; check++)
        {
            Show(world, client, 5_000, Growing(check));
            Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        }

        await AssertNothingTouchedAsync(world, client);
    }

    // ---- Maximum total time ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_past_the_maximum_total_time_is_struck_even_while_its_speed_is_fine()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, maxTotalMinutes: 60));
        using var _ = world;

        for (var check = 1; check <= 2; check++)
        {
            Show(world, client, kilobytesPerSecond: 5_000, bytes: Growing(check), age: TimeSpan.FromMinutes(61));
            var struck = await world.CheckAsync();
            Assert.Equal(check, struck.Strikes(DownloadHealthStrikes.LowSpeed));
            Assert.Contains("maximum total time", struck.Item!.HealthFindings!.Single(f => f.Kind == DownloadHealthStrikes.LowSpeed).Evidence);
            await AssertNothingTouchedAsync(world, client);
        }

        Show(world, client, kilobytesPerSecond: 5_000, bytes: Growing(3), age: TimeSpan.FromMinutes(61));
        var last = await world.CheckAsync();

        Assert.Equal(3, last.Strikes(DownloadHealthStrikes.LowSpeed));
        await AssertReplacedAsync(world, client, "It was still downloading after an hour for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task A_download_that_has_not_yet_taken_the_maximum_total_time_is_not_struck_until_the_clock_passes_it()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, maxTotalMinutes: 60));
        using var _ = world;

        // 55 minutes old at the first check; the second check is 5 minutes and a second later, past the 60.
        Show(world, client, 5_000, Growing(1), age: TimeSpan.FromMinutes(55));
        Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        Show(world, client, 5_000, Growing(2), age: TimeSpan.FromMinutes(55));
        Assert.Equal(1, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
    }

    // ---- No progress -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_with_no_bytes_arriving_is_struck_each_check_through_the_stall_flip_and_replaced_at_the_limit()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress));
        using var _ = world;

        for (var check = 1; check <= 2; check++)
        {
            Show(world, client, kilobytesPerSecond: 0, bytes: 1_000_000);
            var struck = await world.CheckAsync();
            Assert.Equal(check, struck.Strikes(DownloadHealthStrikes.NoThroughput));
            await AssertNothingTouchedAsync(world, client);
        }

        Show(world, client, kilobytesPerSecond: 0, bytes: 1_000_000);
        var last = await world.CheckAsync();

        // Two checks without a byte turned the client's "downloading" into Deluno's "stalled"; the rule kept counting through it.
        Assert.Equal(DownloadQueueStatuses.Stalled, last.Item!.Status);
        Assert.Equal(3, last.Strikes(DownloadHealthStrikes.NoThroughput));
        await AssertReplacedAsync(world, client, "It made no progress for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task A_download_whose_client_shows_a_speed_but_no_new_bytes_is_still_struck_for_no_progress()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress));
        using var _ = world;

        // Deluge reads 0.000057 MB/s on a stuck torrent, but its bytes never move. The first check has no earlier reading to
        // compare with, so it goes by the speed and sees a moving download; every check after it compares the bytes.
        Show(world, client, kilobytesPerSecond: 0.057, bytes: 1_000_000);
        Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.NoThroughput));
        for (var check = 1; check <= 3; check++)
        {
            Show(world, client, kilobytesPerSecond: 0.057, bytes: 1_000_000);
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.NoThroughput));
        }

        await AssertReplacedAsync(world, client, "It made no progress for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task A_stalled_download_that_gets_bytes_again_clears_its_strikes()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress));
        using var _ = world;

        Show(world, client, 0, 1_000_000);
        await world.CheckAsync();
        Show(world, client, 0, 1_000_000);
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.NoThroughput));

        Show(world, client, kilobytesPerSecond: 20, bytes: 9_000_000);
        var moving = await world.CheckAsync();

        Assert.Equal(0, moving.Strikes(DownloadHealthStrikes.NoThroughput));
        Assert.Null(await world.RecordOfAsync(DownloadHealthStrikes.NoThroughput));
        await AssertNothingTouchedAsync(world, client);
    }

    [Fact]
    public async Task A_crawl_that_keeps_adding_bytes_is_never_struck_for_no_progress()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress));
        using var _ = world;

        for (var check = 1; check <= 5; check++)
        {
            Show(world, client, 1, Growing(check));
            Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.NoThroughput));
        }

        await AssertNothingTouchedAsync(world, client);
    }

    // ---- Dead torrent and not enough peers ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_torrent_with_no_peers_past_its_grace_period_is_struck_each_check_and_replaced_at_the_limit()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.DeadTorrent, threshold: 30));
        using var _ = world;

        for (var check = 1; check <= 2; check++)
        {
            Show(world, client, 0, 1_000_000, peers: 0);
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.DeadTorrent));
            await AssertNothingTouchedAsync(world, client);
        }

        Show(world, client, 0, 1_000_000, peers: 0);
        Assert.Equal(3, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.DeadTorrent));
        await AssertReplacedAsync(world, client, "It had no seeds or peers left for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task A_new_torrent_with_no_peers_is_given_its_grace_period_before_the_first_strike()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.DeadTorrent, threshold: 30));
        using var _ = world;

        // 20 minutes old: inside the 30-minute grace period at the first check and for the next two (5 minutes apart).
        for (var check = 1; check <= 2; check++)
        {
            Show(world, client, 0, 1_000_000, peers: 0, age: TimeSpan.FromMinutes(20));
            Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.DeadTorrent));
        }

        // Three more intervals and it is past 30 minutes.
        Show(world, client, 0, 1_000_000, peers: 0, age: TimeSpan.FromMinutes(20));
        await world.CheckAsync(after: TimeSpan.FromMinutes(10));
        Assert.Equal(1, (await world.RecordOfAsync(DownloadHealthStrikes.DeadTorrent))!.StrikeCount);
        await AssertNothingTouchedAsync(world, client);
    }

    [Fact]
    public async Task A_torrent_that_finds_a_peer_before_the_limit_is_not_dead_and_loses_its_strikes()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.DeadTorrent, threshold: 30));
        using var _ = world;

        Show(world, client, 0, 1_000_000, peers: 0);
        await world.CheckAsync();
        Show(world, client, 0, 1_000_000, peers: 0);
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.DeadTorrent));

        Show(world, client, 800, 9_000_000, peers: 4);
        Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.DeadTorrent));
        Assert.Null(await world.RecordOfAsync(DownloadHealthStrikes.DeadTorrent));
        await AssertNothingTouchedAsync(world, client);
    }

    [Fact]
    public async Task A_download_with_fewer_peers_than_the_floor_is_struck_each_check_through_the_stall_flip_and_replaced_at_the_limit()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.LowPeers, threshold: 3));
        using var _ = world;

        for (var check = 1; check <= 2; check++)
        {
            Show(world, client, 0, 1_000_000, peers: 1);
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowPeers));
            await AssertNothingTouchedAsync(world, client);
        }

        Show(world, client, 0, 1_000_000, peers: 1);
        var last = await world.CheckAsync();
        Assert.Equal(DownloadQueueStatuses.Stalled, last.Item!.Status);
        Assert.Equal(3, last.Strikes(DownloadHealthStrikes.LowPeers));
        await AssertReplacedAsync(world, client, "It had fewer than 3 peers for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task A_download_whose_peers_come_back_above_the_floor_clears_its_strikes()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.LowPeers, threshold: 3));
        using var _ = world;

        Show(world, client, 100, Growing(1), peers: 1);
        await world.CheckAsync();
        Show(world, client, 100, Growing(2), peers: 1);
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowPeers));

        Show(world, client, 100, Growing(3), peers: 12);
        Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowPeers));
        Assert.Null(await world.RecordOfAsync(DownloadHealthStrikes.LowPeers));
        await AssertNothingTouchedAsync(world, client);
    }

    // ---- A magnet stuck fetching its metadata -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_magnet_still_fetching_its_metadata_is_struck_each_check_and_replaced_at_the_limit()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.MagnetMetadata));
        using var _ = world;

        for (var check = 1; check <= 2; check++)
        {
            Show(world, client, 0, 0, peers: 0, progress: 0, size: 0, fetchingMetadata: true);
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.MagnetMetadata));
            await AssertNothingTouchedAsync(world, client);
        }

        Show(world, client, 0, 0, peers: 0, progress: 0, size: 0, fetchingMetadata: true);
        Assert.Equal(3, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.MagnetMetadata));
        await AssertReplacedAsync(world, client, "It was still fetching its magnet metadata for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Fact]
    public async Task A_magnet_that_gets_its_metadata_before_the_limit_clears_its_strikes()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.MagnetMetadata));
        using var _ = world;

        Show(world, client, 0, 0, peers: 0, progress: 0, size: 0, fetchingMetadata: true);
        await world.CheckAsync();
        Show(world, client, 0, 0, peers: 0, progress: 0, size: 0, fetchingMetadata: true);
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.MagnetMetadata));

        Show(world, client, 900, Growing(1), peers: 6, progress: 1);
        Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.MagnetMetadata));
        Assert.Null(await world.RecordOfAsync(DownloadHealthStrikes.MagnetMetadata));
        await AssertNothingTouchedAsync(world, client);
    }

    // ---- The rule's scope ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(40, 50, 100, false)]   // below "from"
    [InlineData(50, 50, 100, true)]    // exactly "from"
    [InlineData(80, 50, 100, true)]
    [InlineData(100, 0, 99, false)]    // above "up to"
    [InlineData(99, 0, 99, true)]      // exactly "up to"
    public async Task A_rule_strikes_only_a_download_inside_its_percent_complete_bounds(double progress, int from, int to, bool struck)
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, fromPercent: from, toPercent: to));
        using var _ = world;

        Show(world, client, 50, Growing(1), progress: progress);

        Assert.Equal(struck ? 1 : 0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        Assert.Equal(struck, await world.RecordOfAsync(DownloadHealthStrikes.LowSpeed) is not null);
    }

    [Fact]
    public async Task A_download_that_moves_out_of_the_bounds_stops_being_struck_and_never_reaches_the_limit()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, toPercent: 60));
        using var _ = world;

        Show(world, client, 50, Growing(1), progress: 55);
        await world.CheckAsync();
        Show(world, client, 50, Growing(2), progress: 58);
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));

        for (var check = 3; check <= 5; check++)
        {
            Show(world, client, 50, Growing(check), progress: 61);
            Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        }

        await AssertNothingTouchedAsync(world, client);
    }

    [Theory]
    [InlineData(4_000_000_000, 1_000_000_000, false)]  // bigger than "ignore above this size"
    [InlineData(1_000_000_000, 1_000_000_000, true)]   // exactly the size: still watched
    [InlineData(500_000_000, 1_000_000_000, true)]
    [InlineData(4_000_000_000, 0, true)]               // 0 means no size ceiling
    public async Task A_slow_download_above_the_ignore_size_is_never_struck_by_the_too_slow_rule(long size, long ignoreAbove, bool struck)
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, ignoreAboveBytes: ignoreAbove));
        using var _ = world;

        Show(world, client, 50, Growing(1), size: size);

        Assert.Equal(struck ? 1 : 0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
    }

    [Theory]
    [InlineData(true, true, 0)]     // throttled on purpose, and the rule stands down
    [InlineData(true, false, 1)]    // the rule does not stand down
    [InlineData(false, true, 1)]    // not throttled
    [InlineData(null, true, 1)]     // the client does not say (Deluge, Usenet clients): never hides a slow download
    public async Task The_clients_alternate_speed_limit_makes_the_too_slow_rule_stand_down_only_when_asked(bool? throttled, bool skipWhileThrottled, int strikes)
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, skipWhileAlternateSpeed: skipWhileThrottled));
        using var _ = world;
        client.AlternateSpeedLimit = throttled;

        Show(world, client, 50, Growing(1));

        Assert.Equal(strikes, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
    }

    // ---- What the rule's action does -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_just_tell_me_rule_counts_to_the_limit_and_then_touches_nothing()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, preset: DownloadHealthActionPresets.JustTellMe));
        using var _ = world;

        // A "just tell me" rule is not an acting rule, so the pass never even looks for something to remove.
        for (var check = 1; check <= 4; check++)
        {
            Show(world, client, 50, Growing(check));
            var struck = await world.CheckAsync();
            Assert.Equal(Math.Min(check, 3), struck.Strikes(DownloadHealthStrikes.LowSpeed));
            Assert.Equal(0, struck.Report.Evaluated);
        }

        await AssertNothingTouchedAsync(world, client);
    }

    [Fact]
    public async Task A_retry_elsewhere_rule_refuses_the_release_and_searches_again_but_leaves_the_download_and_the_replacement_ignores_it()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, strikes: 2, preset: DownloadHealthActionPresets.RetryElsewhere));
        using var _ = world;

        Show(world, client, 50, Growing(1));
        await world.CheckAsync();
        Show(world, client, 50, Growing(2));
        var last = await world.CheckAsync();

        Assert.Equal(2, last.Strikes(DownloadHealthStrikes.LowSpeed));
        Assert.Equal(1, last.Report.ReplacementSearchesQueued);
        Assert.Equal(0, last.Report.ClientEntriesRemoved);
        Assert.Empty(client.Actions);
        Assert.True(File.Exists(world.PayloadPath));
        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        await AssertReplacementQueuedAsync(world);
    }

    // ---- Usenet ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task A_usenet_download_under_the_speed_limit_is_struck_each_check_and_replaced_at_the_limit(string protocol)
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 500), protocol);
        using var _ = world;

        for (var check = 1; check <= 3; check++)
        {
            Show(world, client, kilobytesPerSecond: 200, bytes: Growing(check), peers: 0);
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        }

        await AssertReplacedAsync(world, client, "It stayed under 500 KB/s for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Theory]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task A_usenet_download_with_no_bytes_arriving_is_struck_each_check_and_replaced_at_the_limit(string protocol)
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress), protocol);
        using var _ = world;

        for (var check = 1; check <= 3; check++)
        {
            Show(world, client, kilobytesPerSecond: 0, bytes: 1_000_000, peers: 0);
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.NoThroughput));
        }

        await AssertReplacedAsync(world, client, "It made no progress for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    [Theory]
    [InlineData(DownloadHealthRuleKinds.LowPeers, 3)]
    [InlineData(DownloadHealthRuleKinds.DeadTorrent, 30)]
    [InlineData(DownloadHealthRuleKinds.MagnetMetadata, 0)]
    public async Task A_torrent_only_rule_never_strikes_a_usenet_download_which_has_no_peers(string kind, double threshold)
    {
        var (world, client) = await StartAsync(Rule(kind, threshold: threshold), "sabnzbd");
        using var _ = world;

        for (var check = 1; check <= 4; check++)
        {
            Show(world, client, 0, 1_000_000, peers: 0, fetchingMetadata: kind == DownloadHealthRuleKinds.MagnetMetadata);
            Assert.Equal(0, ((await world.CheckAsync()).Item!.HealthFindings ?? []).Count(f => DownloadHealthStrikes.Counts(f.Kind) && f.Kind != DownloadHealthStrikes.ClientStalled));
        }

        await AssertNothingTouchedAsync(world, client);
    }
}
