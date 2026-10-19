using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// The strike rules (too slow, dead torrent, magnet stuck on metadata, the client's own failure) judge what each torrent client
/// really reported. Each torrent comes from the client's recorded real reply (qBittorrent 5.2.1, Transmission 4.1.3, Deluge 2.2.0)
/// through its real adapter, is made into the snapshot the telemetry pass hands the evaluator, and is judged by the real
/// <see cref="DownloadHealthEvaluator"/>. A value is changed in a recording only where a test says what it is about (a tracker's wording,
/// the alternate speed limit, the number of peers).
/// </summary>
public sealed class EachStrikeRuleReadsEachTorrentClientsRecordedQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 0, 0, TimeSpan.Zero);

    private sealed record Polled(DownloadQueueItem Item, bool? AlternateSpeedLimit);

    private static async Task<Polled> PollAsync(string app, string recording, bool alternateSpeedLimit = false, string? trackerMessage = null, int? peers = null)
    {
        DownloadClientTelemetrySnapshot? snapshot;
        switch (app)
        {
            case "qbittorrent":
            {
                // qBittorrent is asked about a tracker only for a torrent moving no data, so a tracker test also sets the speeds to nothing.
                var edits = new List<(string, object?)>();
                if (peers is not null) { edits.Add(("num_seeds", peers.Value)); edits.Add(("num_leechs", 0)); }
                if (trackerMessage is not null) { edits.Add(("dlspeed", 0)); edits.Add(("upspeed", 0)); }
                var info = RecordedQbittorrentRig.Info(recording, edits.ToArray());
                var trackers = trackerMessage is null ? null : RecordedQbittorrentRig.Trackers(4, trackerMessage);
                var handler = RecordedQbittorrentRig.Handler(info, trackers, (request, _) =>
                    alternateSpeedLimit && request.RequestUri!.AbsolutePath.Contains("sync/maindata", StringComparison.Ordinal)
                        ? RecordedReplies.Json(RecordedReplies.Qbittorrent("settings").Body("sync/maindata", ("server_state.use_alt_speed_limits", true)))
                        : null);
                snapshot = await new QbittorrentDownloadClient(() => handler).GetSnapshotAsync(Client(app, "deluno"), Now, CancellationToken.None);
                break;
            }

            case "transmission":
            {
                var edits = new List<(string, object?)>();
                if (peers is not null) edits.Add(("peersConnected", peers.Value));
                if (trackerMessage is not null)
                {
                    edits.Add(("trackerStats.0.hasAnnounced", true));
                    edits.Add(("trackerStats.0.lastAnnounceSucceeded", false));
                    edits.Add(("trackerStats.0.lastAnnounceResult", trackerMessage));
                }

                var handler = RecordedTransmissionRig.Handler(RecordedTransmissionRig.Reply(recording, edits.ToArray()), (_, body) =>
                    alternateSpeedLimit && body.Contains("session-get", StringComparison.Ordinal)
                        ? RecordedReplies.Json(RecordedReplies.Transmission("settings").Body("session-get", ("arguments.alt-speed-enabled", true)))
                        : null);
                snapshot = await new TransmissionDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(app), Now, CancellationToken.None);
                break;
            }

            default:
            {
                var edits = new List<(string, object?)>();
                if (peers is not null) edits.Add(("num_peers", peers.Value));
                if (peers is not null) edits.Add(("num_seeds", 0));
                if (trackerMessage is not null) edits.Add(("tracker_status", trackerMessage));
                var body = RecordedReplies.Deluge(recording).Body("web.update_ui", edits.Select(e => ("result.torrents.*." + e.Item1, e.Item2)).ToArray());
                var handler = RecordedDelugeRig.Handler(body);
                snapshot = await new DelugeDownloadClient(new Factory(handler)).GetSnapshotAsync(Client(app), Now, CancellationToken.None);
                break;
            }
        }

        Assert.NotNull(snapshot);
        return new Polled(Assert.Single(snapshot.Queue), snapshot.AlternateSpeedLimitEnabled);
    }

    private static RecoveryQueueSnapshot Judged(Polled polled)
        => new(
            Status: polled.Item.Status, ErrorMessage: polled.Item.ErrorMessage, SourcePath: polled.Item.SourcePath, SpeedMbps: polled.Item.SpeedMbps,
            AddedUtc: polled.Item.AddedUtc, EtaSeconds: polled.Item.EtaSeconds, ReleaseName: polled.Item.ReleaseName, Peers: polled.Item.Peers,
            IsSetupProblem: polled.Item.IsSetupProblem, ProgressPercent: polled.Item.Progress, IsUsenetProtocol: false, IsPrivate: polled.Item.IsPrivate,
            TrackerStatus: polled.Item.TrackerStatus, Files: null, SizeBytes: polled.Item.SizeBytes,
            AlternateSpeedLimitOn: polled.AlternateSpeedLimit == true, IsFetchingMetadata: polled.Item.IsFetchingMetadata,
            Availability: polled.Item.Availability);

    private static DownloadHealthRule Rule(string kind, double threshold = 0, bool skipWhileAlternateSpeedLimitOn = false)
        => new(
            Id: kind + "-rule", Kind: kind, Enabled: true, StrikeLimit: 3, TimerMinutes: 5, AppliesTo: DownloadHealthAppliesTo.Torrents,
            MinCompletionPercent: 0, MaxCompletionPercent: 100, ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: Now, UpdatedUtc: Now, Threshold: threshold, SkipWhileAlternateSpeedLimitOn: skipWhileAlternateSpeedLimitOn);

    private static IReadOnlyList<RecoveryHealthFinding> Judge(Polled polled, DateTimeOffset at, params DownloadHealthRule[] rules)
        => new DownloadHealthEvaluator().Evaluate(Judged(polled), at, rules);

    // ---- Too slow -------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task A_recorded_download_under_the_speed_floor_is_struck_by_the_too_slow_rule_and_one_over_it_is_not(string app)
    {
        var polled = await PollAsync(app, "downloading");
        var kilobytesPerSecond = polled.Item.SpeedMbps * 1000;
        Assert.True(kilobytesPerSecond > 1000, "the recorded download really moves");
        var at = polled.Item.AddedUtc.AddMinutes(10);

        var slow = Assert.Single(Judge(polled, at, Rule(DownloadHealthRuleKinds.TooSlow, kilobytesPerSecond + 500)), f => f.Kind == DownloadHealthStrikes.LowSpeed);
        Assert.Equal("too-slow-rule", slow.RuleId);
        Assert.Contains("under the limit", slow.Evidence);

        Assert.DoesNotContain(Judge(polled, at, Rule(DownloadHealthRuleKinds.TooSlow, kilobytesPerSecond - 500)), f => f.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    public async Task The_clients_own_alternate_speed_limit_makes_the_too_slow_rule_stand_down_only_when_asked(string app)
    {
        var throttled = await PollAsync(app, "downloading", alternateSpeedLimit: true);
        Assert.True(throttled.AlternateSpeedLimit, "the client says its alternate speed limit is on");
        var floor = throttled.Item.SpeedMbps * 1000 + 500;
        var at = throttled.Item.AddedUtc.AddMinutes(10);

        Assert.DoesNotContain(Judge(throttled, at, Rule(DownloadHealthRuleKinds.TooSlow, floor, skipWhileAlternateSpeedLimitOn: true)), f => f.Kind == DownloadHealthStrikes.LowSpeed);
        Assert.Contains(Judge(throttled, at, Rule(DownloadHealthRuleKinds.TooSlow, floor, skipWhileAlternateSpeedLimitOn: false)), f => f.Kind == DownloadHealthStrikes.LowSpeed);

        var free = await PollAsync(app, "downloading");
        Assert.False(free.AlternateSpeedLimit, "the recorded session is not throttled");
        Assert.Contains(Judge(free, at, Rule(DownloadHealthRuleKinds.TooSlow, floor, skipWhileAlternateSpeedLimitOn: true)), f => f.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public async Task Deluge_reports_no_alternate_speed_limit_so_the_skip_option_never_hides_a_slow_download()
    {
        var polled = await PollAsync("deluge", "downloading");
        Assert.Null(polled.AlternateSpeedLimit);

        var findings = Judge(polled, polled.Item.AddedUtc.AddMinutes(10), Rule(DownloadHealthRuleKinds.TooSlow, polled.Item.SpeedMbps * 1000 + 500, skipWhileAlternateSpeedLimitOn: true));

        Assert.Contains(findings, f => f.Kind == DownloadHealthStrikes.LowSpeed);
    }

    // ---- Dead torrent ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task A_torrent_with_no_peers_is_dead_only_after_its_grace_period(string app)
    {
        var polled = await PollAsync(app, "stalled", peers: 0);
        Assert.Equal(0, polled.Item.Peers);
        var rule = Rule(DownloadHealthRuleKinds.DeadTorrent, threshold: 30);

        var early = Judge(polled, polled.Item.AddedUtc.AddMinutes(10), rule);
        var late = Judge(polled, polled.Item.AddedUtc.AddMinutes(45), rule);

        Assert.DoesNotContain(early, f => f.Kind == DownloadHealthStrikes.DeadTorrent);
        var dead = Assert.Single(late, f => f.Kind == DownloadHealthStrikes.DeadTorrent);
        Assert.Contains("no seeds or peers", dead.Summary);
        Assert.Equal("dead-torrent-rule", dead.RuleId);
    }

    [Theory]
    [InlineData("qbittorrent", "torrent not registered with this tracker")]
    [InlineData("transmission", "torrent not registered with this tracker")]
    [InlineData("deluge", "Error: unregistered torrent")]
    public async Task A_tracker_saying_the_torrent_is_unregistered_makes_it_dead_even_with_peers_and_is_struck_at_once(string app, string trackerSays)
    {
        var polled = await PollAsync(app, "downloading", trackerMessage: trackerSays, peers: 3);
        Assert.Equal(DownloadTrackerStatuses.Unregistered, polled.Item.TrackerStatus);
        Assert.Equal(3, polled.Item.Peers);

        var findings = Judge(polled, polled.Item.AddedUtc.AddMinutes(45), Rule(DownloadHealthRuleKinds.DeadTorrent, threshold: 30));

        var dead = Assert.Single(findings, f => f.Kind == DownloadHealthStrikes.DeadTorrent);
        Assert.Contains("unregistered", dead.Summary);
        var certain = Assert.Single(findings, f => f.Kind == DownloadHealthStrikes.TrackerUnregistered);
        Assert.Equal(1, certain.RuleStrikeLimit);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task A_torrent_with_peers_and_a_working_tracker_is_never_called_dead(string app)
    {
        var polled = await PollAsync(app, "downloading", peers: 4);

        var findings = Judge(polled, polled.Item.AddedUtc.AddMinutes(500), Rule(DownloadHealthRuleKinds.DeadTorrent, threshold: 30));

        Assert.DoesNotContain(findings, f => f.Kind is DownloadHealthStrikes.DeadTorrent or DownloadHealthStrikes.TrackerUnregistered);
    }

    // ---- Magnet stuck fetching metadata -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_qbittorrent_magnet_that_has_no_metadata_is_struck_by_the_magnet_rule_and_one_with_its_metadata_is_not()
    {
        var stuck = await PollAsync("qbittorrent", "magnet-no-metadata");
        Assert.True(stuck.Item.IsFetchingMetadata);
        var rule = Rule(DownloadHealthRuleKinds.MagnetMetadata);

        var finding = Assert.Single(Judge(stuck, stuck.Item.AddedUtc.AddMinutes(30), rule), f => f.Kind == DownloadHealthStrikes.MagnetMetadata);
        Assert.Equal("magnet-metadata-rule", finding.RuleId);
        Assert.Equal(5, finding.RuleTimerMinutes);

        var withMetadata = await PollAsync("qbittorrent", "downloading");
        Assert.False(withMetadata.Item.IsFetchingMetadata);
        Assert.DoesNotContain(Judge(withMetadata, withMetadata.Item.AddedUtc.AddMinutes(30), rule), f => f.Kind == DownloadHealthStrikes.MagnetMetadata);
    }

    /// <summary>Transmission and Deluge give no "fetching metadata" state (the rule's own text says so), so the magnet rule never fires; the no-progress rule is what strikes them, once the stall window has passed.</summary>
    [Theory]
    [InlineData("transmission")]
    [InlineData("deluge")]
    public async Task A_magnet_stuck_without_metadata_on_a_client_with_no_such_state_is_struck_by_the_no_progress_rule_not_the_magnet_rule(string app)
    {
        var polled = await PollAsync(app, "magnet-no-metadata");
        Assert.False(polled.Item.IsFetchingMetadata);
        Assert.Equal(0, polled.Item.SizeBytes);

        var snapshot = Judged(polled) with { Status = DownloadQueueStatuses.Stalled, BytesGrewSinceLastCheck = false };
        var findings = new DownloadHealthEvaluator().Evaluate(snapshot, polled.Item.AddedUtc.AddMinutes(45),
            [Rule(DownloadHealthRuleKinds.MagnetMetadata), Rule(DownloadHealthRuleKinds.NoProgress)]);

        Assert.DoesNotContain(findings, f => f.Kind == DownloadHealthStrikes.MagnetMetadata);
        Assert.Contains(findings, f => f.Kind == DownloadHealthStrikes.NoThroughput && f.RuleId == "no-progress-rule");
    }

    // ---- The client's own failure -----------------------------------------------------------------------------------------------------

    /// <summary>Deluge's recorded failed torrent: 0 bytes, "The tracker isn't answering." The client-failure rule strikes it, beside the always-on finding.</summary>
    [Fact]
    public async Task Deluges_recorded_failed_torrent_is_struck_by_the_client_failure_rule_beside_the_always_on_finding()
    {
        var polled = await PollAsync("deluge", "failed");
        Assert.False(string.IsNullOrWhiteSpace(polled.Item.ErrorMessage));
        Assert.False(polled.Item.IsSetupProblem);

        var findings = Judge(polled, polled.Item.AddedUtc.AddMinutes(45), Rule(DownloadHealthRuleKinds.ClientFailure));

        var struck = Assert.Single(findings, f => f.Kind == DownloadHealthStrikes.ClientFailure);
        Assert.Equal("client-failure-rule", struck.RuleId);
        Assert.Contains("tracker isn't answering", struck.Evidence);
        // The person's own rule decides, so the platform-wide fallback does not also strike it (#1194, #1205).
        Assert.DoesNotContain(findings, f => f.Kind == DownloadHealthStrikes.ClientStalled);
        Assert.Contains(new DownloadHealthEvaluator().Evaluate(Judged(polled), polled.Item.AddedUtc.AddMinutes(45), []), f => f.Kind == DownloadHealthStrikes.ClientStalled);
    }

    /// <summary>Transmission's recorded failure is its own "no data was found" local error: a problem on this machine, so it never earns a strike from the rule.</summary>
    [Fact]
    public async Task Transmissions_recorded_failure_is_a_problem_on_this_machine_and_the_client_failure_rule_never_strikes_it()
    {
        var polled = await PollAsync("transmission", "failed");
        Assert.True(polled.Item.IsSetupProblem);

        var findings = Judge(polled, polled.Item.AddedUtc.AddMinutes(45), Rule(DownloadHealthRuleKinds.ClientFailure));

        var said = Assert.Single(findings, f => f.Kind == DownloadHealthStrikes.ClientSetupProblem);
        Assert.False(DownloadHealthStrikes.Counts(said.Kind));
        Assert.DoesNotContain(findings, f => f.Kind == DownloadHealthStrikes.ClientFailure);
    }

    /// <summary>qBittorrent's own error state (the recorded failed torrent with that one value changed) is a local setup problem, which never earns a strike.</summary>
    [Fact]
    public async Task Qbittorrents_own_error_state_is_a_problem_on_this_machine_and_the_client_failure_rule_never_strikes_it()
    {
        var handler = RecordedQbittorrentRig.Handler(RecordedQbittorrentRig.Info("failed", ("state", "error")));
        var snapshot = await new QbittorrentDownloadClient(() => handler).GetSnapshotAsync(Client("qbittorrent", "deluno"), Now, CancellationToken.None);
        var polled = new Polled(Assert.Single(snapshot!.Queue), snapshot.AlternateSpeedLimitEnabled);
        Assert.True(polled.Item.IsSetupProblem);

        var findings = Judge(polled, polled.Item.AddedUtc.AddMinutes(45), Rule(DownloadHealthRuleKinds.ClientFailure));

        Assert.Contains(findings, f => f.Kind == DownloadHealthStrikes.ClientSetupProblem);
        Assert.DoesNotContain(findings, f => f.Kind == DownloadHealthStrikes.ClientFailure);
    }

    /// <summary>qBittorrent's recorded stalled torrent (nothing moving) once Deluno's own no-progress window has marked it stalled (proved in NearlyFinishedDownloadsFollowTheStrikeRulesTests); the client-failure rule strikes it.</summary>
    [Fact]
    public async Task A_qbittorrent_that_never_moved_a_byte_is_struck_by_the_client_failure_rule_once_it_is_stalled()
    {
        var polled = await PollAsync("qbittorrent", "stalled");
        Assert.Equal(0, polled.Item.SpeedMbps);

        var findings = new DownloadHealthEvaluator().Evaluate(Judged(polled) with { Status = DownloadQueueStatuses.Stalled }, polled.Item.AddedUtc.AddMinutes(45), [Rule(DownloadHealthRuleKinds.ClientFailure)]);

        Assert.Equal("client-failure-rule", Assert.Single(findings, f => f.Kind == DownloadHealthStrikes.ClientFailure).RuleId);
    }

    // ---- Usenet: no peers, and each rule's protocol scope ---------------------------------------------------------------------------

    private static async Task<Polled> PollSabnzbdAsync(string recording)
    {
        var handler = RecordedReplies.Handler("sabnzbd",
            [RecordedReplies.Sabnzbd(recording), RecordedReplies.Sabnzbd("idle"), RecordedReplies.Sabnzbd("version"), RecordedReplies.Sabnzbd("settings")]);
        var snapshot = await new SabnzbdDownloadClient(new Factory(handler)).GetSnapshotAsync(Client("sabnzbd"), Now, CancellationToken.None);
        return new Polled(snapshot!.Queue.First(item => item.SizeBytes > 0), snapshot.AlternateSpeedLimitEnabled);
    }

    private static DownloadHealthRule Scoped(string kind, string appliesTo, double threshold = 0)
        => Rule(kind, threshold) with { AppliesTo = appliesTo };

    private static IReadOnlyList<RecoveryHealthFinding> JudgeAs(Polled polled, bool usenet, string status, DateTimeOffset at, DownloadHealthRule rule)
        => new DownloadHealthEvaluator().Evaluate(Judged(polled) with { IsUsenetProtocol = usenet, Status = status, BytesGrewSinceLastCheck = false }, at, [rule]);

    [Fact]
    public async Task A_not_enough_peers_rule_never_strikes_a_usenet_download_whatever_its_scope()
    {
        var polled = await PollSabnzbdAsync("downloading");
        Assert.Equal(0, polled.Item.Peers);

        foreach (var scope in new[] { DownloadHealthAppliesTo.Both, DownloadHealthAppliesTo.Usenet })
        {
            var findings = JudgeAs(polled, usenet: true, DownloadQueueStatuses.Downloading, polled.Item.AddedUtc.AddMinutes(60), Scoped(DownloadHealthRuleKinds.LowPeers, scope, threshold: 5));
            Assert.DoesNotContain(findings, f => f.Kind == DownloadHealthStrikes.LowPeers);
        }

        var torrent = await PollAsync("qbittorrent", "stalled", peers: 0);
        Assert.Contains(JudgeAs(torrent, usenet: false, DownloadQueueStatuses.Downloading, torrent.Item.AddedUtc.AddMinutes(60), Scoped(DownloadHealthRuleKinds.LowPeers, DownloadHealthAppliesTo.Torrents, threshold: 5)),
            f => f.Kind == DownloadHealthStrikes.LowPeers);
    }

    /// <summary>The protocol scope of each rule kind that can apply to both: a Torrents-only rule skips a Usenet item and a Usenet-only rule skips a torrent, and each strikes the protocol it names.</summary>
    [Theory]
    [InlineData(DownloadHealthRuleKinds.NoProgress, DownloadHealthStrikes.NoThroughput, 0)]
    [InlineData(DownloadHealthRuleKinds.TooSlow, DownloadHealthStrikes.LowSpeed, 500000)]
    [InlineData(DownloadHealthRuleKinds.ClientFailure, DownloadHealthStrikes.ClientFailure, 0)]
    public async Task A_torrents_only_rule_skips_a_usenet_item_and_a_usenet_only_rule_skips_a_torrent(string kind, string strike, double threshold)
    {
        var usenetItem = await PollSabnzbdAsync("downloading");
        var torrentItem = await PollAsync("qbittorrent", "downloading");
        // Stalled with no error text is what makes the client-failure rule apply; the other two kinds read it as downloading too.
        var status = DownloadQueueStatuses.Stalled;
        var usenetAt = usenetItem.Item.AddedUtc.AddMinutes(60);
        var torrentAt = torrentItem.Item.AddedUtc.AddMinutes(60);
        var usenetSlow = kind == DownloadHealthRuleKinds.TooSlow ? usenetItem : usenetItem with { Item = usenetItem.Item with { SpeedMbps = 0.01 } };
        var torrentSlow = kind == DownloadHealthRuleKinds.TooSlow ? torrentItem with { Item = torrentItem.Item with { SpeedMbps = 0.01 } } : torrentItem;
        if (kind == DownloadHealthRuleKinds.TooSlow) status = DownloadQueueStatuses.Downloading;

        Assert.DoesNotContain(JudgeAs(usenetSlow, usenet: true, status, usenetAt, Scoped(kind, DownloadHealthAppliesTo.Torrents, threshold)), f => f.Kind == strike);
        Assert.DoesNotContain(JudgeAs(torrentSlow, usenet: false, status, torrentAt, Scoped(kind, DownloadHealthAppliesTo.Usenet, threshold)), f => f.Kind == strike);
        Assert.Contains(JudgeAs(usenetSlow, usenet: true, status, usenetAt, Scoped(kind, DownloadHealthAppliesTo.Usenet, threshold)), f => f.Kind == strike);
        Assert.Contains(JudgeAs(torrentSlow, usenet: false, status, torrentAt, Scoped(kind, DownloadHealthAppliesTo.Torrents, threshold)), f => f.Kind == strike);
        Assert.Contains(JudgeAs(usenetSlow, usenet: true, status, usenetAt, Scoped(kind, DownloadHealthAppliesTo.Both, threshold)), f => f.Kind == strike);
        Assert.Contains(JudgeAs(torrentSlow, usenet: false, status, torrentAt, Scoped(kind, DownloadHealthAppliesTo.Both, threshold)), f => f.Kind == strike);
    }

    [Theory]
    [InlineData(DownloadHealthRuleKinds.DeadTorrent)]
    [InlineData(DownloadHealthRuleKinds.MagnetMetadata)]
    public async Task The_torrent_only_rule_kinds_never_strike_a_usenet_item_even_on_a_both_scope(string kind)
    {
        var usenetItem = await PollSabnzbdAsync("downloading");

        var findings = new DownloadHealthEvaluator().Evaluate(
            Judged(usenetItem) with { IsUsenetProtocol = true, IsFetchingMetadata = true, Peers = 0 }, usenetItem.Item.AddedUtc.AddMinutes(500),
            [Scoped(kind, DownloadHealthAppliesTo.Both, threshold: 30)]);

        Assert.DoesNotContain(findings, f => f.Kind is DownloadHealthStrikes.DeadTorrent or DownloadHealthStrikes.MagnetMetadata);
    }
}
