using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Jobs.Data;
using Deluno.Jobs.Migrations;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Jobs;

/// <summary>
/// #1100: the releases turned down before the Activity history kept them are written into it once, worded as the
/// recorder words them, so filtering the history to turned-down releases shows what Live counts.
/// </summary>
public sealed class HistoryHoldsEarlierTurnedDownTests
{
    [Fact]
    public async Task Each_release_turned_down_before_the_history_kept_them_is_one_history_line_at_its_time_and_later_ones_are_not_copied()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-02T08:00:00Z"));
        await new JobsSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<JobsSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        // Two releases turned down before V0047 was applied (one stored as "other" with a raw reason the recorder
        // groups as size), and one after, which the recorder already wrote to the history itself.
        await ExecuteAsync(storage.Factory,
            """
            UPDATE schema_migrations SET applied_utc = '2026-10-02T07:00:00.0000000+00:00' WHERE version = 47;
            DELETE FROM schema_migrations WHERE version = 50;
            DELETE FROM activity_events;
            INSERT INTO turned_down_releases (id, searched_utc, release_name, reason_group, raw_reason, indexer_name, title, entity_type, entity_id)
            VALUES ('a', '2026-10-02T01:00:00.0000000+00:00', 'Charade.1963.720p', 'notQuality', 'Quality not wanted', 'YTS', 'Charade (1963)', 'movie', 'm1'),
                   ('b', '2026-10-02T02:00:00.0000000+00:00', 'Charade.1963.2160p', 'other', 'Release is larger than the maximum size', 'EZTV', 'Charade (1963)', 'movie', 'm1'),
                   ('c', '2026-10-02T07:30:00.0000000+00:00', 'Charade.1963.CAM', 'fake', 'Fake', 'EZTV', 'Charade (1963)', 'movie', 'm1');
            """);

        await using (var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None))
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await new V0050HistoryHoldsEarlierTurnedDown().UpAsync(connection, transaction, CancellationToken.None);
            await transaction.CommitAsync();
        }

        var lines = await ReadLinesAsync(storage.Factory);
        Assert.Equal(2, lines.Count);
        Assert.Equal(("release.turned-down", "Turned down Charade.1963.720p · Not a quality you want", "2026-10-02T01:00:00.0000000+00:00", "Charade (1963)", "YTS", "notQuality"), lines[0]);
        Assert.Equal(TurnedDownReasons.GroupFor("Release is larger than the maximum size"), lines[1].Group);
        Assert.Equal($"Turned down Charade.1963.2160p · {TurnedDownGroups.Label(lines[1].Group)}", lines[1].Message);
        Assert.DoesNotContain(lines, line => line.Message.Contains("CAM", StringComparison.Ordinal));
    }

    private static async Task ExecuteAsync(IDelunoDatabaseConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<(string Category, string Message, string Created, string? Title, string? Source, string? Group)>> ReadLinesAsync(IDelunoDatabaseConnectionFactory factory)
    {
        await using var connection = await factory.OpenConnectionAsync(DelunoDatabaseNames.Jobs, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT category, message, created_utc, title, source_name, reason_group FROM activity_events ORDER BY created_utc;";
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<(string, string, string, string?, string?, string?)>();
        while (await reader.ReadAsync())
        {
            lines.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return lines;
    }
}
