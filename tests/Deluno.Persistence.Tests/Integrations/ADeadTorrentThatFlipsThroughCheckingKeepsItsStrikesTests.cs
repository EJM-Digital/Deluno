using System.Text.Json.Nodes;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Recovery.Policies;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.Logging;
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

    [Theory]
    [InlineData(DownloadQueueStatuses.NotTransferring)]
    [InlineData(DownloadQueueStatuses.Queued)]
    [InlineData(DownloadQueueStatuses.Paused)]
    public async Task Any_reading_where_the_rule_does_not_apply_holds_the_strikes_without_adding_one(string status)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var repository = await RepositoryAsync(storage, time);
        var torrent = RecordedTorrent("stalled");
        var rules = new[] { NoProgressRule() };
        await ReadAsync(repository, time, torrent, "stalled", rules);
        time.Advance(Step);
        await ReadAsync(repository, time, torrent, "stalled", rules);

        for (var poll = 0; poll < 4; poll++)
        {
            time.Advance(Step);
            await ReadAsync(repository, time, torrent, status, rules);
            Assert.Equal(2, Assert.Single(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None)).StrikeCount);
        }
    }

    /// <summary>
    /// The checking reading is each client's own recorded reply, read by the client's real status normaliser, not
    /// Deluno's status written by hand: qBittorrent 5.2.1's <c>checkingDL</c> after a recheck, and Transmission 4.1.3's
    /// verifying (2) and queued-to-verify (1) torrents. The dead torrent flips stalled, checking, stalled, checking,
    /// stalled with no bytes gained and still reaches its third strike.
    /// </summary>
    [Theory]
    [InlineData("qbittorrent", "checking-download")]
    [InlineData("transmission", "verifying")]
    [InlineData("transmission", "queued-to-verify")]
    public async Task A_recorded_checking_reading_from_the_client_holds_the_strikes_of_a_dead_torrent(string app, string state)
    {
        var readings = RecordedStatuses(app, state);
        // The recorded torrents are the ones being checked or waiting to be: each must read as a pause, none as downloading.
        var pause = Assert.Single(readings.Where(reading => DownloadHealthStrikes.PausesTransferFindings(reading, 0)).Distinct().Take(1));

        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var repository = await RepositoryAsync(storage, time);
        var torrent = RecordedTorrent("stalled");
        var rules = new[] { NoProgressRule() };
        var strikes = new List<int>();
        foreach (var status in new[] { "stalled", pause, "stalled", pause, "stalled" })
        {
            await repository.ObserveDownloadProgressAsync(ClientId, [(torrent.Hash, torrent.Downloaded)], CancellationToken.None);
            await ReadAsync(repository, time, torrent, status, rules);
            strikes.Add((await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None)).Single().StrikeCount);
            time.Advance(Step);
        }

        Assert.Equal([1, 1, 2, 2, 3], strikes);
    }

    private static IReadOnlyList<string> RecordedStatuses(string app, string state)
    {
        var statuses = new List<string>();
        if (app == "qbittorrent")
        {
            var client = new QbittorrentDownloadClient();
            var recording = RecordedReplies.Qbittorrent(state);
            foreach (var row in JsonNode.Parse(recording.Body("torrents/info"))!.AsArray())
            {
                statuses.Add(client.NormalizeStatus(row!["state"]!.GetValue<string>(), row["progress"]!.GetValue<double>(), null, null));
            }
        }
        else
        {
            var client = new TransmissionDownloadClient(null!);
            foreach (var exchange in RecordedReplies.Transmission(state).Exchanges.Where(e => e.Reply.Body?.Contains("\"torrents\"") == true))
            {
                foreach (var row in JsonNode.Parse(exchange.Reply.Body!)!["arguments"]!["torrents"]!.AsArray())
                {
                    statuses.Add(client.NormalizeStatus(row!["status"]!.GetValue<int>().ToString(), row["percentDone"]!.GetValue<double>(), null, null));
                }
            }
        }

        return statuses;
    }

    [Fact]
    public void The_recorded_queued_torrent_reads_as_a_pause_and_the_recorded_stalled_one_does_not()
    {
        var registry = new DownloadClientRegistry([new QbittorrentDownloadClient()]);
        Assert.True(registry.TryGet("qbittorrent", out var client));
        var queued = JsonNode.Parse(RecordedReplies.Qbittorrent("queued").Body("torrents/info"))![0]!;
        var stalled = JsonNode.Parse(RecordedReplies.Qbittorrent("stalled").Body("torrents/info"))![0]!;

        var queuedStatus = client.NormalizeStatus(queued["state"]!.GetValue<string>(), queued["progress"]!.GetValue<double>(), null, null);
        var stalledStatus = client.NormalizeStatus(stalled["state"]!.GetValue<string>(), stalled["progress"]!.GetValue<double>(), null, null);

        Assert.True(DownloadHealthStrikes.PausesTransferFindings(queuedStatus, 0));
        Assert.False(DownloadHealthStrikes.PausesTransferFindings(stalledStatus, 0));
    }

    [Fact]
    public async Task Bytes_gained_while_it_was_checking_are_real_recovery_and_end_the_record()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var repository = await RepositoryAsync(storage, time);
        var torrent = RecordedTorrent("stalled");
        var rules = new[] { NoProgressRule() };
        await repository.ObserveDownloadProgressAsync(ClientId, [(torrent.Hash, 0)], CancellationToken.None);
        await ReadAsync(repository, time, torrent, "stalled", rules);
        Assert.Single(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));

        time.Advance(Step);
        await repository.ObserveDownloadProgressAsync(ClientId, [(torrent.Hash, 4_096_000)], CancellationToken.None);
        await ReadAsync(repository, time, torrent, DownloadQueueStatuses.NotTransferring, rules);

        Assert.Empty(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));
    }

    [Theory]
    [InlineData("downloading")]
    [InlineData("importReady")]
    public async Task A_download_that_is_moving_again_or_finished_ends_the_record(string status)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var repository = await RepositoryAsync(storage, time);
        var torrent = RecordedTorrent("stalled");
        var rules = new[] { NoProgressRule() };
        await ReadAsync(repository, time, torrent, "stalled", rules);

        time.Advance(Step);
        await RecordAsync(repository, torrent, status, []);

        Assert.Empty(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_paused_record_ends_when_the_download_leaves_the_client()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var repository = await RepositoryAsync(storage, time);
        var torrent = RecordedTorrent("stalled");
        var rules = new[] { NoProgressRule() };
        await ReadAsync(repository, time, torrent, "stalled", rules);
        time.Advance(Step);
        await ReadAsync(repository, time, torrent, DownloadQueueStatuses.NotTransferring, rules);
        Assert.Single(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));

        // The client answered and no longer holds it (#1223): not listed, not held.
        time.Advance(Step);
        await repository.RecordDownloadHealthObservationsAsync(
            ClientId, [], [], Window, TimeSpan.FromHours(24), CancellationToken.None, everythingTheClientHolds: []);

        Assert.Empty(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_pause_longer_than_the_inactivity_window_still_starts_the_spell_again()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var repository = await RepositoryAsync(storage, time);
        var torrent = RecordedTorrent("stalled");
        var rules = new[] { NoProgressRule() };
        await ReadAsync(repository, time, torrent, "stalled", rules);
        time.Advance(Step);
        await ReadAsync(repository, time, torrent, "stalled", rules);
        Assert.Equal(2, Assert.Single(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None)).StrikeCount);

        // Paused for longer than the 24 hour window (#825): the checking polls do not refresh the record.
        time.Advance(TimeSpan.FromHours(10));
        await ReadAsync(repository, time, torrent, DownloadQueueStatuses.NotTransferring, rules);
        time.Advance(TimeSpan.FromHours(15));
        await ReadAsync(repository, time, torrent, DownloadQueueStatuses.NotTransferring, rules);
        await ReadAsync(repository, time, torrent, "stalled", rules);

        Assert.Equal(1, Assert.Single(await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None)).StrikeCount);
    }

    [Theory]
    [InlineData(DownloadHealthStrikes.LowAvailability, true)]
    [InlineData(DownloadHealthStrikes.ZeroPercentDownload, true)]
    [InlineData(DownloadHealthStrikes.DangerousFile, false)]
    public async Task Only_a_finding_about_getting_the_bytes_is_paused_by_a_checking_reading(string kind, bool kept)
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var repository = await RepositoryAsync(storage, time);
        var torrent = RecordedTorrent("stalled");
        await repository.RecordDownloadHealthObservationsAsync(
            ClientId, [torrent.Hash], [new DownloadHealthObservation(ClientId, torrent.Hash, torrent.Name, kind, "warning", "x")],
            Window, TimeSpan.FromHours(24), CancellationToken.None);

        time.Advance(Step);
        await repository.RecordDownloadHealthObservationsAsync(
            ClientId, [torrent.Hash], [], Window, TimeSpan.FromHours(24), CancellationToken.None,
            pausedQueueItemIds: [torrent.Hash]);

        Assert.Equal(kept, (await repository.ListDownloadHealthRecordsAsync(10, CancellationToken.None)).Count == 1);
    }

    private sealed class Lines : ILogger<SqliteDownloadHealthRepository>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task The_log_says_each_strike_each_pause_each_restart_and_each_way_a_record_ends()
    {
        using var storage = TestStorage.Create();
        var time = new FixedTimeProvider(Start);
        var lines = new Lines();
        var repository = await RepositoryAsync(storage, time, lines);
        var torrent = RecordedTorrent("stalled");
        var rules = new[] { NoProgressRule() };

        await ReadAsync(repository, time, torrent, "stalled", rules);
        time.Advance(Step);
        await ReadAsync(repository, time, torrent, "stalled", rules);
        time.Advance(Step);
        await ReadAsync(repository, time, torrent, DownloadQueueStatuses.NotTransferring, rules);
        Assert.Contains(lines.Messages, line => line.Contains("strike 1 of 3 added", StringComparison.Ordinal));
        Assert.Contains(lines.Messages, line => line.Contains("strike 2 of 3 added", StringComparison.Ordinal));
        Assert.Contains(lines.Messages, line => line.Contains("is paused at 2 strike(s)", StringComparison.Ordinal));

        // Paused for longer than the 24 hour window, the spell starts again.
        time.Advance(TimeSpan.FromHours(25));
        await ReadAsync(repository, time, torrent, "stalled", rules);
        Assert.Contains(lines.Messages, line => line.Contains("starts again", StringComparison.Ordinal));

        // The client still lists it but the finding no longer applies, then it leaves the client.
        time.Advance(Step);
        await RecordAsync(repository, torrent, "downloading", []);
        Assert.Contains(lines.Messages, line => line.Contains("the client lists it and the finding no longer applies", StringComparison.Ordinal));
        await ReadAsync(repository, time, torrent, "stalled", rules);
        time.Advance(Step);
        await repository.RecordDownloadHealthObservationsAsync(
            ClientId, [], [], Window, TimeSpan.FromHours(24), CancellationToken.None, everythingTheClientHolds: []);
        Assert.Contains(lines.Messages, line => line.Contains("the download left the client", StringComparison.Ordinal));
    }

    private static async Task ReadAsync(
        SqliteDownloadHealthRepository repository, FixedTimeProvider time, Torrent torrent, string status, DownloadHealthRule[] rules,
        IReadOnlyCollection<string>? held = null)
    {
        var findings = new DownloadHealthEvaluator().Evaluate(
            new RecoveryQueueSnapshot(status, null, null, 0, Start.AddMinutes(-60), 0, torrent.Name, 0,
                ProgressPercent: 0, SizeBytes: torrent.Size, BytesGrewSinceLastCheck: false),
            time.GetUtcNow(), rules);
        await RecordAsync(repository, torrent, status, findings, held);
    }

    private static async Task RecordAsync(
        SqliteDownloadHealthRepository repository, Torrent torrent, string status, IReadOnlyList<RecoveryHealthFinding> findings,
        IReadOnlyCollection<string>? held = null)
    {
        var observations = findings.Where(finding => DownloadHealthStrikes.Counts(finding.Kind)).Select(finding => new DownloadHealthObservation(
            ClientId, torrent.Hash, torrent.Name, finding.Kind, finding.Severity, finding.Evidence,
            finding.RuleId, finding.RuleStrikeLimit, finding.RuleTimerMinutes, finding.RuleActionPreset)).ToArray();
        await repository.RecordDownloadHealthObservationsAsync(
            ClientId, [torrent.Hash], observations, Window, TimeSpan.FromHours(24), CancellationToken.None,
            everythingTheClientHolds: held ?? [torrent.Hash],
            pausedQueueItemIds: DownloadHealthStrikes.PausesTransferFindings(status, 0) ? [torrent.Hash] : []);
    }

    private sealed record Torrent(string Hash, string Name, long Downloaded, long Size);

    private static Torrent RecordedTorrent(string state)
    {
        var row = JsonNode.Parse(RecordedReplies.Qbittorrent(state).Body("torrents/info"))![0]!;
        return new Torrent(row["hash"]!.GetValue<string>(), row["name"]!.GetValue<string>(),
            row["downloaded"]!.GetValue<long>(), row["size"]!.GetValue<long>());
    }

    private static async Task<SqliteDownloadHealthRepository> RepositoryAsync(TestStorage storage, FixedTimeProvider time, ILogger<SqliteDownloadHealthRepository>? logger = null)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, time);
        await new PlatformSchemaInitializer(storage.Factory, migrator, NullLogger<PlatformSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        return new SqliteDownloadHealthRepository(storage.Factory, time, logger: logger ?? NullLogger<SqliteDownloadHealthRepository>.Instance);
    }

    private static DownloadHealthRule NoProgressRule()
        => new(
            Id: "rule-no-progress", Kind: DownloadHealthRuleKinds.NoProgress, Enabled: true, StrikeLimit: 3, TimerMinutes: 5,
            AppliesTo: DownloadHealthAppliesTo.Both, MinCompletionPercent: 0, MaxCompletionPercent: 100,
            ResetStrikesOnProgress: true, ActionPreset: DownloadHealthActionPresets.Replace,
            CreatedUtc: DateTimeOffset.MinValue, UpdatedUtc: DateTimeOffset.MinValue, Threshold: 0);
}
