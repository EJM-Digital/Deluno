using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1504: every strike rule that matches a download keeps its own record and its own strikes. A record ends only on that rule's own
/// recovery evidence (bytes grew, peers are back, the average is back over the limit), never because another rule now matches the
/// same download. When several rules reach their limit the download is acted on once, by the first to reach it, and Activity names
/// that rule. The real telemetry pass, evaluator and repository run on a scripted client and a clock the test moves.
/// </summary>
public sealed class EachStrikeRuleKeepsItsOwnRecordTests
{
    private const long Frozen = 1_000_000_000L;

    private static DownloadHealthRule Rule(string kind, int strikes, double threshold = 0)
        => new(
            Id: kind, Kind: kind, Enabled: true, StrikeLimit: strikes, TimerMinutes: 5, AppliesTo: DownloadHealthAppliesTo.Both,
            MinCompletionPercent: 0, MaxCompletionPercent: 100, ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: AStrikePass.Start, UpdatedUtc: AStrikePass.Start, Threshold: threshold,
            TooSlowMaxTotalMinutes: null, IgnoreAboveSizeBytes: null, SkipWhileAlternateSpeedLimitOn: false);

    private static async Task<(AStrikePass World, ScriptedDownloadClient Client)> StartAsync(params DownloadHealthRule[] rules)
    {
        var client = new ScriptedDownloadClient("qbittorrent");
        return (await AStrikePass.CreateAsync(rules[0], client, alsoRules: rules.Skip(1).ToArray()), client);
    }

    private static void Show(AStrikePass world, ScriptedDownloadClient client, double kilobytesPerSecond, long bytes, int peers)
    {
        client.Queue.Clear();
        client.Queue.Add(new DownloadQueueItem(
            AStrikePass.ItemId, "x", "x", client.Protocol, "movies", "Example Movie", AStrikePass.Release, "deluno-movies",
            DownloadQueueStatuses.Downloading, 40, kilobytesPerSecond / 1000d, 60, 4_000_000_000, bytes, peers, "Example indexer", null,
            AStrikePass.Start - TimeSpan.FromHours(3), world.PayloadPath));
    }

    private static async Task<IReadOnlyList<string>> LinesAsync(AStrikePass world, string category)
        => (await world.Jobs.ListActivityAsync(200, null, null, CancellationToken.None))
            .Where(line => line.Category == category).Select(line => line.Message).ToArray();

    // ---- The rig, 5 Nov 14:54-14:56 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_rig_sequence_keeps_No_progress_strikes_when_Dead_torrent_appears_and_Dead_torrent_acts_at_one_of_one()
    {
        var (world, client) = await StartAsync(
            Rule(DownloadHealthRuleKinds.NoProgress, 3), Rule(DownloadHealthRuleKinds.DeadTorrent, 1), Rule(DownloadHealthRuleKinds.TooSlow, 3, 1000));
        using var _ = world;

        // 14:54:48: peers around, nothing arriving: No progress, strike 1 of 3.
        Show(world, client, 0, Frozen, peers: 3);
        var first = await world.CheckAsync();
        Assert.Equal(1, first.Strikes(DownloadHealthStrikes.NoThroughput));
        Assert.Empty(client.Actions);

        // 14:55:36: no peers left. Dead torrent strikes (1 of 1) and No progress must keep its strike, not be cleared.
        Show(world, client, 0, Frozen, peers: 0);
        await world.PollAsync(world.Window + TimeSpan.FromSeconds(1));
        Assert.Equal(2, (await world.RecordOfAsync(DownloadHealthStrikes.NoThroughput))!.StrikeCount);
        Assert.Equal(1, (await world.RecordOfAsync(DownloadHealthStrikes.DeadTorrent))!.StrikeCount);
        Assert.Empty(await LinesAsync(world, "download.health.strikes-cleared"));

        // The pass that acts finds Dead torrent at its limit, however the other rules stand, and acts once.
        var acted = await world.CheckAsync(after: TimeSpan.FromSeconds(10));
        Assert.Equal(1, acted.Report.ClientEntriesRemoved);
        Assert.Equal([(DownloadClientActions.DeleteWithData, AStrikePass.ItemId)], client.Actions);
        var said = Assert.Single(await LinesAsync(world, "download.health.remediated"));
        Assert.Contains("no seeds or peers left", said);
        Assert.Empty(await LinesAsync(world, "download.health.strikes-cleared"));
    }

