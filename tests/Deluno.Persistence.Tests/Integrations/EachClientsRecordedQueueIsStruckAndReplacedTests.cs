using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Integrations.Tests.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1476, #1482, #1268: the strike pass end to end on each download client's own recorded replies. The client is the real adapter
/// (qBittorrent 5.2.1, Transmission 4.1.3, Deluge 2.2.0, rTorrent 0.16.25, uTorrent 3.6, SABnzbd 5.1.3, NZBGet 26.3) answering from
/// <c>tests/fixtures/recorded</c>; its torrent is made Deluno's by a dispatch with the id and name the client gave it. The
/// telemetry pass reads it, the real rules count one strike per check as the test moves the clock, and at the limit the release is
/// blocklisted, the removal (with data) is asked of the client, the dispatch is archived, Activity says why and a replacement search
/// is queued. A recording is one state of the client, so the same reading comes back at every check; a value is changed only where a
/// test says so (uTorrent's speed, which no recording of a moving torrent has).
/// </summary>
public sealed class EachClientsRecordedQueueIsStruckAndReplacedTests
{
    private sealed record Source(IDownloadClient Client, string Username);

    private static DownloadHealthRule Rule(string kind, double threshold = 0, string appliesTo = DownloadHealthAppliesTo.Both)
        => new(
            Id: "strike-rule", Kind: kind, Enabled: true, StrikeLimit: 3, TimerMinutes: 5, AppliesTo: appliesTo,
            MinCompletionPercent: 0, MaxCompletionPercent: 100, ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: AStrikePass.Start, UpdatedUtc: AStrikePass.Start, Threshold: threshold);

