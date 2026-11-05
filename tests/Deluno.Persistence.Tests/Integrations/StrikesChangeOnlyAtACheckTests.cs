using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1501: a strike is added and cleared only at a rule's check, on that rule's own measure. A poll between two checks (the client is
/// asked every few seconds) changes nothing, so a download hovering near a limit does not flap between strike 1 and "cleared" in
/// Activity, and one that really sits under the limit on average reaches its strike limit. The first test replays the rig's 5 Nov
/// pattern (Too slow under 1000 KB/s, polls every few seconds, instant readings above the limit between slow averages).
/// </summary>
public sealed class StrikesChangeOnlyAtACheckTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(7);

    private static DownloadHealthRule Rule(string kind, double threshold = 0, int strikes = 3)
        => new(
            Id: "strike-rule", Kind: kind, Enabled: true, StrikeLimit: strikes, TimerMinutes: 5, AppliesTo: DownloadHealthAppliesTo.Both,
            MinCompletionPercent: 0, MaxCompletionPercent: 100, ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: AStrikePass.Start, UpdatedUtc: AStrikePass.Start, Threshold: threshold);

    private static async Task<(AStrikePass World, ScriptedDownloadClient Client)> StartAsync(DownloadHealthRule rule)
    {
        var client = new ScriptedDownloadClient("qbittorrent");
        return (await AStrikePass.CreateAsync(rule, client), client);
    }

    private static void Show(AStrikePass world, ScriptedDownloadClient client, double kilobytesPerSecond, long bytes)
    {
        client.Queue.Clear();
        client.Queue.Add(new DownloadQueueItem(
            AStrikePass.ItemId, "x", "x", client.Protocol, "movies", "Example Movie", AStrikePass.Release, "deluno-movies",
            DownloadQueueStatuses.Downloading, 40, kilobytesPerSecond / 1000d, 60, 4_000_000_000, bytes, 10, "Example indexer", null,
            AStrikePass.Start - TimeSpan.FromHours(3), world.PayloadPath));
    }

    private static async Task<IReadOnlyList<string>> LinesAsync(AStrikePass world, string category)
        => (await world.Jobs.ListActivityAsync(500, null, null, CancellationToken.None))
            .Where(item => item.Category == category).Select(item => item.Message).ToArray();

    /// <summary>
    /// One check interval as the rig lives it: a poll every few seconds, each reading its own speed (the pattern's values repeat), and the
    /// last poll of the interval is the check. <paramref name="intervalKilobytesPerSecond"/> is the average over the whole interval.
    /// </summary>
    private static async Task<AStrikePass.Check> IntervalAsync(
        AStrikePass world, ScriptedDownloadClient client, long startBytes, double intervalKilobytesPerSecond, double[] instantPattern)
    {
        var total = world.Window + TimeSpan.FromSeconds(1);
        var polls = (int)(total / Poll);
        var elapsed = TimeSpan.Zero;
        for (var poll = 1; poll < polls; poll++)
        {
            elapsed += Poll;
            // Bytes arrive evenly, but the client's own instant reading is whatever the pattern says.
            Show(world, client, instantPattern[poll % instantPattern.Length], startBytes + (long)(intervalKilobytesPerSecond * 1000 * elapsed.TotalSeconds));
            await world.CheckAsync(Poll);
        }

        Show(world, client, instantPattern[0], startBytes + (long)(intervalKilobytesPerSecond * 1000 * total.TotalSeconds));
        return await world.CheckAsync(total - elapsed);
    }

    [Fact]
    public async Task The_rigs_5_Nov_pattern_strikes_once_per_check_reaches_three_and_never_flaps()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 1000));
        using var _ = world;
        var bytes = 1_000_000_000L;
        Show(world, client, 1_200, bytes);
        Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));

        // Averaging 900 KB/s between checks while the client's instant reading keeps jumping over 1 MB/s.
        double[] instant = [1_200, 764, 1_100, 651, 1_000, 693];
        var struck = new List<int>();
        for (var check = 1; check <= 3; check++)
        {
            var result = await IntervalAsync(world, client, bytes, 900, instant);
            bytes += (long)(900 * 1000 * (world.Window + TimeSpan.FromSeconds(1)).TotalSeconds);
            struck.Add(result.Strikes(DownloadHealthStrikes.LowSpeed));
        }

        Assert.Equal([1, 2, 3], struck);
        var strikeLines = (await LinesAsync(world, "download.health.strike")).Reverse().ToArray();
        Assert.Equal(3, strikeLines.Length);
        Assert.Contains("strike 1 of 3", strikeLines[0]);
        Assert.Contains("strike 2 of 3", strikeLines[1]);
        Assert.Contains("strike 3 of 3", strikeLines[2]);
        Assert.Empty(await LinesAsync(world, "download.health.strikes-cleared"));
        Assert.NotEmpty(client.Actions);
        Assert.Empty(client.Queue);
    }

    [Fact]
    public async Task A_poll_between_checks_that_reads_over_the_limit_neither_clears_nor_adds_a_strike()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 1000));
        using var _ = world;
        var bytes = 1_000_000_000L;
        Show(world, client, 300, bytes);
        Assert.Equal(1, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));

        // Four polls over the next minute: a fast burst, a flat spell, a fast instant reading, nothing at all.
        foreach (var (gained, speed) in new[] { (60_000_000L, 5_000d), (0L, 0d), (0L, 4_000d), (0L, 0d) })
        {
            bytes += gained;
            Show(world, client, speed, bytes);
            var between = await world.CheckAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, between.Strikes(DownloadHealthStrikes.LowSpeed));
            Assert.Equal(1, (await world.RecordOfAsync(DownloadHealthStrikes.LowSpeed))!.StrikeCount);
        }

        Assert.Single(await LinesAsync(world, "download.health.strike"));
        Assert.Empty(await LinesAsync(world, "download.health.strikes-cleared"));
    }

    [Fact]
    public async Task A_download_that_recovers_clears_at_the_next_check_and_says_so_once()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.TooSlow, threshold: 1000));
        using var _ = world;
        var bytes = 1_000_000_000L;
        Show(world, client, 300, bytes);
        Assert.Equal(1, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        bytes += 90_000_000;
        Show(world, client, 300, bytes);
        Assert.Equal(2, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));

        // Back at 2.5 MB/s: a poll in the middle changes nothing, the next check clears.
        bytes += 400_000_000;
        Show(world, client, 2_500, bytes);
        Assert.Equal(2, (await world.CheckAsync(TimeSpan.FromSeconds(20))).Strikes(DownloadHealthStrikes.LowSpeed));
        Assert.Empty(await LinesAsync(world, "download.health.strikes-cleared"));

        bytes += 350_000_000;
        Show(world, client, 2_500, bytes);
        Assert.Equal(0, (await world.CheckAsync(world.Window)).Strikes(DownloadHealthStrikes.LowSpeed));
        Assert.Null(await world.RecordOfAsync(DownloadHealthStrikes.LowSpeed));
        Assert.Single(await LinesAsync(world, "download.health.strikes-cleared"));
        Assert.Empty(client.Actions);
    }

    [Fact]
    public async Task No_progress_is_cleared_at_a_check_not_by_a_burst_seen_at_a_poll_between_two()
    {
        var (world, client) = await StartAsync(Rule(DownloadHealthRuleKinds.NoProgress));
        using var _ = world;
        var bytes = 1_000_000L;
        Show(world, client, 0, bytes);
        Assert.Equal(1, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.NoThroughput));

        bytes += 5_000_000;
        Show(world, client, 800, bytes);
        var between = await world.CheckAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, between.Strikes(DownloadHealthStrikes.NoThroughput));
        Assert.Empty(await LinesAsync(world, "download.health.strikes-cleared"));

        bytes += 5_000_000;
        Show(world, client, 800, bytes);
        Assert.Equal(0, (await world.CheckAsync(world.Window)).Strikes(DownloadHealthStrikes.NoThroughput));
        Assert.Single(await LinesAsync(world, "download.health.strikes-cleared"));
    }
}
