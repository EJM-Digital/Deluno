using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Recovery.Policies;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// Deluno has to run unattended. A warning about a download that has since
/// finished is not something the owner should ever have to clear, and one real
/// problem must not become a new warning every hour.
///
/// <para>All of this was seen on the E2E rig before the soak started. A film
/// was imported, and the hourly poller then raised "Download not detected in
/// qBittorrent" about it three times over the next five hours. Each counted as
/// an open problem, and each also added an activity entry and a recovery case on
/// the film saying the download never reached the client.</para>
/// </summary>
public sealed class DispatchProblemsSettleThemselvesTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-16T00:00:00Z");

    [Fact]
    public async Task An_imported_download_is_never_reported_as_not_detected()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        // Exactly the rig's row: grabbed, never stamped as detected by the
        // client, and imported anyway - through the processor callback.
        await InsertDispatchAsync(storage.Factory, "sintel", grabbedAgo: TimeSpan.FromHours(6),
            clock: clock, importStatus: "imported", importCompletedAgo: TimeSpan.FromHours(5));

        var (poller, problems, recovery) = CreatePoller(storage, clock);
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, report.GrabTimeoutsDetected);
        Assert.Equal(0, report.RecoveryCasesRecorded);
        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        // Nor a recovery case telling the owner the download never reached
        // the client - the user-facing half of the same false alarm.
        Assert.Equal(0, recovery.GrabTimeouts);
    }

    [Fact]
    public async Task An_archived_download_is_never_reported_as_not_detected()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "archived", grabbedAgo: TimeSpan.FromHours(6),
            clock: clock, status: "archived");

        var (poller, problems, recovery) = CreatePoller(storage, clock);
        await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        Assert.Equal(0, recovery.GrabTimeouts);
    }

    [Fact]
    public async Task A_download_that_is_really_stuck_is_recorded_once_not_every_hour()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "stuck", grabbedAgo: TimeSpan.FromHours(6), clock: clock);

        var (poller, problems, recovery) = CreatePoller(storage, clock);

        var first = await poller.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var second = await poller.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var third = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(1, first.GrabTimeoutsDetected);
        Assert.Equal(0, second.GrabTimeoutsDetected);
        Assert.Equal(0, third.GrabTimeoutsDetected);

        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));
        Assert.True(await problems.IsRecordedAsync("stuck", "grab-timeout", CancellationToken.None));
        // One recovery case, not one per hour.
        Assert.Equal(1, recovery.GrabTimeouts);
    }

    /// <summary>
    /// #537, seen on the rig: a person dismissed a stuck download's issue,
    /// and at the next hourly poll it was raised again, with a new import
    /// issue, because only open problems counted as already known.
    /// </summary>
    [Fact]
    public async Task A_stuck_download_a_person_acknowledged_is_not_reported_again()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertDispatchAsync(storage.Factory, "stuck", grabbedAgo: TimeSpan.FromHours(6), clock: clock);
        var (poller, problems, recovery) = CreatePoller(storage, clock);

        await poller.PollAsync(CancellationToken.None);
        Assert.Equal(1, await problems.AcknowledgeForDispatchAsync("stuck", CancellationToken.None));

        clock.Advance(TimeSpan.FromHours(1));
        var next = await poller.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var after = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, next.GrabTimeoutsDetected);
        Assert.Equal(0, after.GrabTimeoutsDetected);
        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        Assert.Equal(1, recovery.GrabTimeouts);
    }

    [Fact]
    public async Task A_problem_closes_itself_when_the_download_moves_on()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "slow", grabbedAgo: TimeSpan.FromHours(6), clock: clock);
        var (poller, problems, recovery) = CreatePoller(storage, clock);

        await poller.PollAsync(CancellationToken.None);
        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));

        // The client picks it up late. Nobody touches the problem.
        clock.Advance(TimeSpan.FromHours(1));
        await ExecuteAsync(storage.Factory,
            "UPDATE download_dispatches SET detected_utc = @at WHERE id = 'slow'",
            ("@at", clock.GetUtcNow().ToString("O")));

        clock.Advance(TimeSpan.FromHours(1));
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(1, report.ProblemsSettled);
        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        // Released, so if it sticks again that is a new problem to report.
        Assert.False(await problems.IsRecordedAsync("slow", "grab-timeout", CancellationToken.None));

        // And the recovery case written beside it is told, so it stops saying
        // "never appeared" about a download the client has shown (#559).
        Assert.Equal(("slow", "The download client picked it up."), Assert.Single(recovery.Settled));
    }

    [Fact]
    public async Task Problems_left_over_from_before_this_fix_close_on_the_next_pass()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        // The rig's state at the start of the soak: one film, imported and
        // archived, with three grab-timeout problems still open against it.
        await InsertDispatchAsync(storage.Factory, "sintel", grabbedAgo: TimeSpan.FromHours(12),
            clock: clock, status: "archived", importStatus: "imported", importCompletedAgo: TimeSpan.FromHours(11));
        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        for (var i = 0; i < 3; i++)
        {
            await problems.RecordAsync("sintel", "grab-timeout", CancellationToken.None);
        }
        Assert.Equal(3, await problems.CountOpenAsync(CancellationToken.None));

        var (poller, _, recovery) = CreatePoller(storage, clock);
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(3, report.ProblemsSettled);
        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        Assert.Equal(("sintel", "The download was archived."), Assert.Single(recovery.Settled));
    }

    [Fact]
    public async Task A_download_that_is_still_stuck_keeps_its_problem()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "stuck", grabbedAgo: TimeSpan.FromHours(6), clock: clock);
        var (poller, problems, _) = CreatePoller(storage, clock);

        await poller.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(3));
        var report = await poller.PollAsync(CancellationToken.None);

        // Settling must not become a way of hiding a problem that is real.
        Assert.Equal(0, report.ProblemsSettled);
        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_metrics_reading_counts_only_problems_that_are_still_live()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "done", grabbedAgo: TimeSpan.FromHours(8),
            clock: clock, importStatus: "imported", importCompletedAgo: TimeSpan.FromHours(7));
        await InsertDispatchAsync(storage.Factory, "stuck", grabbedAgo: TimeSpan.FromHours(6), clock: clock);

        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        await problems.RecordAsync("done", "grab-timeout", CancellationToken.None);

        var (poller, _, _) = CreatePoller(storage, clock);
        await poller.PollAsync(CancellationToken.None);

        // The soak collector and the dashboard read this figure; it is the one
        // that said "3 download problem(s) are open" about a film that was done.
        var metrics = await new SqliteDispatchMetricsRepository(storage.Factory, clock, problems).GetMetricsAsync(CancellationToken.None);
        Assert.Equal(1, metrics.RecoveryCasesOpenCount);
    }

    /// <summary>
    /// #1167: a download Deluno's own stall rules blocked, removed and searched
    /// again is Activity's story. It used to be written down as a failed import
    /// (with a list of codecs and permissions to check), which put 88 rows on
    /// Needs action overnight for things Deluno had already dealt with.
    /// </summary>
    [Fact]
    public async Task A_download_the_stall_rules_handled_is_not_reported_as_a_failed_import()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertHandledByDelunoAsync(storage, clock, "stalled");

        var (poller, problems, recovery) = CreatePoller(storage, clock);
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, report.ImportFailuresDetected);
        Assert.Equal(0, recovery.ImportFailures);
        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
    }

    /// <summary>#1167: what an earlier build already raised about such a download resolves itself.</summary>
    [Fact]
    public async Task An_old_alarm_about_a_download_the_stall_rules_handled_resolves_itself()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertHandledByDelunoAsync(storage, clock, "stalled");
        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        await problems.RecordAsync("stalled", "import-failed", CancellationToken.None);
        await problems.RecordAsync("stalled", "vanished-downloading", CancellationToken.None);
        Assert.Equal(2, await problems.CountOpenAsync(CancellationToken.None));

        var (poller, _, recovery) = CreatePoller(storage, clock);
        await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        Assert.Contains(recovery.Settled, entry => entry.DispatchId == "stalled");
        // And it is not raised again on the next pass.
        clock.Advance(TimeSpan.FromHours(1));
        await poller.PollAsync(CancellationToken.None);
        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        Assert.Single(recovery.Settled);
    }

    /// <summary>#1167: a real failed import is still raised, with its own real cause.</summary>
    [Fact]
    public async Task A_real_failed_import_is_still_reported()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertHandledByDelunoAsync(storage, clock, "bad-file", code: "no-video-file");

        var (poller, problems, recovery) = CreatePoller(storage, clock);
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(1, report.ImportFailuresDetected);
        Assert.Equal(1, recovery.ImportFailures);
        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));
    }

    /// <summary>
    /// #1167, the second path: an import Deluno refused (another show, a cam, a
    /// bad file) and then blocked, removed from the client and searched again by
    /// itself. Activity says so; nothing is left on Needs action.
    /// </summary>
    [Fact]
    public async Task An_import_refused_blocked_removed_and_replaced_leaves_nothing_for_the_person()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertHandledByDelunoAsync(storage, clock, "wrong-show", code: "title-mismatch");
        var blocked = new SqliteBlockedReleaseRepository(storage.Factory, clock);
        await BlockAsync(blocked, "wrong-show", cleanedUp: true, storage);

        var (poller, problems, recovery) = CreatePoller(storage, clock, blocked);
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, report.ImportFailuresDetected);
        Assert.Equal(0, recovery.ImportFailures);
        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        var activity = new SqliteJobStore(storage.Factory, clock, new NullRealtimeEventPublisher(), new SqliteDownloadDispatchesRepository(storage.Factory, clock));
        Assert.Single(await activity.ListActivityAsync(50, null, null, CancellationToken.None));
    }

    /// <summary>#1167: old alarms of both kinds, raised before this was handled, settle on the next poll.</summary>
    [Fact]
    public async Task Old_alarms_about_a_refusal_deluno_handled_settle_on_the_next_poll()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertHandledByDelunoAsync(storage, clock, "wrong-show", code: "title-mismatch");
        var blocked = new SqliteBlockedReleaseRepository(storage.Factory, clock);
        await BlockAsync(blocked, "wrong-show", cleanedUp: true, storage);
        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        await problems.RecordAsync("wrong-show", "import-failed", CancellationToken.None);
        await problems.RecordAsync("wrong-show", "vanished-downloading", CancellationToken.None);
        Assert.Equal(2, await problems.CountOpenAsync(CancellationToken.None));

        var (poller, _, recovery) = CreatePoller(storage, clock, blocked);
        await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
        Assert.Contains(recovery.Settled, entry => entry.DispatchId == "wrong-show");
    }

    /// <summary>#1167: a refusal whose removal has not happened stays, with its real reason.</summary>
    [Fact]
    public async Task A_refusal_whose_removal_failed_stays_listed_with_its_reason()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertHandledByDelunoAsync(storage, clock, "stuck", code: "no-video-stream");
        var blocked = new SqliteBlockedReleaseRepository(storage.Factory, clock);
        await BlockAsync(blocked, "stuck", cleanedUp: false, storage, reasonCode: ImportFailurePolicy.NoVideoStream);

        var (poller, problems, recovery) = CreatePoller(storage, clock, blocked);
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(1, report.ImportFailuresDetected);
        Assert.Equal(1, recovery.ImportFailures);
        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));
    }

    /// <summary>#1167: a stall remediation that could not remove the client entry is not "handled".</summary>
    [Fact]
    public async Task A_stall_handling_that_could_not_finish_is_still_reported()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertHandledByDelunoAsync(storage, clock, "half", code: "health-remediation-incomplete");

        var (poller, problems, recovery) = CreatePoller(storage, clock);
        await poller.PollAsync(CancellationToken.None);

        Assert.Equal(1, recovery.ImportFailures);
        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));
    }

    private static async Task BlockAsync(SqliteBlockedReleaseRepository blocked, string id, bool cleanedUp, TestStorage storage, string reasonCode = "title-mismatch")
    {
        var release = await blocked.BlockAsync(
            new BlockedRelease(
                Guid.NewGuid().ToString("n"), $"{id}.2010.1080p.web-dl-deluno", $"{id}.2010.1080p.WEB-DL-DELUNO", "test-indexer",
                "movie", id, "Example", reasonCode, "It is another show (2019, not 1959).", "hash-" + id, "qbittorrent-main", "qBittorrent",
                DateTimeOffset.MinValue),
            CancellationToken.None);
        if (cleanedUp)
        {
            await blocked.MarkCleanedUpAsync(release.Id, CancellationToken.None);
        }
    }

    private static async Task InsertHandledByDelunoAsync(TestStorage storage, FixedTimeProvider clock, string id, string code = "health-remediation-applied")
    {
        var now = clock.GetUtcNow();
        await ExecuteAsync(storage.Factory,
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, created_utc,
                grab_status, grab_attempted_utc, detected_utc, import_status, import_failure_code, import_failure_message
            ) VALUES (
                @id, 'movies-main', 'movie', 'movie', @id, @release,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'sent', @when,
                'succeeded', @when, @when, 'failed', @code, 'release refused; replacement search queued'
            )
            """,
            ("@id", id),
            ("@release", $"{id}.2010.1080p.WEB-DL-DELUNO"),
            ("@when", now.Subtract(TimeSpan.FromHours(1)).ToString("O")),
            ("@code", code));
    }

    private static (DownloadDispatchPollingService Poller, SqliteDispatchProblemLog Problems, CountingRecoveryHandler Recovery) CreatePoller(TestStorage storage, FixedTimeProvider clock, IBlockedReleaseRepository? blocked = null)
    {
        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        var recovery = new CountingRecoveryHandler();
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, clock);
        var activity = new SqliteJobStore(storage.Factory, clock, new NullRealtimeEventPublisher(), dispatches);

        var poller = new DownloadDispatchPollingService(
            storage.Factory,
            clock,
            recovery,
            activity,
            dispatches,
            problems,
            new NullRealtimeEventPublisher(),
            blockedReleaseRepository: blocked);

        return (poller, problems, recovery);
    }

    /// <summary>
    /// #537: dismissing an import issue settles the problem recorded beside it. On
    /// the rig four grab-timeout problems for removed proof films stayed open after
    /// their issues were dismissed, with no screen to close them - and an older
    /// issue names only the client and release, while that release had been
    /// grabbed twice.
    /// </summary>
    [Fact]
    public async Task Dismissing_an_older_issue_closes_the_problems_on_every_grab_of_its_release_and_no_others()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        const string release = "Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
        await InsertDispatchAsync(storage.Factory, "first-grab", TimeSpan.FromHours(9), clock, release: release);
        await InsertDispatchAsync(storage.Factory, "second-grab", TimeSpan.FromHours(6), clock, release: release);
        await InsertDispatchAsync(storage.Factory, "other", TimeSpan.FromHours(6), clock);
        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        foreach (var dispatch in new[] { "first-grab", "second-grab", "other" })
        {
            await problems.RecordAsync(dispatch, "grab-timeout", CancellationToken.None);
        }

        var closed = await RecoveryCaseProblems.AcknowledgeAsync(
            $$"""{"DownloadClientId":"qbittorrent-main","DownloadClientName":"qBittorrent","ReleaseName":"{{release}}","MediaType":"movies"}""",
            problems,
            CancellationToken.None);

        Assert.Equal(2, closed);
        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));
        Assert.Equal(1, await problems.AcknowledgeForDispatchAsync("other", CancellationToken.None));
    }

    [Fact]
    public async Task Dismissing_an_issue_that_names_its_grab_closes_only_that_grabs_problems()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        const string release = "Dementia.13.1963.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
        await InsertDispatchAsync(storage.Factory, "first-grab", TimeSpan.FromHours(9), clock, release: release);
        await InsertDispatchAsync(storage.Factory, "second-grab", TimeSpan.FromHours(6), clock, release: release);
        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        await problems.RecordAsync("first-grab", "grab-timeout", CancellationToken.None);
        await problems.RecordAsync("second-grab", "grab-timeout", CancellationToken.None);

        var closed = await RecoveryCaseProblems.AcknowledgeAsync(
            $$"""{"DispatchId":"second-grab","DownloadClientId":"qbittorrent-main","ReleaseName":"{{release}}"}""",
            problems,
            CancellationToken.None);

        Assert.Equal(1, closed);
        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));
        Assert.Equal(1, await problems.AcknowledgeForDispatchAsync("first-grab", CancellationToken.None));
    }

    /// <summary>
    /// #602: an import issue keeps its grab inside the retry it offers. Read only
    /// at the top, dismissing it settled nothing, and the poller raised the same
    /// failure again under another name two hours later (seen on the soak rig).
    /// </summary>
    [Fact]
    public async Task Dismissing_an_import_issue_closes_the_problems_on_the_grab_its_retry_names()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        const string release = "Breaking.Bad.S01E02.1080p.WEB-DL.DDP5.1.H.264-UPGRADE";
        await InsertDispatchAsync(storage.Factory, "duplicate-grab", TimeSpan.FromHours(2), clock, release: release);
        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        await problems.RecordAsync("duplicate-grab", "import-failed", CancellationToken.None);

        var closed = await RecoveryCaseProblems.AcknowledgeAsync(
            """{"RetryRequest":{"Preview":{"SourcePath":"C:\\Refined\\x.mkv"},"DispatchId":"duplicate-grab"},"SourcePath":"C:\\Refined\\x.mkv"}""",
            problems,
            CancellationToken.None);

        Assert.Equal(1, closed);
        Assert.Equal(0, await problems.CountOpenAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"MediaType":"movies"}""")]
    public async Task An_issue_that_names_no_download_closes_nothing(string? detailsJson)
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertDispatchAsync(storage.Factory, "stuck", TimeSpan.FromHours(6), clock);
        var problems = new SqliteDispatchProblemLog(storage.Factory, clock);
        await problems.RecordAsync("stuck", "grab-timeout", CancellationToken.None);

        Assert.Equal(0, await RecoveryCaseProblems.AcknowledgeAsync(detailsJson, problems, CancellationToken.None));
        Assert.Equal(1, await problems.CountOpenAsync(CancellationToken.None));
    }

    private static async Task InitializeAsync(TestStorage storage, TimeProvider clock)
        => await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

    private static Task InsertDispatchAsync(
        IDelunoDatabaseConnectionFactory factory,
        string id,
        TimeSpan grabbedAgo,
        FixedTimeProvider clock,
        string status = "sent",
        string? importStatus = null,
        TimeSpan? importCompletedAgo = null,
        string? release = null)
    {
        var now = clock.GetUtcNow();
        return ExecuteAsync(factory,
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, created_utc,
                grab_status, grab_attempted_utc, detected_utc, import_status, import_completed_utc
            ) VALUES (
                @id, 'movies-main', 'movie', 'movie', @id, @release,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', @status, @grabbed,
                'succeeded', @grabbed, NULL, @importStatus, @importCompleted
            )
            """,
            ("@id", id),
            ("@release", release ?? $"{id}.2010.1080p.WEB-DL-DELUNO"),
            ("@status", status),
            ("@grabbed", now.Subtract(grabbedAgo).ToString("O")),
            ("@importStatus", importStatus),
            ("@importCompleted", importCompletedAgo is { } ago ? now.Subtract(ago).ToString("O") : null));
    }

    private static async Task ExecuteAsync(
        IDelunoDatabaseConnectionFactory factory,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>
    /// Counts the recovery cases the poller asks for. Each one becomes a case on
    /// the title telling the owner something went wrong, which is the part of a
    /// repeated report that a person actually sees.
    /// </summary>
    private sealed class CountingRecoveryHandler : IDispatchRecoveryHandler
    {
        public int GrabTimeouts { get; private set; }

        public int ImportFailures { get; private set; }

        public List<(string DispatchId, string Reason)> Settled { get; } = [];

        public Task HandleDispatchSettledAsync(string dispatchId, string reason, CancellationToken cancellationToken)
        {
            Settled.Add((dispatchId, reason));
            return Task.CompletedTask;
        }

        public Task HandleGrabTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string detailsJson, CancellationToken cancellationToken)
        {
            GrabTimeouts++;
            return Task.CompletedTask;
        }

        public Task HandleDetectionTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task HandleImportTimeoutAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string detailsJson, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task HandleImportFailureAsync(string title, string mediaType, string downloadClientId, string downloadClientName, string releaseName, string entityId, string importFailureCode, string importFailureMessage, string detailsJson, CancellationToken cancellationToken)
        {
            ImportFailures++;
            return Task.CompletedTask;
        }
    }
}
