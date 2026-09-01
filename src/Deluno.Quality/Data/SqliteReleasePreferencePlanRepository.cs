using System.Data.Common;
using Deluno.Infrastructure.Storage;
using Deluno.Quality.ReleasePreferences;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Quality.Data;

public sealed class SqliteReleasePreferencePlanRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IReleasePreferencePlanRepository
{
    public async Task<StoredReleasePreferencePlan> SaveAsync(
        ReleasePreferencePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ReleasePreferencePlanValidator.ThrowIfInvalid(plan);

        var json = ReleasePreferencePlanCodec.Serialize(plan);
        var planHash = plan.PlanHash;
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText =
                "SELECT plan_hash, plan_json, created_utc FROM release_preference_plans WHERE plan_id = @planId AND version = @version LIMIT 1;";
            AddParameter(existing, "@planId", plan.Id);
            AddParameter(existing, "@version", plan.Version);
            using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existingHash = reader.GetString(0);
                var existingJson = reader.GetString(1);
                var createdUtc = ParseTimestamp(reader.GetString(2));
                if (!string.Equals(existingHash, planHash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existingJson, json, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Release-preference plan '{plan.Id}' version '{plan.Version}' is immutable and already contains a different definition.");
                }

                await transaction.CommitAsync(cancellationToken);
                return new StoredReleasePreferencePlan(
                    ReleasePreferencePlanCodec.Deserialize(existingJson),
                    existingHash,
                    createdUtc);
            }
        }

        var now = timeProvider.GetUtcNow();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO release_preference_plans (plan_id, version, media_type, plan_hash, plan_json, created_utc) VALUES (@planId, @version, @mediaType, @planHash, @planJson, @createdUtc);";
            AddParameter(insert, "@planId", plan.Id);
            AddParameter(insert, "@version", plan.Version);
            AddParameter(insert, "@mediaType", plan.MediaType.Trim().ToLowerInvariant());
            AddParameter(insert, "@planHash", planHash);
            AddParameter(insert, "@planJson", json);
            AddParameter(insert, "@createdUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new StoredReleasePreferencePlan(plan, planHash, now);
    }

    public async Task<StoredReleasePreferencePlan?> GetAsync(
        string planId,
        string? version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(planId)) return null;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(version)
            ? "SELECT plan_hash, plan_json, created_utc FROM release_preference_plans WHERE plan_id = @planId ORDER BY created_utc DESC, version DESC LIMIT 1;"
            : "SELECT plan_hash, plan_json, created_utc FROM release_preference_plans WHERE plan_id = @planId AND version = @version LIMIT 1;";
        AddParameter(command, "@planId", planId.Trim());
        if (!string.IsNullOrWhiteSpace(version)) AddParameter(command, "@version", version.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadStored(reader)
            : null;
    }

    public async Task<IReadOnlyList<StoredReleasePreferencePlan>> ListAsync(
        string? mediaType,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(mediaType)
            ? "SELECT plan_hash, plan_json, created_utc FROM release_preference_plans ORDER BY created_utc DESC, plan_id ASC, version DESC;"
            : "SELECT plan_hash, plan_json, created_utc FROM release_preference_plans WHERE media_type = @mediaType ORDER BY created_utc DESC, plan_id ASC, version DESC;";
        if (!string.IsNullOrWhiteSpace(mediaType))
            AddParameter(command, "@mediaType", mediaType.Trim().ToLowerInvariant());

        var items = new List<StoredReleasePreferencePlan>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(ReadStored(reader));
        return items;
    }

    private static StoredReleasePreferencePlan ReadStored(DbDataReader reader)
    {
        var hash = reader.GetString(0);
        var json = reader.GetString(1);
        var plan = ReleasePreferencePlanCodec.Deserialize(json);
        if (!string.Equals(hash, plan.PlanHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Stored release-preference plan '{plan.Id}' has an invalid hash.");
        }

        return new StoredReleasePreferencePlan(plan, hash, ParseTimestamp(reader.GetString(2)));
    }
}
