using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Infrastructure.Storage;

/// <summary>
/// An indexer's id is what history, grabs and statistics are filed under
/// (#1077), so renaming the indexer never splits them. Rows written by code
/// that only has the indexer's name at hand ask here for the id at the moment
/// they are written, while name and id still agree.
///
/// <para>The indexer lives in the platform database and the rows live in
/// others, so this is the one place that reads across.</para>
/// </summary>
public static class IndexerIdentity
{
    /// <summary>The id of the indexer with this name, or null when none has it (a removed one, or a name typed by hand).</summary>
    public static async Task<string?> ResolveIdAsync(
        IDelunoDatabaseConnectionFactory databaseConnectionFactory,
        string? indexerName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(indexerName))
        {
            return null;
        }

        try
        {
            await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(
                DelunoDatabaseNames.Platform,
                cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id FROM indexer_sources WHERE name = @name COLLATE NOCASE ORDER BY created_utc LIMIT 1;";
            AddParameter(command, "@name", indexerName.Trim());
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is null or DBNull ? null : Convert.ToString(value);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not being able to tell is not a reason to lose the row: it keeps
            // its name and the next start's backfill links it.
            return null;
        }
    }

    /// <summary>
    /// Links rows written before ids were kept: each name that matches an
    /// indexer gets that indexer's id. A name that matches none is left as it
    /// is, shown by its name. Safe to run every start.
    /// </summary>
    public static async Task<int> BackfillAsync(
        IDelunoDatabaseConnectionFactory databaseConnectionFactory,
        CancellationToken cancellationToken)
    {
        var indexers = new List<(string Id, string Name)>();
        await using (var platform = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken))
        {
            using var read = platform.CreateCommand();
            read.CommandText = "SELECT id, name FROM indexer_sources ORDER BY created_utc DESC;";
            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexers.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (indexers.Count == 0)
        {
            return 0;
        }

        var linked = 0;
        foreach (var (database, table, idColumn, nameColumn) in Targets)
        {
            try
            {
                await using var connection = await databaseConnectionFactory.OpenConnectionAsync(database, cancellationToken);
                // Oldest last, so where two indexers share a name the older one wins.
                foreach (var (id, name) in indexers)
                {
                    using var update = connection.CreateCommand();
                    update.CommandText =
                        $"UPDATE {table} SET {idColumn} = @id WHERE ({idColumn} IS NULL OR {idColumn} = '') AND {nameColumn} = @name COLLATE NOCASE;";
                    AddParameter(update, "@id", id);
                    AddParameter(update, "@name", name);
                    linked += await update.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A database that is not there yet has nothing to link.
            }
        }

        return linked;
    }

    private static readonly (string Database, string Table, string IdColumn, string NameColumn)[] Targets =
    [
        (DelunoDatabaseNames.Jobs, "download_dispatches", "indexer_id", "indexer_name"),
        (DelunoDatabaseNames.Movies, "movie_search_history", "indexer_id", "indexer_name"),
        (DelunoDatabaseNames.Series, "series_search_history", "indexer_id", "indexer_name"),
        (DelunoDatabaseNames.Platform, "indexer_history_events", "indexer_id", "indexer_name")
    ];
}

/// <summary>Links older rows to their indexer once at start, after every database has its schema.</summary>
public sealed class IndexerIdentityBackfillService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ILogger<IndexerIdentityBackfillService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var linked = await IndexerIdentity.BackfillAsync(databaseConnectionFactory, cancellationToken);
            if (linked > 0)
            {
                logger.LogInformation("Linked {Count} history rows to their indexer by id.", linked);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not link older indexer history to its indexers; it stays filed by name.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
