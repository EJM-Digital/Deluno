using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Policies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1205, #1206, #1207: a person's strike rule keeps striking a download through the stall flip, one download is struck
/// by one rule per check, each removal says its own rule's reason, and No progress judges by bytes, not reported speed.
/// </summary>
public sealed class AStarvedOrStuckDownloadKeepsItsOwnRulesStrikesTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-10T09:00:00Z");

    [Fact]
    public async Task A_peers_starved_download_that_flips_to_stalled_between_checks_is_struck_each_check_and_reaches_its_count()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        var repository = await RepositoryAsync(storage, time);
        var rules = new[] { Rule(DownloadHealthRuleKinds.LowPeers, threshold: 2, strikes: 3) };

        // Check 1 reads downloading, checks 2 and 3 read stalled (the flip after one quiet interval).
        var statuses = new[] { DownloadQueueStatuses.Downloading, DownloadQueueStatuses.Stalled, DownloadQueueStatuses.Stalled };
        DownloadHealthRecord? last = null;
        for (var check = 0; check < statuses.Length; check++)
        {
            var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(statuses[check], peers: 1, speed: 0, grew: false), time.GetUtcNow(), rules);
            var counted = Assert.Single(findings, finding => DownloadHealthStrikes.Counts(finding.Kind));
            Assert.Equal(DownloadHealthStrikes.LowPeers, counted.Kind);
            last = Assert.Single(await Record(repository, findings));
            Assert.Equal(check + 1, last.StrikeCount);
            time.Advance(Window + TimeSpan.FromSeconds(1));
        }

        Assert.True(last!.BlocksCandidate(time.GetUtcNow()));
        Assert.Equal(DownloadHealthStrikes.LowPeers, last.Kind);
    }

    [Fact]
    public async Task Recovery_above_the_peer_floor_still_resets_the_strikes_of_a_stalled_download()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        var repository = await RepositoryAsync(storage, time);
        var rules = new[] { Rule(DownloadHealthRuleKinds.LowPeers, threshold: 2, strikes: 3) };

        var struck = await Record(repository, new DownloadHealthEvaluator().Evaluate(Snapshot(DownloadQueueStatuses.Stalled, peers: 0, speed: 0, grew: false), time.GetUtcNow(), rules));
        Assert.Equal(1, Assert.Single(struck).StrikeCount);
        time.Advance(Window + TimeSpan.FromSeconds(1));

        // Peers are back: no peers finding, so the record ends.
        var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(DownloadQueueStatuses.Stalled, peers: 5, speed: 0, grew: true), time.GetUtcNow(), rules);
        Assert.DoesNotContain(findings, finding => finding.Kind == DownloadHealthStrikes.LowPeers);
        await Record(repository, findings);
        Assert.DoesNotContain(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None), record => record.Kind == DownloadHealthStrikes.LowPeers);
    }

    [Theory]
    [InlineData(DownloadQueueStatuses.Downloading)]
    [InlineData(DownloadQueueStatuses.Stalled)]
    public void With_peers_and_no_progress_both_on_one_download_is_struck_once_and_by_the_peers_rule(string status)
    {
        var rules = new[] { Rule(DownloadHealthRuleKinds.LowPeers, threshold: 2, strikes: 3), Rule(DownloadHealthRuleKinds.NoProgress, threshold: 0, strikes: 3) };

        var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(status, peers: 0, speed: 0, grew: false), Now, rules);

        var counted = Assert.Single(findings, finding => DownloadHealthStrikes.Counts(finding.Kind));
        Assert.Equal(DownloadHealthStrikes.LowPeers, counted.Kind);
    }

    [Fact]
    public void The_platform_fallback_yields_to_a_peers_rule_that_is_striking_but_stays_when_that_rule_is_not()
    {
        var rules = new[] { Rule(DownloadHealthRuleKinds.LowPeers, threshold: 2, strikes: 3) };

        var starved = new DownloadHealthEvaluator().Evaluate(Snapshot(DownloadQueueStatuses.Stalled, peers: 0, speed: 0, grew: false), Now, rules);
        Assert.DoesNotContain(starved, finding => finding.Kind == DownloadHealthStrikes.ClientStalled);
        Assert.Contains(starved, finding => finding.Kind == DownloadHealthStrikes.LowPeers);

        // Plenty of peers: the person's rule is not striking, so the fallback is not stolen.
        var healthySwarm = new DownloadHealthEvaluator().Evaluate(Snapshot(DownloadQueueStatuses.Stalled, peers: 9, speed: 0, grew: false), Now, rules);
        Assert.Contains(healthySwarm, finding => finding.Kind == DownloadHealthStrikes.ClientStalled);
        Assert.DoesNotContain(healthySwarm, finding => finding.Kind == DownloadHealthStrikes.LowPeers);
    }

    [Fact]
    public void A_dead_torrent_and_a_too_slow_crawl_are_still_judged_after_the_stall_flip()
    {
        var dead = new DownloadHealthEvaluator().Evaluate(
            Snapshot(DownloadQueueStatuses.Stalled, peers: 0, speed: 0, grew: false) with { AddedUtc = Now.AddHours(-2) }, Now,
            [Rule(DownloadHealthRuleKinds.DeadTorrent, threshold: 30, strikes: 3)]);
        Assert.Contains(dead, finding => finding.Kind == DownloadHealthStrikes.DeadTorrent);

        var crawl = new DownloadHealthEvaluator().Evaluate(
            Snapshot(DownloadQueueStatuses.Stalled, peers: 5, speed: 0.00005, grew: true), Now,
            [Rule(DownloadHealthRuleKinds.TooSlow, threshold: 75, strikes: 3)]);
        Assert.Contains(crawl, finding => finding.Kind == DownloadHealthStrikes.LowSpeed);
    }

    [Theory]
    [InlineData(0.000057, false, true)]   // Deluge on a stuck torrent: a speed, but no bytes
    [InlineData(0.0, false, true)]
    [InlineData(0.000057, true, false)]   // a crawl that does add bytes is progress
    [InlineData(0.0, true, false)]        // bytes grew, whatever the speed reads
    [InlineData(0.000057, null, false)]   // no earlier reading: falls back to the reported speed
    [InlineData(0.0, null, true)]
    public void No_progress_judges_by_downloaded_bytes_not_by_the_reported_speed(double speed, bool? grew, bool struck)
    {
        var rules = new[] { Rule(DownloadHealthRuleKinds.NoProgress, threshold: 0, strikes: 3) };

        var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(DownloadQueueStatuses.Downloading, peers: 8, speed: speed, grew: grew), Now, rules);

        Assert.Equal(struck, findings.Any(finding => finding.Kind == DownloadHealthStrikes.NoThroughput));
    }

    [Fact]
    public async Task The_bytes_seen_at_the_last_check_are_kept_across_a_restart_and_say_whether_they_grew()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Now);
        var repository = await RepositoryAsync(storage, time);

        var first = await repository.ObserveDownloadProgressAsync("client-1", [("q1", 1_000L)], CancellationToken.None);
        Assert.Null(first["q1"].BytesGrew);

        // A new repository over the same database is a restart: nothing is held in memory.
        time.Advance(Window);
        var restarted = new SqliteDownloadHealthRepository(storage.Factory, time);
        var stuck = await restarted.ObserveDownloadProgressAsync("client-1", [("q1", 1_000L)], CancellationToken.None);
        Assert.False(stuck["q1"].BytesGrew);

        time.Advance(Window);
        var moving = await restarted.ObserveDownloadProgressAsync("client-1", [("q1", 4_096L)], CancellationToken.None);
        Assert.True(moving["q1"].BytesGrew);
    }

    [Theory]
    [InlineData(DownloadHealthStrikes.NoThroughput, DownloadHealthRuleKinds.NoProgress, 0, "It made no progress for 3 checks in a row, so Deluno")]
    [InlineData(DownloadHealthStrikes.LowPeers, DownloadHealthRuleKinds.LowPeers, 2, "It had fewer than 2 peers for 3 checks in a row, so Deluno")]
    [InlineData(DownloadHealthStrikes.LowSpeed, DownloadHealthRuleKinds.TooSlow, 75, "It stayed under 75 KB/s for 3 checks in a row, so Deluno")]
    [InlineData(DownloadHealthStrikes.DeadTorrent, DownloadHealthRuleKinds.DeadTorrent, 30, "It had no seeds or peers left for 3 checks in a row, so Deluno")]
    [InlineData(DownloadHealthStrikes.MagnetMetadata, DownloadHealthRuleKinds.MagnetMetadata, 0, "It was still fetching its magnet metadata for 3 checks in a row, so Deluno")]
    [InlineData(DownloadHealthStrikes.ClientFailure, DownloadHealthRuleKinds.ClientFailure, 0, "Its download client reported it as stalled or in error for 3 checks in a row, so Deluno")]
    [InlineData(DownloadHealthStrikes.DangerousFile, DownloadHealthRuleKinds.DangerousFile, 0, "It contained a file that matches your content blocklist, so Deluno")]
    public void Each_rules_removal_says_in_plain_words_why_that_rule_removed_it(string kind, string ruleKind, double threshold, string says)
    {
        var rule = Rule(ruleKind, threshold, strikes: 3);
        var finding = new DownloadHealthFinding("warning", kind, "x", "y", "z", true, false, StrikeCount: 3, RuleId: rule.Id, RuleStrikeLimit: 3);

        var reason = DownloadClientTelemetryService.StalledReason(finding, "qBittorrent", Now, rule);

        Assert.StartsWith(says, reason, StringComparison.Ordinal);
        if (kind != DownloadHealthStrikes.ClientFailure)
        {
            Assert.DoesNotContain("stalled or in error", reason);
        }

        Assert.DoesNotContain("minutes", reason);
        Assert.DoesNotContain("hours", reason);
    }

    [Fact]
    public void A_peers_removal_after_four_minutes_does_not_borrow_the_stall_sentence()
    {
        var rule = Rule(DownloadHealthRuleKinds.LowPeers, threshold: 1, strikes: 1);
        var finding = new DownloadHealthFinding("warning", DownloadHealthStrikes.LowPeers, "x", "y", "z", true, false,
            StrikeCount: 1, RuleId: rule.Id, RuleStrikeLimit: 1, SpellStartedUtc: Now.AddMinutes(-4));

        Assert.StartsWith("It had fewer than 1 peer for 1 check, so Deluno", DownloadClientTelemetryService.StalledReason(finding, "qBittorrent", Now, rule));
    }

    private static async Task<SqliteDownloadHealthRepository> RepositoryAsync(TestStorage storage, FixedTimeProvider time)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteDownloadHealthRepository(storage.Factory, time);
    }

    private static async Task<IReadOnlyList<DownloadHealthRecord>> Record(SqliteDownloadHealthRepository repository, IReadOnlyList<RecoveryHealthFinding> findings)
    {
        var observations = findings.Where(finding => DownloadHealthStrikes.Counts(finding.Kind)).Select(finding => new DownloadHealthObservation(
            "client-1", "queue-1", "Example.Movie.2026.1080p", finding.Kind, finding.Severity, finding.Evidence,
            finding.RuleId, finding.RuleStrikeLimit, finding.RuleTimerMinutes, finding.RuleActionPreset)).ToArray();
        return await repository.RecordDownloadHealthObservationsAsync("client-1", ["queue-1"], observations, Window, TimeSpan.FromHours(24), CancellationToken.None);
    }

    private static RecoveryQueueSnapshot Snapshot(string status, int peers, double speed, bool? grew)
        => new(status, null, null, speed, Now.AddHours(-3), 0, "Example.Movie.2026.1080p", peers,
            ProgressPercent: 40, SizeBytes: 1_000_000_000, BytesGrewSinceLastCheck: grew);

    private static DownloadHealthRule Rule(string kind, double threshold, int strikes)
        => new(
            Id: "rule-" + kind, Kind: kind, Enabled: true, StrikeLimit: strikes, TimerMinutes: 5,
            AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
            ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue, Threshold: threshold);
}
