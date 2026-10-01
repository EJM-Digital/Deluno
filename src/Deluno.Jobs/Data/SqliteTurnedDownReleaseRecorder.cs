using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;

namespace Deluno.Jobs.Data;

public sealed class SqliteTurnedDownReleaseRecorder(
    IDelunoDatabaseConnectionFactory databases,
    TimeProvider timeProvider) : ITurnedDownReleaseRecorder
{
    /// <summary>How long turned-down releases are kept; the screen only ever asks for today.</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>The same reason for the same release inside this window is not written again; the table stays small and the card loses nothing.</summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(1);

    /// <summary>A search lists at most this many; the rest of a long candidate list adds nothing a person reads.</summary>
    internal const int MaxPerSearch = 200;

    public async Task<IReadOnlySet<string>> RecordAsync(
        string? title,
        string? entityType,
        string? entityId,
        IReadOnlyList<TurnedDownRelease> releases,
        CancellationToken cancellationToken)
    {
        var newToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (releases.Count == 0)
        {
            return newToday;
        }

        var now = timeProvider.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, timeProvider.LocalTimeZone);
        var dayStart = new DateTimeOffset(local.Date, local.Offset).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var repeatSince = now.Subtract(RepeatWindow).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
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

            // The release's newest row today: none means it is new today; the same reason within the hour means there is nothing to add.
            using (var lookup = connection.CreateCommand())
            {
                lookup.Transaction = transaction;
                lookup.CommandText =
                    """
                    SELECT reason_group, raw_reason, searched_utc FROM turned_down_releases
                    WHERE release_name = @release COLLATE NOCASE AND searched_utc >= @day
                    ORDER BY searched_utc DESC LIMIT 1;
                    """;
                Add(lookup, "@release", release.Release);
                Add(lookup, "@day", dayStart);
                await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var sameReason = string.Equals(reader.GetString(0), release.Group, StringComparison.Ordinal)
                        && string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), release.RawReason, StringComparison.Ordinal);
                    if (sameReason && string.CompareOrdinal(reader.GetString(2), repeatSince) >= 0)
                    {
                        continue;
                    }
                }
                else
                {
                    newToday.Add(release.Release);
                }
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

            // The group a screen reads: a row written as "other" is worded from its own raw reason, as Live reads it.
            var group = release.Group == TurnedDownGroups.Other ? TurnedDownReasons.GroupFor(release.RawReason) : release.Group;

            // The same release is also one line of the Activity history (#1100), written here where it is turned
            // down so the history needs no second pass; Activity's own retention prunes it.
            using var history = connection.CreateCommand();
            history.Transaction = transaction;
            history.CommandText =
                """
                INSERT INTO activity_events (
                    id, category, message, details_json, related_job_id, related_entity_type, related_entity_id, created_utc,
                    title, file_path, source_name, reason_group
                )
                VALUES (
                    @id, @category, @message, @details, NULL, @entityType, @entityId, @createdUtc,
                    @title, NULL, @indexer, @group
                );
                """;
            Add(history, "@id", Guid.CreateVersion7().ToString("N"));
            Add(history, "@category", ActivityHistory.TurnedDownCategory);
            Add(history, "@message", $"Turned down {release.Release} · {TurnedDownGroups.Label(group)}");
            Add(history, "@details", System.Text.Json.JsonSerializer.Serialize(new
            {
                release = release.Release,
                reason = release.RawReason,
                indexerName = release.IndexerName,
                title
            }));
            Add(history, "@entityType", entityType);
            Add(history, "@entityId", entityId);
            Add(history, "@createdUtc", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            Add(history, "@title", title);
            Add(history, "@indexer", release.IndexerName);
            Add(history, "@group", group);
            await history.ExecuteNonQueryAsync(cancellationToken);
        }

        using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = "DELETE FROM turned_down_releases WHERE searched_utc < @cutoff;";
        Add(prune, "@cutoff", now.Subtract(Retention).UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await prune.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return newToday;
    }

    private static void Add(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
