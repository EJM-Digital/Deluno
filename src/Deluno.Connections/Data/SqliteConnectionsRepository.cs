using Deluno.Connections.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Security;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;
using static Deluno.Contracts.DelunoValueNormalizers;

namespace Deluno.Connections.Data;

/// <summary>
/// Split out of SqlitePlatformSettingsRepository by ADR-001 Step 1; method
/// bodies are unchanged. The "indexer:api-key" and "download-client:secret"
/// strings passed to ISecretProtector are cryptographic purpose labels, not
/// namespaces -- they must not change even though the class that owns them
/// has moved, or every already-stored secret becomes undecryptable.
/// </summary>
public sealed class SqliteConnectionsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
    : IConnectionsRepository
{
    public async Task<IReadOnlyList<ConnectionItem>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<ConnectionItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, connection_kind, role, endpoint_url, is_enabled, created_utc, updated_utc
            FROM app_connections
            ORDER BY connection_kind ASC, name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ConnectionItem(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                ConnectionKind: reader.GetString(2),
                Role: reader.GetString(3),
                EndpointUrl: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsEnabled: reader.GetInt64(5) == 1,
                CreatedUtc: ParseTimestamp(reader.GetString(6)),
                UpdatedUtc: ParseTimestamp(reader.GetString(7))));
        }

        return items;
    }

    public async Task<IReadOnlyList<IndexerItem>> ListIndexersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<IndexerItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, protocol, privacy, base_url, api_key, priority, categories, tags,
                media_scope, is_enabled, health_status, last_health_message,
                last_health_failure_category, last_health_latency_ms, last_health_test_utc,
                consecutive_failures, rate_limited_until_utc, disabled_reason,
                created_utc, updated_utc
            FROM indexer_sources
            ORDER BY priority ASC, name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new IndexerItem(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                Protocol: reader.GetString(2),
                Privacy: reader.GetString(3),
                BaseUrl: reader.GetString(4),
                ApiKey: reader.IsDBNull(5) ? null : secretProtector.Unprotect("indexer:api-key", reader.GetString(5)),
                Priority: reader.GetInt32(6),
                Categories: reader.GetString(7),
                Tags: reader.GetString(8),
                MediaScope: reader.IsDBNull(9) ? "both" : reader.GetString(9),
                IsEnabled: reader.GetInt64(10) == 1,
                HealthStatus: reader.GetString(11),
                LastHealthMessage: reader.IsDBNull(12) ? null : reader.GetString(12),
                LastHealthFailureCategory: reader.IsDBNull(13) ? null : reader.GetString(13),
                LastHealthLatencyMs: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                LastHealthTestUtc: reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)),
                ConsecutiveFailures: reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                RateLimitedUntilUtc: reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
                DisabledReason: reader.IsDBNull(18) ? null : reader.GetString(18),
                CreatedUtc: ParseTimestamp(reader.GetString(19)),
                UpdatedUtc: ParseTimestamp(reader.GetString(20))));
        }

        return items;
    }

    public async Task<IReadOnlyList<DownloadClientItem>> ListDownloadClientsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<DownloadClientItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, protocol, host, port, username, secret, endpoint_url,
                movies_category, tv_category, category_template, priority,
                is_enabled, health_status, last_health_message,
                last_health_failure_category, last_health_latency_ms, last_health_test_utc,
                created_utc, updated_utc
            FROM download_clients
            ORDER BY priority ASC, name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DownloadClientItem(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                Protocol: reader.GetString(2),
                Host: reader.IsDBNull(3) ? null : reader.GetString(3),
                Port: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Username: reader.IsDBNull(5) ? null : reader.GetString(5),
                Secret: reader.IsDBNull(6) ? null : secretProtector.Unprotect("download-client:secret", reader.GetString(6)),
                EndpointUrl: reader.IsDBNull(7) ? null : reader.GetString(7),
                MoviesCategory: reader.IsDBNull(8) ? null : reader.GetString(8),
                TvCategory: reader.IsDBNull(9) ? null : reader.GetString(9),
                CategoryTemplate: reader.IsDBNull(10) ? null : reader.GetString(10),
                Priority: reader.GetInt32(11),
                IsEnabled: reader.GetInt64(12) == 1,
                HealthStatus: reader.GetString(13),
                LastHealthMessage: reader.IsDBNull(14) ? null : reader.GetString(14),
                LastHealthFailureCategory: reader.IsDBNull(15) ? null : reader.GetString(15),
                LastHealthLatencyMs: reader.IsDBNull(16) ? null : reader.GetInt32(16),
                LastHealthTestUtc: reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
                CreatedUtc: ParseTimestamp(reader.GetString(18)),
                UpdatedUtc: ParseTimestamp(reader.GetString(19))));
        }

        return items;
    }

    public async Task<ConnectionItem> CreateConnectionAsync(
        CreateConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new ConnectionItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New connection",
            ConnectionKind: NormalizeConnectionKind(request.ConnectionKind),
            Role: NormalizeName(request.Role) ?? "General",
            EndpointUrl: NormalizePath(request.EndpointUrl),
            IsEnabled: request.IsEnabled,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_connections (
                id, name, connection_kind, role, endpoint_url, is_enabled, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @connectionKind, @role, @endpointUrl, @isEnabled, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@connectionKind", item.ConnectionKind);
        AddParameter(command, "@role", item.Role);
        AddParameter(command, "@endpointUrl", item.EndpointUrl);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<IndexerItem> CreateIndexerAsync(
        CreateIndexerRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new IndexerItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New indexer",
            Protocol: NormalizeIndexerProtocol(request.Protocol),
            Privacy: NormalizeIndexerPrivacy(request.Privacy),
            BaseUrl: NormalizePath(request.BaseUrl) ?? string.Empty,
            ApiKey: NormalizeName(request.ApiKey),
            Priority: request.Priority is >= 1 ? request.Priority.Value : 100,
            Categories: NormalizeCsv(request.Categories),
            Tags: NormalizeCsv(request.Tags),
            MediaScope: NormalizeMediaScope(request.MediaScope),
            IsEnabled: request.IsEnabled,
            HealthStatus: request.IsEnabled ? "untested" : "disabled",
            LastHealthMessage: request.IsEnabled ? "Not tested yet." : "Disabled until you turn it on.",
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            ConsecutiveFailures: 0,
            RateLimitedUntilUtc: null,
            DisabledReason: null,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO indexer_sources (
                id, name, protocol, privacy, base_url, api_key, priority, categories, tags,
                media_scope, is_enabled, health_status, last_health_message, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @protocol, @privacy, @baseUrl, @apiKey, @priority, @categories, @tags,
                @mediaScope, @isEnabled, @healthStatus, @lastHealthMessage, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@protocol", item.Protocol);
        AddParameter(command, "@privacy", item.Privacy);
        AddParameter(command, "@baseUrl", item.BaseUrl);
        AddParameter(
            command,
            "@apiKey",
            string.IsNullOrWhiteSpace(item.ApiKey)
                ? null
                : secretProtector.Protect("indexer:api-key", item.ApiKey));
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@categories", item.Categories);
        AddParameter(command, "@tags", item.Tags);
        AddParameter(command, "@mediaScope", item.MediaScope);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", item.HealthStatus);
        AddParameter(command, "@lastHealthMessage", item.LastHealthMessage);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<IndexerItem?> UpdateIndexerAsync(
        string id,
        UpdateIndexerRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        // Fetch current row so we can merge patch fields
        var existing = (await ListIndexersAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return null;

        var newName     = NormalizeName(request.Name) ?? existing.Name;
        var newProtocol = request.Protocol is not null ? NormalizeIndexerProtocol(request.Protocol) : existing.Protocol;
        var newPrivacy  = request.Privacy is not null ? NormalizeIndexerPrivacy(request.Privacy) : existing.Privacy;
        var newBaseUrl  = NormalizePath(request.BaseUrl) ?? existing.BaseUrl;
        var newApiKey   = request.ApiKey is not null ? NormalizeName(request.ApiKey) : existing.ApiKey;
        var newPriority = request.Priority is >= 1 ? request.Priority.Value : existing.Priority;
        var newCats     = request.Categories is not null ? NormalizeCsv(request.Categories) : existing.Categories;
        var newTags     = request.Tags is not null ? NormalizeCsv(request.Tags) : existing.Tags;
        var newScope    = request.MediaScope is not null ? NormalizeMediaScope(request.MediaScope) : existing.MediaScope;
        var newEnabled  = request.IsEnabled ?? existing.IsEnabled;

        // If enabling a previously-disabled indexer, reset health status so the UI prompts a test
        var newHealth = newEnabled && !existing.IsEnabled ? "untested" : existing.HealthStatus;
        var newMsg    = newEnabled && !existing.IsEnabled ? "Re-enabled — test connection to confirm." : existing.LastHealthMessage;

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE indexer_sources
            SET
                name = @name,
                protocol = @protocol,
                privacy = @privacy,
                base_url = @baseUrl,
                api_key = @apiKey,
                priority = @priority,
                categories = @categories,
                tags = @tags,
                media_scope = @mediaScope,
                is_enabled = @isEnabled,
                health_status = @healthStatus,
                last_health_message = @lastHealthMessage,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", newName);
        AddParameter(command, "@protocol", newProtocol);
        AddParameter(command, "@privacy", newPrivacy);
        AddParameter(command, "@baseUrl", newBaseUrl);
        AddParameter(
            command,
            "@apiKey",
            string.IsNullOrWhiteSpace(newApiKey)
                ? null
                : secretProtector.Protect("indexer:api-key", newApiKey));
        AddParameter(command, "@priority", newPriority);
        AddParameter(command, "@categories", newCats);
        AddParameter(command, "@tags", newTags);
        AddParameter(command, "@mediaScope", newScope);
        AddParameter(command, "@isEnabled", newEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", newHealth);
        AddParameter(command, "@lastHealthMessage", newMsg);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;

        return existing with
        {
            Name     = newName,
            Protocol = newProtocol,
            Privacy  = newPrivacy,
            BaseUrl  = newBaseUrl,
            ApiKey   = newApiKey,
            Priority = newPriority,
            Categories = newCats,
            Tags     = newTags,
            MediaScope = newScope,
            IsEnabled  = newEnabled,
            HealthStatus = newHealth,
            LastHealthMessage = newMsg,
            UpdatedUtc = now
        };
    }

    public async Task<DownloadClientItem> CreateDownloadClientAsync(
        CreateDownloadClientRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new DownloadClientItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New download client",
            Protocol: NormalizeDownloadProtocol(request.Protocol),
            Host: NormalizeName(request.Host),
            Port: NormalizeNullablePositiveValue(request.Port),
            Username: NormalizeName(request.Username),
            Secret: NormalizeName(request.Password),
            EndpointUrl: NormalizePath(request.EndpointUrl),
            MoviesCategory: NormalizeName(request.MoviesCategory),
            TvCategory: NormalizeName(request.TvCategory),
            CategoryTemplate: NormalizeName(request.CategoryTemplate),
            Priority: request.Priority is >= 1 ? request.Priority.Value : 100,
            IsEnabled: request.IsEnabled,
            HealthStatus: request.IsEnabled ? "untested" : "disabled",
            LastHealthMessage: request.IsEnabled ? "Not tested yet." : "Disabled until you turn it on.",
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_clients (
                id, name, protocol, host, port, username, secret, endpoint_url,
                movies_category, tv_category, category_template, priority,
                is_enabled, health_status, last_health_message, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @protocol, @host, @port, @username, @secret, @endpointUrl,
                @moviesCategory, @tvCategory, @categoryTemplate, @priority,
                @isEnabled, @healthStatus, @lastHealthMessage, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@protocol", item.Protocol);
        AddParameter(command, "@host", item.Host);
        AddParameter(command, "@port", item.Port);
        AddParameter(command, "@username", item.Username);
        AddParameter(
            command,
            "@secret",
            string.IsNullOrWhiteSpace(item.Secret)
                ? null
                : secretProtector.Protect("download-client:secret", item.Secret));
        AddParameter(command, "@endpointUrl", item.EndpointUrl);
        AddParameter(command, "@moviesCategory", item.MoviesCategory);
        AddParameter(command, "@tvCategory", item.TvCategory);
        AddParameter(command, "@categoryTemplate", item.CategoryTemplate);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", item.HealthStatus);
        AddParameter(command, "@lastHealthMessage", item.LastHealthMessage);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<IReadOnlyList<DownloadClientPathMappingItem>> ListDownloadClientPathMappingsAsync(
        string? downloadClientId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<DownloadClientPathMappingItem>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, download_client_id, remote_path, local_path, is_enabled, priority, created_utc, updated_utc
            FROM download_client_path_mappings
            WHERE @downloadClientId IS NULL OR download_client_id = @downloadClientId
            ORDER BY priority ASC, LENGTH(remote_path) DESC, remote_path ASC;
            """;
        AddParameter(command, "@downloadClientId", string.IsNullOrWhiteSpace(downloadClientId) ? null : downloadClientId.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DownloadClientPathMappingItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4) == 1,
                reader.GetInt32(5),
                ParseTimestamp(reader.GetString(6)),
                ParseTimestamp(reader.GetString(7))));
        }

        return items;
    }

    public async Task<DownloadClientPathMappingItem?> CreateDownloadClientPathMappingAsync(
        string downloadClientId,
        CreateDownloadClientPathMappingRequest request,
        CancellationToken cancellationToken)
    {
        var client = (await ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, downloadClientId, StringComparison.OrdinalIgnoreCase));
        if (client is null) return null;

        var remotePath = NormalizePath(request.RemotePath);
        var localPath = NormalizePath(request.LocalPath);
        if (string.IsNullOrWhiteSpace(remotePath) || string.IsNullOrWhiteSpace(localPath)) return null;

        var now = timeProvider.GetUtcNow();
        var item = new DownloadClientPathMappingItem(
            Guid.CreateVersion7().ToString("N"),
            client.Id,
            remotePath,
            localPath,
            request.IsEnabled,
            request.Priority is >= 1 ? request.Priority.Value : 10,
            now,
            now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_client_path_mappings (
                id, download_client_id, remote_path, local_path, is_enabled, priority, created_utc, updated_utc)
            VALUES (@id, @downloadClientId, @remotePath, @localPath, @isEnabled, @priority, @createdUtc, @updatedUtc);
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@downloadClientId", item.DownloadClientId);
        AddParameter(command, "@remotePath", item.RemotePath);
        AddParameter(command, "@localPath", item.LocalPath);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    public async Task<bool> DeleteDownloadClientPathMappingAsync(
        string downloadClientId,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM download_client_path_mappings WHERE id = @id AND download_client_id = @downloadClientId;";
        AddParameter(command, "@id", id);
        AddParameter(command, "@downloadClientId", downloadClientId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<DownloadClientItem?> UpdateDownloadClientAsync(
        string id,
        UpdateDownloadClientRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var existing = (await ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return null;

        var newName     = NormalizeName(request.Name) ?? existing.Name;
        var newProtocol = request.Protocol is not null ? NormalizeDownloadProtocol(request.Protocol) : existing.Protocol;
        var newHost     = request.Host is not null ? NormalizeName(request.Host) : existing.Host;
        var newPort     = request.Port is >= 1 ? request.Port : existing.Port;
        var newUsername = request.Username is not null ? NormalizeName(request.Username) : existing.Username;
        var newSecret   = request.Password is not null ? NormalizeName(request.Password) : existing.Secret;
        var newEndpoint = request.EndpointUrl is not null ? NormalizePath(request.EndpointUrl) : existing.EndpointUrl;
        var newMovieCat = request.MoviesCategory is not null ? NormalizeName(request.MoviesCategory) : existing.MoviesCategory;
        var newTvCat    = request.TvCategory is not null ? NormalizeName(request.TvCategory) : existing.TvCategory;
        var newCatTmpl  = request.CategoryTemplate is not null ? NormalizeName(request.CategoryTemplate) : existing.CategoryTemplate;
        var newPriority = request.Priority is >= 1 ? request.Priority.Value : existing.Priority;
        var newEnabled  = request.IsEnabled ?? existing.IsEnabled;

        var newHealth = newEnabled && !existing.IsEnabled ? "untested" : existing.HealthStatus;
        var newMsg    = newEnabled && !existing.IsEnabled ? "Re-enabled — test connection to confirm." : existing.LastHealthMessage;

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_clients
            SET
                name = @name,
                protocol = @protocol,
                host = @host,
                port = @port,
                username = @username,
                secret = @secret,
                endpoint_url = @endpointUrl,
                movies_category = @moviesCategory,
                tv_category = @tvCategory,
                category_template = @categoryTemplate,
                priority = @priority,
                is_enabled = @isEnabled,
                health_status = @healthStatus,
                last_health_message = @lastHealthMessage,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", newName);
        AddParameter(command, "@protocol", newProtocol);
        AddParameter(command, "@host", newHost);
        AddParameter(command, "@port", newPort);
        AddParameter(command, "@username", newUsername);
        AddParameter(
            command,
            "@secret",
            string.IsNullOrWhiteSpace(newSecret)
                ? null
                : secretProtector.Protect("download-client:secret", newSecret));
        AddParameter(command, "@endpointUrl", newEndpoint);
        AddParameter(command, "@moviesCategory", newMovieCat);
        AddParameter(command, "@tvCategory", newTvCat);
        AddParameter(command, "@categoryTemplate", newCatTmpl);
        AddParameter(command, "@priority", newPriority);
        AddParameter(command, "@isEnabled", newEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", newHealth);
        AddParameter(command, "@lastHealthMessage", newMsg);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;

        return existing with
        {
            Name             = newName,
            Protocol         = newProtocol,
            Host             = newHost,
            Port             = newPort,
            Username         = newUsername,
            Secret           = newSecret,
            EndpointUrl      = newEndpoint,
            MoviesCategory   = newMovieCat,
            TvCategory       = newTvCat,
            CategoryTemplate = newCatTmpl,
            Priority         = newPriority,
            IsEnabled        = newEnabled,
            HealthStatus     = newHealth,
            LastHealthMessage = newMsg,
            UpdatedUtc       = now
        };
    }

    public async Task<IndexerTestResult?> UpdateIndexerHealthAsync(
        string id,
        string healthStatus,
        string message,
        string? failureCategory,
        int? latencyMs,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var isFailure = healthStatus is "degraded" or "failing" or "timeout" or "unreachable";
        var isRateLimit = failureCategory is "rateLimit" or "rateLimited" or "rate_limit";

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        // Read current consecutive_failures so we can increment
        int currentFailures = 0;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT consecutive_failures FROM indexer_sources WHERE id = @id;";
            AddParameter(read, "@id", id);
            var scalar = await read.ExecuteScalarAsync(cancellationToken);
            if (scalar is null) return null;
            currentFailures = Convert.ToInt32(scalar);
        }

        var newFailures = isFailure ? currentFailures + 1 : 0;
        DateTimeOffset? rateLimitedUntil = null;
        if (isRateLimit)
        {
            rateLimitedUntil = now.AddMinutes(60);
        }
        else if (isFailure && newFailures >= 5)
        {
            rateLimitedUntil = now.AddMinutes(30);
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE indexer_sources
            SET
                health_status = @healthStatus,
                last_health_message = @lastHealthMessage,
                last_health_failure_category = @lastHealthFailureCategory,
                last_health_latency_ms = @lastHealthLatencyMs,
                last_health_test_utc = @lastHealthTestUtc,
                consecutive_failures = @consecutiveFailures,
                rate_limited_until_utc = @rateLimitedUntilUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@healthStatus", healthStatus);
        AddParameter(command, "@lastHealthMessage", message);
        AddParameter(command, "@lastHealthFailureCategory", failureCategory);
        AddParameter(command, "@lastHealthLatencyMs", latencyMs);
        AddParameter(command, "@lastHealthTestUtc", now.ToString("O"));
        AddParameter(command, "@consecutiveFailures", newFailures);
        AddParameter(command, "@rateLimitedUntilUtc", rateLimitedUntil?.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return new IndexerTestResult(
            Id: id,
            HealthStatus: healthStatus,
            Message: message,
            FailureCategory: failureCategory,
            LatencyMs: latencyMs,
            TestedUtc: now);
    }

    public async Task<IndexerItem?> ResetIndexerCircuitAsync(string id, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE indexer_sources
            SET
                consecutive_failures = 0,
                rate_limited_until_utc = NULL,
                health_status = CASE WHEN is_enabled = 1 THEN 'untested' ELSE 'disabled' END,
                last_health_message = 'Circuit reset manually.',
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        var items = await ListIndexersAsync(cancellationToken);
        return items.FirstOrDefault(i => i.Id == id);
    }

    public async Task<IndexerTestResult?> UpdateDownloadClientHealthAsync(
        string id,
        string healthStatus,
        string message,
        string? failureCategory,
        int? latencyMs,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE download_clients
            SET
                health_status = @healthStatus,
                last_health_message = @lastHealthMessage,
                last_health_failure_category = @lastHealthFailureCategory,
                last_health_latency_ms = @lastHealthLatencyMs,
                last_health_test_utc = @lastHealthTestUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@healthStatus", healthStatus);
        AddParameter(command, "@lastHealthMessage", message);
        AddParameter(command, "@lastHealthFailureCategory", failureCategory);
        AddParameter(command, "@lastHealthLatencyMs", latencyMs);
        AddParameter(command, "@lastHealthTestUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return new IndexerTestResult(
            Id: id,
            HealthStatus: healthStatus,
            Message: message,
            FailureCategory: failureCategory,
            LatencyMs: latencyMs,
            TestedUtc: now);
    }

    public async Task<bool> DeleteConnectionAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM app_connections WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteIndexerAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM indexer_sources WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteDownloadClientAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM download_clients WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static string NormalizeConnectionKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "indexer" => "indexer",
            "downloadclient" => "downloadClient",
            "download client" => "downloadClient",
            "notification" => "notification",
            "mediaserver" => "mediaServer",
            "media server" => "mediaServer",
            _ => "indexer"
        };
    }

    private static string NormalizeIndexerProtocol(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "newznab" => "newznab",
            "torznab" => "torznab",
            "rss" => "rss",
            "usenet" => "newznab",
            "torrent" => "torznab",
            _ => "torznab"
        };
    }

    private static string NormalizeMediaScope(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "movies" => "movies",
            "movie" => "movies",
            "tv" => "tv",
            "shows" => "tv",
            "series" => "tv",
            _ => "both"
        };
    }

    private static string NormalizeIndexerPrivacy(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "private" => "private",
            _ => "public"
        };
    }

    private static string NormalizeDownloadProtocol(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "qbittorrent" => "qbittorrent",
            "sabnzbd" => "sabnzbd",
            "nzbget" => "nzbget",
            "transmission" => "transmission",
            "deluge" => "deluge",
            "custom" => "custom",
            "usenet" => "usenet",
            "torrent" => "torrent",
            _ => "qbittorrent"
        };
    }
}
