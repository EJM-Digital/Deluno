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
/// an open alert, and each also added an activity entry and a recovery case on
/// the film saying the download never reached the client.</para>
/// </summary>
public sealed class DispatchAlertsSettleThemselvesTests
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

        var (poller, alerts, recovery) = CreatePoller(storage, clock);
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(0, report.GrabTimeoutsDetected);
        Assert.Equal(0, report.RecoveryCasesRecorded);
        Assert.Empty(await alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None));
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

        var (poller, alerts, recovery) = CreatePoller(storage, clock);
        await poller.PollAsync(CancellationToken.None);

        Assert.Empty(await alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None));
        Assert.Equal(0, recovery.GrabTimeouts);
    }

    [Fact]
    public async Task A_download_that_is_really_stuck_is_recorded_once_not_every_hour()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "stuck", grabbedAgo: TimeSpan.FromHours(6), clock: clock);

        var (poller, alerts, recovery) = CreatePoller(storage, clock);

        var first = await poller.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var second = await poller.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var third = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(1, first.GrabTimeoutsDetected);
        Assert.Equal(0, second.GrabTimeoutsDetected);
        Assert.Equal(0, third.GrabTimeoutsDetected);

        var open = await alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None);
        var alert = Assert.Single(open);
        Assert.Equal("grab-timeout", alert.AlertKind);
        // One recovery case, not one per hour.
        Assert.Equal(1, recovery.GrabTimeouts);
    }

    [Fact]
    public async Task An_alert_closes_itself_when_the_download_moves_on()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "slow", grabbedAgo: TimeSpan.FromHours(6), clock: clock);
        var (poller, alerts, _) = CreatePoller(storage, clock);

        await poller.PollAsync(CancellationToken.None);
        Assert.Single(await alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None));
        Assert.Equal(1, await alerts.GetOpenAlertCountBySeverityAsync(CancellationToken.None));

        // The client picks it up late. Nobody touches the alert.
        clock.Advance(TimeSpan.FromHours(1));
        await ExecuteAsync(storage.Factory,
            "UPDATE download_dispatches SET detected_utc = @at WHERE id = 'slow'",
            ("@at", clock.GetUtcNow().ToString("O")));

        clock.Advance(TimeSpan.FromHours(1));
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(1, report.AlertsResolved);
        Assert.Empty(await alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None));
        Assert.Equal(0, await alerts.GetOpenAlertCountBySeverityAsync(CancellationToken.None));

        var (resolvedUtc, reason, acknowledged) = await ReadResolutionAsync(storage.Factory, "slow");
        Assert.NotNull(resolvedUtc);
        Assert.Equal("The download client picked it up.", reason);
        // Resolved, not acknowledged: the record still says nobody looked.
        Assert.False(acknowledged);
    }

    [Fact]
    public async Task Alerts_left_over_from_before_this_fix_close_on_the_next_pass()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        // The rig's state at the start of the soak: one film, imported and
        // archived, with three grab-timeout alerts still open against it.
        await InsertDispatchAsync(storage.Factory, "sintel", grabbedAgo: TimeSpan.FromHours(12),
            clock: clock, status: "archived", importStatus: "imported", importCompletedAgo: TimeSpan.FromHours(11));
        var alerts = new SqliteDispatchAlertRepository(storage.Factory, clock);
        for (var i = 0; i < 3; i++)
        {
            await alerts.CreateAlertAsync("sintel", "Sintel", "Download not detected in qBittorrent",
                "grab-timeout", "warning", null, CancellationToken.None);
        }
        Assert.Equal(3, await alerts.GetOpenAlertCountBySeverityAsync(CancellationToken.None));

        var (poller, _, _) = CreatePoller(storage, clock);
        var report = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(3, report.AlertsResolved);
        Assert.Equal(0, await alerts.GetOpenAlertCountBySeverityAsync(CancellationToken.None));
        var (_, reason, _) = await ReadResolutionAsync(storage.Factory, "sintel");
        Assert.Equal("The download was archived.", reason);
    }

    [Fact]
    public async Task A_download_that_is_still_stuck_keeps_its_alert()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "stuck", grabbedAgo: TimeSpan.FromHours(6), clock: clock);
        var (poller, alerts, _) = CreatePoller(storage, clock);

        await poller.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(3));
        var report = await poller.PollAsync(CancellationToken.None);

        // Settling must not become a way of hiding a problem that is real.
        Assert.Equal(0, report.AlertsResolved);
        Assert.Single(await alerts.GetOpenAlertsAsync(null, 100, CancellationToken.None));
    }

    [Fact]
    public async Task A_metrics_reading_counts_only_alerts_that_are_still_live()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);

        await InsertDispatchAsync(storage.Factory, "done", grabbedAgo: TimeSpan.FromHours(8),
            clock: clock, importStatus: "imported", importCompletedAgo: TimeSpan.FromHours(7));
        await InsertDispatchAsync(storage.Factory, "stuck", grabbedAgo: TimeSpan.FromHours(6), clock: clock);

        var alerts = new SqliteDispatchAlertRepository(storage.Factory, clock);
        await alerts.CreateAlertAsync("done", "Done", "Download not detected", "grab-timeout", "warning", null, CancellationToken.None);

        var (poller, _, _) = CreatePoller(storage, clock);
        await poller.PollAsync(CancellationToken.None);

        // The soak collector and the dashboard read this figure; it is the one
        // that said "3 dispatch alert(s) are open" about a film that was done.
        var metrics = await new SqliteDispatchMetricsRepository(storage.Factory, clock).GetMetricsAsync(CancellationToken.None);
        Assert.Equal(1, metrics.RecoveryCasesOpenCount);
    }

    private static (DownloadDispatchPollingService Poller, SqliteDispatchAlertRepository Alerts, CountingRecoveryHandler Recovery) CreatePoller(
        TestStorage storage,
        FixedTimeProvider clock)
    {
        var alerts = new SqliteDispatchAlertRepository(storage.Factory, clock);
        var scheduler = new InMemoryJobScheduler();
        var recovery = new CountingRecoveryHandler();
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, clock);
        var activity = new SqliteJobStore(storage.Factory, clock, new NullRealtimeEventPublisher(), dispatches);

        var poller = new DownloadDispatchPollingService(
            storage.Factory,
            clock,
            recovery,
            activity,
            dispatches,
            alerts,
            scheduler,
            new NullRealtimeEventPublisher(),
            new RetryPolicyCatalog());

        return (poller, alerts, recovery);
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
        TimeSpan? importCompletedAgo = null)
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
            ("@release", $"{id}.2010.1080p.WEB-DL-DELUNO"),
            ("@status", status),
            ("@grabbed", now.Subtract(grabbedAgo).ToString("O")),
            ("@importStatus", importStatus),
            ("@importCompleted", importCompletedAgo is { } ago ? now.Subtract(ago).ToString("O") : null));
    }

    private static async Task<(string? ResolvedUtc, string? Reason, bool Acknowledged)> ReadResolutionAsync(
        IDelunoDatabaseConnectionFactory factory,
        string dispatchId)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT resolved_utc, resolved_reason, acknowledged FROM dispatch_alerts WHERE dispatch_id = @id LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = dispatchId;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2) == 1);
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
    /// repeated alert that a person actually sees.
    /// </summary>
    private sealed class CountingRecoveryHandler : IDispatchRecoveryHandler
    {
        public int GrabTimeouts { get; private set; }

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
            => Task.CompletedTask;
    }
}
