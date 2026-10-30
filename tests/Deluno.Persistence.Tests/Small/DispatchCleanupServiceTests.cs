using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.RecordContracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Deluno.Recovery.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Small;

/// <summary>
/// #1268: the cleanup pass that archives failed dispatches older than a week and prunes old
/// telemetry, completed jobs and saved repeat keys. Nothing a dispatch is still waiting on is archived,
/// and a housekeeping step that fails never stops the others or the recovery pass.
/// </summary>
public sealed class DispatchCleanupServiceTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");

    private static DownloadDispatchItem Dispatch(string id, string? grabStatus, string? grabCode, string? importCode)
        => ((DownloadDispatchItem)RecordContractCoverageTests.Sample(typeof(DownloadDispatchItem), 1)!) with
        {
            Id = id,
            CreatedUtc = Now.AddDays(-10),
            GrabStatus = grabStatus,
            GrabFailureCode = grabCode,
            ImportFailureCode = importCode,
        };

    private sealed class Rig
    {
        public List<(string Id, string Reason)> Archived { get; } = [];
        public List<(string Id, string Type, string? Details)> Timeline { get; } = [];
        public TimeSpan? MinAge { get; private set; }
        public int? Limit { get; private set; }
        public IReadOnlyList<DownloadDispatchItem> Stale { get; init; } = [];

        public IDownloadDispatchesRepository Dispatches => InterfaceStub.Create<IDownloadDispatchesRepository>((name, args) =>
        {
            switch (name)
            {
                case "FindStaleFailedDispatchesAsync":
                    MinAge = (TimeSpan)args[0]!;
                    Limit = (int)args[1]!;
                    return Task.FromResult(Stale);
                case "ArchiveDispatchAsync":
                    Archived.Add(((string)args[0]!, (string)args[1]!));
                    return Task.CompletedTask;
                case "RecordTimelineEventAsync":
                    Timeline.Add(((string)args[0]!, (string)args[1]!, (string?)args[2]));
                    return Task.FromResult<DispatchTimelineEvent>(null!);
                default:
                    throw new NotSupportedException(name);
            }
        });

        public DispatchCleanupService Service(
            IIndexerQueryStatsRepository? stats = null,
            IJobQueueRepository? jobs = null,
            IAutomationIdempotencyStore? keys = null)
            => new(Dispatches, new FixedTimeProvider(Now), NullLogger<DispatchCleanupService>.Instance, stats, jobs, keys);
    }

    [Fact]
    public async Task With_nothing_stale_and_no_housekeeping_the_pass_reports_none_found()
    {
        var rig = new Rig();

        var result = await rig.Service().RunCleanupPassAsync(Token);

        Assert.Equal((0, 0, "No stale dispatches found."), (result.ArchivedCount, result.SkippedCount, result.Summary));
        Assert.Equal(TimeSpan.FromDays(7), rig.MinAge);
        Assert.Equal(50, rig.Limit);
        Assert.Empty(rig.Archived);
    }

    [Fact]
    public async Task A_failed_dispatch_is_archived_but_one_still_waiting_on_something_is_left_alone()
    {
        var rig = new Rig
        {
            Stale =
            [
                Dispatch("gave-up", "failed", "client-rejected", null),
                Dispatch("retries", "planned", "paused", "max-retries-exceeded"),
                Dispatch("import-failed", "succeeded", null, "file-missing"),
                Dispatch("paused", "failed", "paused", null),
                Dispatch("not-found", "failed", "not_found", null),
                Dispatch("planned-status", "planned", null, null),
                Dispatch("circuit", "circuit_open", null, null),
                Dispatch("circuit-code", null, "circuit_open", null),
                Dispatch("planned-code", null, "planned", null),
                Dispatch("no-codes", null, null, null),
            ]
        };

        var result = await rig.Service().RunCleanupPassAsync(Token);

        Assert.Equal(["gave-up", "retries", "import-failed", "no-codes"], rig.Archived.Select(a => a.Id));
        Assert.All(rig.Archived, a => Assert.Equal("auto-cleanup: dispatch exceeded retention period", a.Reason));
        Assert.Equal(["gave-up", "retries", "import-failed", "no-codes"], rig.Timeline.Select(t => t.Id));
        var first = rig.Timeline[0];
        Assert.Equal("auto-archived", first.Type);
        Assert.Equal("{\"reason\":\"Exceeded retention period\",\"policy\":\"archive\",\"agedays\":10}", first.Details);
        Assert.Equal((4, 6, "Archived 4 stale dispatch(es); skipped 6."), (result.ArchivedCount, result.SkippedCount, result.Summary));
    }

    [Fact]
    public async Task A_pass_that_only_skips_still_says_what_it_skipped()
    {
        var rig = new Rig { Stale = [Dispatch("waiting", "planned", null, null)] };

        var result = await rig.Service().RunCleanupPassAsync(Token);

        Assert.Equal((0, 1, "Archived 0 stale dispatch(es); skipped 1."), (result.ArchivedCount, result.SkippedCount, result.Summary));
    }

    [Fact]
    public async Task Housekeeping_prunes_telemetry_after_thirty_days_jobs_after_a_year_and_repeat_keys_after_a_week()
    {
        DateTimeOffset? statsCutoff = null, jobsCutoff = null, keysCutoff = null;
        var stats = InterfaceStub.Create<IIndexerQueryStatsRepository>((_, args) =>
        {
            statsCutoff = (DateTimeOffset)args[0]!;
            return Task.FromResult(12);
        });
        var jobs = InterfaceStub.Create<IJobQueueRepository>((_, args) =>
        {
            jobsCutoff = (DateTimeOffset)args[0]!;
            return Task.FromResult(3);
        });
        var keys = InterfaceStub.Create<IAutomationIdempotencyStore>((_, args) =>
        {
            keysCutoff = (DateTimeOffset)args[0]!;
            return Task.FromResult(0);
        });

        await new Rig().Service(stats, jobs, keys).RunCleanupPassAsync(Token);

        Assert.Equal(Now.AddDays(-30), statsCutoff);
        Assert.Equal(Now.AddDays(-365), jobsCutoff);
        Assert.Equal(Now.AddDays(-7), keysCutoff);
    }

    [Fact]
    public async Task A_housekeeping_step_that_fails_does_not_stop_the_others_or_the_pass()
    {
        var calls = new List<string>();
        var stats = InterfaceStub.Create<IIndexerQueryStatsRepository>((_, _) => { calls.Add("stats"); throw new InvalidOperationException("db busy"); });
        var jobs = InterfaceStub.Create<IJobQueueRepository>((_, _) => { calls.Add("jobs"); throw new InvalidOperationException("db busy"); });
        var keys = InterfaceStub.Create<IAutomationIdempotencyStore>((_, _) => { calls.Add("keys"); throw new InvalidOperationException("db busy"); });

        var result = await new Rig().Service(stats, jobs, keys).RunCleanupPassAsync(Token);

        Assert.Equal(["stats", "jobs", "keys"], calls);
        Assert.Equal("No stale dispatches found.", result.Summary);
    }

    [Theory]
    [InlineData("stats")]
    [InlineData("jobs")]
    [InlineData("keys")]
    public async Task A_cancelled_housekeeping_step_cancels_the_pass(string step)
    {
        static Func<string, object?[], object?> Cancel() => (_, _) => throw new OperationCanceledException();
        static Func<string, object?[], object?> Zero() => (_, _) => Task.FromResult(0);
        var stats = InterfaceStub.Create<IIndexerQueryStatsRepository>(step == "stats" ? Cancel() : Zero());
        var jobs = InterfaceStub.Create<IJobQueueRepository>(step == "jobs" ? Cancel() : Zero());
        var keys = InterfaceStub.Create<IAutomationIdempotencyStore>(step == "keys" ? Cancel() : Zero());

        await Assert.ThrowsAsync<OperationCanceledException>(() => new Rig().Service(stats, jobs, keys).RunCleanupPassAsync(Token));
    }
}
