using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Security.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Security.Data;

/// <summary>
/// Skip sign-in for local devices (#993), stored the same way Allowed Hosts
/// (#939) is: key/value rows in the Platform database's
/// <c>system_settings</c> table. A missing row reads as off, exactly what
/// every install before this feature already did and what a brand-new
/// install should still do until setup or the Security page turns it on
/// (#936).
/// </summary>
public sealed class SqliteLocalNetworkAuthRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : ILocalNetworkAuthRepository
{
    private const string EnabledKey = "security.localNetworkAuth.enabled";
    private const string AdditionalNetworksKey = "security.localNetworkAuth.additionalNetworks";
    private const string AllowListKey = "security.localNetworkAuth.allowList";
    private const string TrustedProxiesKey = "security.localNetworkAuth.trustedProxies";

    public async Task<LocalNetworkAuthSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT setting_key, setting_value FROM system_settings WHERE setting_key IN (@enabledKey, @additionalKey, @allowListKey, @proxiesKey);";
        AddParameter(command, "@enabledKey", EnabledKey);
        AddParameter(command, "@additionalKey", AdditionalNetworksKey);
        AddParameter(command, "@allowListKey", AllowListKey);
        AddParameter(command, "@proxiesKey", TrustedProxiesKey);

        var enabled = false;
        IReadOnlyList<string> additionalNetworks = [];
        IReadOnlyList<string> allowList = [];
        IReadOnlyList<string> trustedProxies = [];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var value = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (key == EnabledKey)
            {
                enabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
            else if (key == AdditionalNetworksKey && !string.IsNullOrWhiteSpace(value))
            {
                additionalNetworks = ParseList(value);
            }
            else if (key == AllowListKey && !string.IsNullOrWhiteSpace(value))
            {
                allowList = ParseList(value);
            }
            else if (key == TrustedProxiesKey && !string.IsNullOrWhiteSpace(value))
            {
                trustedProxies = ParseList(value);
            }
        }

        return new LocalNetworkAuthSettings(enabled, additionalNetworks, allowList, trustedProxies);
    }

    public async Task SaveAsync(LocalNetworkAuthSettings settings, CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow().ToString("O");

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertAsync(connection, transaction, EnabledKey, settings.Enabled ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertAsync(connection, transaction, AdditionalNetworksKey, JsonSerializer.Serialize(Normalize(settings.AdditionalLocalNetworks)), updatedUtc, cancellationToken);
        await UpsertAsync(connection, transaction, AllowListKey, JsonSerializer.Serialize(Normalize(settings.AllowList)), updatedUtc, cancellationToken);
        await UpsertAsync(connection, transaction, TrustedProxiesKey, JsonSerializer.Serialize(Normalize(settings.TrustedProxies)), updatedUtc, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static string[] Normalize(IReadOnlyList<string> values)
        => values
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> ParseList(string json)
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
