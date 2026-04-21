using System.Globalization;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public sealed class SqlitePlatformSettingsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IPlatformSettingsRepository
{
    public async Task<PlatformSettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var roots = await ReadRootsAsync(connection, cancellationToken);
        return CreateSnapshot(settings, roots);
    }

    public async Task<PlatformSettingsSnapshot> SaveAsync(
        UpdatePlatformSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await UpsertSettingAsync(connection, transaction, "app.instanceName", NormalizeName(request.AppInstanceName) ?? "Deluno", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "jobs.autoStart", request.AutoStartJobs ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "notifications.enabled", request.EnableNotifications ? "true" : "false", updatedUtc, cancellationToken);

        await UpsertRootAsync(connection, transaction, "movies", NormalizePath(request.MovieRootPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "series", NormalizePath(request.SeriesRootPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "downloads", NormalizePath(request.DownloadsPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "downloads.incomplete", NormalizePath(request.IncompleteDownloadsPath), updatedUtc, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var roots = await ReadRootsAsync(connection, cancellationToken);
        return CreateSnapshot(settings, roots);
    }

    public async Task<IReadOnlyList<LibraryItem>> ListLibrariesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await EnsureSeedLibrariesAsync(connection, cancellationToken);

        var items = new List<LibraryItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, purpose, root_path, downloads_path, auto_search_enabled,
                missing_search_enabled, upgrade_search_enabled, search_interval_hours,
                retry_delay_hours, max_items_per_run, created_utc, updated_utc
            FROM libraries
            ORDER BY media_type ASC, name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LibraryItem(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                MediaType: reader.GetString(2),
                Purpose: reader.GetString(3),
                RootPath: reader.GetString(4),
                DownloadsPath: reader.IsDBNull(5) ? null : reader.GetString(5),
                AutoSearchEnabled: reader.GetInt64(6) == 1,
                MissingSearchEnabled: reader.GetInt64(7) == 1,
                UpgradeSearchEnabled: reader.GetInt64(8) == 1,
                SearchIntervalHours: reader.GetInt32(9),
                RetryDelayHours: reader.GetInt32(10),
                MaxItemsPerRun: reader.GetInt32(11),
                AutomationStatus: "idle",
                SearchRequested: false,
                LastSearchedUtc: null,
                NextSearchUtc: null,
                CreatedUtc: ParseTimestamp(reader.GetString(12)),
                UpdatedUtc: ParseTimestamp(reader.GetString(13))));
        }

        return items;
    }

    public async Task<LibraryItem> CreateLibraryAsync(
        CreateLibraryRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new LibraryItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New library",
            MediaType: NormalizeMediaType(request.MediaType),
            Purpose: NormalizeName(request.Purpose) ?? "General",
            RootPath: NormalizePath(request.RootPath) ?? string.Empty,
            DownloadsPath: NormalizePath(request.DownloadsPath),
            AutoSearchEnabled: request.AutoSearchEnabled,
            MissingSearchEnabled: request.MissingSearchEnabled,
            UpgradeSearchEnabled: request.UpgradeSearchEnabled,
            SearchIntervalHours: NormalizePositiveValue(request.SearchIntervalHours, 6),
            RetryDelayHours: NormalizePositiveValue(request.RetryDelayHours, 24),
            MaxItemsPerRun: NormalizePositiveValue(request.MaxItemsPerRun, 25),
            AutomationStatus: "idle",
            SearchRequested: false,
            LastSearchedUtc: null,
            NextSearchUtc: null,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO libraries (
                id, name, media_type, purpose, root_path, downloads_path, auto_search_enabled,
                missing_search_enabled, upgrade_search_enabled, search_interval_hours,
                retry_delay_hours, max_items_per_run, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @purpose, @rootPath, @downloadsPath, @autoSearchEnabled,
                @missingSearchEnabled, @upgradeSearchEnabled, @searchIntervalHours,
                @retryDelayHours, @maxItemsPerRun, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@mediaType", item.MediaType);
        AddParameter(command, "@purpose", item.Purpose);
        AddParameter(command, "@rootPath", item.RootPath);
        AddParameter(command, "@downloadsPath", item.DownloadsPath);
        AddParameter(command, "@autoSearchEnabled", item.AutoSearchEnabled ? 1 : 0);
        AddParameter(command, "@missingSearchEnabled", item.MissingSearchEnabled ? 1 : 0);
        AddParameter(command, "@upgradeSearchEnabled", item.UpgradeSearchEnabled ? 1 : 0);
        AddParameter(command, "@searchIntervalHours", item.SearchIntervalHours);
        AddParameter(command, "@retryDelayHours", item.RetryDelayHours);
        AddParameter(command, "@maxItemsPerRun", item.MaxItemsPerRun);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<LibraryItem?> UpdateLibraryAutomationAsync(
        string id,
        UpdateLibraryAutomationRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE libraries
                SET
                    auto_search_enabled = @autoSearchEnabled,
                    missing_search_enabled = @missingSearchEnabled,
                    upgrade_search_enabled = @upgradeSearchEnabled,
                    search_interval_hours = @searchIntervalHours,
                    retry_delay_hours = @retryDelayHours,
                    max_items_per_run = @maxItemsPerRun,
                    updated_utc = @updatedUtc
                WHERE id = @id;
                """;

            AddParameter(command, "@id", id);
            AddParameter(command, "@autoSearchEnabled", request.AutoSearchEnabled ? 1 : 0);
            AddParameter(command, "@missingSearchEnabled", request.MissingSearchEnabled ? 1 : 0);
            AddParameter(command, "@upgradeSearchEnabled", request.UpgradeSearchEnabled ? 1 : 0);
            AddParameter(command, "@searchIntervalHours", NormalizePositiveValue(request.SearchIntervalHours, 6));
            AddParameter(command, "@retryDelayHours", NormalizePositiveValue(request.RetryDelayHours, 24));
            AddParameter(command, "@maxItemsPerRun", NormalizePositiveValue(request.MaxItemsPerRun, 25));
            AddParameter(command, "@updatedUtc", now.ToString("O"));

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return null;
            }
        }

        return await GetLibraryAsync(connection, id, cancellationToken);
    }

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
                id, name, protocol, privacy, base_url, priority, categories, tags,
                is_enabled, health_status, last_health_message, created_utc, updated_utc
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
                Priority: reader.GetInt32(5),
                Categories: reader.GetString(6),
                Tags: reader.GetString(7),
                IsEnabled: reader.GetInt64(8) == 1,
                HealthStatus: reader.GetString(9),
                LastHealthMessage: reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedUtc: ParseTimestamp(reader.GetString(11)),
                UpdatedUtc: ParseTimestamp(reader.GetString(12))));
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
            Priority: request.Priority is >= 1 ? request.Priority.Value : 100,
            Categories: NormalizeCsv(request.Categories),
            Tags: NormalizeCsv(request.Tags),
            IsEnabled: request.IsEnabled,
            HealthStatus: request.IsEnabled ? "ready" : "paused",
            LastHealthMessage: request.IsEnabled ? "Ready to test and use." : "Disabled until you turn it on.",
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO indexer_sources (
                id, name, protocol, privacy, base_url, priority, categories, tags,
                is_enabled, health_status, last_health_message, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @protocol, @privacy, @baseUrl, @priority, @categories, @tags,
                @isEnabled, @healthStatus, @lastHealthMessage, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@protocol", item.Protocol);
        AddParameter(command, "@privacy", item.Privacy);
        AddParameter(command, "@baseUrl", item.BaseUrl);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@categories", item.Categories);
        AddParameter(command, "@tags", item.Tags);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", item.HealthStatus);
        AddParameter(command, "@lastHealthMessage", item.LastHealthMessage);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<IndexerTestResult?> UpdateIndexerHealthAsync(
        string id,
        string healthStatus,
        string message,
        CancellationToken cancellationToken)
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
                health_status = @healthStatus,
                last_health_message = @lastHealthMessage,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@healthStatus", healthStatus);
        AddParameter(command, "@lastHealthMessage", message);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return new IndexerTestResult(
            Id: id,
            HealthStatus: healthStatus,
            Message: message,
            TestedUtc: now);
    }

    public async Task<bool> DeleteLibraryAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM libraries WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
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

    private static PlatformSettingsSnapshot CreateSnapshot(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> roots)
    {
        return new PlatformSettingsSnapshot(
            AppInstanceName: GetValue(settings, "app.instanceName") ?? "Deluno",
            MovieRootPath: GetValue(roots, "movies"),
            SeriesRootPath: GetValue(roots, "series"),
            DownloadsPath: GetValue(roots, "downloads"),
            IncompleteDownloadsPath: GetValue(roots, "downloads.incomplete"),
            AutoStartJobs: string.Equals(GetValue(settings, "jobs.autoStart"), "true", StringComparison.OrdinalIgnoreCase),
            EnableNotifications: string.Equals(GetValue(settings, "notifications.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            UpdatedUtc: DateTimeOffset.UtcNow);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadSettingsAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_key, setting_value FROM system_settings;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadRootsAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT root_key, root_path FROM root_paths;";

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private static async Task UpsertSettingAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        string value,
        DateTimeOffset updatedUtc,
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
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRootAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string key,
        string? value,
        DateTimeOffset updatedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        if (string.IsNullOrWhiteSpace(value))
        {
            command.CommandText = "DELETE FROM root_paths WHERE root_key = @key;";
            AddParameter(command, "@key", key);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        command.CommandText =
            """
            INSERT INTO root_paths (root_key, root_path, updated_utc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(root_key) DO UPDATE SET
                root_path = excluded.root_path,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@key", key);
        AddParameter(command, "@value", value);
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSeedLibrariesAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var count = 0;

        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM libraries;";
            var scalar = await countCommand.ExecuteScalarAsync(cancellationToken);
            count = Convert.ToInt32(scalar ?? 0, CultureInfo.InvariantCulture);
        }

        if (count > 0)
        {
            return;
        }

        var roots = await ReadRootsAsync(connection, cancellationToken);
        var downloadsPath = GetValue(roots, "downloads");
        var now = DateTimeOffset.UtcNow;

        var seeds = new List<LibraryItem>();
        var movieRoot = GetValue(roots, "movies");
        if (!string.IsNullOrWhiteSpace(movieRoot))
        {
            seeds.Add(new LibraryItem(
                Id: Guid.CreateVersion7().ToString("N"),
                Name: "Movies / Main",
                MediaType: "movies",
                Purpose: "Everyday library",
                RootPath: movieRoot,
                DownloadsPath: downloadsPath,
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalHours: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25,
                AutomationStatus: "idle",
                SearchRequested: false,
                LastSearchedUtc: null,
                NextSearchUtc: null,
                CreatedUtc: now,
                UpdatedUtc: now));
        }

        var tvRoot = GetValue(roots, "series");
        if (!string.IsNullOrWhiteSpace(tvRoot))
        {
            seeds.Add(new LibraryItem(
                Id: Guid.CreateVersion7().ToString("N"),
                Name: "TV Shows / Main",
                MediaType: "tv",
                Purpose: "General shows",
                RootPath: tvRoot,
                DownloadsPath: downloadsPath,
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalHours: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25,
                AutomationStatus: "idle",
                SearchRequested: false,
                LastSearchedUtc: null,
                NextSearchUtc: null,
                CreatedUtc: now,
                UpdatedUtc: now));
        }

        foreach (var item in seeds)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO libraries (
                    id, name, media_type, purpose, root_path, downloads_path, auto_search_enabled,
                    missing_search_enabled, upgrade_search_enabled, search_interval_hours,
                    retry_delay_hours, max_items_per_run, created_utc, updated_utc
                )
                VALUES (
                    @id, @name, @mediaType, @purpose, @rootPath, @downloadsPath, @autoSearchEnabled,
                    @missingSearchEnabled, @upgradeSearchEnabled, @searchIntervalHours,
                    @retryDelayHours, @maxItemsPerRun, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(command, "@id", item.Id);
            AddParameter(command, "@name", item.Name);
            AddParameter(command, "@mediaType", item.MediaType);
            AddParameter(command, "@purpose", item.Purpose);
            AddParameter(command, "@rootPath", item.RootPath);
            AddParameter(command, "@downloadsPath", item.DownloadsPath);
            AddParameter(command, "@autoSearchEnabled", item.AutoSearchEnabled ? 1 : 0);
            AddParameter(command, "@missingSearchEnabled", item.MissingSearchEnabled ? 1 : 0);
            AddParameter(command, "@upgradeSearchEnabled", item.UpgradeSearchEnabled ? 1 : 0);
            AddParameter(command, "@searchIntervalHours", item.SearchIntervalHours);
            AddParameter(command, "@retryDelayHours", item.RetryDelayHours);
            AddParameter(command, "@maxItemsPerRun", item.MaxItemsPerRun);
            AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
            AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<LibraryItem?> GetLibraryAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, purpose, root_path, downloads_path, auto_search_enabled,
                missing_search_enabled, upgrade_search_enabled, search_interval_hours,
                retry_delay_hours, max_items_per_run, created_utc, updated_utc
            FROM libraries
            WHERE id = @id
            LIMIT 1;
            """;

        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LibraryItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            MediaType: reader.GetString(2),
            Purpose: reader.GetString(3),
            RootPath: reader.GetString(4),
            DownloadsPath: reader.IsDBNull(5) ? null : reader.GetString(5),
            AutoSearchEnabled: reader.GetInt64(6) == 1,
            MissingSearchEnabled: reader.GetInt64(7) == 1,
            UpgradeSearchEnabled: reader.GetInt64(8) == 1,
            SearchIntervalHours: reader.GetInt32(9),
            RetryDelayHours: reader.GetInt32(10),
            MaxItemsPerRun: reader.GetInt32(11),
            AutomationStatus: "idle",
            SearchRequested: false,
            LastSearchedUtc: null,
            NextSearchUtc: null,
            CreatedUtc: ParseTimestamp(reader.GetString(12)),
            UpdatedUtc: ParseTimestamp(reader.GetString(13)));
    }

    private static string? NormalizeName(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizePath(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeMediaType(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "movies" => "movies",
            "tv" => "tv",
            "tv shows" => "tv",
            "tvshows" => "tv",
            _ => "movies"
        };
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
            "usenet" => "usenet",
            _ => "torrent"
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

    private static string NormalizeCsv(string? value)
    {
        return string.Join(
            ", ",
            (value ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static int NormalizePositiveValue(int? value, int fallback)
    {
        return value is > 0 ? value.Value : fallback;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
