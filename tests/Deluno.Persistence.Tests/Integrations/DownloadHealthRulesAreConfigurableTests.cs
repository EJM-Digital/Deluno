using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #905's per-rule engine: each rule kind is switched, timed and thresholded
/// on its own, with the defaults Deluno originally shipped (#659) so an
/// install that never opens the settings screen behaves exactly as before.
/// </summary>
public sealed class DownloadHealthRulesAreConfigurableTests
{
    private static readonly DateTimeOffset CapturedUtc = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly IRecoveryHealthEvaluator Evaluator = new DownloadHealthEvaluator();

    [Fact]
    public void Defaults_behave_exactly_as_the_hard_coded_rule_did()
    {
        var findings = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-31)), DownloadHealthRules.Defaults);

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.NoThroughput, finding.Kind);
    }

    [Fact]
    public void No_progress_rule_switched_off_never_fires()
    {
        var rules = new[] { NoProgressRule(enabled: false) };
        var findings = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-60)), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.NoThroughput);
    }

    [Fact]
    public void No_progress_strikes_on_the_first_check_whatever_the_age_of_the_download()
    {
        // #1194: a strike per check; the first strike never waits for the download to reach an age.
        var rules = new[] { NoProgressRule() };

        var justAdded = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-1)), rules);

        Assert.Contains(justAdded, finding => finding.Kind == DownloadHealthStrikes.NoThroughput);
    }

    [Fact]
    public void Minimum_speed_rule_is_off_by_default()
    {
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60)), DownloadHealthRules.Defaults);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Minimum_speed_rule_flags_a_download_under_the_floor_for_long_enough()
    {
        var rules = new[] { TooSlowRule(kbps: 100, timerMinutes: 30) };
        // 0.001 MB/s == 1 KB/s, well under a 100 KB/s floor.
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-31)), rules);

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.LowSpeed, finding.Kind);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    [Fact]
    public void Minimum_speed_rule_leaves_a_download_above_the_floor_alone()
    {
        var rules = new[] { TooSlowRule(kbps: 100, timerMinutes: 30) };
        // 0.5 MB/s == 500 KB/s, comfortably above the floor.
        var findings = Evaluate(CreateItem(speedMbps: 0.5, addedUtc: CapturedUtc.AddMinutes(-60)), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Minimum_peers_rule_is_off_by_default()
    {
        var findings = Evaluate(CreateItem(peers: 0, addedUtc: CapturedUtc.AddMinutes(-60)), DownloadHealthRules.Defaults);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowPeers);
    }

    [Fact]
    public void Minimum_peers_rule_flags_a_download_under_the_floor_for_long_enough()
    {
        var rules = new[] { LowPeersRule(minPeers: 3, timerMinutes: 30) };
        var findings = Evaluate(CreateItem(peers: 1, addedUtc: CapturedUtc.AddMinutes(-31)), rules);

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.LowPeers, finding.Kind);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    /// <summary>#1223: the evidence counted "1 peers".</summary>
    [Theory]
    [InlineData(1, "1 peer at this check")]
    [InlineData(2, "2 peers at this check")]
    public void The_peers_evidence_uses_the_right_plural(int peers, string expected)
    {
        var rules = new[] { LowPeersRule(minPeers: 3, timerMinutes: 30) };
        var finding = Assert.Single(Evaluate(CreateItem(peers: peers, addedUtc: CapturedUtc.AddMinutes(-31)), rules));

        Assert.StartsWith(expected, finding.Evidence);
    }

    [Fact]
    public void The_no_peers_evidence_after_one_minute_says_minute()
    {
        var item = CreateItem(peers: 0, addedUtc: CapturedUtc.AddMinutes(-1)) with { Progress = 0 };
        var findings = Evaluate(item, new[] { NoProgressRule(enabled: true) with { TimerMinutes = 1 } });

        foreach (var finding in findings)
        {
            Assert.DoesNotContain("1 minutes", finding.Evidence);
        }
    }

    [Fact]
    public void Minimum_peers_rule_leaves_a_well_seeded_download_alone()
    {
        var rules = new[] { LowPeersRule(minPeers: 3, timerMinutes: 30) };
        var findings = Evaluate(CreateItem(peers: 12, addedUtc: CapturedUtc.AddMinutes(-60)), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowPeers);
    }

    [Fact]
    public void Dead_torrent_rule_is_off_by_default()
    {
        var findings = Evaluate(CreateItem(peers: 0, addedUtc: CapturedUtc.AddMinutes(-90)), DownloadHealthRules.Defaults);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.DeadTorrent);
    }

    [Fact]
    public void Dead_torrent_rule_flags_zero_peers_past_its_grace_period()
    {
        var rules = new[] { DeadTorrentRule(graceMinutes: 60, timerMinutes: 30) };
        var findings = Evaluate(CreateItem(peers: 0, addedUtc: CapturedUtc.AddMinutes(-91)), rules);

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.DeadTorrent, finding.Kind);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    [Fact]
    public void Dead_torrent_rule_leaves_a_freshly_added_download_alone_during_its_grace_period()
    {
        var rules = new[] { DeadTorrentRule(graceMinutes: 60, timerMinutes: 30) };
        var findings = Evaluate(CreateItem(peers: 0, addedUtc: CapturedUtc.AddMinutes(-10)), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.DeadTorrent);
    }

    [Fact]
    public void Dead_torrent_rule_never_applies_to_usenet()
    {
        var rules = new[] { DeadTorrentRule(graceMinutes: 0, timerMinutes: 1) };
        var findings = Evaluate(CreateItem(peers: 0, addedUtc: CapturedUtc.AddMinutes(-90), isUsenet: true), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.DeadTorrent);
    }

    /// <summary>#818: a healthy swarm whose tracker itself says the torrent is unregistered is dead too, not merely "low peers".</summary>
    [Fact]
    public void Dead_torrent_rule_flags_a_tracker_reported_unregistered_torrent_even_with_peers()
    {
        var rules = new[] { DeadTorrentRule(graceMinutes: 60, timerMinutes: 30) };
        var findings = Evaluate(CreateItem(peers: 12, addedUtc: CapturedUtc.AddMinutes(-91), trackerStatus: DownloadTrackerStatuses.Unregistered), rules);

        var finding = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.DeadTorrent);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    /// <summary>#818: a tracker that is merely unreachable right now (Unknown, not Unregistered) is a temporary outage, not a dead verdict.</summary>
    [Fact]
    public void Dead_torrent_rule_does_not_treat_a_tracker_outage_as_dead()
    {
        var rules = new[] { DeadTorrentRule(graceMinutes: 60, timerMinutes: 30) };
        var findings = Evaluate(CreateItem(peers: 12, addedUtc: CapturedUtc.AddMinutes(-91), trackerStatus: DownloadTrackerStatuses.Unknown), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.DeadTorrent);
    }

    /// <summary>
    /// #878, cleanup sheet C5: unlike every other kind here, this is not
    /// something a person opts into - it is James's decided behaviour, so it
    /// fires with no configured rule at all, and carries a strike limit of
    /// one so it can act on the very first sighting rather than waiting for
    /// strikes to build up.
    /// </summary>
    [Fact]
    public void Tracker_unregistered_finding_fires_with_no_rule_configured_and_acts_on_the_first_sighting()
    {
        var findings = Evaluate(CreateItem(trackerStatus: DownloadTrackerStatuses.Unregistered), DownloadHealthRules.Defaults);

        var finding = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.TrackerUnregistered);
        Assert.Equal("critical", finding.Severity);
        Assert.Equal(1, finding.RuleStrikeLimit);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    [Fact]
    public void Tracker_unregistered_finding_never_applies_to_usenet()
    {
        var findings = Evaluate(CreateItem(trackerStatus: DownloadTrackerStatuses.Unregistered, isUsenet: true), DownloadHealthRules.Defaults);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.TrackerUnregistered);
    }

    [Fact]
    public void A_healthy_tracker_status_never_raises_the_tracker_unregistered_finding()
    {
        var findings = Evaluate(CreateItem(trackerStatus: DownloadTrackerStatuses.Ok), DownloadHealthRules.Defaults);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.TrackerUnregistered);
    }

    [Fact]
    public void A_rule_scoped_to_the_wrong_completion_range_never_matches()
    {
        var rules = new[] { TooSlowRule(kbps: 100, timerMinutes: 30) with { MinCompletionPercent = 60, MaxCompletionPercent = 100 } };
        // 10% complete is outside the 60-100% range this rule covers.
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60), progressPercent: 10), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    /// <summary>#815: a private-only rule never strikes a public download.</summary>
    [Fact]
    public void A_rule_scoped_to_private_only_never_matches_a_public_download()
    {
        var rules = new[] { TooSlowRule(kbps: 100, timerMinutes: 30) with { PrivacyScope = DownloadHealthPrivacyScopes.Private } };
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60), isPrivate: false), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    /// <summary>#815: a public-only rule never strikes a private download.</summary>
    [Fact]
    public void A_rule_scoped_to_public_only_never_matches_a_private_download()
    {
        var rules = new[] { TooSlowRule(kbps: 100, timerMinutes: 30) with { PrivacyScope = DownloadHealthPrivacyScopes.Public } };
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60), isPrivate: true), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    /// <summary>The flip side: a rule matching the download's actual privacy does still strike it.</summary>
    [Fact]
    public void A_rule_scoped_to_the_downloads_own_privacy_still_matches()
    {
        var rules = new[] { TooSlowRule(kbps: 100, timerMinutes: 30) with { PrivacyScope = DownloadHealthPrivacyScopes.Private } };
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60), isPrivate: true), rules);

        Assert.Contains(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    /// <summary>An unknown privacy never satisfies a scoped rule - only "both" is safe to assume nothing about.</summary>
    [Fact]
    public void A_scoped_rule_never_matches_an_unknown_privacy()
    {
        var rules = new[] { TooSlowRule(kbps: 100, timerMinutes: 30) with { PrivacyScope = DownloadHealthPrivacyScopes.Private } };
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60), isPrivate: null), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Two_privacy_scoped_rules_of_the_same_kind_and_range_can_both_be_saved()
    {
        var privateRule = TooSlowRule(kbps: 200, timerMinutes: 30) with { Id = "private-rule", PrivacyScope = DownloadHealthPrivacyScopes.Private };
        var publicRule = TooSlowRule(kbps: 50, timerMinutes: 30) with { Id = "public-rule", PrivacyScope = DownloadHealthPrivacyScopes.Public };

        Assert.Null(DownloadHealthRuleValidator.Validate(privateRule, [publicRule]));
        Assert.Null(DownloadHealthRuleValidator.Validate(publicRule, [privateRule]));
    }

    [Fact]
    public void Two_overlapping_privacy_scopes_of_the_same_kind_and_range_are_refused()
    {
        var first = TooSlowRule(kbps: 200, timerMinutes: 30) with { Id = "first", PrivacyScope = DownloadHealthPrivacyScopes.Both };
        var second = TooSlowRule(kbps: 50, timerMinutes: 30) with { Id = "second", PrivacyScope = DownloadHealthPrivacyScopes.Private };

        var error = DownloadHealthRuleValidator.Validate(second, [first]);

        Assert.NotNull(error);
    }

    [Fact]
    public void Two_non_overlapping_rules_of_the_same_kind_can_both_be_saved()
    {
        var early = TooSlowRule(kbps: 200, timerMinutes: 30) with { Id = "early", MinCompletionPercent = 0, MaxCompletionPercent = 49 };
        var late = TooSlowRule(kbps: 50, timerMinutes: 30) with { Id = "late", MinCompletionPercent = 50, MaxCompletionPercent = 100 };

        Assert.Null(DownloadHealthRuleValidator.Validate(early, [late]));
        Assert.Null(DownloadHealthRuleValidator.Validate(late, [early]));
    }

    // #905 acceptance: "Queued-by-client and paused downloads never collect
    // strikes" - every timed rule only ever looks at a download whose status
    // is "downloading"; a download the client itself is holding back (its own
    // queue limit) or that a person paused never reaches that check at all.
    [Theory]
    [InlineData(DownloadQueueStatuses.Queued)]
    [InlineData(DownloadQueueStatuses.Paused)]
    public void A_queued_or_paused_download_never_trips_no_progress_however_long_it_waits(string status)
    {
        var item = CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-120)) with { Status = status };
        var findings = Evaluate(item, DownloadHealthRules.Defaults);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.NoThroughput);
    }

    [Theory]
    [InlineData(DownloadQueueStatuses.Queued)]
    [InlineData(DownloadQueueStatuses.Paused)]
    public void A_queued_or_paused_download_never_trips_too_slow_low_peers_or_dead_torrent(string status)
    {
        var rules = new[] { TooSlowRule(kbps: 100, timerMinutes: 30), LowPeersRule(minPeers: 5, timerMinutes: 30), DeadTorrentRule(graceMinutes: 10, timerMinutes: 30) };
        var item = CreateItem(speedMbps: 0.001, peers: 0, addedUtc: CapturedUtc.AddMinutes(-120)) with { Status = status };
        var findings = Evaluate(item, rules);

        Assert.Empty(findings);
    }

    [Fact]
    public void Two_overlapping_enabled_rules_of_the_same_kind_are_refused()
    {
        var first = TooSlowRule(kbps: 200, timerMinutes: 30) with { Id = "first", MinCompletionPercent = 0, MaxCompletionPercent = 60 };
        var second = TooSlowRule(kbps: 50, timerMinutes: 30) with { Id = "second", MinCompletionPercent = 50, MaxCompletionPercent = 100 };

        var error = DownloadHealthRuleValidator.Validate(second, [first]);

        Assert.NotNull(error);
        Assert.Contains("overlaps", error, StringComparison.OrdinalIgnoreCase);
    }

    private static DownloadHealthRule NoProgressRule(bool enabled = true, int timerMinutes = 30) => new(
        Id: "test-no-progress", Kind: DownloadHealthRuleKinds.NoProgress, Enabled: enabled, StrikeLimit: 3,
        TimerMinutes: timerMinutes, AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
        ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.JustTellMe,
        CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue);

    private static DownloadHealthRule TooSlowRule(double kbps, int timerMinutes) => new(
        Id: "test-too-slow", Kind: DownloadHealthRuleKinds.TooSlow, Enabled: true, StrikeLimit: 3,
        TimerMinutes: timerMinutes, AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
        ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.JustTellMe,
        CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue, Threshold: kbps);

    private static DownloadHealthRule ClientFailureRule() => new(
        Id: "test-client-failure", Kind: DownloadHealthRuleKinds.ClientFailure, Enabled: true, StrikeLimit: 3,
        TimerMinutes: 30, AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
        ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
        CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue);

    private static DownloadHealthRule MagnetMetadataRule(int timerMinutes) => new(
        Id: "test-magnet-metadata", Kind: DownloadHealthRuleKinds.MagnetMetadata, Enabled: true, StrikeLimit: 3,
        TimerMinutes: timerMinutes, AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
        ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.JustTellMe,
        CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue);

    private static DownloadHealthRule LowPeersRule(int minPeers, int timerMinutes) => new(
        Id: "test-low-peers", Kind: DownloadHealthRuleKinds.LowPeers, Enabled: true, StrikeLimit: 3,
        TimerMinutes: timerMinutes, AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
        ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.JustTellMe,
        CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue, Threshold: minPeers);

    private static DownloadHealthRule DeadTorrentRule(int graceMinutes, int timerMinutes) => new(
        Id: "test-dead-torrent", Kind: DownloadHealthRuleKinds.DeadTorrent, Enabled: true, StrikeLimit: 3,
        TimerMinutes: timerMinutes, AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
        ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.JustTellMe,
        CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue, Threshold: graceMinutes);

    private static IReadOnlyList<RecoveryHealthFinding> Evaluate(
        DownloadQueueItem item,
        IReadOnlyList<DownloadHealthRule> rules,
        bool alternateSpeedLimitOn = false,
        bool isFetchingMetadata = false)
        => Evaluator.Evaluate(
            new RecoveryQueueSnapshot(
                item.Status,
                item.ErrorMessage,
                item.SourcePath,
                item.SpeedMbps,
                item.AddedUtc,
                item.EtaSeconds,
                item.ReleaseName,
                item.Peers,
                IsSetupProblem: item.IsSetupProblem,
                ProgressPercent: item.Progress,
                IsUsenetProtocol: string.Equals(item.Protocol, "sabnzbd", StringComparison.OrdinalIgnoreCase),
                IsPrivate: item.IsPrivate,
                TrackerStatus: item.TrackerStatus,
                SizeBytes: item.SizeBytes,
                AlternateSpeedLimitOn: alternateSpeedLimitOn,
                IsFetchingMetadata: isFetchingMetadata),
            CapturedUtc,
            rules);

    private static DownloadQueueItem CreateItem(
        double speedMbps = 1.2,
        int peers = 4,
        DateTimeOffset? addedUtc = null,
        double progressPercent = 50,
        bool isUsenet = false,
        bool? isPrivate = null,
        string trackerStatus = DownloadTrackerStatuses.Unknown,
        string status = DownloadQueueStatuses.Downloading,
        string? errorMessage = null,
        long sizeBytes = 1_000_000_000)
        => new(
            Id: "queue-1",
            ClientId: "client-1",
            ClientName: "Test client",
            Protocol: isUsenet ? "sabnzbd" : "qbittorrent",
            MediaType: "movies",
            Title: "Example Movie",
            ReleaseName: "Example.Movie.2026.1080p.mkv",
            Category: "movies",
            Status: status,
            Progress: progressPercent,
            SpeedMbps: speedMbps,
            EtaSeconds: 600,
            SizeBytes: sizeBytes,
            DownloadedBytes: 500_000_000,
            Peers: peers,
            IndexerName: "Test indexer",
            ErrorMessage: errorMessage,
            AddedUtc: addedUtc ?? CapturedUtc.AddMinutes(-10),
            SourcePath: "D:\\downloads\\example.mkv",
            IsPrivate: isPrivate,
            TrackerStatus: trackerStatus);

    // #951: client-failure rule.
    [Fact]
    public void Client_failure_rule_is_off_by_default()
    {
        var findings = Evaluate(CreateItem(status: DownloadQueueStatuses.Stalled, errorMessage: "Tracker timed out."), DownloadHealthRules.Defaults);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.ClientFailure);
        // The unconditional, non-rule finding still shows regardless (existing behaviour, unchanged).
        Assert.Contains(findings, finding => finding.Kind == DownloadHealthStrikes.ClientStalled);
    }

    [Fact]
    public void Client_failure_rule_enabled_gives_the_same_signal_its_own_strikes_and_action()
    {
        var rules = new[] { ClientFailureRule() };
        var findings = Evaluate(CreateItem(status: DownloadQueueStatuses.Stalled, errorMessage: "Tracker timed out."), rules);

        var finding = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.ClientFailure);
        Assert.Equal("test-client-failure", finding.RuleId);
        Assert.Equal(DownloadHealthActionPresets.Replace, finding.RuleActionPreset);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
        // One rule owns the stall (#1194): the platform-wide fallback does not also strike beside it.
        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.ClientStalled);
    }

    [Fact]
    public void Client_failure_rule_never_fires_for_a_setup_problem()
    {
        var rules = new[] { ClientFailureRule() };
        var item = CreateItem(status: DownloadQueueStatuses.Stalled, errorMessage: "Disk full.") with { IsSetupProblem = true };
        var findings = Evaluate(item, rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.ClientFailure);
    }

    // #951: magnet-stuck-fetching-metadata rule.
    [Fact]
    public void Magnet_metadata_rule_is_off_by_default()
    {
        var findings = Evaluate(CreateItem(addedUtc: CapturedUtc.AddMinutes(-60)), DownloadHealthRules.Defaults, isFetchingMetadata: true);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.MagnetMetadata);
    }

    [Fact]
    public void Magnet_metadata_rule_flags_a_magnet_still_fetching_metadata_past_its_timer()
    {
        var rules = new[] { MagnetMetadataRule(timerMinutes: 30) };
        var findings = Evaluate(CreateItem(addedUtc: CapturedUtc.AddMinutes(-31)), rules, isFetchingMetadata: true);

        var finding = Assert.Single(findings, finding => finding.Kind == DownloadHealthStrikes.MagnetMetadata);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    [Fact]
    public void Magnet_metadata_rule_leaves_an_ordinary_download_alone()
    {
        var rules = new[] { MagnetMetadataRule(timerMinutes: 30) };
        var findings = Evaluate(CreateItem(addedUtc: CapturedUtc.AddMinutes(-31)), rules, isFetchingMetadata: false);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.MagnetMetadata);
    }

    [Fact]
    public void Magnet_metadata_rule_never_applies_to_usenet()
    {
        var rules = new[] { MagnetMetadataRule(timerMinutes: 1) };
        var findings = Evaluate(CreateItem(addedUtc: CapturedUtc.AddMinutes(-31), isUsenet: true), rules, isFetchingMetadata: true);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.MagnetMetadata);
    }

    // #951: "too slow" options.
    [Fact]
    public void Too_slow_max_total_time_trips_even_when_the_speed_floor_is_not_breached()
    {
        var rules = new[] { TooSlowRule(kbps: 1, timerMinutes: 30) with { TooSlowMaxTotalMinutes = 60 } };
        // Comfortably above the 1 KB/s floor, so only the total-time ceiling can trip this.
        var findings = Evaluate(CreateItem(speedMbps: 5, addedUtc: CapturedUtc.AddMinutes(-61)), rules);

        Assert.Contains(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Too_slow_max_total_time_leaves_a_download_alone_before_the_ceiling()
    {
        var rules = new[] { TooSlowRule(kbps: 1, timerMinutes: 30) with { TooSlowMaxTotalMinutes = 60 } };
        var findings = Evaluate(CreateItem(speedMbps: 5, addedUtc: CapturedUtc.AddMinutes(-30)), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Too_slow_ignore_above_size_exempts_a_large_download()
    {
        var rules = new[] { TooSlowRule(kbps: 1_000_000, timerMinutes: 30) with { IgnoreAboveSizeBytes = 2_000_000_000 } };
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60), sizeBytes: 5_000_000_000), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Too_slow_ignore_above_size_still_matches_a_download_at_or_under_the_ceiling()
    {
        var rules = new[] { TooSlowRule(kbps: 1_000_000, timerMinutes: 30) with { IgnoreAboveSizeBytes = 2_000_000_000 } };
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60), sizeBytes: 1_000_000_000), rules);

        Assert.Contains(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Too_slow_skips_entirely_while_the_clients_alternate_speed_limit_is_on()
    {
        var rules = new[] { TooSlowRule(kbps: 1_000_000, timerMinutes: 30) with { SkipWhileAlternateSpeedLimitOn = true } };
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60)), rules, alternateSpeedLimitOn: true);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Too_slow_still_applies_when_the_alternate_speed_limit_is_off()
    {
        var rules = new[] { TooSlowRule(kbps: 1_000_000, timerMinutes: 30) with { SkipWhileAlternateSpeedLimitOn = true } };
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60)), rules, alternateSpeedLimitOn: false);

        Assert.Contains(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    // #951: a 0% download says why.
    [Fact]
    public void Zero_percent_download_says_no_bytes_have_arrived()
    {
        var findings = Evaluate(CreateItem(speedMbps: 0, progressPercent: 0, addedUtc: CapturedUtc.AddMinutes(-31)), []);

        var finding = Assert.Single(findings, finding => finding.Kind == "zero-percent-download");
        Assert.Contains("No bytes have arrived", finding.Evidence);
    }

    [Fact]
    public void Zero_percent_download_says_it_is_still_fetching_metadata_when_that_is_the_reason()
    {
        var findings = Evaluate(CreateItem(speedMbps: 0, progressPercent: 0, addedUtc: CapturedUtc.AddMinutes(-31)), [], isFetchingMetadata: true);

        var finding = Assert.Single(findings, finding => finding.Kind == "zero-percent-download");
        Assert.Contains("fetching the torrent's own metadata", finding.Evidence);
    }

    [Fact]
    public void Zero_percent_download_says_no_peers_are_connected_when_that_is_the_reason()
    {
        var findings = Evaluate(CreateItem(speedMbps: 0, progressPercent: 0, peers: 0, addedUtc: CapturedUtc.AddMinutes(-31)), []);

        var finding = Assert.Single(findings, finding => finding.Kind == "zero-percent-download");
        Assert.Contains("No peers connected", finding.Evidence);
    }

    [Fact]
    public void A_download_making_any_progress_never_raises_the_zero_percent_finding()
    {
        var findings = Evaluate(CreateItem(speedMbps: 1, progressPercent: 1, addedUtc: CapturedUtc.AddMinutes(-31)), []);

        Assert.DoesNotContain(findings, finding => finding.Kind == "zero-percent-download");
    }
}
