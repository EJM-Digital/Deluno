using System.Text.Json;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>How often one download's import has been stopped, and how often it has been forced (#1013).</summary>
public sealed record ForceImportAttempts(int Blocked, int Forced);

/// <summary>
/// The count Force Import needs to know when it has used its tries on one
/// download (#1013). Kept as one settings row, like the kept-downloads list
/// beside it, so it needs no schema of its own.
/// </summary>
public interface IForceImportAttemptsRepository
{
    /// <summary>Notes that this download's import stopped for a reason Force Import could go past, and returns the counts including this stop.</summary>
    Task<ForceImportAttempts> NoteBlockedAsync(string dispatchId, CancellationToken cancellationToken);

    /// <summary>Notes that Force Import went past the stop, and returns how many forced tries this download has now had.</summary>
    Task<int> NoteForcedAsync(string dispatchId, CancellationToken cancellationToken);
}

public sealed class SqliteForceImportAttemptsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IForceImportAttemptsRepository
{
    private const string SettingKey = "imports.forceImport.attempts.v1";
    private const int KeepNewest = 500;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<ForceImportAttempts> NoteBlockedAsync(string dispatchId, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAsync(cancellationToken);
            var current = all.GetValueOrDefault(dispatchId) ?? new Entry(0, 0, timeProvider.GetUtcNow());
            var next = current with { Blocked = current.Blocked + 1, UpdatedUtc = timeProvider.GetUtcNow() };
            all[dispatchId] = next;
            await WriteAsync(all, cancellationToken);
            return new ForceImportAttempts(next.Blocked, next.Forced);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<int> NoteForcedAsync(string dispatchId, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAsync(cancellationToken);
            var current = all.GetValueOrDefault(dispatchId) ?? new Entry(0, 0, timeProvider.GetUtcNow());
            var next = current with { Forced = current.Forced + 1, UpdatedUtc = timeProvider.GetUtcNow() };
            all[dispatchId] = next;
            await WriteAsync(all, cancellationToken);
            return next.Forced;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<Dictionary<string, Entry>> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", SettingKey);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }

        try
        {
            return new Dictionary<string, Entry>(
                JsonSerializer.Deserialize<Dictionary<string, Entry>>(value) ?? [],
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, Entry>(StringComparer.Ordinal);
        }
    }

    private async Task WriteAsync(Dictionary<string, Entry> all, CancellationToken cancellationToken)
    {
        // A download that finished long ago no longer needs its count.
        var kept = all.Count <= KeepNewest
            ? all
            : all.OrderByDescending(pair => pair.Value.UpdatedUtc).Take(KeepNewest).ToDictionary(pair => pair.Key, pair => pair.Value);

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
        AddParameter(command, "@key", SettingKey);
        AddParameter(command, "@value", JsonSerializer.Serialize(kept));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record Entry(int Blocked, int Forced, DateTimeOffset UpdatedUtc);
}
