using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// The memory that stops a stuck download being reported again every hour. It
/// lives on the download's own timeline, never in the old dispatch_alerts table.
/// </summary>
public sealed class DispatchProblemLogTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-03T00:00:00Z");

    [Fact]
    public async Task A_problem_is_recorded_once_and_released_when_the_download_moves_on_in_the_same_instant()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertDispatchAsync(storage.Factory, "d1", detected: false);
        var log = new SqliteDispatchProblemLog(storage.Factory, clock);

        await log.RecordAsync("d1", "grab-timeout", CancellationToken.None);
        Assert.True(await log.IsRecordedAsync("d1", "grab-timeout", CancellationToken.None));
        Assert.False(await log.IsRecordedAsync("d1", "import-failed", CancellationToken.None));

        await SetDetectedAsync(storage.Factory, "d1");
        var settled = await log.SettleMovedOnAsync(CancellationToken.None);

        // The clock never moved, so the release and the raise carry the same
        // time: the order they were written in still says which came last.
        Assert.Equal(("d1", "grab-timeout", "The download client picked it up."),
            (settled.Single().DispatchId, settled.Single().Kind, settled.Single().Reason));
        Assert.False(await log.IsRecordedAsync("d1", "grab-timeout", CancellationToken.None));

        await log.RecordAsync("d1", "grab-timeout", CancellationToken.None);
        Assert.True(await log.IsRecordedAsync("d1", "grab-timeout", CancellationToken.None));
    }

    [Fact]
    public async Task A_dismissed_problem_stays_recorded_but_is_no_longer_waiting_and_is_not_settled_later()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertDispatchAsync(storage.Factory, "d1", detected: false);
        var log = new SqliteDispatchProblemLog(storage.Factory, clock);
        await log.RecordAsync("d1", "grab-timeout", CancellationToken.None);

        Assert.Equal(1, await log.AcknowledgeForDispatchAsync("d1", CancellationToken.None));

        Assert.True(await log.IsRecordedAsync("d1", "grab-timeout", CancellationToken.None));
        Assert.Equal(0, await log.CountOpenAsync(CancellationToken.None));
        await SetDetectedAsync(storage.Factory, "d1");
        Assert.Empty(await log.SettleMovedOnAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_bookkeeping_never_shows_on_the_downloads_timeline()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        await InsertDispatchAsync(storage.Factory, "d1", detected: false);
        var log = new SqliteDispatchProblemLog(storage.Factory, clock);
        var dispatches = new SqliteDownloadDispatchesRepository(storage.Factory, clock);
        await dispatches.RecordTimelineEventAsync("d1", "grab_attempted", null, CancellationToken.None);

        await log.RecordAsync("d1", "grab-timeout", CancellationToken.None);
        await log.AcknowledgeForDispatchAsync("d1", CancellationToken.None);

        var timeline = await dispatches.GetDispatchTimelineAsync("d1", CancellationToken.None);
        Assert.Equal("grab_attempted", Assert.Single(timeline).EventType);
    }

    [Fact]
    public async Task A_problem_the_old_alert_table_still_held_open_is_carried_over_without_writing_to_it()
    {
        using var storage = TestStorage.Create();
        var clock = new FixedTimeProvider(Start);
        await InitializeAsync(storage, clock);
        foreach (var id in new[] { "open", "dismissed", "resolved" })
        {
            await InsertDispatchAsync(storage.Factory, id, detected: false);
        }

        await InsertLegacyAlertAsync(storage.Factory, "open", acknowledged: false, resolved: false);
        await InsertLegacyAlertAsync(storage.Factory, "dismissed", acknowledged: true, resolved: false);
        await InsertLegacyAlertAsync(storage.Factory, "resolved", acknowledged: false, resolved: true);

        var log = new SqliteDispatchProblemLog(storage.Factory, clock);

        Assert.True(await log.IsRecordedAsync("open", "grab-timeout", CancellationToken.None));
        Assert.True(await log.IsRecordedAsync("dismissed", "grab-timeout", CancellationToken.None));
        Assert.False(await log.IsRecordedAsync("resolved", "grab-timeout", CancellationToken.None));
        // Only the one nobody dismissed is still waiting on the owner.
        Assert.Equal(1, await log.CountOpenAsync(CancellationToken.None));

        // A second start does not carry them over again.
        var again = new SqliteDispatchProblemLog(storage.Factory, clock);
        Assert.Equal(1, await again.CountOpenAsync(CancellationToken.None));
        Assert.Equal(3, await CountRowsAsync(storage.Factory, "dispatch_alerts"));
    }

    private static async Task InitializeAsync(TestStorage storage, TimeProvider clock)
        => await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

    private static Task InsertDispatchAsync(IDelunoDatabaseConnectionFactory factory, string id, bool detected)
        => ExecuteAsync(factory,
            """
            INSERT INTO download_dispatches (
                id, library_id, media_type, entity_type, entity_id, release_name,
                indexer_name, download_client_id, download_client_name, status, created_utc,
                grab_status, grab_attempted_utc, detected_utc
            ) VALUES (
                @id, 'movies-main', 'movie', 'movie', @id, @id,
                'test-indexer', 'qbittorrent-main', 'qBittorrent', 'sent', @now,
                'succeeded', @now, @detected
            )
            """,
            ("@id", id),
            ("@now", Start.AddHours(-6).ToString("O")),
            ("@detected", detected ? Start.ToString("O") : null));

    private static Task SetDetectedAsync(IDelunoDatabaseConnectionFactory factory, string id)
        => ExecuteAsync(factory, "UPDATE download_dispatches SET detected_utc = @at WHERE id = @id", ("@at", Start.ToString("O")), ("@id", id));

    private static Task InsertLegacyAlertAsync(IDelunoDatabaseConnectionFactory factory, string dispatchId, bool acknowledged, bool resolved)
        => ExecuteAsync(factory,
            """
            INSERT INTO dispatch_alerts (id, dispatch_id, title, summary, alert_kind, severity, metadata_json, detected_utc, acknowledged, acknowledged_utc, resolved_utc, resolved_reason)
            VALUES (@id, @dispatchId, 'x', 'x', 'grab-timeout', 'warning', NULL, @at, @ack, @ackAt, @resolvedAt, NULL)
            """,
            ("@id", Guid.NewGuid().ToString("N")),
            ("@dispatchId", dispatchId),
            ("@at", Start.AddHours(-2).ToString("O")),
            ("@ack", acknowledged ? 1 : 0),
            ("@ackAt", acknowledged ? Start.AddHours(-1).ToString("O") : null),
            ("@resolvedAt", resolved ? Start.AddHours(-1).ToString("O") : null));

    private static async Task<long> CountRowsAsync(IDelunoDatabaseConnectionFactory factory, string table)
        => await ScalarAsync(factory, $"SELECT COUNT(*) FROM {table}");

    private static async Task<long> ScalarAsync(IDelunoDatabaseConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task ExecuteAsync(IDelunoDatabaseConnectionFactory factory, string sql, params (string Name, object? Value)[] parameters)
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
}
