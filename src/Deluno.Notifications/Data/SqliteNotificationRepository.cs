using Deluno.Infrastructure.Storage;
using Deluno.Notifications.Contracts;
using Deluno.Contracts;
using System.Text.Json;
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
    public async Task<bool> AreOutboundNotificationsEnabledAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = 'notifications.enabled';";
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        // Same read semantics as the Platform settings snapshot: only an
        // explicit "true" counts as on, matching what the settings page shows.
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

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

    public async Task<NotificationWebhookDeliveryRecord> CreateNotificationWebhookDeliveryAsync(
        string webhookId,
        string eventCategory,
        string title,
        string message,
        string? detailsJson,
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
            INSERT INTO notification_webhook_deliveries (
                id, webhook_id, event_category, title, message, details_json,
                status, attempt_count, max_attempts, next_attempt_utc,
                last_attempt_utc, last_status_code, last_error, created_utc, updated_utc)
            VALUES (
                @id, @webhookId, @eventCategory, @title, @message, @detailsJson,
                @status, 0, 3, NULL, NULL, NULL, NULL, @createdUtc, @updatedUtc);
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@webhookId", webhookId.Trim());
        AddParameter(command, "@eventCategory", eventCategory.Trim());
        AddParameter(command, "@title", title.Trim());
        AddParameter(command, "@message", message);
        AddParameter(command, "@detailsJson", detailsJson);
        AddParameter(command, "@status", NotificationWebhookDeliveryStatuses.Pending);
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetNotificationWebhookDeliveryAsync(id, cancellationToken))
            ?? throw new InvalidOperationException("The notification webhook delivery could not be read after it was created.");
    }

    public async Task<NotificationWebhookDeliveryRecord?> GetNotificationWebhookDeliveryAsync(
        string deliveryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = DeliverySelectSql + " WHERE d.id = @id LIMIT 1;";
        AddParameter(command, "@id", deliveryId.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDelivery(reader) : null;
    }

    public async Task<IReadOnlyList<NotificationWebhookDeliveryItem>> ListNotificationWebhookDeliveriesAsync(
        string? status,
        string? webhookId,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = DeliverySelectSql +
            " WHERE (@status = '' OR d.status = @status)" +
            " AND (@webhookId = '' OR d.webhook_id = @webhookId)" +
            " ORDER BY d.created_utc DESC LIMIT @take;";
        AddParameter(command, "@status", status?.Trim().ToLowerInvariant() ?? string.Empty);
        AddParameter(command, "@webhookId", webhookId?.Trim() ?? string.Empty);
        AddParameter(command, "@take", Math.Clamp(take, 1, 500));

        var items = new List<NotificationWebhookDeliveryItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadDelivery(reader).Item);
        }

        return items;
    }

    public async Task RecordNotificationWebhookDeliveryAttemptAsync(
        string deliveryId,
        string status,
        int attemptCount,
        int? statusCode,
        string? error,
        DateTimeOffset? nextAttemptUtc,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notification_webhook_deliveries
            SET status = @status,
                attempt_count = @attemptCount,
                next_attempt_utc = @nextAttemptUtc,
                last_attempt_utc = @lastAttemptUtc,
                last_status_code = @statusCode,
                last_error = @lastError,
                failure_json = @failureJson,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", deliveryId.Trim());
        AddParameter(command, "@status", status.Trim().ToLowerInvariant());
        AddParameter(command, "@attemptCount", Math.Max(0, attemptCount));
        AddParameter(command, "@nextAttemptUtc", nextAttemptUtc?.ToString("O"));
        AddParameter(command, "@lastAttemptUtc", now.ToString("O"));
        AddParameter(command, "@statusCode", statusCode);
        AddParameter(command, "@lastError", error);
        AddParameter(command, "@failureJson", failure is null ? null : JsonSerializer.Serialize(failure));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string DeliverySelectSql =
        "SELECT d.id, d.webhook_id, d.event_category, d.title, d.message, d.details_json, " +
        "d.status, d.attempt_count, d.max_attempts, d.next_attempt_utc, d.last_attempt_utc, " +
        "d.last_status_code, d.last_error, d.failure_json, d.created_utc, d.updated_utc, w.url " +
        "FROM notification_webhook_deliveries d " +
        "LEFT JOIN notification_webhooks w ON w.id = d.webhook_id";

    private static NotificationWebhookDeliveryRecord ReadDelivery(System.Data.Common.DbDataReader reader)
    {
        var item = new NotificationWebhookDeliveryItem(
            Id: reader.GetString(0),
            WebhookId: reader.GetString(1),
            EventCategory: reader.GetString(2),
            Title: reader.GetString(3),
            Status: reader.GetString(6),
            AttemptCount: Convert.ToInt32(reader.GetInt64(7)),
            MaxAttempts: Convert.ToInt32(reader.GetInt64(8)),
            NextAttemptUtc: ReadOptionalTimestamp(reader, 9),
            LastAttemptUtc: ReadOptionalTimestamp(reader, 10),
            LastStatusCode: reader.IsDBNull(11) ? null : Convert.ToInt32(reader.GetInt64(11)),
            LastError: reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedUtc: ParseTimestamp(reader.GetString(14)),
            UpdatedUtc: ParseTimestamp(reader.GetString(15)),
            Failure: reader.IsDBNull(13) ? null : DeserializeFailure(reader.GetString(13)));

        return new NotificationWebhookDeliveryRecord(
            item,
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static IntegrationFailure? DeserializeFailure(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IntegrationFailure>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadOptionalTimestamp(System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

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
