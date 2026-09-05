using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using System.Data.Common;
using System.Globalization;

namespace Deluno.Jobs.Data;

public sealed class SqliteBlockedReleaseRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IBlockedReleaseRepository
{
    public async Task<BlockedRelease> BlockAsync(BlockedRelease release, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        // The same release failing again is one entry, and the first reason
        // stands. A second row would say nothing the first did not, and would
        // make the list grow with repetition rather than with problems.
        command.CommandText =
            """
            INSERT INTO blocked_releases (
                id, release_key, release_name, indexer_name, media_type, entity_id, title,
                reason_code, reason, torrent_hash_or_item_id, download_client_id,
                download_client_name, blocked_utc
            ) VALUES (
                @id, @releaseKey, @releaseName, @indexerName, @mediaType, @entityId, @title,
                @reasonCode, @reason, @hash, @clientId, @clientName, @blockedUtc
            )
            ON CONFLICT (release_key) DO NOTHING;
            """;

        var blockedUtc = release.BlockedUtc == default ? timeProvider.GetUtcNow() : release.BlockedUtc;
        Add(command, "@id", release.Id);
        Add(command, "@releaseKey", release.ReleaseKey);
        Add(command, "@releaseName", release.ReleaseName);
        Add(command, "@indexerName", release.IndexerName);
        Add(command, "@mediaType", release.MediaType);
        Add(command, "@entityId", (object?)release.EntityId ?? DBNull.Value);
        Add(command, "@title", (object?)release.Title ?? DBNull.Value);
        Add(command, "@reasonCode", release.ReasonCode);
        Add(command, "@reason", release.Reason);
        Add(command, "@hash", (object?)release.TorrentHashOrItemId ?? DBNull.Value);
        Add(command, "@clientId", (object?)release.DownloadClientId ?? DBNull.Value);
        Add(command, "@clientName", (object?)release.DownloadClientName ?? DBNull.Value);
        Add(command, "@blockedUtc", blockedUtc.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await ListAsync(cancellationToken))
            .First(item => string.Equals(item.ReleaseKey, release.ReleaseKey, StringComparison.Ordinal));
    }

    public Task<IReadOnlyList<BlockedRelease>> ListAsync(CancellationToken cancellationToken)
        => ListWhereAsync(null, cancellationToken);

    private async Task<IReadOnlyList<BlockedRelease>> ListWhereAsync(string? where, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, release_key, release_name, indexer_name, media_type, entity_id, title, "
            + "reason_code, reason, torrent_hash_or_item_id, download_client_id, "
            + "download_client_name, blocked_utc FROM blocked_releases "
            + (where is null ? string.Empty : $"WHERE {where} ")
            + "ORDER BY blocked_utc DESC;";

        var results = new List<BlockedRelease>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new BlockedRelease(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture)));
        }

        return results;
    }

    public async Task<IReadOnlySet<string>> ListKeysAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT release_key FROM blocked_releases;";

        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    public async Task<bool> UnblockAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM blocked_releases WHERE id = @id;";
        Add(command, "@id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<BlockedRelease>> ListAwaitingCleanupAsync(CancellationToken cancellationToken)
        => (await ListWhereAsync(
                "cleaned_up_utc IS NULL AND download_client_id IS NOT NULL AND torrent_hash_or_item_id IS NOT NULL",
                cancellationToken))
            .Where(release => ImportFailurePolicy.ShouldDeletePayload(release.ReasonCode))
            .ToArray();

    public async Task<IReadOnlyList<BlockedRelease>> ListForAsync(
        string mediaType,
        string entityId,
        CancellationToken cancellationToken)
        => (await ListAsync(cancellationToken))
            .Where(release =>
                string.Equals(release.MediaType, mediaType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(release.EntityId, entityId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public async Task MarkCleanedUpAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Jobs,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE blocked_releases SET cleaned_up_utc = @now WHERE id = @id;";
        Add(command, "@now", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
