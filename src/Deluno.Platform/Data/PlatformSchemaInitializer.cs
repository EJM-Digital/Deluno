using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Deluno.Platform.Data;

public sealed class PlatformSchemaInitializer(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    ILogger<PlatformSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS system_settings (
                setting_key TEXT PRIMARY KEY,
                setting_value TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS root_paths (
                root_key TEXT PRIMARY KEY,
                root_path TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS libraries (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                purpose TEXT NOT NULL,
                root_path TEXT NOT NULL,
                downloads_path TEXT NULL,
                quality_profile_id TEXT NULL,
                import_workflow TEXT NOT NULL DEFAULT 'standard',
                processor_name TEXT NULL,
                processor_output_path TEXT NULL,
                processor_timeout_minutes INTEGER NOT NULL DEFAULT 360,
                processor_failure_mode TEXT NOT NULL DEFAULT 'block',
                auto_search_enabled INTEGER NOT NULL DEFAULT 1,
                missing_search_enabled INTEGER NOT NULL DEFAULT 1,
                upgrade_search_enabled INTEGER NOT NULL DEFAULT 1,
                search_interval_hours INTEGER NOT NULL DEFAULT 6,
                retry_delay_hours INTEGER NOT NULL DEFAULT 24,
                max_items_per_run INTEGER NOT NULL DEFAULT 25,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (quality_profile_id) REFERENCES quality_profiles(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS quality_profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                cutoff_quality TEXT NOT NULL,
                allowed_qualities TEXT NOT NULL,
                custom_format_ids TEXT NOT NULL DEFAULT '',
                upgrade_until_cutoff INTEGER NOT NULL DEFAULT 1,
                upgrade_unknown_items INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tags (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                color TEXT NOT NULL DEFAULT 'slate',
                description TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_tags_name
                ON tags (name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS intake_sources (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                provider TEXT NOT NULL,
                feed_url TEXT NOT NULL,
                media_type TEXT NOT NULL,
                library_id TEXT NULL,
                quality_profile_id TEXT NULL,
                search_on_add INTEGER NOT NULL DEFAULT 1,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE SET NULL,
                FOREIGN KEY (quality_profile_id) REFERENCES quality_profiles(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS custom_formats (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                score INTEGER NOT NULL DEFAULT 0,
                conditions TEXT NOT NULL DEFAULT '',
                upgrade_allowed INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS destination_rules (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                match_kind TEXT NOT NULL,
                match_value TEXT NOT NULL,
                root_path TEXT NOT NULL,
                folder_template TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_destination_rules_media_type_priority
                ON destination_rules (media_type, priority, name);

            CREATE TABLE IF NOT EXISTS policy_sets (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                media_type TEXT NOT NULL,
                quality_profile_id TEXT NULL,
                destination_rule_id TEXT NULL,
                custom_format_ids TEXT NOT NULL DEFAULT '',
                search_interval_override_hours INTEGER NULL,
                retry_delay_override_hours INTEGER NULL,
                upgrade_until_cutoff INTEGER NOT NULL DEFAULT 1,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                notes TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (quality_profile_id) REFERENCES quality_profiles(id) ON DELETE SET NULL,
                FOREIGN KEY (destination_rule_id) REFERENCES destination_rules(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS library_views (
                id TEXT PRIMARY KEY,
                user_id TEXT NOT NULL,
                variant TEXT NOT NULL,
                name TEXT NOT NULL,
                quick_filter TEXT NOT NULL,
                sort_field TEXT NOT NULL,
                sort_direction TEXT NOT NULL,
                view_mode TEXT NOT NULL,
                card_size TEXT NOT NULL,
                display_options_json TEXT NOT NULL DEFAULT '{}',
                rules_json TEXT NOT NULL DEFAULT '[]',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_library_views_user_variant
                ON library_views (user_id, variant, name);

            CREATE INDEX IF NOT EXISTS ix_policy_sets_media_type_name
                ON policy_sets (media_type, name);

            CREATE INDEX IF NOT EXISTS ix_quality_profiles_media_type
                ON quality_profiles (media_type, name);

            CREATE TABLE IF NOT EXISTS users (
                id TEXT PRIMARY KEY,
                username TEXT NOT NULL,
                display_name TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                avatar_initials TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_users_username
                ON users (username COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS api_keys (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                key_hash TEXT NOT NULL,
                prefix TEXT NOT NULL,
                scopes TEXT NOT NULL DEFAULT 'all',
                last_used_utc TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_api_keys_key_hash
                ON api_keys (key_hash);

            CREATE TABLE IF NOT EXISTS app_connections (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                connection_kind TEXT NOT NULL,
                role TEXT NOT NULL,
                endpoint_url TEXT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS indexer_sources (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                protocol TEXT NOT NULL,
                privacy TEXT NOT NULL,
                base_url TEXT NOT NULL,
                api_key TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                categories TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '',
                media_scope TEXT NOT NULL DEFAULT 'both',
                is_enabled INTEGER NOT NULL DEFAULT 1,
                health_status TEXT NOT NULL DEFAULT 'unknown',
                last_health_message TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS download_clients (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                protocol TEXT NOT NULL,
                host TEXT NULL,
                port INTEGER NULL,
                username TEXT NULL,
                secret TEXT NULL,
                endpoint_url TEXT NULL,
                movies_category TEXT NULL,
                tv_category TEXT NULL,
                category_template TEXT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                health_status TEXT NOT NULL DEFAULT 'unknown',
                last_health_message TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS library_source_links (
                id TEXT PRIMARY KEY,
                library_id TEXT NOT NULL,
                indexer_id TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                required_tags TEXT NOT NULL DEFAULT '',
                excluded_tags TEXT NOT NULL DEFAULT '',
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
                FOREIGN KEY (indexer_id) REFERENCES indexer_sources(id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_library_source_links_unique
                ON library_source_links (library_id, indexer_id);

            CREATE TABLE IF NOT EXISTS library_download_client_links (
                id TEXT PRIMARY KEY,
                library_id TEXT NOT NULL,
                download_client_id TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 100,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                FOREIGN KEY (library_id) REFERENCES libraries(id) ON DELETE CASCADE,
                FOREIGN KEY (download_client_id) REFERENCES download_clients(id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_library_download_client_links_unique
                ON library_download_client_links (library_id, download_client_id);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureLibraryColumnAsync(connection, "quality_profile_id", "TEXT NULL", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "import_workflow", "TEXT NOT NULL DEFAULT 'standard'", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "processor_name", "TEXT NULL", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "processor_output_path", "TEXT NULL", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "processor_timeout_minutes", "INTEGER NOT NULL DEFAULT 360", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "processor_failure_mode", "TEXT NOT NULL DEFAULT 'block'", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "missing_search_enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "upgrade_search_enabled", "INTEGER NOT NULL DEFAULT 1", cancellationToken);
        await EnsureLibraryColumnAsync(connection, "max_items_per_run", "INTEGER NOT NULL DEFAULT 25", cancellationToken);
        await EnsureQualityProfileColumnAsync(connection, "sort_order", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await EnsureQualityProfileColumnAsync(connection, "custom_format_ids", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureDownloadClientColumnAsync(connection, "host", "TEXT NULL", cancellationToken);
        await EnsureDownloadClientColumnAsync(connection, "port", "INTEGER NULL", cancellationToken);
        await EnsureDownloadClientColumnAsync(connection, "username", "TEXT NULL", cancellationToken);
        await EnsureDownloadClientColumnAsync(connection, "secret", "TEXT NULL", cancellationToken);
        await EnsureDownloadClientColumnAsync(connection, "movies_category", "TEXT NULL", cancellationToken);
        await EnsureDownloadClientColumnAsync(connection, "tv_category", "TEXT NULL", cancellationToken);
        await EnsureIndexerColumnAsync(connection, "api_key", "TEXT NULL", cancellationToken);
        await EnsureIndexerColumnAsync(connection, "media_scope", "TEXT NOT NULL DEFAULT 'both'", cancellationToken);
        await EnsureUsersSchemaAsync(connection, logger, cancellationToken);
        await DeleteLegacySeedUserAsync(connection, logger, cancellationToken);

        logger.LogInformation(
            "Platform schema is ready at {DatabasePath}.",
            databaseConnectionFactory.GetDatabasePath(DelunoDatabaseNames.Platform));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureLibraryColumnAsync(
        System.Data.Common.DbConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(libraries);";

        var exists = false;
        using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE libraries ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureQualityProfileColumnAsync(
        System.Data.Common.DbConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(quality_profiles);";

        var exists = false;
        using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE quality_profiles ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureDownloadClientColumnAsync(
        System.Data.Common.DbConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(download_clients);";

        var exists = false;
        using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE download_clients ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureIndexerColumnAsync(
        System.Data.Common.DbConnection connection,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(indexer_sources);";

        var exists = false;
        using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE indexer_sources ADD COLUMN {columnName} {columnDefinition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureUsersSchemaAsync(
        System.Data.Common.DbConnection connection,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(users);";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = await check.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        var expected = new[]
        {
            "id",
            "username",
            "display_name",
            "password_hash",
            "avatar_initials",
            "created_utc",
            "updated_utc"
        };

        if (expected.All(columns.Contains) && columns.Count == expected.Length)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            using var disableForeignKeys = connection.CreateCommand();
            disableForeignKeys.Transaction = transaction;
            disableForeignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            await disableForeignKeys.ExecuteNonQueryAsync(cancellationToken);

            using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText =
                """
                CREATE TABLE users_next (
                    id TEXT PRIMARY KEY,
                    username TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    password_hash TEXT NOT NULL,
                    avatar_initials TEXT NOT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);

            using var copy = connection.CreateCommand();
            copy.Transaction = transaction;
            copy.CommandText =
                """
                INSERT INTO users_next (
                    id, username, display_name, password_hash, avatar_initials, created_utc, updated_utc
                )
                SELECT
                    id,
                    username,
                    display_name,
                    password_hash,
                    avatar_initials,
                    created_utc,
                    updated_utc
                FROM users;
                """;
            await copy.ExecuteNonQueryAsync(cancellationToken);

            using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = "DROP TABLE users;";
            await drop.ExecuteNonQueryAsync(cancellationToken);

            using var rename = connection.CreateCommand();
            rename.Transaction = transaction;
            rename.CommandText = "ALTER TABLE users_next RENAME TO users;";
            await rename.ExecuteNonQueryAsync(cancellationToken);

            using var index = connection.CreateCommand();
            index.Transaction = transaction;
            index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ix_users_username ON users (username COLLATE NOCASE);";
            await index.ExecuteNonQueryAsync(cancellationToken);

            using var enableForeignKeys = connection.CreateCommand();
            enableForeignKeys.Transaction = transaction;
            enableForeignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            await enableForeignKeys.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Migrated users table to the current single-user schema.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task DeleteLegacySeedUserAsync(
        System.Data.Common.DbConnection connection,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM users
            WHERE username = 'admin'
              AND display_name = 'Administrator'
              AND avatar_initials = 'AD';
            """;

        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} legacy seeded user row(s).", removed);
        }
    }
}
