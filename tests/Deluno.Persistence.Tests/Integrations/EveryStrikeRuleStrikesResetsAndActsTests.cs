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
/// #1193: every strike rule works the same way, at any percent below complete. A check where the problem holds adds
/// one strike, the first check where it is gone clears them, and at the person's strike limit the download is
/// due for the rule's action. Shown here at 99.95%, where a torrent missing its last piece really is stuck.
/// </summary>
public sealed class EveryStrikeRuleStrikesResetsAndActsTests
{
    private const double Progress = 99.95;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    public enum Condition { NoProgress, TooSlow, LowPeers, ClientStalled }

    [Theory]
    [InlineData(Condition.NoProgress)]
    [InlineData(Condition.TooSlow)]
    [InlineData(Condition.LowPeers)]
    [InlineData(Condition.ClientStalled)]
    public async Task A_check_adds_a_strike_recovery_clears_them_and_the_limit_is_reached_in_a_row(Condition condition)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-09T09:00:00Z"));
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        var repository = new SqliteDownloadHealthRepository(storage.Factory, time);
        // The client-reported stall is the always-on finding, struck at the platform-wide limit with no rule needed.
        var rules = condition == Condition.ClientStalled ? Array.Empty<DownloadHealthRule>() : [Rule(condition)];
        var added = time.GetUtcNow().AddHours(-3);

        async Task<int> CheckAsync(bool bad)
        {
            var findings = new DownloadHealthEvaluator().Evaluate(Snapshot(condition, bad, added), time.GetUtcNow(), rules);
            var counted = findings.Where(finding => DownloadHealthStrikes.Counts(finding.Kind)).ToArray();
            Assert.Equal(bad ? 1 : 0, counted.Length);
            var observations = counted.Select(finding => new DownloadHealthObservation(
                "client-1", "queue-1", "Example.Movie.2026.1080p", finding.Kind, finding.Severity, finding.Evidence,
                finding.RuleId, finding.RuleStrikeLimit, finding.RuleTimerMinutes, finding.RuleActionPreset)).ToArray();
            await repository.RecordDownloadHealthObservationsAsync("client-1", ["queue-1"], observations, Window, TimeSpan.FromHours(24), CancellationToken.None);
            var records = await repository.ListDownloadHealthRecordsAsync(50, CancellationToken.None);
            var record = records.SingleOrDefault();
            time.Advance(Window + TimeSpan.FromMinutes(1));
            return record?.StrikeCount ?? 0;
        }

        Assert.Equal(1, await CheckAsync(bad: true));
        Assert.Equal(2, await CheckAsync(bad: true));
        // Recovered: speed back over the limit, progress made, peers back. Strikes go to nothing.
        Assert.Equal(0, await CheckAsync(bad: false));
        // Then three bad checks in a row reach the limit of three.
        Assert.Equal(1, await CheckAsync(bad: true));
        Assert.Equal(2, await CheckAsync(bad: true));
        Assert.Equal(3, await CheckAsync(bad: true));
    }

    [Fact]
    public void A_stuck_download_at_99_99_percent_is_only_ever_finished_when_it_is_complete()
    {
        Assert.False(DownloadHealthStrikes.IsFinishedOrHandedOff(DownloadQueueStatuses.Downloading, 99.99));
        Assert.False(DownloadHealthStrikes.IsFinishedOrHandedOff(DownloadQueueStatuses.Stalled, 99.999));
        Assert.True(DownloadHealthStrikes.IsFinishedOrHandedOff(DownloadQueueStatuses.Downloading, 100));
    }

    private static RecoveryQueueSnapshot Snapshot(Condition condition, bool bad, DateTimeOffset added)
    {
        var status = condition == Condition.ClientStalled && bad ? DownloadQueueStatuses.Stalled : DownloadQueueStatuses.Downloading;
        var speed = condition switch
        {
            Condition.NoProgress => bad ? 0d : 1.5,
            Condition.TooSlow => bad ? 0.001 : 1.5, // 1 KB/s against a 100 KB/s limit
            _ => 1.5
        };
        var peers = condition == Condition.LowPeers && bad ? 0 : 6;
        return new RecoveryQueueSnapshot(
            status, null, null, speed, added, 0, "Example.Movie.2026.1080p", peers,
            ProgressPercent: Progress, SizeBytes: 1_000_000_000);
    }

    private static DownloadHealthRule Rule(Condition condition)
    {
        var kind = condition switch
        {
            Condition.NoProgress => DownloadHealthRuleKinds.NoProgress,
            Condition.TooSlow => DownloadHealthRuleKinds.TooSlow,
            Condition.LowPeers => DownloadHealthRuleKinds.LowPeers,
            _ => DownloadHealthRuleKinds.ClientFailure
        };
        return new DownloadHealthRule(
            Id: "rule-" + kind, Kind: kind, Enabled: true, StrikeLimit: 3, TimerMinutes: 30,
            AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
            ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue,
            Threshold: condition switch { Condition.TooSlow => 100, Condition.LowPeers => 3, _ => 0 });
    }
}