    [Fact]
    public async Task A_crawl_that_stops_keeps_its_Too_slow_strikes_and_never_reads_as_recovered()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress, 3), Rule(DownloadHealthRuleKinds.TooSlow, 3, 1000));
        using var _ = world;
        var bytes = Frozen;

        // 3 KB/s on average, the rig's 14:55:54 reading: Too slow strikes.
        Show(world, client, 3, bytes, peers: 3);
        await world.CheckAsync();
        bytes += (long)(3_000 * (world.Window.TotalSeconds + 1));
        Show(world, client, 3, bytes, peers: 3);
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));

        // It stops. No progress starts its own record; Too slow's is kept, not cleared as "moving again".
        Show(world, client, 0, bytes, peers: 3);
        await world.CheckAsync();
        Assert.NotNull(await world.RecordOfAsync(DownloadHealthStrikes.NoThroughput));
        Assert.Equal(2, (await world.RecordOfAsync(DownloadHealthStrikes.LowSpeed))!.StrikeCount);
        Assert.Empty(await LinesAsync(world, "download.health.strikes-cleared"));
    }

    // ---- Every overlapping pair: both keep their strikes, the first to its limit acts once and is named ---------------------------------

    [Theory]
    [InlineData(DownloadHealthRuleKinds.NoProgress, DownloadHealthRuleKinds.DeadTorrent, 0, false, "made no progress")]
    [InlineData(DownloadHealthRuleKinds.NoProgress, DownloadHealthRuleKinds.LowPeers, 1, false, "made no progress")]
    [InlineData(DownloadHealthRuleKinds.DeadTorrent, DownloadHealthRuleKinds.NoProgress, 0, false, "no seeds or peers left")]
    [InlineData(DownloadHealthRuleKinds.LowPeers, DownloadHealthRuleKinds.NoProgress, 1, false, "fewer than 2 peers")]
    [InlineData(DownloadHealthRuleKinds.DeadTorrent, DownloadHealthRuleKinds.LowPeers, 0, false, "no seeds or peers left")]
    [InlineData(DownloadHealthRuleKinds.LowPeers, DownloadHealthRuleKinds.DeadTorrent, 0, false, "fewer than 2 peers")]
    [InlineData(DownloadHealthRuleKinds.DeadTorrent, DownloadHealthRuleKinds.TooSlow, 0, true, "no seeds or peers left")]
    [InlineData(DownloadHealthRuleKinds.TooSlow, DownloadHealthRuleKinds.DeadTorrent, 0, true, "stayed under")]
    [InlineData(DownloadHealthRuleKinds.LowPeers, DownloadHealthRuleKinds.TooSlow, 1, true, "fewer than 2 peers")]
    [InlineData(DownloadHealthRuleKinds.TooSlow, DownloadHealthRuleKinds.LowPeers, 1, true, "stayed under")]
    public async Task Two_rules_on_one_download_each_keep_their_strikes_and_the_first_to_its_limit_acts_once(
        string first, string second, int peers, bool crawling, string namedReason)
    {
        // The first rule needs 2 strikes and the second 3, so at check 2 the first is at its limit and the second is not.
        var (world, client) = await StartAsync(Rule(first, 2, ThresholdOf(first)), Rule(second, 3, ThresholdOf(second)));
        using var _ = world;
        var bytes = Frozen;

        for (var check = 1; check <= 2; check++)
        {
            if (crawling) bytes += 1_000 * check;
            Show(world, client, crawling ? 3 : 0, bytes, peers);
            await world.CheckAsync();
            if (check == 1)
            {
                Assert.Equal(1, (await world.RecordOfAsync(first))!.StrikeCount);
                Assert.Equal(1, (await world.RecordOfAsync(second))!.StrikeCount);
                Assert.Empty(client.Actions);
            }
        }

        Assert.Equal([(DownloadClientActions.DeleteWithData, AStrikePass.ItemId)], client.Actions);
        var said = Assert.Single(await LinesAsync(world, "download.health.remediated"));
        Assert.Contains(namedReason, said);
        Assert.Empty(await LinesAsync(world, "download.health.strikes-cleared"));
    }

    private static double ThresholdOf(string kind) => kind switch
    {
        DownloadHealthRuleKinds.LowPeers => 2,
        DownloadHealthRuleKinds.TooSlow => 1000,
        _ => 0
    };

    // ---- Each rule clears on its own recovery evidence, and says so ------------------------------------------------------------------

    [Fact]
    public async Task No_progress_clears_when_bytes_arrive_and_says_so_while_a_still_matching_peers_rule_keeps_its_strikes()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress, 3), Rule(DownloadHealthRuleKinds.LowPeers, 3, 2));
        using var _ = world;
        Show(world, client, 0, Frozen, peers: 1);
        await world.CheckAsync();

        Show(world, client, 40, Frozen + 5_000_000, peers: 1);
        await world.CheckAsync();
        Show(world, client, 40, Frozen + 6_000_000, peers: 1);
        await world.PollAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await world.RecordOfAsync(DownloadHealthStrikes.NoThroughput));
        Assert.Equal(2, (await world.RecordOfAsync(DownloadHealthStrikes.LowPeers))!.StrikeCount);
        var line = Assert.Single(await LinesAsync(world, "download.health.strikes-cleared"));
        Assert.Contains("new data arrived since the last check", line);
        Assert.DoesNotContain("KB/s", line);
        Assert.Empty(client.Actions);
    }

    [Theory]
    [InlineData(DownloadHealthRuleKinds.DeadTorrent, 0)]
    [InlineData(DownloadHealthRuleKinds.LowPeers, 2)]
    public async Task Dead_torrent_and_Not_enough_peers_clear_when_peers_are_back_while_No_progress_keeps_its_strikes(string kind, double threshold)
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress, 4), Rule(kind, 4, threshold));
        using var _ = world;
        Show(world, client, 0, Frozen, peers: 0);
        await world.CheckAsync();
        await world.CheckAsync();

        Show(world, client, 0, Frozen, peers: 8);
        await world.CheckAsync();
        await world.PollAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await world.RecordOfAsync(kind));
        Assert.Equal(3, (await world.RecordOfAsync(DownloadHealthStrikes.NoThroughput))!.StrikeCount);
        var line = Assert.Single(await LinesAsync(world, "download.health.strikes-cleared"));
        Assert.Contains("peers are back (8 peers)", line);
    }

    [Fact]
    public async Task Too_slow_clears_on_the_average_back_over_the_limit_and_says_the_average()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, 3, 1000), Rule(DownloadHealthRuleKinds.NoProgress, 3));
        using var _ = world;
        var bytes = Frozen;
        Show(world, client, 300, bytes, peers: 5);
        await world.CheckAsync();

        var rest = world.Window + TimeSpan.FromSeconds(1);
        bytes += (long)(3_000_000 * rest.TotalSeconds);
        Show(world, client, 3_000, bytes, peers: 5);
        await world.CheckAsync(after: rest);
        bytes += (long)(3_000_000 * rest.TotalSeconds);
        Show(world, client, 3_000, bytes, peers: 5);
        await world.PollAsync(rest);

        Assert.Null(await world.RecordOfAsync(DownloadHealthStrikes.LowSpeed));
        var line = Assert.Single(await LinesAsync(world, "download.health.strikes-cleared"));
        Assert.Contains("moving again: averaged 3.0 MB/s since the last check", line);
    }
}
