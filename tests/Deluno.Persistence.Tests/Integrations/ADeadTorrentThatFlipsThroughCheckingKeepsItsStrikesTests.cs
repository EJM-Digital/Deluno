using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Policies;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// #1476, seen on the rig: three torrents sat at 0% with no-progress strikes at 2 of 3, then started again from zero
/// and were never replaced. A dead torrent that the client shows for one poll as checking (qBittorrent
/// <c>notTransferring</c>) or queued is listed without the no-progress finding, because that rule does not apply to
/// the status. The record ended as "recovered" and its strikes were lost. The torrent here is qBittorrent 5.2.1's
/// recorded <c>stalled</c> torrent (a dead tracker, nothing ever arrives); the statuses are Deluno's own.
/// </summary>
public sealed class ADeadTorrentThatFlipsThroughCheckingKeepsItsStrikesTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Step = Window + TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-05T19:47:00Z");
    private const string ClientId = "client-1";

    [Fact]
    public async Task Stalled_then_checking_then_stalled_with_no_bytes_gained_keeps_counting_and_reaches_the_third_strike()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var repository = await RepositoryAsync(storage, time);
        var torrent = RecordedTorrent("stalled");
        var rules = new[] { NoProgressRule() };

        var strikes = new List<int>();
        foreach (var status in new[] { "stalled", DownloadQueueStatuses.NotTransferring, "stalled", DownloadQueueStatuses.NotTransferring, "stalled" })
        {
            await repository.ObserveDownloadProgressAsync(ClientId, [(torrent.Hash, torrent.Downloaded)], CancellationToken.None);
            await ReadAsync(repository, time, torrent, status, rules);
            var record = (await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None))
                .SingleOrDefault(candidate => candidate.Kind == DownloadHealthStrikes.NoThroughput);
            strikes.Add(record?.StrikeCount ?? 0);
            time.Advance(Step);
        }

        // stalled: 1, checking: held at 1, stalled: 2, checking: held at 2, stalled: 3.
        Assert.Equal([1, 1, 2, 2, 3], strikes);
        var last = Assert.Single(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));
        Assert.True(last.BlocksCandidate(time.GetUtcNow()));
    }

    private static async Task ReadAsync(
        SqliteDownloadHealthRepository repository, FixedTimeProvider time, Torrent torrent, string status, DownloadHealthRule[] rules)
    {
        var findings = new DownloadHealthEvaluator().Evaluate(
            new RecoveryQueueSnapshot(status, null, null, 0, Start.AddMinutes(-60), 0, torrent.Name, 0,
                ProgressPercent: 0, SizeBytes: torrent.Size, BytesGrewSinceLastCheck: false),
            time.GetUtcNow(), rules);
        await RecordAsync(repository, torrent, status, findings);
    }

    private static async Task RecordAsync(
        SqliteDownloadHealthRepository repository, Torrent torrent, string status, IReadOnlyList<RecoveryHealthFinding> findings)
    {
        var observations = findings.Where(finding => DownloadHealthStrikes.Counts(finding.Kind)).Select(finding => new DownloadHealthObservation(
            ClientId, torrent.Hash, torrent.Name, finding.Kind, finding.Severity, finding.Evidence,
            finding.RuleId, finding.RuleStrikeLimit, finding.RuleTimerMinutes, finding.RuleActionPreset)).ToArray();
        await repository.RecordDownloadHealthObservationsAsync(
            ClientId, [torrent.Hash], observations, Window, TimeSpan.FromHours(24), CancellationToken.None);
    }

    private sealed record Torrent(string Hash, string Name, long Downloaded, long Size);

    private static Torrent RecordedTorrent(string state)
    {
        var row = JsonNode.Parse(RecordedReplies.Qbittorrent(state).Body("torrents/info"))![0]!;
        return new Torrent(row["hash"]!.GetValue<string>(), row["name"]!.GetValue<string>(),
            row["downloaded"]!.GetValue<long>(), row["size"]!.GetValue<long>());
    }

    private static async Task<SqliteDownloadHealthRepository> RepositoryAsync(TestStorage storage, FixedTimeProvider time)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteDownloadHealthRepository(storage.Factory, time);
    }

    private static DownloadHealthRule NoProgressRule()
        => new(
            Id: "rule-no-progress", Kind: DownloadHealthRuleKinds.NoProgress, Enabled: true, StrikeLimit: 3, TimerMinutes: 5,
            AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
            ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue, Threshold: 0);
}
