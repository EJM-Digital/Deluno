using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Security.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Security.Data;

/// <summary>
/// Allowed Hosts (#939), stored as two rows in the Platform database's
/// <c>system_settings</c> key/value table -- the same shape
/// <c>SqliteRssSyncStateRepository</c> and friends use for a single setting
/// that is read and rewritten whole. A missing enabled row reads as off,
/// which is exactly what every install before this feature already did and
/// what a brand-new install should still do until setup or the Security page
/// turns it on (#936).
/// </summary>
public sealed class SqliteAllowedHostsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IAllowedHostsRepository
{
    private const string EnabledKey = "security.allowedHosts.enabled";
    private const string ListKey = "security.allowedHosts.list";

    public async Task<AllowedHostsSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM system_settings WHERE setting_key IN (@enabledKey, @listKey);";
        AddParameter(command, "@enabledKey", EnabledKey);
        AddParameter(command, "@listKey", ListKey);

        var enabled = false;
        IReadOnlyList<string> hosts = [];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (key == EnabledKey)
            {
                enabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == ListKey && !string.IsNullOrWhiteSpace(value))
            {
                hosts = ParseHosts(value);
            }
        }

        return new AllowedHostsSettings(enabled, hosts);
    }

    public async Task SaveAsync(AllowedHostsSettings settings, CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow().ToString("O");
        var normalizedHosts = settings.AllowedHosts
            .Select(host => host.Trim())
            .Where(host => host.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertAsync(connection, transaction, EnabledKey, settings.Enabled ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertAsync(connection, transaction, ListKey, JsonSerializer.Serialize(normalizedHosts), updatedUtc, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static IReadOnlyList<string> ParseHosts(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            // Unreadable is treated the same as empty: nothing saved yet, not
            // a reason to refuse every request that follows.
            return [];
        }
    }

    private static async Task UpsertAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        string value,
        string updatedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
        AddParameter(command, "@updatedUtc", updatedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
