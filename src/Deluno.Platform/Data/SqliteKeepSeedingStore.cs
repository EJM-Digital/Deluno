using System.Text.Json;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>
/// The keep-seeding settings and problems (#1240), each one JSON document in <c>system_settings</c> like the strike
/// rules beside them. A missing document reads as empty, so nothing is seeded by a migration: a fresh database and an
/// upgraded one both start with no category, no private override, no tag and no problem.
/// </summary>
public sealed class SqliteKeepSeedingStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IKeepSeedingStore
{
    internal const string SettingsKey = "keep-seeding.settings.v1";
    internal const string ProblemsKey = "keep-seeding.problems.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<KeepSeedingSettings> GetAsync(CancellationToken cancellationToken)
    {
        var json = await ReadAsync(SettingsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return KeepSeedingSettings.Empty;
        }

        try
        {
            return (JsonSerializer.Deserialize<KeepSeedingSettings>(json, Json) ?? KeepSeedingSettings.Empty).Normalized();
        }
        catch (JsonException)
        {
            return KeepSeedingSettings.Empty;
        }
    }

    public async Task<KeepSeedingSettings> SaveAsync(KeepSeedingSettings settings, CancellationToken cancellationToken)
    {
        var kept = settings.Normalized();
        await WriteAsync(SettingsKey, JsonSerializer.Serialize(kept, Json), cancellationToken);
        return kept;
    }

    public async Task<IReadOnlyList<KeepSeedingProblem>> ListProblemsAsync(CancellationToken cancellationToken)
    {
        var json = await ReadAsync(ProblemsKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<KeepSeedingProblem>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveProblemAsync(KeepSeedingProblem problem, CancellationToken cancellationToken)
    {
        var all = (await ListProblemsAsync(cancellationToken))
            .Where(existing => !string.Equals(existing.Key, problem.Key, StringComparison.OrdinalIgnoreCase))
            .Append(problem)
            .ToArray();
        await WriteAsync(ProblemsKey, JsonSerializer.Serialize(all, Json), cancellationToken);
    }

    public async Task<bool> ClearProblemAsync(string key, CancellationToken cancellationToken)
    {
        var all = await ListProblemsAsync(cancellationToken);
        var remaining = all.Where(existing => !string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remaining.Length == all.Count)
        {
            return false;
        }

        await WriteAsync(ProblemsKey, JsonSerializer.Serialize(remaining, Json), cancellationToken);
        return true;
    }

    private async Task<string?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private async Task WriteAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO system_settings (setting_key, setting_value, updated_utc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@key", key);
        AddParameter(command, "@value", value);
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
