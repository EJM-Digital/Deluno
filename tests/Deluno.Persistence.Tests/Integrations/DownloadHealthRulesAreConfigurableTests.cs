using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #659's second scope: James asked for these thresholds to be configurable
/// like Cleanuparr's rather than hard-coded, each switchable on its own, with
/// the defaults Deluno originally shipped so an install that never opens the
/// settings screen behaves exactly as before.
/// </summary>
public sealed class DownloadHealthRulesAreConfigurableTests
{
    private static readonly DateTimeOffset CapturedUtc = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly IRecoveryHealthEvaluator Evaluator = new DownloadHealthEvaluator();

    [Fact]
    public void Defaults_behave_exactly_as_the_hard_coded_rule_did()
    {
        var findings = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-31)), DownloadHealthRulesSettings.Default);

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.NoThroughput, finding.Kind);
    }

    [Fact]
    public void No_progress_rule_switched_off_never_fires()
    {
        var rules = DownloadHealthRulesSettings.Default with { NoProgressEnabled = false };
        var findings = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-60)), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.NoThroughput);
    }

    [Fact]
    public void No_progress_minutes_is_a_setting_not_a_constant()
    {
        var rules = DownloadHealthRulesSettings.Default with { NoProgressMinutes = 10 };

        var tooSoonUnderDefault = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-11)), DownloadHealthRulesSettings.Default);
        var flaggedUnderCustom = Evaluate(CreateItem(speedMbps: 0, addedUtc: CapturedUtc.AddMinutes(-11)), rules);

        Assert.DoesNotContain(tooSoonUnderDefault, finding => finding.Kind == DownloadHealthStrikes.NoThroughput);
        Assert.Contains(flaggedUnderCustom, finding => finding.Kind == DownloadHealthStrikes.NoThroughput);
    }

    [Fact]
    public void Minimum_speed_rule_is_off_by_default()
    {
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-60)), DownloadHealthRulesSettings.Default);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Minimum_speed_rule_flags_a_download_under_the_floor_for_long_enough()
    {
        var rules = DownloadHealthRulesSettings.Default with { MinSpeedEnabled = true, MinSpeedKbps = 100, MinSpeedMinutes = 30 };
        // 0.001 MB/s == 1 KB/s, well under a 100 KB/s floor.
        var findings = Evaluate(CreateItem(speedMbps: 0.001, addedUtc: CapturedUtc.AddMinutes(-31)), rules);

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.LowSpeed, finding.Kind);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    [Fact]
    public void Minimum_speed_rule_leaves_a_download_above_the_floor_alone()
    {
        var rules = DownloadHealthRulesSettings.Default with { MinSpeedEnabled = true, MinSpeedKbps = 100, MinSpeedMinutes = 30 };
        // 0.5 MB/s == 500 KB/s, comfortably above the floor.
        var findings = Evaluate(CreateItem(speedMbps: 0.5, addedUtc: CapturedUtc.AddMinutes(-60)), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Fact]
    public void Minimum_peers_rule_is_off_by_default()
    {
        var findings = Evaluate(CreateItem(peers: 0, addedUtc: CapturedUtc.AddMinutes(-60)), DownloadHealthRulesSettings.Default);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowPeers);
    }

    [Fact]
    public void Minimum_peers_rule_flags_a_download_under_the_floor_for_long_enough()
    {
        var rules = DownloadHealthRulesSettings.Default with { MinPeersEnabled = true, MinPeers = 3, MinPeersMinutes = 30 };
        var findings = Evaluate(CreateItem(peers: 1, addedUtc: CapturedUtc.AddMinutes(-31)), rules);

        var finding = Assert.Single(findings);
        Assert.Equal(DownloadHealthStrikes.LowPeers, finding.Kind);
        Assert.True(DownloadHealthStrikes.Counts(finding.Kind));
    }

    [Fact]
    public void Minimum_peers_rule_leaves_a_well_seeded_download_alone()
    {
        var rules = DownloadHealthRulesSettings.Default with { MinPeersEnabled = true, MinPeers = 3, MinPeersMinutes = 30 };
        var findings = Evaluate(CreateItem(peers: 12, addedUtc: CapturedUtc.AddMinutes(-60)), rules);

        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowPeers);
    }

    private static IReadOnlyList<RecoveryHealthFinding> Evaluate(DownloadQueueItem item, DownloadHealthRulesSettings rules)
        => Evaluator.Evaluate(
            new RecoveryQueueSnapshot(
                item.Status,
                item.ErrorMessage,
                item.SourcePath,
                item.SpeedMbps,
                item.AddedUtc,
                item.EtaSeconds,
                item.ReleaseName,
                item.Peers),
            CapturedUtc,
            rules);

    private static DownloadQueueItem CreateItem(
        double speedMbps = 1.2,
        int peers = 4,
        DateTimeOffset? addedUtc = null)
        => new(
            Id: "queue-1",
            ClientId: "client-1",
            ClientName: "Test client",
            Protocol: "qbittorrent",
            MediaType: "movies",
            Title: "Example Movie",
            ReleaseName: "Example.Movie.2026.1080p.mkv",
            Category: "movies",
            Status: DownloadQueueStatuses.Downloading,
            Progress: 50,
            SpeedMbps: speedMbps,
            EtaSeconds: 600,
            SizeBytes: 1_000_000_000,
            DownloadedBytes: 500_000_000,
            Peers: peers,
            IndexerName: "Test indexer",
            ErrorMessage: null,
            AddedUtc: addedUtc ?? CapturedUtc.AddMinutes(-10),
            SourcePath: "D:\\downloads\\example.mkv");
}