    private sealed class RoutedHandler(Func<HttpRequestMessage, string, string?> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var reply = answer(request, body) ?? throw new InvalidOperationException("The recordings do not answer: " + request.Method + " " + request.RequestUri + " " + body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8) };
        }
    }

    private static readonly string EmptySabHistory = RecordedReplies.Sabnzbd("removed-with-data")
        .Body("mode=history&output", ("history.slots", new System.Text.Json.Nodes.JsonArray()));

    /// <summary>The real adapter of <paramref name="app"/>, answering from its recording of <paramref name="state"/>.</summary>
    private static Source Recorded(string app, string state, params (string Field, object? Value)[] edits)
    {
        switch (app)
        {
            case "qbittorrent":
                return new Source(new QbittorrentDownloadClient(() => RecordedQbittorrentRig.Handler(RecordedQbittorrentRig.Info(state, edits))), "deluno");

            case "transmission":
                return new Source(new TransmissionDownloadClient(new RecordedClientCalls.Factory(RecordedTransmissionRig.Handler(RecordedTransmissionRig.Reply(state, edits)))), string.Empty);

            case "deluge":
                return new Source(new DelugeDownloadClient(new RecordedClientCalls.Factory(RecordedDelugeRig.Handler(
                    RecordedReplies.Deluge(state).Body("web.update_ui", edits.Select(e => ("result.torrents.*." + e.Field, e.Value)).ToArray())))), string.Empty);

            case "rtorrent":
                return new Source(new RtorrentDownloadClient(new RecordedClientCalls.Factory(
                    RecordedReplies.Handler("rtorrent", [RecordedReplies.Rtorrent(state), RecordedReplies.Rtorrent("version")]))), string.Empty);

            case "utorrent":
            {
                var list = RecordedReplies.Utorrent(state).Body("list=1", edits.Select(e => ("torrents.0." + e.Field, e.Value)).ToArray());
                var handler = new RoutedHandler((request, _) =>
                    request.RequestUri!.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal) ? RecordedReplies.UtorrentTokenPage()
                    : request.RequestUri.Query.Contains("list=1", StringComparison.Ordinal) ? list
                    : "{}");
                return new Source(new UTorrentDownloadClient(() => handler), "user");
            }

            case "sabnzbd":
            {
                var queue = RecordedReplies.Sabnzbd(state).Body("mode=queue&output", edits.Select(e => ("queue." + e.Field, e.Value)).ToArray());
                return new Source(new SabnzbdDownloadClient(new RecordedClientCalls.Factory(new RoutedHandler((request, _) =>
                    request.RequestUri!.Query.Contains("mode=queue", StringComparison.Ordinal) ? queue
                    : request.RequestUri.Query.Contains("mode=history", StringComparison.Ordinal) ? EmptySabHistory
                    : RecordedReplies.Sabnzbd("version").Body("mode=version")))), string.Empty);
            }

            case "nzbget":
            {
                // NZBGet reports its rate in the status call, not on the job; every other value is on the job (the second, DOWNLOADING one).
                var rate = edits.Where(e => e.Field == "DownloadRate").Select(e => e.Value).FirstOrDefault() ?? 0;
                var groups = RecordedReplies.Nzbget(state).Body("listgroups", edits.Where(e => e.Field != "DownloadRate").Select(e => ("result.1." + e.Field, e.Value)).ToArray());
                return new Source(new NzbGetDownloadClient(new RecordedClientCalls.Factory(new RoutedHandler((_, body) =>
                    body.Contains("\"listgroups\"", StringComparison.Ordinal) ? groups
                    : body.Contains("\"history\"", StringComparison.Ordinal) ? RecordedReplies.Nzbget("removed-with-data").Body("history\"", ("result", new System.Text.Json.Nodes.JsonArray()))
                    : RecordedReplies.Nzbget("settings").Body("\"status\"", ("result.DownloadRate", rate))))), string.Empty);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(app));
        }
    }

    /// <summary>
    /// Deluno's category on the recorded download, in the client's own field. The recordings were made with downloads added by hand, so
    /// they carry none; Deluno only removes what it can prove carries its category, so the test says it does. rTorrent's list has no
    /// label to set (see <see cref="An_rTorrent_download_struck_out_is_removed_from_rTorrent"/>); SABnzbd's recorded one is the
    /// client's own default.
    /// </summary>
    private static (string Field, object? Value)[] Labelled(string app) => app switch
    {
        "qbittorrent" => [("category", "deluno-movies")],
        "transmission" => [("labels", new System.Text.Json.Nodes.JsonArray("deluno-movies"))],
        "deluge" => [("label", "deluno-movies")],
        "utorrent" => [("11", "deluno-movies")],
        "nzbget" => [("Category", "deluno-movies")],
        _ => []
    };

    /// <summary>The recorded torrent with nobody connected: the one value a dead-torrent test is about.</summary>
    private static (string Field, object? Value)[] NoPeers(string app) => app switch
    {
        "qbittorrent" => [("num_seeds", 0), ("num_leechs", 0)],
        "transmission" => [("peersConnected", 0)],
        "deluge" => [("num_peers", 0), ("num_seeds", 0)],
        "utorrent" => [("12", 0), ("14", 0)],
        _ => []
    };

    /// <summary>Reads the recording once, as the client would list it, to learn the id, name and category Deluno's dispatch must carry.</summary>
    private static async Task<DownloadQueueItem> TheDownloadAsync(string app, Source source)
    {
        var snapshot = await source.Client.GetSnapshotAsync(RecordedClientCalls.Client(app, source.Username), AStrikePass.Start, CancellationToken.None);
        Assert.NotNull(snapshot);
        // A recording may hold other downloads beside the one it is about (SABnzbd and NZBGet queue several): the one being downloaded.
        return snapshot.Queue.FirstOrDefault(item => item.Status == DownloadQueueStatuses.Downloading && item.SizeBytes > 0)
            ?? snapshot.Queue.FirstOrDefault(item => item.Status == DownloadQueueStatuses.Stalled)
            ?? snapshot.Queue.First();
    }

    private static Task<(AStrikePass World, RecordedClientThatKeepsItsActions Client, DownloadQueueItem Item)> StartAsync(
        string app, Source source, DownloadHealthRule rule)
        => StartAsync(app, () => source, rule);

    private static async Task<(AStrikePass World, RecordedClientThatKeepsItsActions Client, DownloadQueueItem Item)> StartAsync(
        string app, Func<Source> current, DownloadHealthRule rule)
    {
        var first = current();
        var item = await TheDownloadAsync(app, first);
        var client = new RecordedClientThatKeepsItsActions(() => current().Client);
        var world = await AStrikePass.CreateAsync(
            rule, client, item.ReleaseName, item.Id, item.Category, client.Protocol,
            username: string.IsNullOrEmpty(first.Username) ? null : first.Username, secret: "secret");
        return (world, client, item);
    }

    // ---- Too slow judges the average since the last check, on every client (#1490) --------------------------------------------------------

    private const long JobBytes = 4_000_000_000;

    /// <summary>
    /// What the recorded download shows when <paramref name="downloaded"/> of its <see cref="JobBytes"/> have arrived and the client reads
    /// <paramref name="instantKilobytesPerSecond"/> at this moment: the bytes and the speed, each in the client's own field.
    /// </summary>
    private static (string Field, object? Value)[] Reading(string app, long downloaded, double instantKilobytesPerSecond)
    {
        var rate = (long)(instantKilobytesPerSecond * 1000);
        return app switch
        {
            "qbittorrent" => [("size", JobBytes), ("downloaded", downloaded), ("dlspeed", rate)],
            "transmission" => [("totalSize", JobBytes), ("downloadedEver", downloaded), ("rateDownload", rate)],
            "deluge" => [("total_size", JobBytes), ("total_done", downloaded), ("download_payload_rate", rate)],
            "utorrent" => [("3", JobBytes), ("4", (int)(downloaded * 1000 / JobBytes)), ("9", rate)],
            "sabnzbd" => [("speed", $"{instantKilobytesPerSecond / 1000:0.###} M"), ("slots.0.mb", (JobBytes / 1_048_576).ToString()), ("slots.0.mbleft", ((JobBytes - downloaded) / 1_048_576).ToString())],
            "nzbget" => [("FileSizeHi", 0), ("FileSizeLo", JobBytes), ("RemainingSizeHi", 0), ("RemainingSizeLo", JobBytes - downloaded), ("DownloadRate", rate)],
            _ => throw new ArgumentOutOfRangeException(nameof(app))
        };
    }

    private static (string Field, object? Value)[] ReadingOf(string app, long downloaded, double instantKilobytesPerSecond)
        => Labelled(app).Concat(Reading(app, downloaded, instantKilobytesPerSecond)).ToArray();

    private static string RecordingFor(string app) => app == "utorrent" ? "stalled" : "downloading";

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    [InlineData("utorrent")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task A_recorded_download_averaging_3_4_MB_a_second_is_not_struck_for_one_slow_instant_reading(string app)
    {
        long bytes = 100_000_000;
        double instant = 3_400;
        var (world, client, _) = await StartAsync(app, () => Recorded(app, RecordingFor(app), ReadingOf(app, bytes, instant)), Rule(DownloadHealthRuleKinds.TooSlow, 1000));
        using var _w = world;
        var gain = (long)(3_400_000 * (world.Window + TimeSpan.FromSeconds(1)).TotalSeconds);

        // A first reading of 3.4 MB/s; then 3.4 MB/s of bytes arrive between checks while the client reads 200 KB/s at the check itself.
        Assert.Equal(0, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        for (var check = 2; check <= 4; check++)
        {
            bytes += gain;
            instant = 200;
            var dip = await world.CheckAsync();
            Assert.Equal(0, dip.Strikes(DownloadHealthStrikes.LowSpeed));
        }

        Assert.Empty(client.Actions);
        Assert.Empty(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Empty(await world.JobsQueuedAsync());
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    [InlineData("utorrent")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task A_recorded_download_averaging_300_KB_a_second_is_struck_at_the_third_check_with_three_strikes_and_replaced(string app)
    {
        long bytes = 100_000_000;
        var (world, client, _) = await StartAsync(app, () => Recorded(app, RecordingFor(app), ReadingOf(app, bytes, 300)), Rule(DownloadHealthRuleKinds.TooSlow, 1000));
        using var _w = world;
        var gain = (long)(300_000 * (world.Window + TimeSpan.FromSeconds(1)).TotalSeconds);

        Assert.Equal(1, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        bytes += gain;
        var second = await world.CheckAsync();
        Assert.Equal(2, second.Strikes(DownloadHealthStrikes.LowSpeed));
        Assert.Matches(@"^It averaged \d+(\.\d)? KB/s over the last 5 minutes, under your 1000 KB/s limit\.$", second.Item!.HealthFindings!.Single(f => f.Kind == DownloadHealthStrikes.LowSpeed).Evidence);
        bytes += gain;
        Assert.Equal(3, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));

        await AStrikeAssertions.ReplacedAsync(world, client.Actions, "It stayed under 1000 KB/s for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    // ---- Too slow, on every client that moves a download in a recording --------------------------------------------------------------------

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    [InlineData("sabnzbd")]
    [InlineData("nzbget")]
    public async Task A_recorded_download_under_the_speed_limit_is_struck_each_check_and_at_the_limit_removed_blocklisted_and_replaced(string app)
    {
        // NZBGet's recorded rate is at rest; a slow 20 KB/s is put on it. Every other client's recorded download is moving as recorded.
        var edits = Labelled(app).Concat(app == "nzbget" ? [("DownloadRate", (object?)20_000)] : []).ToArray();
        var source = Recorded(app, "downloading", edits);
        var item = await TheDownloadAsync(app, source);
        Assert.True(item.SpeedMbps > 0, $"the recorded download really moves");

        await AssertSlowDownloadIsReplacedAsync(app, source, Math.Ceiling(item.SpeedMbps * 1000) + 500);
    }

    [Fact]
    public async Task A_recorded_utorrent_download_at_20_KB_a_second_under_the_speed_limit_is_struck_each_check_and_at_the_limit_removed_blocklisted_and_replaced()
    {
        // uTorrent's row gives the download rate in bytes a second at index 9; the recorded row is at nothing, so a slow one is set to 20 KB/s.
        var source = Recorded("utorrent", "stalled", Labelled("utorrent").Append(("9", 20_000)).ToArray());
        Assert.Equal(20, (await TheDownloadAsync("utorrent", source)).SpeedMbps * 1000, 1);

        await AssertSlowDownloadIsReplacedAsync("utorrent", source, floor: 75);
    }

    private static async Task AssertSlowDownloadIsReplacedAsync(string app, Source source, double floor)
    {
        var (world, client, _) = await StartAsync(app, source, Rule(DownloadHealthRuleKinds.TooSlow, floor));
        using var _w = world;

        for (var check = 1; check <= 3; check++)
        {
            var struck = await world.CheckAsync();
            Assert.Equal(check, struck.Strikes(DownloadHealthStrikes.LowSpeed));
            Assert.Equal(check == 3 ? 1 : 0, struck.Report.ClientEntriesRemoved);
            Assert.Equal(check == 3 ? 1 : 0, client.Actions.Count);
        }

        await AStrikeAssertions.ReplacedAsync(world, client.Actions, $"It stayed under {floor:0.#} KB/s for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    // ---- No progress, on every torrent client's recording of a stuck torrent ------------------------------------------------------------------

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    [InlineData("utorrent")]
    public async Task A_recorded_torrent_with_nothing_arriving_is_struck_each_check_and_at_the_limit_removed_blocklisted_and_replaced(string app)
    {
        var (world, client, item) = await StartAsync(app, Recorded(app, "stalled", Labelled(app)), Rule(DownloadHealthRuleKinds.NoProgress));
        using var _w = world;
        Assert.Equal(0, item.SpeedMbps, 3);

        for (var check = 1; check <= 3; check++)
        {
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.NoThroughput));
            Assert.Equal(check == 3 ? 1 : 0, client.Actions.Count);
        }

        await AStrikeAssertions.ReplacedAsync(world, client.Actions, "It made no progress for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    // ---- A magnet that never got its metadata ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_recorded_qbittorrent_magnet_with_no_metadata_is_struck_each_check_and_at_the_limit_removed_blocklisted_and_replaced()
    {
        var (world, client, item) = await StartAsync("qbittorrent", Recorded("qbittorrent", "magnet-no-metadata", Labelled("qbittorrent")), Rule(DownloadHealthRuleKinds.MagnetMetadata));
        using var _w = world;
        Assert.True(item.IsFetchingMetadata);

        for (var check = 1; check <= 3; check++)
        {
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.MagnetMetadata));
        }

        await AStrikeAssertions.ReplacedAsync(world, client.Actions, "It was still fetching its magnet metadata for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    // ---- Dead torrent -----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    [InlineData("utorrent")]
    public async Task A_recorded_torrent_with_no_peers_past_its_grace_period_is_struck_each_check_and_at_the_limit_removed_blocklisted_and_replaced(string app)
    {
        var (world, client, item) = await StartAsync(app, Recorded(app, "stalled", Labelled(app).Concat(NoPeers(app)).ToArray()), Rule(DownloadHealthRuleKinds.DeadTorrent, threshold: 30));
        using var _w = world;
        Assert.Equal(0, item.Peers);
        Assert.True(AStrikePass.Start - item.AddedUtc > TimeSpan.FromMinutes(30), "the recorded torrent was added long before the check");

        for (var check = 1; check <= 3; check++)
        {
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.DeadTorrent));
        }

        await AStrikeAssertions.ReplacedAsync(world, client.Actions, "It had no seeds or peers left for 3 checks in a row, so Deluno stopped waiting for it and will not pick it again.");
    }

    // ---- rTorrent -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// DEFECT, found by this test: rTorrent's adapter always reports an empty category (its list call does not ask for the label), so a
    /// struck rTorrent download can never be proven to carry Deluno's category. The strikes count and the release is blocklisted and
    /// searched again, but the entry is never removed from rTorrent: Activity says "client entry was retained because its Deluno category
    /// could not be proven". Skipped until the adapter reads d.custom1 (the label Deluno sets on add).
    /// </summary>
    [Fact(Skip = "Defect: rTorrent's adapter reports no category, so a struck rTorrent download is never removed from rTorrent. See the report on #1476.")]
    public async Task An_rTorrent_download_struck_out_is_removed_from_rTorrent()
    {
        var source = Recorded("rtorrent", "downloading");
        var item = await TheDownloadAsync("rtorrent", source);
        var (world, client, _) = await StartAsync("rtorrent", source, Rule(DownloadHealthRuleKinds.TooSlow, Math.Ceiling(item.SpeedMbps * 1000) + 500));
        using var _w = world;

        for (var check = 1; check <= 3; check++)
        {
            Assert.Equal(check, (await world.CheckAsync()).Strikes(DownloadHealthStrikes.LowSpeed));
        }

        Assert.Single(await world.Blocklist.ListAsync(CancellationToken.None));
        Assert.Single(await world.JobsQueuedAsync());
        await AStrikeAssertions.ReplacedAsync(world, client.Actions, "It stayed under");
    }
}
