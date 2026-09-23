using System.Text.Json;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

/// <summary>
/// Where RSS sync got to on each indexer's feed (#587): the newest release it
/// has already acted on, so a release is acted on once rather than on every
/// pass for as long as the indexer keeps listing it.
/// </summary>
public interface IRssSyncStateRepository
{
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLastSeenAsync(CancellationToken cancellationToken);

    Task SaveLastSeenAsync(IReadOnlyDictionary<string, DateTimeOffset> lastSeenByIndexer, CancellationToken cancellationToken);
}

/// <summary>
/// One settings row, like the sharing snapshot next door: it is read and
/// rewritten whole each pass, and a table would buy nothing.
/// </summary>
public sealed class SqliteRssSyncStateRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IRssSyncStateRepository
{
    // v2: the first build's passes moved v1's marks while unable to match
    // anything, so the releases they had seen would never have been acted on.
    private const string SettingKey = "rss-sync.last-seen.v2";

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetLastSeenAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", SettingKey);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return new Dictionary<string, DateTimeOffset>(
                JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(value) ?? [],
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Unreadable means start over: each feed is read once more in full,
            // and anything already fetched is found in flight or held.
            return new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task SaveLastSeenAsync(IReadOnlyDictionary<string, DateTimeOffset> lastSeenByIndexer, CancellationToken cancellationToken)
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
        AddParameter(command, "@key", SettingKey);
        AddParameter(command, "@value", JsonSerializer.Serialize(lastSeenByIndexer));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
