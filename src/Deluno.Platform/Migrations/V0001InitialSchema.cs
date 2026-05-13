using Deluno.Infrastructure.Storage.Migrations;

namespace Deluno.Platform.Migrations;

public static class PlatformDatabaseMigrations
{
    public static readonly IReadOnlyList<IDelunoDatabaseMigration> All =
    [
        new V0001InitialSchema(),
        new V0002UserSecurityStamp(),
        new V0003IntegrationHealth(),
        new V0004QualityProfileReplacementProtection(),
        new V0005QualityProfilePresetTracking(),
        new V0006IndexerRateLimitTracking(),
        new V0007LibrarySearchWindows(),
        new V0008NotificationWebhooks(),
        new V0009CustomFormatTrashIds(),
        new V0010IntakeSourceSyncConfig()
    ];

    private sealed class V0001InitialSchema : SqliteSqlMigration
    {
        public override int Version => 1;

        public override string Name => "initial_schema";

        protected override string Sql =>
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
    }
}
