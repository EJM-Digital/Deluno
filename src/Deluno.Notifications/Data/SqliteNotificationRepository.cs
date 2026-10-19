using Deluno.Infrastructure.Storage;
using Deluno.Notifications.Contracts;
using Deluno.Contracts;
using Deluno.Security;
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
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
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

    public Task<IReadOnlyList<NotificationWebhookItem>> ListNotificationWebhooksAsync(CancellationToken cancellationToken)
        => ListAsync(reveal: false, cancellationToken);

    public Task<IReadOnlyList<NotificationWebhookItem>> ListNotificationWebhooksWithSecretsAsync(CancellationToken cancellationToken)
        => ListAsync(reveal: true, cancellationToken);

    private async Task<IReadOnlyList<NotificationWebhookItem>> ListAsync(bool reveal, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, url, event_filters, is_enabled, last_fired_utc, last_error, created_utc, updated_utc, provider, config_json, tags, include_health_warnings
            FROM notification_webhooks
            ORDER BY name ASC;
            """;

        var items = new List<NotificationWebhookItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadNotificationWebhook(reader, reveal));
        }

        return items;
    }

    /// <summary>#1076: "Discord on discord.com". Never typed; two of one app on one host tell themselves apart by port, then a suffix.</summary>
    private static async Task<string> NameForAsync(
        System.Data.Common.DbConnection connection,
        string id,
        string provider,
        string? url,
        CancellationToken cancellationToken)
        => ConnectionNames.Unique(
            ConnectionNames.AppOnHost(NotificationProviders.DisplayName(provider), url),
            ConnectionNames.PortOf(url),
            id,
            await ConnectionNameRows.OtherNamesAsync(connection, "notification_webhooks", id, cancellationToken));

    private static async Task<string> ProviderOfAsync(System.Data.Common.DbConnection connection, string id, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider FROM notification_webhooks WHERE id = @id;";
        AddParameter(command, "@id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return NotificationProviders.Normalize(value as string);
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
            INSERT INTO notification_webhooks (id, name, url, event_filters, is_enabled, last_fired_utc, last_error, created_utc, updated_utc, provider, config_json, tags, include_health_warnings)
            VALUES (@id, @name, @url, @eventFilters, @isEnabled, NULL, NULL, @createdUtc, @updatedUtc, @provider, @configJson, @tags, @includeHealthWarnings);

            SELECT id, name, url, event_filters, is_enabled, last_fired_utc, last_error, created_utc, updated_utc, provider, config_json, tags, include_health_warnings
            FROM notification_webhooks WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", await NameForAsync(connection, id, NotificationProviders.Normalize(request.Provider), request.Url, cancellationToken));
        AddParameter(command, "@url", ProtectUrl(request.Url?.Trim() ?? string.Empty));
        AddParameter(command, "@eventFilters", NormalizeCsv(request.EventFilters));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        AddParameter(command, "@provider", NotificationProviders.Normalize(request.Provider));
        AddParameter(command, "@configJson", ProtectConfig(request.ConfigJson));
        AddParameter(command, "@tags", NormalizeCsv(request.Tags));
        // A new connection starts without health warnings, as in Radarr and Sonarr; only the migration keeps existing ones on (#936).
        AddParameter(command, "@includeHealthWarnings", request.IncludeHealthWarnings == true ? 1 : 0);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadNotificationWebhook(reader, reveal: false);
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

        var (storedUrl, storedConfig) = await StoredSecretsAsync(connection, id, cancellationToken);
        var url = NotificationSecrets.KeepMaskedUrl(request.Url, storedUrl);
        var config = NotificationSecrets.KeepMaskedConfig(request.ConfigJson, storedConfig);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notification_webhooks
            SET name = @name, url = @url, event_filters = @eventFilters, is_enabled = @isEnabled, updated_utc = @updatedUtc,
                provider = COALESCE(@provider, provider), config_json = COALESCE(@configJson, config_json),
                tags = COALESCE(@tags, tags),
                include_health_warnings = COALESCE(@includeHealthWarnings, include_health_warnings)
            WHERE id = @id;

            SELECT id, name, url, event_filters, is_enabled, last_fired_utc, last_error, created_utc, updated_utc, provider, config_json, tags, include_health_warnings
            FROM notification_webhooks WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", await NameForAsync(connection, id, request.Provider is null ? await ProviderOfAsync(connection, id, cancellationToken) : NotificationProviders.Normalize(request.Provider), url, cancellationToken));
        AddParameter(command, "@url", ProtectUrl(url));
        AddParameter(command, "@eventFilters", NormalizeCsv(request.EventFilters));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        AddParameter(command, "@provider", request.Provider is null ? null : NotificationProviders.Normalize(request.Provider));
        AddParameter(command, "@configJson", ProtectConfig(config));
        AddParameter(command, "@tags", request.Tags is null ? null : NormalizeCsv(request.Tags));
        AddParameter(command, "@includeHealthWarnings", request.IncludeHealthWarnings is null ? null : request.IncludeHealthWarnings.Value ? 1 : 0);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNotificationWebhook(reader, reveal: false) : null;
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
        "d.last_status_code, d.last_error, d.failure_json, d.created_utc, d.updated_utc, w.url, " +
        "w.provider, w.config_json " +
        "FROM notification_webhook_deliveries d " +
        "LEFT JOIN notification_webhooks w ON w.id = d.webhook_id";

    private NotificationWebhookDeliveryRecord ReadDelivery(System.Data.Common.DbDataReader reader)
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
            reader.IsDBNull(16) ? null : Reveal(NotificationSecrets.UrlPurpose, reader.GetString(16)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(17) ? NotificationProviders.Generic : reader.GetString(17),
            reader.IsDBNull(18) ? null : Reveal(NotificationSecrets.ConfigPurpose, reader.GetString(18)));
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

    private NotificationWebhookItem ReadNotificationWebhook(System.Data.Common.DbDataReader reader, bool reveal)
    {
        return new NotificationWebhookItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Url: reveal ? Reveal(NotificationSecrets.UrlPurpose, reader.GetString(2)) ?? string.Empty : NotificationSecrets.MaskUrl(Reveal(NotificationSecrets.UrlPurpose, reader.GetString(2))),
            EventFilters: reader.GetString(3),
            IsEnabled: reader.GetInt64(4) == 1,
            LastFiredUtc: reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
            LastError: reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedUtc: ParseTimestamp(reader.GetString(7)),
            UpdatedUtc: ParseTimestamp(reader.GetString(8)),
            Provider: reader.IsDBNull(9) ? NotificationProviders.Generic : reader.GetString(9),
            ConfigJson: reader.IsDBNull(10) ? null : reveal ? Reveal(NotificationSecrets.ConfigPurpose, reader.GetString(10)) : NotificationSecrets.MaskConfig(Reveal(NotificationSecrets.ConfigPurpose, reader.GetString(10))),
            Tags: reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : "",
            IncludeHealthWarnings: reader.FieldCount > 12 && !reader.IsDBNull(12) && reader.GetInt64(12) == 1);
    }

    private string ProtectUrl(string url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : secretProtector.Protect(NotificationSecrets.UrlPurpose, url);

    private string? ProtectConfig(string? configJson)
        => string.IsNullOrWhiteSpace(configJson) ? null : secretProtector.Protect(NotificationSecrets.ConfigPurpose, configJson);

    /// <summary>A stored value in clear. One saved elsewhere and unreadable here comes back empty, so the screen asks for it again.</summary>
    private string? Reveal(string purpose, string? stored)
        => string.IsNullOrWhiteSpace(stored) ? null : secretProtector.TryUnprotect(purpose, stored).Value;

    private async Task<(string? Url, string? Config)> StoredSecretsAsync(System.Data.Common.DbConnection connection, string id, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url, config_json FROM notification_webhooks WHERE id = @id;";
        AddParameter(command, "@id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (Reveal(NotificationSecrets.UrlPurpose, reader.GetString(0)), reader.IsDBNull(1) ? null : Reveal(NotificationSecrets.ConfigPurpose, reader.GetString(1)))
            : (null, null);
    }

    /// <summary>
    /// #1364: protects every address and setting saved in clear by an earlier build. Safe to run again: a value
    /// already protected is left as it is. Returns how many connections it changed.
    /// </summary>
    public async Task<int> ProtectStoredSecretsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var rows = new List<(string Id, string Url, string? Config)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, url, config_json FROM notification_webhooks;";
            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var changed = 0;
        foreach (var (id, url, config) in rows)
        {
            var urlClear = !string.IsNullOrWhiteSpace(url) && !secretProtector.IsProtected(url);
            var configClear = !string.IsNullOrWhiteSpace(config) && !secretProtector.IsProtected(config);
            if (!urlClear && !configClear)
            {
                continue;
            }

            using var write = connection.CreateCommand();
            write.CommandText = "UPDATE notification_webhooks SET url = @url, config_json = @config WHERE id = @id;";
            AddParameter(write, "@id", id);
            AddParameter(write, "@url", urlClear ? ProtectUrl(url) : url);
            AddParameter(write, "@config", configClear ? ProtectConfig(config) : config);
            await write.ExecuteNonQueryAsync(cancellationToken);
            changed++;
        }

        return changed;
    }
}
