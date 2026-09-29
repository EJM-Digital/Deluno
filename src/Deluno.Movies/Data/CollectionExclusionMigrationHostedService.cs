using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Movies.Data;

/// <summary>
/// Moves collection-member exclusions written by the pre-unified schema into
/// the shared Platform record. It runs after the Movies schema initializer and
/// clears the compatibility bit only after its corresponding shared write
/// succeeds, so an interrupted upgrade cannot lose a decision.
/// </summary>
public sealed class CollectionExclusionMigrationHostedService(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    IUnifiedExclusionRepository unifiedExclusionRepository,
    ILogger<CollectionExclusionMigrationHostedService> logger,
    IBackgroundProblemReporter? problemReporter = null)
    : IHostedService
{
    internal const string ProblemKey = "startup:collection-exclusions";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // This must not stop the host from starting (#1080), but it must not
        // be silent either: until it finishes, exclusions still sit in the old
        // per-collection flag, so Health says so. It runs again next start.
        try
        {
            await RunAsync(cancellationToken);
            problemReporter?.Clear(ProblemKey);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The collection exclusion move did not finish; it will run again on the next start.");
            problemReporter?.Report(
                ProblemKey,
                "Collection exclusions were not moved",
                "Deluno could not move your excluded collection movies to their new place when it started. They are still excluded in the old way, so check that none has been added back. Deluno tries again each time it starts.",
                exception.Message);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.id, c.name, c.provider, m.provider_id, m.title,
                   m.release_year, m.imdb_id
            FROM movie_collection_members m
            INNER JOIN movie_collections c ON c.id = m.collection_id
            WHERE m.is_excluded = 1;
            """;

        var migrated = new List<(string CollectionId, string ProviderId)>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var collectionId = reader.GetString(0);
                var providerId = reader.GetString(3);
                await unifiedExclusionRepository.UpsertAsync(
                    new UpsertMediaExclusionRequest(
                        MediaType: "movies",
                        SourceKind: MediaExclusionSourceKinds.Collection,
                        SourceId: collectionId,
                        SourceName: reader.GetString(1),
                        Provider: reader.GetString(2),
                        EntryKey: providerId,
                        Title: reader.GetString(4),
                        Year: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                        ImdbId: reader.IsDBNull(6) ? null : reader.GetString(6),
                        DurationDays: null,
                        Reason: "Excluded from collection by user"),
                    cancellationToken);
                migrated.Add((collectionId, providerId));
            }
        }

        foreach (var (collectionId, providerId) in migrated)
        {
            using var clear = connection.CreateCommand();
            clear.CommandText =
                "UPDATE movie_collection_members SET is_excluded = 0 WHERE collection_id = @collectionId AND provider_id = @providerId;";
            AddParameter(clear, "@collectionId", collectionId);
            AddParameter(clear, "@providerId", providerId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        if (migrated.Count > 0)
        {
            logger.LogInformation("Migrated {Count} legacy collection exclusions into the shared exclusion store.", migrated.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
