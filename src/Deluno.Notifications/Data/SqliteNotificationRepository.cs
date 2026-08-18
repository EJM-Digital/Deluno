using Deluno.Infrastructure.Storage;
using Deluno.Notifications.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Notifications.Data;

/// <summary>
/// The notification-webhook slice of the Platform SQLite database. Split out
/// of SqlitePlatformSettingsRepository by ADR-001 Step 1, bodies unchanged.
/// The table stays under the Platform migrations (V0008).
/// </summary>
public sealed class SqliteNotificationRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : INotificationRepository
{
    public async Task<IReadOnlyList<NotificationWebhookItem>> ListNotificationWebhooksAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, url, event_filters, is_enabled, last_fired_utc, last_error, created_utc, updated_utc
            FROM notification_webhooks
            ORDER BY name ASC;
            """;

        var items = new List<NotificationWebhookItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadNotificationWebhook(reader));
        }

        return items;
    }

    public async Task<NotificationWebhookItem> CreateNotificationWebhookAsync(
        CreateNotificationWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var id = Guid.CreateVersion7().ToString("N");

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO notification_webhooks (id, name, url, event_filters, is_enabled, last_fired_utc, last_error, created_utc, updated_utc)
            VALUES (@id, @name, @url, @eventFilters, @isEnabled, NULL, NULL, @createdUtc, @updatedUtc);

            SELECT id, name, url, event_filters, is_enabled, last_fired_utc, last_error, created_utc, updated_utc
            FROM notification_webhooks WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? "Webhook");
        AddParameter(command, "@url", request.Url?.Trim() ?? string.Empty);
        AddParameter(command, "@eventFilters", NormalizeCsv(request.EventFilters));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadNotificationWebhook(reader);
    }

    public async Task<NotificationWebhookItem?> UpdateNotificationWebhookAsync(
        string id,
        UpdateNotificationWebhookRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notification_webhooks
            SET name = @name, url = @url, event_filters = @eventFilters, is_enabled = @isEnabled, updated_utc = @updatedUtc
            WHERE id = @id;

            SELECT id, name, url, event_filters, is_enabled, last_fired_utc, last_error, created_utc, updated_utc
            FROM notification_webhooks WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? "Webhook");
        AddParameter(command, "@url", request.Url?.Trim() ?? string.Empty);
        AddParameter(command, "@eventFilters", NormalizeCsv(request.EventFilters));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotificationWebhook(reader) : null;
    }

    public async Task<bool> DeleteNotificationWebhookAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notification_webhooks WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task RecordNotificationWebhookFiredAsync(string id, string? error, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notification_webhooks
            SET last_fired_utc = @lastFiredUtc, last_error = @lastError, updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@lastFiredUtc", now.ToString("O"));
        AddParameter(command, "@lastError", error);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NotificationWebhookItem ReadNotificationWebhook(System.Data.Common.DbDataReader reader)
    {
        return new NotificationWebhookItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Url: reader.GetString(2),
            EventFilters: reader.GetString(3),
            IsEnabled: reader.GetInt64(4) == 1,
            LastFiredUtc: reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
            LastError: reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedUtc: ParseTimestamp(reader.GetString(7)),
            UpdatedUtc: ParseTimestamp(reader.GetString(8)));
    }

}
