using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1329: the strike rules judge what uTorrent 3.6 really reported. Each torrent comes from the recorded list (tests/fixtures/recorded/utorrent)
/// through the real adapter, is made into the snapshot the evaluator is handed, and is judged by the real <see cref="DownloadHealthEvaluator"/>;
/// the files are the recorded getfiles reply. A value is changed in a recorded row only where a test says what it is about (a speed, an error bit).
/// </summary>
public sealed class UTorrentStrikeRulesOnItsRecordedRepliesTests
{
    private sealed class UTorrent(string list, string? files = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = uri.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal) ? RecordedReplies.UtorrentTokenPage()
                : uri.Query.Contains("list=1", StringComparison.Ordinal) ? list
                : uri.Query.Contains("action=getfiles", StringComparison.Ordinal) && files is not null ? files
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }

    private static DownloadClientItem Client()
        => new("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, "user", "secret", "http://localhost:8081/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static async Task<DownloadQueueItem> Poll(string list, int row = 0)
    {
        var snapshot = await new UTorrentDownloadClient(() => new UTorrent(list)).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);
        return snapshot!.Queue[row];
    }

    private static RecoveryQueueSnapshot Judged(DownloadQueueItem item)
        => new(
            Status: item.Status, ErrorMessage: item.ErrorMessage, SourcePath: item.SourcePath, SpeedMbps: item.SpeedMbps, AddedUtc: item.AddedUtc,
            EtaSeconds: item.EtaSeconds, ReleaseName: item.ReleaseName, Peers: item.Peers, IsSetupProblem: item.IsSetupProblem, ProgressPercent: item.Progress,
            IsUsenetProtocol: false, IsPrivate: item.IsPrivate, TrackerStatus: item.TrackerStatus, Files: null, SizeBytes: item.SizeBytes,
            IsFetchingMetadata: item.IsFetchingMetadata, Availability: item.Availability);

    private static DownloadHealthRule Rule(string kind, double threshold = 0)
    {
        var at = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        return new(Id: kind + "-rule", Kind: kind, Enabled: true, StrikeLimit: 3, TimerMinutes: 5, AppliesTo: DownloadHealthAppliesTo.Torrents,
            MinCompletionPercent: 0, MaxCompletionPercent: 100, ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: at, UpdatedUtc: at, Threshold: threshold);
    }

    private static IReadOnlyList<RecoveryHealthFinding> Judge(DownloadQueueItem item, TimeSpan after, params DownloadHealthRule[] rules)
        => new DownloadHealthEvaluator().Evaluate(Judged(item), item.AddedUtc + after, rules);

    // ---- Too slow ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_under_the_speed_floor_is_struck_by_the_too_slow_rule_and_one_over_it_is_not()
    {
        // uTorrent's row gives the download rate in bytes a second at index 9; the recorded row is at nothing, so a moving one is set to 5 MB/s.
        var fast = await Poll(RecordedReplies.Utorrent("stalled").Body("list=1", ("torrents.0.9", 5_000_000)));
        var kilobytesPerSecond = fast.SpeedMbps * 1000;

        Assert.Equal(5000, kilobytesPerSecond, 1);
        var slow = Assert.Single(Judge(fast, TimeSpan.FromMinutes(10), Rule(DownloadHealthRuleKinds.TooSlow, kilobytesPerSecond + 500)), f => f.Kind == DownloadHealthStrikes.LowSpeed);
        Assert.Contains("under the limit", slow.Evidence);
        Assert.DoesNotContain(Judge(fast, TimeSpan.FromMinutes(10), Rule(DownloadHealthRuleKinds.TooSlow, kilobytesPerSecond - 500)), f => f.Kind == DownloadHealthStrikes.LowSpeed);
    }

    // The same rule on rTorrent 0.16.25's recorded download (the register row names the two clients together).
    [Fact]
    public async Task An_rTorrent_download_under_the_speed_floor_is_struck_by_the_too_slow_rule_and_one_over_it_is_not()
    {
        var handler = RecordedReplies.Handler("rtorrent", [RecordedReplies.Rtorrent("downloading"), RecordedReplies.Rtorrent("version")]);
        var rtorrent = new DownloadClientItem("rtorrent-1", "rTorrent", "rtorrent", "localhost", 8080, "user", "secret", "http://localhost:8080/RPC2", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var snapshot = await new RtorrentDownloadClient(new HandlerFactory(handler)).GetSnapshotAsync(rtorrent, DateTimeOffset.UtcNow, CancellationToken.None);
        var item = Assert.Single(snapshot!.Queue);
        var kilobytesPerSecond = item.SpeedMbps * 1000;
        Assert.True(kilobytesPerSecond > 0, "the recorded download really moves");

        var slow = Assert.Single(Judge(item, TimeSpan.FromMinutes(10), Rule(DownloadHealthRuleKinds.TooSlow, kilobytesPerSecond * 2)), f => f.Kind == DownloadHealthStrikes.LowSpeed);
        Assert.Contains("under the limit", slow.Evidence);
        Assert.DoesNotContain(Judge(item, TimeSpan.FromMinutes(10), Rule(DownloadHealthRuleKinds.TooSlow, kilobytesPerSecond / 2)), f => f.Kind == DownloadHealthStrikes.LowSpeed);
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // ---- Dead torrent, unregistered, paused ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_torrent_with_no_peers_is_dead_only_after_its_grace_period()
    {
        var item = await Poll(RecordedReplies.Utorrent("stalled").Body("list=1"));
        var rule = Rule(DownloadHealthRuleKinds.DeadTorrent, 30);

        Assert.Equal(0, item.Peers);
        Assert.DoesNotContain(Judge(item, TimeSpan.FromMinutes(10), rule), f => f.Kind == DownloadHealthStrikes.DeadTorrent);
        var dead = Assert.Single(Judge(item, TimeSpan.FromMinutes(45), rule), f => f.Kind == DownloadHealthStrikes.DeadTorrent);
        Assert.Contains("no seeds or peers", dead.Summary);
    }

    [Fact]
    public async Task A_torrent_uTorrent_has_found_peers_for_is_never_called_dead()
    {
        var item = await Poll(RecordedReplies.Utorrent("stalled").Body("list=1", ("torrents.0.12", 2), ("torrents.0.14", 3)));

        var findings = Judge(item, TimeSpan.FromMinutes(500), Rule(DownloadHealthRuleKinds.DeadTorrent, 30));

        Assert.Equal(5, item.Peers);
        Assert.DoesNotContain(findings, f => f.Kind is DownloadHealthStrikes.DeadTorrent or DownloadHealthStrikes.TrackerUnregistered);
    }

    // The text a refused torrent shows in the status was not seen on the VM (the recorded dead tracker leaves the row without an error), so the
    // error bit (0x10) and the wording are put on the recorded row, as the snapshot tests do.
    [Fact]
    public async Task A_row_whose_message_says_the_torrent_is_unregistered_is_struck_at_once_as_a_tracker_rejection()
    {
        var item = await Poll(RecordedReplies.Utorrent("stalled").Body("list=1", ("torrents.0.1", 217), ("torrents.0.21", "Error: Tracker: Unregistered torrent"), ("torrents.0.12", 2)));

        var findings = Judge(item, TimeSpan.FromMinutes(45), Rule(DownloadHealthRuleKinds.DeadTorrent, 30));

        Assert.Equal(DownloadTrackerStatuses.Unregistered, item.TrackerStatus);
        Assert.False(item.IsSetupProblem);
        var certain = Assert.Single(findings, f => f.Kind == DownloadHealthStrikes.TrackerUnregistered);
        Assert.Equal(1, certain.RuleStrikeLimit);
    }

    [Fact]
    public async Task A_paused_torrent_is_not_called_dead_or_slow_while_it_is_paused()
    {
        var paused = await Poll(RecordedReplies.Utorrent("paused").Exchanges.Last(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).Reply.Body!);

        var findings = Judge(paused, TimeSpan.FromMinutes(500), Rule(DownloadHealthRuleKinds.DeadTorrent, 30), Rule(DownloadHealthRuleKinds.TooSlow, 1000));

        Assert.Equal(DownloadQueueStatuses.Paused, paused.Status);
        Assert.DoesNotContain(findings, f => f.Kind is DownloadHealthStrikes.DeadTorrent or DownloadHealthStrikes.LowSpeed);
    }

    // ---- The dangerous file rule on uTorrent's own getfiles --------------------------------------------------------------------------

    private static DownloadHealthRule DangerousFileRule()
    {
        var at = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        return new(Id: "dangerous-file", Kind: DownloadHealthRuleKinds.DangerousFile, Enabled: true, StrikeLimit: 1, TimerMinutes: 1,
            AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100, ResetStrikesOnProgress: true,
            ActionPreset: DownloadHealthActionPresets.Replace, CreatedUtc: at, UpdatedUtc: at);
    }

    private static ContentBlocklistSettings Blocklist(params string[] patterns)
        => new(Enabled: true, Mode: ContentBlocklistModes.Blacklist, UseBuiltInPatterns: false, CustomPatterns: patterns, RemoteUrl: null, RemotePatterns: [], LastRefreshedUtc: null, UpdatedUtc: DateTimeOffset.UtcNow);

    private static async Task<IReadOnlyList<string>> RecordedFiles()
    {
        var recording = RecordedReplies.Utorrent("finished-folder");
        var reply = recording.Exchanges.Last(e => e.Request.Path.Contains("action=getfiles", StringComparison.Ordinal)).Reply.Body!;
        var files = await new UTorrentDownloadClient(() => new UTorrent("{}", reply)).GetFilesAsync(Client(), "88b7f3162f5b6f3dbb72c2bf2339fb3e51d793dc", CancellationToken.None);
        Assert.NotNull(files);
        Assert.Equal(2, files.Count);
        return files;
    }

    private static RecoveryQueueSnapshot WithFiles(IReadOnlyList<string> files)
        => new(Status: "downloading", ErrorMessage: null, SourcePath: null, SpeedMbps: 5, AddedUtc: DateTimeOffset.UtcNow.AddMinutes(-10), EtaSeconds: 60,
            ReleaseName: "Recorded.utorrent.Folder", Peers: 5, IsUsenetProtocol: false, Files: files);

    [Fact]
    public async Task A_blocked_file_in_uTorrents_own_file_list_is_one_instant_strike()
    {
        var files = await RecordedFiles();
        var blocked = files.First(name => name.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase));

        var findings = new DownloadHealthEvaluator().Evaluate(WithFiles(files), DateTimeOffset.UtcNow, [DangerousFileRule()], Blocklist("*.nfo"));

        var strike = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.DangerousFile);
        Assert.Equal(1, strike.RuleStrikeLimit);
        Assert.Contains(blocked, strike.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_listing_with_no_blocked_name_is_left_alone()
    {
        var files = await RecordedFiles();

        var findings = new DownloadHealthEvaluator().Evaluate(WithFiles(files), DateTimeOffset.UtcNow, [DangerousFileRule()], Blocklist("*.exe", "*.scr"));

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.DangerousFile);
    }
}
