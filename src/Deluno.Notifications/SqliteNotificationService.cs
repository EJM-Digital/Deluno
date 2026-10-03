using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Notifications.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Notifications;

/// <summary>
/// Which kinds of event may reach the outbound notification connections,
/// kept in the Platform database's <c>system_settings</c> table (one JSON row)
/// so a restart no longer puts the switches back to their defaults (#1120).
/// Nothing is stored until the user changes something, so an existing install
/// keeps today's defaults on first start after the upgrade.
/// </summary>
public sealed class SqliteNotificationService(IDelunoDatabaseConnectionFactory databaseConnectionFactory, TimeProvider timeProvider)
    : INotificationService
{
    public const string SettingKey = "notifications.preferences";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<NotificationPreferences> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @key;";
        AddParameter(command, "@key", SettingKey);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new NotificationPreferences();
        }

        try
        {
            return JsonSerializer.Deserialize<NotificationPreferences>(json, JsonOptions) ?? new NotificationPreferences();
        }
        catch (JsonException)
        {
            return new NotificationPreferences();
        }
    }

    public async Task UpdatePreferencesAsync(NotificationPreferences preferences, CancellationToken cancellationToken = default)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

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
        AddParameter(command, "@value", JsonSerializer.Serialize(preferences, JsonOptions));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
