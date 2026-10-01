using System.Globalization;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

public sealed class SqliteTurnedDownReleaseRecorder(
    IDelunoDatabaseConnectionFactory databases,
    TimeProvider timeProvider) : ITurnedDownReleaseRecorder
{
    /// <summary>How long turned-down releases are kept; the screen only ever asks for today.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>A search lists at most this many; the rest of a long candidate list adds nothing a person reads.</summary>
    internal const int MaxPerSearch = 200;

    public async Task RecordAsync(
        string? title,
        string? entityType,
        string? entityId,
        IReadOnlyList<TurnedDownRelease> releases,
        CancellationToken cancellationToken)
    {
        if (releases.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var at = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await databases.OpenConnectionAsync(DelunoDatabaseNames.Jobs, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // A release is once per search, whichever indexers offered it; the first reason stands.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var release in releases)
        {
            if (seen.Count >= MaxPerSearch || !seen.Add(release.Release))
            {
                continue;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO turned_down_releases (id, searched_utc, release_name, reason_group, raw_reason, indexer_name, title, entity_type, entity_id)
                VALUES (@id, @at, @release, @group, @raw, @indexer, @title, @entityType, @entityId);
                """;
            Add(insert, "@id", Guid.NewGuid().ToString("N"));
            Add(insert, "@at", at);
            Add(insert, "@release", release.Release);
            Add(insert, "@group", release.Group);
            Add(insert, "@raw", release.RawReason);
            Add(insert, "@indexer", release.IndexerName);
            Add(insert, "@title", title);
            Add(insert, "@entityType", entityType);
            Add(insert, "@entityId", entityId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = "DELETE FROM turned_down_releases WHERE searched_utc < @cutoff;";
        Add(prune, "@cutoff", now.Subtract(Retention).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await prune.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
