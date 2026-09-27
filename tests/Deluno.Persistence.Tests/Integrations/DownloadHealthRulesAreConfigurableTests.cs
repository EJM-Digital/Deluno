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
    public void No_progress_minutes_is_a_setting_not_a_constant()
    {
        var rules = new[] { NoProgressRule(timerMinutes: 10) };

        var tooSoonUnderDefault = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-11)), DownloadHealthRules.Defaults);
        var flaggedUnderCustom = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-11)), rules);

        Assert.DoesNotContain(tooSoonUnderDefault, finding => finding.Kind == DownloadHealthStrikes.NoThroughput);
        Assert.Contains(flaggedUnderCustom, finding => finding.Kind == DownloadHealthStrikes.NoThroughput);
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

    private static IReadOnlyList<RecoveryHealthFinding> Evaluate(DownloadQueueItem item, IReadOnlyList<DownloadHealthRule> rules)
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
                ProgressPercent: item.Progress,
                IsUsenetProtocol: string.Equals(item.Protocol, "sabnzbd", StringComparison.OrdinalIgnoreCase),
                IsPrivate: item.IsPrivate),
            CapturedUtc,
            rules);

    private static DownloadQueueItem CreateItem(
        double speedMbps = 1.2,
        int peers = 4,
        DateTimeOffset? addedUtc = null,
        double progressPercent = 50,
        bool isUsenet = false,
        bool? isPrivate = null)
        => new(
            Id: "queue-1",
            ClientId: "client-1",
            ClientName: "Test client",
            Protocol: isUsenet ? "sabnzbd" : "qbittorrent",
            MediaType: "movies",
            Title: "Example Movie",
            ReleaseName: "Example.Movie.2026.1080p.mkv",
            Category: "movies",
            Status: DownloadQueueStatuses.Downloading,
            Progress: progressPercent,
            SpeedMbps: speedMbps,
            EtaSeconds: 600,
            SizeBytes: 1_000_000_000,
            DownloadedBytes: 500_000_000,
            Peers: peers,
            IndexerName: "Test indexer",
            ErrorMessage: null,
            AddedUtc: addedUtc ?? CapturedUtc.AddMinutes(-10),
            SourcePath: "D:\\downloads\\example.mkv",
            IsPrivate: isPrivate);
}
