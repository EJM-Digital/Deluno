using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using Deluno.Platform.Security;

namespace Deluno.Platform.Data;

public sealed class SqlitePlatformSettingsRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    ISecretProtector secretProtector)
    : IPlatformSettingsRepository
{
    // Pre-computed hash used to ensure constant-time response when a username is not found,
    // preventing timing-based username enumeration attacks.
    private static readonly string DummyPasswordHash =
        "100000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

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
        await UpsertSettingAsync(connection, transaction, "media.renameOnImport", request.RenameOnImport ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.useHardlinks", request.UseHardlinks ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.cleanupEmptyFolders", request.CleanupEmptyFolders ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.removeCompletedDownloads", request.RemoveCompletedDownloads ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.unmonitorWhenCutoffMet", request.UnmonitorWhenCutoffMet ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.movieFolderFormat", NormalizeName(request.MovieFolderFormat) ?? "{Movie Title} ({Release Year})", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.seriesFolderFormat", NormalizeName(request.SeriesFolderFormat) ?? "{Series Title} ({Series Year})", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.episodeFileFormat", NormalizeName(request.EpisodeFileFormat) ?? "{Series Title} - S{season:00}E{episode:00} - {Episode Title}", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.bindAddress", NormalizeName(request.HostBindAddress) ?? "127.0.0.1", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.port", NormalizePositiveValue(request.HostPort, 5099).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "host.urlBase", NormalizeName(request.UrlBase) ?? string.Empty, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "security.requireAuthentication", "true", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.theme", NormalizeUiTheme(request.UiTheme), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.density", NormalizeUiDensity(request.UiDensity), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.defaultMovieView", NormalizeUiView(request.DefaultMovieView), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "ui.defaultShowView", NormalizeUiView(request.DefaultShowView), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.nfoEnabled", request.MetadataNfoEnabled ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.artworkEnabled", request.MetadataArtworkEnabled ? "true" : "false", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.certificationCountry", NormalizeName(request.MetadataCertificationCountry) ?? "US", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.language", NormalizeName(request.MetadataLanguage) ?? "en", updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.providerMode", NormalizeMetadataProviderMode(request.MetadataProviderMode), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "metadata.brokerUrl", NormalizeMetadataBrokerUrl(request.MetadataBrokerUrl) ?? string.Empty, updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "search.neverGrabPatterns", NormalizeNeverGrabPatterns(request.ReleaseNeverGrabPatterns), updatedUtc, cancellationToken);
        await UpsertSettingAsync(connection, transaction, "media.importRecoveryRetentionDays", NormalizePositiveValue(request.ImportRecoveryRetentionDays ?? 30, 30).ToString(CultureInfo.InvariantCulture), updatedUtc, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.MetadataTmdbApiKey))
        {
            await UpsertSettingAsync(
                connection,
                transaction,
                "metadata.tmdbApiKey",
                secretProtector.Protect("metadata:tmdb", request.MetadataTmdbApiKey.Trim()),
                updatedUtc,
                cancellationToken);
        }
        if (!string.IsNullOrWhiteSpace(request.MetadataOmdbApiKey))
        {
            await UpsertSettingAsync(
                connection,
                transaction,
                "metadata.omdbApiKey",
                secretProtector.Protect("metadata:omdb", request.MetadataOmdbApiKey.Trim()),
                updatedUtc,
                cancellationToken);
        }

        await UpsertRootAsync(connection, transaction, "movies", NormalizePath(request.MovieRootPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "series", NormalizePath(request.SeriesRootPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "downloads", NormalizePath(request.DownloadsPath), updatedUtc, cancellationToken);
        await UpsertRootAsync(connection, transaction, "downloads.incomplete", NormalizePath(request.IncompleteDownloadsPath), updatedUtc, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var settings = await ReadSettingsAsync(connection, cancellationToken);
        var roots = await ReadRootsAsync(connection, cancellationToken);
        return CreateSnapshot(settings, roots);
    }

    public async Task<string?> GetMetadataProviderSecretAsync(string provider, CancellationToken cancellationToken)
    {
        var settingKey = provider.Trim().ToLowerInvariant() switch
        {
            "tmdb" => "metadata.tmdbApiKey",
            "omdb" => "metadata.omdbApiKey",
            _ => null
        };

        if (settingKey is null)
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_value FROM system_settings WHERE setting_key = @settingKey;";
        AddParameter(command, "@settingKey", settingKey);
        var stored = await command.ExecuteScalarAsync(cancellationToken) as string;
        return secretProtector.Unprotect($"metadata:{provider.Trim().ToLowerInvariant()}", stored);
    }

    public async Task<IReadOnlyList<LibraryItem>> ListLibrariesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await EnsureSeedQualityProfilesAsync(connection, cancellationToken);
        await BackfillLibraryQualityProfilesAsync(connection, cancellationToken);
        await EnsureSeedLibrariesAsync(connection, cancellationToken);

        var items = new List<LibraryItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                l.id, l.name, l.media_type, l.purpose, l.root_path, l.downloads_path,
                l.quality_profile_id, q.name, q.cutoff_quality, q.upgrade_until_cutoff, q.upgrade_unknown_items,
                l.import_workflow, l.processor_name, l.processor_output_path, l.processor_timeout_minutes, l.processor_failure_mode,
                l.auto_search_enabled, l.missing_search_enabled, l.upgrade_search_enabled, l.search_interval_hours,
                l.retry_delay_hours, l.max_items_per_run, l.search_window_start_hour, l.search_window_end_hour,
                l.created_utc, l.updated_utc
            FROM libraries l
            LEFT JOIN quality_profiles q ON q.id = l.quality_profile_id
            ORDER BY l.media_type ASC, l.name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadLibrary(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<QualityProfileItem>> ListQualityProfilesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await EnsureSeedQualityProfilesAsync(connection, cancellationToken);

        var items = new List<QualityProfileItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, cutoff_quality, allowed_qualities, custom_format_ids,
                upgrade_until_cutoff, upgrade_unknown_items, allow_lower_quality_replacements,
                preset_id, preset_version, created_utc, updated_utc
            FROM quality_profiles
            ORDER BY sort_order ASC, media_type ASC, name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadQualityProfile(reader));
        }

        return items;
    }

    public async Task ReorderQualityProfilesAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        for (var index = 0; index < ids.Count; index++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE quality_profiles
                SET sort_order = @sortOrder
                WHERE id = @id;
                """;
            AddParameter(command, "@id", ids[index]);
            AddParameter(command, "@sortOrder", index + 1);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TagItem>> ListTagsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<TagItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, color, description, created_utc, updated_utc
            FROM tags
            ORDER BY name COLLATE NOCASE ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadTag(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<IntakeSourceItem>> ListIntakeSourcesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<IntakeSourceItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id, s.name, s.provider, s.feed_url, s.media_type,
                s.library_id, l.name, s.quality_profile_id, q.name,
                s.required_genres, s.minimum_rating, s.minimum_year, s.maximum_age_days,
                s.allowed_certifications, s.audience, s.sync_interval_hours,
                s.last_sync_utc, s.last_sync_status, s.last_sync_summary,
                s.search_on_add, s.is_enabled, s.created_utc, s.updated_utc
            FROM intake_sources s
            LEFT JOIN libraries l ON l.id = s.library_id
            LEFT JOIN quality_profiles q ON q.id = s.quality_profile_id
            ORDER BY s.name COLLATE NOCASE ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadIntakeSource(reader));
        }

        return items;
    }

    public async Task<IntakeSourceItem?> GetIntakeSourceAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        return await GetIntakeSourceAsync(connection, id, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomFormatItem>> ListCustomFormatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<CustomFormatItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, score, trash_id, conditions, upgrade_allowed, created_utc, updated_utc
            FROM custom_formats
            ORDER BY media_type ASC, score DESC, name COLLATE NOCASE ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadCustomFormat(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<DestinationRuleItem>> ListDestinationRulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<DestinationRuleItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, match_kind, match_value, root_path, folder_template,
                priority, is_enabled, created_utc, updated_utc
            FROM destination_rules
            ORDER BY media_type ASC, priority ASC, name COLLATE NOCASE ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadDestinationRule(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<PolicySetItem>> ListPolicySetsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<PolicySetItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                p.id, p.name, p.media_type,
                p.quality_profile_id, q.name,
                p.destination_rule_id, d.name,
                p.custom_format_ids, p.search_interval_override_hours, p.retry_delay_override_hours,
                p.upgrade_until_cutoff, p.is_enabled, p.notes, p.created_utc, p.updated_utc
            FROM policy_sets p
            LEFT JOIN quality_profiles q ON q.id = p.quality_profile_id
            LEFT JOIN destination_rules d ON d.id = p.destination_rule_id
            ORDER BY p.media_type ASC, p.name COLLATE NOCASE ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadPolicySet(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<LibraryViewItem>> ListLibraryViewsAsync(
        string userId,
        string variant,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<LibraryViewItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, user_id, variant, name, quick_filter, sort_field, sort_direction,
                view_mode, card_size, display_options_json, rules_json, created_utc, updated_utc
            FROM library_views
            WHERE user_id = @userId AND variant = @variant
            ORDER BY name COLLATE NOCASE ASC;
            """;
        AddParameter(command, "@userId", userId);
        AddParameter(command, "@variant", NormalizeLibraryViewVariant(variant));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadLibraryView(reader));
        }

        return items;
    }

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        return await HasUsersAsync(connection, cancellationToken);
    }

    public async Task<bool> RequiresBootstrapAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        return await RequiresBootstrapAsync(connection, cancellationToken);
    }

    public async Task<UserItem?> ValidateUserCredentialsAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, username, display_name, password_hash, avatar_initials, security_stamp, created_utc
            FROM users
            WHERE username = @username COLLATE NOCASE
            LIMIT 1;
            """;
        AddParameter(command, "@username", NormalizeName(username));

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            VerifyPassword(password, DummyPasswordHash);
            return null;
        }

        var passwordHash = reader.GetString(3);
        if (!VerifyPassword(password, passwordHash))
        {
            return null;
        }

        return new UserItem(
            Id: reader.GetString(0),
            Username: reader.GetString(1),
            DisplayName: reader.GetString(2),
            AvatarInitials: reader.GetString(4),
            SecurityStamp: ReadSecurityStamp(reader, 5),
            CreatedUtc: ParseTimestamp(reader.GetString(6)));
    }

    public async Task<UserItem?> GetUserByIdAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, username, display_name, avatar_initials, security_stamp, created_utc
            FROM users
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserItem(
            Id: reader.GetString(0),
            Username: reader.GetString(1),
            DisplayName: reader.GetString(2),
            AvatarInitials: reader.GetString(3),
            SecurityStamp: ReadSecurityStamp(reader, 4),
            CreatedUtc: ParseTimestamp(reader.GetString(5)));
    }

    public async Task<IReadOnlyList<ApiKeyItem>> ListApiKeysAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<ApiKeyItem>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, prefix, scopes, last_used_utc, created_utc, updated_utc
            FROM api_keys
            ORDER BY created_utc DESC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadApiKey(reader));
        }

        return items;
    }

    public async Task<CreatedApiKeyResponse> CreateApiKeyAsync(
        CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        var rawKey = GenerateApiKey();
        var prefix = BuildApiKeyPrefix(rawKey);
        var item = new ApiKeyItem(
            Guid.CreateVersion7().ToString("N"),
            NormalizeName(request.Name) ?? "API key",
            prefix,
            NormalizeApiScopes(request.Scopes),
            null,
            now,
            now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO api_keys (
                id, name, key_hash, prefix, scopes, last_used_utc, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @keyHash, @prefix, @scopes, NULL, @createdUtc, @updatedUtc
            );
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@keyHash", HashApiKey(rawKey));
        AddParameter(command, "@prefix", item.Prefix);
        AddParameter(command, "@scopes", item.Scopes);
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new CreatedApiKeyResponse(item, rawKey);
    }

    public async Task<ApiKeyItem?> ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var keyHash = HashApiKey(apiKey.Trim());
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, prefix, scopes, last_used_utc, created_utc, updated_utc
            FROM api_keys
            WHERE key_hash = @keyHash
            LIMIT 1;
            """;
        AddParameter(command, "@keyHash", keyHash);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var item = ReadApiKey(reader);
        await reader.DisposeAsync();

        using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE api_keys
            SET last_used_utc = @lastUsedUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        var now = timeProvider.GetUtcNow();
        AddParameter(update, "@id", item.Id);
        AddParameter(update, "@lastUsedUtc", now.ToString("O"));
        AddParameter(update, "@updatedUtc", now.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);

        return item with
        {
            LastUsedUtc = now,
            UpdatedUtc = now
        };
    }

    public async Task<bool> DeleteApiKeyAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM api_keys WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ChangeUserPasswordAsync(
        string userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText =
            """
            SELECT password_hash
            FROM users
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(readCommand, "@id", userId);

        var existing = await readCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(existing) || !VerifyPassword(currentPassword, existing))
        {
            return false;
        }

        using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText =
            """
            UPDATE users
            SET password_hash = @passwordHash,
                security_stamp = @securityStamp,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(updateCommand, "@id", userId);
        AddParameter(updateCommand, "@passwordHash", HashPassword(newPassword));
        AddParameter(updateCommand, "@securityStamp", CreateSecurityStamp());
        AddParameter(updateCommand, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));

        return await updateCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> RevokeUserAccessTokensAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE users
            SET security_stamp = @securityStamp,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", userId);
        AddParameter(command, "@securityStamp", CreateSecurityStamp());
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<UserItem> BootstrapUserAsync(
        BootstrapUserRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        if (await HasUsersAsync(connection, cancellationToken))
        {
            throw new InvalidOperationException("Deluno has already been configured.");
        }

        var now = timeProvider.GetUtcNow();
        var username = NormalizeName(request.Username) ?? "user";
        var displayName = NormalizeName(request.DisplayName) ?? username;
        var item = new UserItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Username: username,
            DisplayName: displayName,
            AvatarInitials: BuildAvatarInitials(displayName),
            SecurityStamp: CreateSecurityStamp(),
            CreatedUtc: now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO users (
                id, username, display_name, password_hash, avatar_initials, security_stamp, created_utc, updated_utc
            )
            VALUES (
                @id, @username, @displayName, @passwordHash, @avatarInitials, @securityStamp, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@username", item.Username);
        AddParameter(command, "@displayName", item.DisplayName);
        AddParameter(command, "@passwordHash", HashPassword(request.Password ?? string.Empty));
        AddParameter(command, "@avatarInitials", item.AvatarInitials);
        AddParameter(command, "@securityStamp", item.SecurityStamp);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<QualityProfileItem> CreateQualityProfileAsync(
        CreateQualityProfileRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new QualityProfileItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New quality profile",
            MediaType: NormalizeMediaType(request.MediaType),
            CutoffQuality: NormalizeName(request.CutoffQuality) ?? DefaultCutoffForMediaType(request.MediaType),
            AllowedQualities: string.IsNullOrWhiteSpace(request.AllowedQualities)
                ? DefaultAllowedQualities(request.MediaType)
                : NormalizeCsv(request.AllowedQualities),
            CustomFormatIds: NormalizeCsv(request.CustomFormatIds),
            UpgradeUntilCutoff: request.UpgradeUntilCutoff,
            UpgradeUnknownItems: request.UpgradeUnknownItems,
            AllowLowerQualityReplacements: false,
            PresetId: null,
            PresetVersion: null,
            PresetDrifted: false,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await InsertQualityProfileAsync(connection, item, cancellationToken);
        return item;
    }

    public async Task<QualityProfileItem> CreateQualityProfileFromPresetAsync(
        string presetId,
        string? nameOverride,
        CancellationToken cancellationToken)
    {
        var preset = Presets.QualityProfilePresetCatalog.FindById(presetId)
            ?? throw new InvalidOperationException($"Preset '{presetId}' not found.");

        var now = timeProvider.GetUtcNow();
        var item = new QualityProfileItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(nameOverride) ?? preset.Name,
            MediaType: preset.MediaType,
            CutoffQuality: preset.CutoffQuality,
            AllowedQualities: preset.AllowedQualities,
            CustomFormatIds: string.Empty,
            UpgradeUntilCutoff: preset.UpgradeUntilCutoff,
            UpgradeUnknownItems: preset.UpgradeUnknownItems,
            AllowLowerQualityReplacements: false,
            PresetId: preset.Id,
            PresetVersion: preset.Version,
            PresetDrifted: false,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await InsertQualityProfileAsync(connection, item, cancellationToken);
        return item;
    }

    private async Task InsertQualityProfileAsync(
        System.Data.Common.DbConnection connection,
        QualityProfileItem item,
        CancellationToken cancellationToken)
    {
        var sortOrder = await GetNextQualityProfileSortOrderAsync(connection, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO quality_profiles (
                id, name, media_type, sort_order, cutoff_quality, allowed_qualities, custom_format_ids,
                upgrade_until_cutoff, upgrade_unknown_items, allow_lower_quality_replacements,
                preset_id, preset_version, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @sortOrder, @cutoffQuality, @allowedQualities, @customFormatIds,
                @upgradeUntilCutoff, @upgradeUnknownItems, @allowLowerQualityReplacements,
                @presetId, @presetVersion, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@mediaType", item.MediaType);
        AddParameter(command, "@sortOrder", sortOrder);
        AddParameter(command, "@cutoffQuality", item.CutoffQuality);
        AddParameter(command, "@allowedQualities", item.AllowedQualities);
        AddParameter(command, "@customFormatIds", item.CustomFormatIds);
        AddParameter(command, "@upgradeUntilCutoff", item.UpgradeUntilCutoff ? 1 : 0);
        AddParameter(command, "@upgradeUnknownItems", item.UpgradeUnknownItems ? 1 : 0);
        AddParameter(command, "@allowLowerQualityReplacements", item.AllowLowerQualityReplacements ? 1 : 0);
        AddParameter(command, "@presetId", item.PresetId);
        AddParameter(command, "@presetVersion", (object?)item.PresetVersion ?? DBNull.Value);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TagItem> CreateTagAsync(
        CreateTagRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new TagItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New tag",
            Color: NormalizeTagColor(request.Color),
            Description: NormalizeName(request.Description) ?? string.Empty,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tags (
                id, name, color, description, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @color, @description, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@color", item.Color);
        AddParameter(command, "@description", item.Description);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<IntakeSourceItem> CreateIntakeSourceAsync(
        CreateIntakeSourceRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var mediaType = NormalizeMediaType(request.MediaType);
        var item = new IntakeSourceItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New list source",
            Provider: NormalizeName(request.Provider) ?? "manual",
            FeedUrl: NormalizeName(request.FeedUrl) ?? string.Empty,
            MediaType: mediaType,
            LibraryId: NormalizeName(request.LibraryId),
            LibraryName: null,
            QualityProfileId: NormalizeName(request.QualityProfileId),
            QualityProfileName: null,
            RequiredGenres: NormalizeCsv(request.RequiredGenres),
            MinimumRating: NormalizeNullableRating(request.MinimumRating),
            MinimumYear: NormalizeNullableYear(request.MinimumYear),
            MaximumAgeDays: NormalizeNullablePositiveValue(request.MaximumAgeDays),
            AllowedCertifications: NormalizeCsv(request.AllowedCertifications),
            Audience: NormalizeAudience(request.Audience),
            SyncIntervalHours: NormalizeSyncIntervalHours(request.SyncIntervalHours),
            LastSyncUtc: null,
            LastSyncStatus: "never",
            LastSyncSummary: null,
            SearchOnAdd: request.SearchOnAdd,
            IsEnabled: request.IsEnabled,
            CreatedUtc: now,
            UpdatedUtc: now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO intake_sources (
                id, name, provider, feed_url, media_type, library_id, quality_profile_id,
                required_genres, minimum_rating, minimum_year, maximum_age_days,
                allowed_certifications, audience, sync_interval_hours, last_sync_utc, last_sync_status, last_sync_summary,
                search_on_add, is_enabled, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @provider, @feedUrl, @mediaType, @libraryId, @qualityProfileId,
                @requiredGenres, @minimumRating, @minimumYear, @maximumAgeDays,
                @allowedCertifications, @audience, @syncIntervalHours, @lastSyncUtc, @lastSyncStatus, @lastSyncSummary,
                @searchOnAdd, @isEnabled, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@provider", item.Provider);
        AddParameter(command, "@feedUrl", item.FeedUrl);
        AddParameter(command, "@mediaType", item.MediaType);
        AddParameter(command, "@libraryId", item.LibraryId);
        AddParameter(command, "@qualityProfileId", item.QualityProfileId);
        AddParameter(command, "@requiredGenres", item.RequiredGenres);
        AddParameter(command, "@minimumRating", item.MinimumRating);
        AddParameter(command, "@minimumYear", item.MinimumYear);
        AddParameter(command, "@maximumAgeDays", item.MaximumAgeDays);
        AddParameter(command, "@allowedCertifications", item.AllowedCertifications);
        AddParameter(command, "@audience", item.Audience);
        AddParameter(command, "@syncIntervalHours", item.SyncIntervalHours);
        AddParameter(command, "@lastSyncUtc", item.LastSyncUtc?.ToString("O"));
        AddParameter(command, "@lastSyncStatus", item.LastSyncStatus);
        AddParameter(command, "@lastSyncSummary", item.LastSyncSummary);
        AddParameter(command, "@searchOnAdd", item.SearchOnAdd ? 1 : 0);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetIntakeSourceAsync(connection, item.Id, cancellationToken))!;
    }

    public async Task<CustomFormatItem> CreateCustomFormatAsync(
        CreateCustomFormatRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new CustomFormatItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New custom format",
            MediaType: NormalizeMediaType(request.MediaType),
            Score: request.Score,
            TrashId: NormalizeName(request.TrashId),
            Conditions: NormalizeName(request.Conditions) ?? string.Empty,
            UpgradeAllowed: request.UpgradeAllowed,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO custom_formats (
                id, name, media_type, score, trash_id, conditions, upgrade_allowed, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @score, @trashId, @conditions, @upgradeAllowed, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@mediaType", item.MediaType);
        AddParameter(command, "@score", item.Score);
        AddParameter(command, "@trashId", item.TrashId);
        AddParameter(command, "@conditions", item.Conditions);
        AddParameter(command, "@upgradeAllowed", item.UpgradeAllowed ? 1 : 0);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<DestinationRuleItem> CreateDestinationRuleAsync(
        CreateDestinationRuleRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new DestinationRuleItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New destination rule",
            MediaType: NormalizeMediaType(request.MediaType),
            MatchKind: NormalizeDestinationMatchKind(request.MatchKind),
            MatchValue: NormalizeName(request.MatchValue) ?? string.Empty,
            RootPath: NormalizePath(request.RootPath) ?? string.Empty,
            FolderTemplate: NormalizeName(request.FolderTemplate),
            Priority: NormalizePriorityValue(request.Priority),
            IsEnabled: request.IsEnabled,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO destination_rules (
                id, name, media_type, match_kind, match_value, root_path, folder_template,
                priority, is_enabled, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @matchKind, @matchValue, @rootPath, @folderTemplate,
                @priority, @isEnabled, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@mediaType", item.MediaType);
        AddParameter(command, "@matchKind", item.MatchKind);
        AddParameter(command, "@matchValue", item.MatchValue);
        AddParameter(command, "@rootPath", item.RootPath);
        AddParameter(command, "@folderTemplate", item.FolderTemplate);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<PolicySetItem> CreatePolicySetAsync(
        CreatePolicySetRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new PolicySetItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New policy set",
            MediaType: NormalizeMediaType(request.MediaType),
            QualityProfileId: NormalizeName(request.QualityProfileId),
            QualityProfileName: null,
            DestinationRuleId: NormalizeName(request.DestinationRuleId),
            DestinationRuleName: null,
            CustomFormatIds: NormalizeCsv(request.CustomFormatIds),
            SearchIntervalOverrideHours: NormalizeNullablePositiveValue(request.SearchIntervalOverrideHours),
            RetryDelayOverrideHours: NormalizeNullablePositiveValue(request.RetryDelayOverrideHours),
            UpgradeUntilCutoff: request.UpgradeUntilCutoff,
            IsEnabled: request.IsEnabled,
            Notes: NormalizeName(request.Notes),
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO policy_sets (
                id, name, media_type, quality_profile_id, destination_rule_id, custom_format_ids,
                search_interval_override_hours, retry_delay_override_hours,
                upgrade_until_cutoff, is_enabled, notes, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @qualityProfileId, @destinationRuleId, @customFormatIds,
                @searchIntervalOverrideHours, @retryDelayOverrideHours,
                @upgradeUntilCutoff, @isEnabled, @notes, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@mediaType", item.MediaType);
        AddParameter(command, "@qualityProfileId", item.QualityProfileId);
        AddParameter(command, "@destinationRuleId", item.DestinationRuleId);
        AddParameter(command, "@customFormatIds", item.CustomFormatIds);
        AddParameter(command, "@searchIntervalOverrideHours", item.SearchIntervalOverrideHours);
        AddParameter(command, "@retryDelayOverrideHours", item.RetryDelayOverrideHours);
        AddParameter(command, "@upgradeUntilCutoff", item.UpgradeUntilCutoff ? 1 : 0);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@notes", item.Notes);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return (await GetPolicySetAsync(connection, item.Id, cancellationToken))!;
    }

    public async Task<LibraryViewItem> CreateLibraryViewAsync(
        string userId,
        CreateLibraryViewRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new LibraryViewItem(
            Id: Guid.CreateVersion7().ToString("N"),
            UserId: userId,
            Variant: NormalizeLibraryViewVariant(request.Variant),
            Name: NormalizeName(request.Name) ?? "New view",
            QuickFilter: NormalizeName(request.QuickFilter) ?? "all",
            SortField: NormalizeName(request.SortField) ?? "title",
            SortDirection: NormalizeSortDirection(request.SortDirection),
            ViewMode: NormalizeUiView(request.ViewMode),
            CardSize: NormalizeCardSize(request.CardSize),
            DisplayOptionsJson: NormalizeJson(request.DisplayOptionsJson, "{}"),
            RulesJson: NormalizeJson(request.RulesJson, "[]"),
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO library_views (
                id, user_id, variant, name, quick_filter, sort_field, sort_direction,
                view_mode, card_size, display_options_json, rules_json, created_utc, updated_utc
            )
            VALUES (
                @id, @userId, @variant, @name, @quickFilter, @sortField, @sortDirection,
                @viewMode, @cardSize, @displayOptionsJson, @rulesJson, @createdUtc, @updatedUtc
            );
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@userId", item.UserId);
        AddParameter(command, "@variant", item.Variant);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@quickFilter", item.QuickFilter);
        AddParameter(command, "@sortField", item.SortField);
        AddParameter(command, "@sortDirection", item.SortDirection);
        AddParameter(command, "@viewMode", item.ViewMode);
        AddParameter(command, "@cardSize", item.CardSize);
        AddParameter(command, "@displayOptionsJson", item.DisplayOptionsJson);
        AddParameter(command, "@rulesJson", item.RulesJson);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<QualityProfileItem?> UpdateQualityProfileAsync(
        string id,
        UpdateQualityProfileRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var current = await GetQualityProfileAsync(connection, id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE quality_profiles
            SET
                name = @name,
                cutoff_quality = @cutoffQuality,
                allowed_qualities = @allowedQualities,
                custom_format_ids = @customFormatIds,
                upgrade_until_cutoff = @upgradeUntilCutoff,
                upgrade_unknown_items = @upgradeUnknownItems,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? current.Name);
        AddParameter(command, "@cutoffQuality", NormalizeName(request.CutoffQuality) ?? current.CutoffQuality);
        AddParameter(
            command,
            "@allowedQualities",
            string.IsNullOrWhiteSpace(request.AllowedQualities)
                ? current.AllowedQualities
                : NormalizeCsv(request.AllowedQualities));
        AddParameter(
            command,
            "@customFormatIds",
            string.IsNullOrWhiteSpace(request.CustomFormatIds)
                ? string.Empty
                : NormalizeCsv(request.CustomFormatIds));
        AddParameter(command, "@upgradeUntilCutoff", request.UpgradeUntilCutoff ? 1 : 0);
        AddParameter(command, "@upgradeUnknownItems", request.UpgradeUnknownItems ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetQualityProfileAsync(connection, id, cancellationToken);
    }

    public async Task<TagItem?> UpdateTagAsync(
        string id,
        UpdateTagRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var current = await GetTagAsync(connection, id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE tags
            SET
                name = @name,
                color = @color,
                description = @description,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? current.Name);
        AddParameter(command, "@color", NormalizeTagColor(request.Color ?? current.Color));
        AddParameter(command, "@description", NormalizeName(request.Description) ?? string.Empty);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetTagAsync(connection, id, cancellationToken);
    }

    public async Task<IntakeSourceItem?> UpdateIntakeSourceAsync(
        string id,
        UpdateIntakeSourceRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var current = await GetIntakeSourceAsync(connection, id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE intake_sources
            SET
                name = @name,
                provider = @provider,
                feed_url = @feedUrl,
                media_type = @mediaType,
                library_id = @libraryId,
                quality_profile_id = @qualityProfileId,
                required_genres = @requiredGenres,
                minimum_rating = @minimumRating,
                minimum_year = @minimumYear,
                maximum_age_days = @maximumAgeDays,
                allowed_certifications = @allowedCertifications,
                audience = @audience,
                sync_interval_hours = @syncIntervalHours,
                search_on_add = @searchOnAdd,
                is_enabled = @isEnabled,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? current.Name);
        AddParameter(command, "@provider", NormalizeName(request.Provider) ?? current.Provider);
        AddParameter(command, "@feedUrl", NormalizeName(request.FeedUrl) ?? current.FeedUrl);
        AddParameter(command, "@mediaType", NormalizeMediaType(request.MediaType));
        AddParameter(command, "@libraryId", NormalizeName(request.LibraryId));
        AddParameter(command, "@qualityProfileId", NormalizeName(request.QualityProfileId));
        AddParameter(command, "@requiredGenres", NormalizeCsv(request.RequiredGenres));
        AddParameter(command, "@minimumRating", NormalizeNullableRating(request.MinimumRating));
        AddParameter(command, "@minimumYear", NormalizeNullableYear(request.MinimumYear));
        AddParameter(command, "@maximumAgeDays", NormalizeNullablePositiveValue(request.MaximumAgeDays));
        AddParameter(command, "@allowedCertifications", NormalizeCsv(request.AllowedCertifications));
        AddParameter(command, "@audience", NormalizeAudience(request.Audience));
        AddParameter(command, "@syncIntervalHours", NormalizeSyncIntervalHours(request.SyncIntervalHours));
        AddParameter(command, "@searchOnAdd", request.SearchOnAdd ? 1 : 0);
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetIntakeSourceAsync(connection, id, cancellationToken);
    }

    public async Task<IntakeSourceItem?> RecordIntakeSourceSyncResultAsync(
        string id,
        DateTimeOffset syncedUtc,
        string status,
        string? summary,
        CancellationToken cancellationToken)
    {
        var updatedUtc = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE intake_sources
            SET
                last_sync_utc = @lastSyncUtc,
                last_sync_status = @lastSyncStatus,
                last_sync_summary = @lastSyncSummary,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@lastSyncUtc", syncedUtc.ToString("O"));
        AddParameter(command, "@lastSyncStatus", NormalizeIntakeSyncStatus(status));
        AddParameter(command, "@lastSyncSummary", NormalizeName(summary));
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await GetIntakeSourceAsync(connection, id, cancellationToken);
    }

    public async Task<CustomFormatItem?> UpdateCustomFormatAsync(
        string id,
        UpdateCustomFormatRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var current = await GetCustomFormatAsync(connection, id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE custom_formats
            SET
                name = @name,
                media_type = @mediaType,
                score = @score,
                trash_id = @trashId,
                conditions = @conditions,
                upgrade_allowed = @upgradeAllowed,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? current.Name);
        AddParameter(command, "@mediaType", NormalizeMediaType(request.MediaType ?? current.MediaType));
        AddParameter(command, "@score", request.Score);
        AddParameter(command, "@trashId", NormalizeName(request.TrashId) ?? current.TrashId);
        AddParameter(command, "@conditions", NormalizeName(request.Conditions) ?? string.Empty);
        AddParameter(command, "@upgradeAllowed", request.UpgradeAllowed ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetCustomFormatAsync(connection, id, cancellationToken);
    }

    public async Task<DestinationRuleItem?> UpdateDestinationRuleAsync(
        string id,
        UpdateDestinationRuleRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var current = await GetDestinationRuleAsync(connection, id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE destination_rules
            SET
                name = @name,
                media_type = @mediaType,
                match_kind = @matchKind,
                match_value = @matchValue,
                root_path = @rootPath,
                folder_template = @folderTemplate,
                priority = @priority,
                is_enabled = @isEnabled,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? current.Name);
        AddParameter(command, "@mediaType", NormalizeMediaType(request.MediaType ?? current.MediaType));
        AddParameter(command, "@matchKind", NormalizeDestinationMatchKind(request.MatchKind ?? current.MatchKind));
        AddParameter(command, "@matchValue", NormalizeName(request.MatchValue) ?? string.Empty);
        AddParameter(command, "@rootPath", NormalizePath(request.RootPath) ?? current.RootPath);
        AddParameter(command, "@folderTemplate", NormalizeName(request.FolderTemplate));
        AddParameter(command, "@priority", NormalizePriorityValue(request.Priority));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetDestinationRuleAsync(connection, id, cancellationToken);
    }

    public async Task<PolicySetItem?> UpdatePolicySetAsync(
        string id,
        UpdatePolicySetRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var current = await GetPolicySetAsync(connection, id, cancellationToken);
        if (current is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE policy_sets
            SET
                name = @name,
                media_type = @mediaType,
                quality_profile_id = @qualityProfileId,
                destination_rule_id = @destinationRuleId,
                custom_format_ids = @customFormatIds,
                search_interval_override_hours = @searchIntervalOverrideHours,
                retry_delay_override_hours = @retryDelayOverrideHours,
                upgrade_until_cutoff = @upgradeUntilCutoff,
                is_enabled = @isEnabled,
                notes = @notes,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? current.Name);
        AddParameter(command, "@mediaType", NormalizeMediaType(request.MediaType ?? current.MediaType));
        AddParameter(command, "@qualityProfileId", NormalizeName(request.QualityProfileId));
        AddParameter(command, "@destinationRuleId", NormalizeName(request.DestinationRuleId));
        AddParameter(command, "@customFormatIds", NormalizeCsv(request.CustomFormatIds));
        AddParameter(command, "@searchIntervalOverrideHours", NormalizeNullablePositiveValue(request.SearchIntervalOverrideHours));
        AddParameter(command, "@retryDelayOverrideHours", NormalizeNullablePositiveValue(request.RetryDelayOverrideHours));
        AddParameter(command, "@upgradeUntilCutoff", request.UpgradeUntilCutoff ? 1 : 0);
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@notes", NormalizeName(request.Notes));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetPolicySetAsync(connection, id, cancellationToken);
    }

    public async Task<LibraryViewItem?> UpdateLibraryViewAsync(
        string userId,
        string id,
        UpdateLibraryViewRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE library_views
            SET name = @name,
                quick_filter = @quickFilter,
                sort_field = @sortField,
                sort_direction = @sortDirection,
                view_mode = @viewMode,
                card_size = @cardSize,
                display_options_json = @displayOptionsJson,
                rules_json = @rulesJson,
                updated_utc = @updatedUtc
            WHERE id = @id AND user_id = @userId;
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@userId", userId);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? "Updated view");
        AddParameter(command, "@quickFilter", NormalizeName(request.QuickFilter) ?? "all");
        AddParameter(command, "@sortField", NormalizeName(request.SortField) ?? "title");
        AddParameter(command, "@sortDirection", NormalizeSortDirection(request.SortDirection));
        AddParameter(command, "@viewMode", NormalizeUiView(request.ViewMode));
        AddParameter(command, "@cardSize", NormalizeCardSize(request.CardSize));
        AddParameter(command, "@displayOptionsJson", NormalizeJson(request.DisplayOptionsJson, "{}"));
        AddParameter(command, "@rulesJson", NormalizeJson(request.RulesJson, "[]"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updated <= 0)
        {
            return null;
        }

        return await GetLibraryViewAsync(connection, userId, id, cancellationToken);
    }

    public async Task<LibraryItem> CreateLibraryAsync(
        CreateLibraryRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var mediaType = NormalizeMediaType(request.MediaType);
        var item = new LibraryItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: NormalizeName(request.Name) ?? "New library",
            MediaType: mediaType,
            Purpose: NormalizeName(request.Purpose) ?? "General",
            RootPath: NormalizePath(request.RootPath) ?? string.Empty,
            DownloadsPath: NormalizePath(request.DownloadsPath),
            QualityProfileId: null,
            QualityProfileName: null,
            CutoffQuality: null,
            UpgradeUntilCutoff: true,
            UpgradeUnknownItems: false,
            ImportWorkflow: NormalizeImportWorkflow(request.ImportWorkflow),
            ProcessorName: NormalizeName(request.ProcessorName),
            ProcessorOutputPath: NormalizePath(request.ProcessorOutputPath),
            ProcessorTimeoutMinutes: NormalizePositiveValue(request.ProcessorTimeoutMinutes, 360),
            ProcessorFailureMode: NormalizeProcessorFailureMode(request.ProcessorFailureMode),
            AutoSearchEnabled: request.AutoSearchEnabled,
            MissingSearchEnabled: request.MissingSearchEnabled,
            UpgradeSearchEnabled: request.UpgradeSearchEnabled,
            SearchIntervalHours: NormalizePositiveValue(request.SearchIntervalHours, 6),
            RetryDelayHours: NormalizePositiveValue(request.RetryDelayHours, 24),
            MaxItemsPerRun: NormalizePositiveValue(request.MaxItemsPerRun, 25),
            SearchWindowStartHour: null,
            SearchWindowEndHour: null,
            AutomationStatus: "idle",
            SearchRequested: false,
            LastSearchedUtc: null,
            NextSearchUtc: null,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await EnsureSeedQualityProfilesAsync(connection, cancellationToken);
        var qualityProfileId = await ResolveQualityProfileIdAsync(
            connection,
            mediaType,
            NormalizeName(request.QualityProfileId),
            cancellationToken);
        var profile = qualityProfileId is null
            ? null
            : await GetQualityProfileAsync(connection, qualityProfileId, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO libraries (
                id, name, media_type, purpose, root_path, downloads_path, quality_profile_id,
                import_workflow, processor_name, processor_output_path, processor_timeout_minutes, processor_failure_mode,
                auto_search_enabled,
                missing_search_enabled, upgrade_search_enabled, search_interval_hours,
                retry_delay_hours, max_items_per_run,
                search_window_start_hour, search_window_end_hour,
                created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @purpose, @rootPath, @downloadsPath, @qualityProfileId,
                @importWorkflow, @processorName, @processorOutputPath, @processorTimeoutMinutes, @processorFailureMode,
                @autoSearchEnabled,
                @missingSearchEnabled, @upgradeSearchEnabled, @searchIntervalHours,
                @retryDelayHours, @maxItemsPerRun,
                @searchWindowStartHour, @searchWindowEndHour,
                @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@mediaType", item.MediaType);
        AddParameter(command, "@purpose", item.Purpose);
        AddParameter(command, "@rootPath", item.RootPath);
        AddParameter(command, "@downloadsPath", item.DownloadsPath);
        AddParameter(command, "@qualityProfileId", qualityProfileId);
        AddParameter(command, "@importWorkflow", item.ImportWorkflow);
        AddParameter(command, "@processorName", item.ProcessorName);
        AddParameter(command, "@processorOutputPath", item.ProcessorOutputPath);
        AddParameter(command, "@processorTimeoutMinutes", item.ProcessorTimeoutMinutes);
        AddParameter(command, "@processorFailureMode", item.ProcessorFailureMode);
        AddParameter(command, "@autoSearchEnabled", item.AutoSearchEnabled ? 1 : 0);
        AddParameter(command, "@missingSearchEnabled", item.MissingSearchEnabled ? 1 : 0);
        AddParameter(command, "@upgradeSearchEnabled", item.UpgradeSearchEnabled ? 1 : 0);
        AddParameter(command, "@searchIntervalHours", item.SearchIntervalHours);
        AddParameter(command, "@retryDelayHours", item.RetryDelayHours);
        AddParameter(command, "@maxItemsPerRun", item.MaxItemsPerRun);
        AddParameter(command, "@searchWindowStartHour", item.SearchWindowStartHour);
        AddParameter(command, "@searchWindowEndHour", item.SearchWindowEndHour);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item with
        {
            QualityProfileId = profile?.Id,
            QualityProfileName = profile?.Name,
            CutoffQuality = profile?.CutoffQuality,
            UpgradeUntilCutoff = profile?.UpgradeUntilCutoff ?? true,
            UpgradeUnknownItems = profile?.UpgradeUnknownItems ?? false
        };
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
                    search_window_start_hour = @searchWindowStartHour,
                    search_window_end_hour = @searchWindowEndHour,
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
            AddParameter(command, "@searchWindowStartHour", NormalizeSearchWindowHour(request.SearchWindowStartHour));
            AddParameter(command, "@searchWindowEndHour", NormalizeSearchWindowHour(request.SearchWindowEndHour));
            AddParameter(command, "@updatedUtc", now.ToString("O"));

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return null;
            }
        }

        return await GetLibraryAsync(connection, id, cancellationToken);
    }

    public async Task<LibraryItem?> UpdateLibraryQualityProfileAsync(
        string id,
        UpdateLibraryQualityProfileRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var library = await GetLibraryAsync(connection, id, cancellationToken);
        if (library is null)
        {
            return null;
        }

        await EnsureSeedQualityProfilesAsync(connection, cancellationToken);
        var qualityProfileId = await ResolveQualityProfileIdAsync(
            connection,
            library.MediaType,
            NormalizeName(request.QualityProfileId),
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE libraries
            SET
                quality_profile_id = @qualityProfileId,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@qualityProfileId", qualityProfileId);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetLibraryAsync(connection, id, cancellationToken);
    }

    public async Task<LibraryItem?> UpdateLibraryWorkflowAsync(
        string id,
        UpdateLibraryWorkflowRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var workflow = NormalizeImportWorkflow(request.ImportWorkflow);
        var processorOutputPath = NormalizePath(request.ProcessorOutputPath);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE libraries
            SET
                import_workflow = @importWorkflow,
                processor_name = @processorName,
                processor_output_path = @processorOutputPath,
                processor_timeout_minutes = @processorTimeoutMinutes,
                processor_failure_mode = @processorFailureMode,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@importWorkflow", workflow);
        AddParameter(command, "@processorName", NormalizeName(request.ProcessorName));
        AddParameter(command, "@processorOutputPath", processorOutputPath);
        AddParameter(command, "@processorTimeoutMinutes", NormalizePositiveValue(request.ProcessorTimeoutMinutes, 360));
        AddParameter(command, "@processorFailureMode", NormalizeProcessorFailureMode(request.ProcessorFailureMode));
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
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

    public async Task<LibraryRoutingSnapshot?> GetLibraryRoutingAsync(string libraryId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var library = await GetLibraryAsync(connection, libraryId, cancellationToken);
        if (library is null)
        {
            return null;
        }

        var sources = await ReadLibrarySourceLinksAsync(connection, libraryId, cancellationToken);
        var downloadClients = await ReadLibraryDownloadClientLinksAsync(connection, libraryId, cancellationToken);
        return new LibraryRoutingSnapshot(library.Id, library.Name, sources, downloadClients);
    }

    public async Task<LibraryRoutingSnapshot?> SaveLibraryRoutingAsync(
        string libraryId,
        UpdateLibraryRoutingRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var library = await GetLibraryAsync(connection, libraryId, cancellationToken);
        if (library is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var deleteSources = connection.CreateCommand())
        {
            deleteSources.Transaction = transaction;
            deleteSources.CommandText = "DELETE FROM library_source_links WHERE library_id = @libraryId;";
            AddParameter(deleteSources, "@libraryId", libraryId);
            await deleteSources.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var source in request.Sources ?? [])
        {
            using var insertSource = connection.CreateCommand();
            insertSource.Transaction = transaction;
            insertSource.CommandText =
                """
                INSERT INTO library_source_links (
                    id, library_id, indexer_id, priority, required_tags, excluded_tags, created_utc, updated_utc
                )
                VALUES (
                    @id, @libraryId, @indexerId, @priority, @requiredTags, @excludedTags, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(insertSource, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(insertSource, "@libraryId", libraryId);
            AddParameter(insertSource, "@indexerId", source.IndexerId);
            AddParameter(insertSource, "@priority", source.Priority is >= 1 ? source.Priority.Value : 100);
            AddParameter(insertSource, "@requiredTags", NormalizeCsv(source.RequiredTags));
            AddParameter(insertSource, "@excludedTags", NormalizeCsv(source.ExcludedTags));
            AddParameter(insertSource, "@createdUtc", now.ToString("O"));
            AddParameter(insertSource, "@updatedUtc", now.ToString("O"));
            await insertSource.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var deleteClients = connection.CreateCommand())
        {
            deleteClients.Transaction = transaction;
            deleteClients.CommandText = "DELETE FROM library_download_client_links WHERE library_id = @libraryId;";
            AddParameter(deleteClients, "@libraryId", libraryId);
            await deleteClients.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var client in request.DownloadClients ?? [])
        {
            using var insertClient = connection.CreateCommand();
            insertClient.Transaction = transaction;
            insertClient.CommandText =
                """
                INSERT INTO library_download_client_links (
                    id, library_id, download_client_id, priority, created_utc, updated_utc
                )
                VALUES (
                    @id, @libraryId, @downloadClientId, @priority, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(insertClient, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(insertClient, "@libraryId", libraryId);
            AddParameter(insertClient, "@downloadClientId", client.DownloadClientId);
            AddParameter(insertClient, "@priority", client.Priority is >= 1 ? client.Priority.Value : 100);
            AddParameter(insertClient, "@createdUtc", now.ToString("O"));
            AddParameter(insertClient, "@updatedUtc", now.ToString("O"));
            await insertClient.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var sources = await ReadLibrarySourceLinksAsync(connection, libraryId, cancellationToken);
        var downloadClients = await ReadLibraryDownloadClientLinksAsync(connection, libraryId, cancellationToken);
        return new LibraryRoutingSnapshot(library.Id, library.Name, sources, downloadClients);
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

    public async Task<bool> DeleteQualityProfileAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM quality_profiles WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteTagAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tags WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteIntakeSourceAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM intake_sources WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteCustomFormatAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM custom_formats WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteDestinationRuleAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM destination_rules WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeletePolicySetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM policy_sets WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteLibraryViewAsync(string userId, string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM library_views WHERE id = @id AND user_id = @userId;";
        AddParameter(command, "@id", id);
        AddParameter(command, "@userId", userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static PlatformSettingsSnapshot CreateSnapshot(
        IReadOnlyDictionary<string, string> settings,
        IReadOnlyDictionary<string, string> roots)
    {
        var brokerUrl = NormalizeMetadataBrokerUrl(GetValue(settings, "metadata.brokerUrl")) ?? string.Empty;

        return new PlatformSettingsSnapshot(
            AppInstanceName: GetValue(settings, "app.instanceName") ?? "Deluno",
            MovieRootPath: GetValue(roots, "movies"),
            SeriesRootPath: GetValue(roots, "series"),
            DownloadsPath: GetValue(roots, "downloads"),
            IncompleteDownloadsPath: GetValue(roots, "downloads.incomplete"),
            AutoStartJobs: string.Equals(GetValue(settings, "jobs.autoStart"), "true", StringComparison.OrdinalIgnoreCase),
            EnableNotifications: string.Equals(GetValue(settings, "notifications.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            RenameOnImport: !string.Equals(GetValue(settings, "media.renameOnImport"), "false", StringComparison.OrdinalIgnoreCase),
            UseHardlinks: !string.Equals(GetValue(settings, "media.useHardlinks"), "false", StringComparison.OrdinalIgnoreCase),
            CleanupEmptyFolders: string.Equals(GetValue(settings, "media.cleanupEmptyFolders"), "true", StringComparison.OrdinalIgnoreCase),
            RemoveCompletedDownloads: string.Equals(GetValue(settings, "media.removeCompletedDownloads"), "true", StringComparison.OrdinalIgnoreCase),
            UnmonitorWhenCutoffMet: string.Equals(GetValue(settings, "media.unmonitorWhenCutoffMet"), "true", StringComparison.OrdinalIgnoreCase),
            MovieFolderFormat: GetValue(settings, "media.movieFolderFormat") ?? "{Movie Title} ({Release Year})",
            SeriesFolderFormat: GetValue(settings, "media.seriesFolderFormat") ?? "{Series Title} ({Series Year})",
            EpisodeFileFormat: GetValue(settings, "media.episodeFileFormat") ?? "{Series Title} - S{season:00}E{episode:00} - {Episode Title}",
            HostBindAddress: GetValue(settings, "host.bindAddress") ?? "127.0.0.1",
            HostPort: int.TryParse(GetValue(settings, "host.port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostPort) ? hostPort : 5099,
            UrlBase: GetValue(settings, "host.urlBase") ?? string.Empty,
            RequireAuthentication: true,
            UiTheme: NormalizeUiTheme(GetValue(settings, "ui.theme")),
            UiDensity: NormalizeUiDensity(GetValue(settings, "ui.density")),
            DefaultMovieView: NormalizeUiView(GetValue(settings, "ui.defaultMovieView")),
            DefaultShowView: NormalizeUiView(GetValue(settings, "ui.defaultShowView")),
            MetadataNfoEnabled: string.Equals(GetValue(settings, "metadata.nfoEnabled"), "true", StringComparison.OrdinalIgnoreCase),
            MetadataArtworkEnabled: !string.Equals(GetValue(settings, "metadata.artworkEnabled"), "false", StringComparison.OrdinalIgnoreCase),
            MetadataCertificationCountry: NormalizeName(GetValue(settings, "metadata.certificationCountry")) ?? "US",
            MetadataLanguage: NormalizeName(GetValue(settings, "metadata.language")) ?? "en",
            MetadataProviderMode: NormalizeMetadataProviderMode(GetValue(settings, "metadata.providerMode")),
            MetadataBrokerUrl: brokerUrl,
            MetadataBrokerConfigured: !string.IsNullOrWhiteSpace(brokerUrl),
            MetadataTmdbApiKeyConfigured: !string.IsNullOrWhiteSpace(GetValue(settings, "metadata.tmdbApiKey")),
            MetadataOmdbApiKeyConfigured: !string.IsNullOrWhiteSpace(GetValue(settings, "metadata.omdbApiKey")),
            ReleaseNeverGrabPatterns: NormalizeNeverGrabPatterns(GetValue(settings, "search.neverGrabPatterns")),
            ImportRecoveryRetentionDays: int.TryParse(GetValue(settings, "media.importRecoveryRetentionDays"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retentionDays) && retentionDays > 0 ? retentionDays : 30,
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
        await EnsureSeedQualityProfilesAsync(connection, cancellationToken);
        await BackfillLibraryQualityProfilesAsync(connection, cancellationToken);

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
        var defaultMovieProfileId = await ResolveQualityProfileIdAsync(connection, "movies", null, cancellationToken);
        var defaultTvProfileId = await ResolveQualityProfileIdAsync(connection, "tv", null, cancellationToken);

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
                QualityProfileId: defaultMovieProfileId,
                QualityProfileName: "Movies / Standard",
                CutoffQuality: "WEB 1080p",
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                ImportWorkflow: "standard",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: 360,
                ProcessorFailureMode: "block",
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalHours: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25,
                SearchWindowStartHour: null,
                SearchWindowEndHour: null,
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
                QualityProfileId: defaultTvProfileId,
                QualityProfileName: "TV Shows / Standard",
                CutoffQuality: "WEB 1080p",
                UpgradeUntilCutoff: true,
                UpgradeUnknownItems: false,
                ImportWorkflow: "standard",
                ProcessorName: null,
                ProcessorOutputPath: null,
                ProcessorTimeoutMinutes: 360,
                ProcessorFailureMode: "block",
                AutoSearchEnabled: true,
                MissingSearchEnabled: true,
                UpgradeSearchEnabled: true,
                SearchIntervalHours: 6,
                RetryDelayHours: 24,
                MaxItemsPerRun: 25,
                SearchWindowStartHour: null,
                SearchWindowEndHour: null,
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
                    id, name, media_type, purpose, root_path, downloads_path, quality_profile_id,
                    import_workflow, processor_name, processor_output_path, processor_timeout_minutes, processor_failure_mode,
                    auto_search_enabled,
                    missing_search_enabled, upgrade_search_enabled, search_interval_hours,
                    retry_delay_hours, max_items_per_run,
                    search_window_start_hour, search_window_end_hour,
                    created_utc, updated_utc
                )
                VALUES (
                    @id, @name, @mediaType, @purpose, @rootPath, @downloadsPath, @qualityProfileId,
                    @importWorkflow, @processorName, @processorOutputPath, @processorTimeoutMinutes, @processorFailureMode,
                    @autoSearchEnabled,
                    @missingSearchEnabled, @upgradeSearchEnabled, @searchIntervalHours,
                    @retryDelayHours, @maxItemsPerRun,
                    @searchWindowStartHour, @searchWindowEndHour,
                    @createdUtc, @updatedUtc
                );
                """;

            AddParameter(command, "@id", item.Id);
            AddParameter(command, "@name", item.Name);
            AddParameter(command, "@mediaType", item.MediaType);
            AddParameter(command, "@purpose", item.Purpose);
            AddParameter(command, "@rootPath", item.RootPath);
            AddParameter(command, "@downloadsPath", item.DownloadsPath);
            AddParameter(command, "@qualityProfileId", item.QualityProfileId);
            AddParameter(command, "@importWorkflow", item.ImportWorkflow);
            AddParameter(command, "@processorName", item.ProcessorName);
            AddParameter(command, "@processorOutputPath", item.ProcessorOutputPath);
            AddParameter(command, "@processorTimeoutMinutes", item.ProcessorTimeoutMinutes);
            AddParameter(command, "@processorFailureMode", item.ProcessorFailureMode);
            AddParameter(command, "@autoSearchEnabled", item.AutoSearchEnabled ? 1 : 0);
            AddParameter(command, "@missingSearchEnabled", item.MissingSearchEnabled ? 1 : 0);
            AddParameter(command, "@upgradeSearchEnabled", item.UpgradeSearchEnabled ? 1 : 0);
            AddParameter(command, "@searchIntervalHours", item.SearchIntervalHours);
            AddParameter(command, "@retryDelayHours", item.RetryDelayHours);
            AddParameter(command, "@maxItemsPerRun", item.MaxItemsPerRun);
            AddParameter(command, "@searchWindowStartHour", item.SearchWindowStartHour);
            AddParameter(command, "@searchWindowEndHour", item.SearchWindowEndHour);
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
                l.id, l.name, l.media_type, l.purpose, l.root_path, l.downloads_path,
                l.quality_profile_id, q.name, q.cutoff_quality, q.upgrade_until_cutoff, q.upgrade_unknown_items,
                l.import_workflow, l.processor_name, l.processor_output_path, l.processor_timeout_minutes, l.processor_failure_mode,
                l.auto_search_enabled, l.missing_search_enabled, l.upgrade_search_enabled, l.search_interval_hours,
                l.retry_delay_hours, l.max_items_per_run, l.search_window_start_hour, l.search_window_end_hour,
                l.created_utc, l.updated_utc
            FROM libraries l
            LEFT JOIN quality_profiles q ON q.id = l.quality_profile_id
            WHERE l.id = @id
            LIMIT 1;
            """;

        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadLibrary(reader);
    }

    private static async Task EnsureSeedQualityProfilesAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM quality_profiles;";
        var scalar = await countCommand.ExecuteScalarAsync(cancellationToken);
        var count = Convert.ToInt32(scalar ?? 0, CultureInfo.InvariantCulture);
        if (count > 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var seeds = new[]
        {
            new QualityProfileItem(Guid.CreateVersion7().ToString("N"), "Movies / Standard", "movies", "WEB 1080p", "WEB 1080p, Bluray 1080p, Remux 1080p", string.Empty, true, false, false, null, null, false, now, now),
            new QualityProfileItem(Guid.CreateVersion7().ToString("N"), "Movies / Premium 4K", "movies", "Remux 2160p", "WEB 2160p, Bluray 2160p, Remux 2160p", string.Empty, true, true, false, null, null, false, now, now),
            new QualityProfileItem(Guid.CreateVersion7().ToString("N"), "TV Shows / Standard", "tv", "WEB 1080p", "WEB 720p, WEB 1080p, HDTV 1080p", string.Empty, true, false, false, null, null, false, now, now),
            new QualityProfileItem(Guid.CreateVersion7().ToString("N"), "TV Shows / Premium 4K", "tv", "WEB 2160p", "WEB 1080p, WEB 2160p, Bluray 2160p", string.Empty, true, true, false, null, null, false, now, now)
        };

        foreach (var item in seeds)
        {
            var sortOrder = Array.IndexOf(seeds, item) + 1;
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO quality_profiles (
                    id, name, media_type, sort_order, cutoff_quality, allowed_qualities, custom_format_ids,
                    upgrade_until_cutoff, upgrade_unknown_items, allow_lower_quality_replacements, created_utc, updated_utc
                )
                VALUES (
                    @id, @name, @mediaType, @sortOrder, @cutoffQuality, @allowedQualities, @customFormatIds,
                    @upgradeUntilCutoff, @upgradeUnknownItems, @allowLowerQualityReplacements, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(command, "@id", item.Id);
            AddParameter(command, "@name", item.Name);
            AddParameter(command, "@mediaType", item.MediaType);
            AddParameter(command, "@sortOrder", sortOrder);
            AddParameter(command, "@cutoffQuality", item.CutoffQuality);
            AddParameter(command, "@allowedQualities", item.AllowedQualities);
            AddParameter(command, "@customFormatIds", item.CustomFormatIds);
            AddParameter(command, "@upgradeUntilCutoff", item.UpgradeUntilCutoff ? 1 : 0);
            AddParameter(command, "@upgradeUnknownItems", item.UpgradeUnknownItems ? 1 : 0);
            AddParameter(command, "@allowLowerQualityReplacements", item.AllowLowerQualityReplacements ? 1 : 0);
            AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
            AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task BackfillLibraryQualityProfilesAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var pendingLibraries = new List<(string LibraryId, string MediaType)>();
        var assignments = new List<(string LibraryId, string ProfileId)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, media_type
                FROM libraries
                WHERE quality_profile_id IS NULL;
                """;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                pendingLibraries.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var library in pendingLibraries)
        {
            var profileId = await ResolveQualityProfileIdAsync(
                connection,
                library.MediaType,
                null,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(profileId))
            {
                assignments.Add((library.LibraryId, profileId));
            }
        }

        foreach (var assignment in assignments)
        {
            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE libraries
                SET quality_profile_id = @qualityProfileId
                WHERE id = @id;
                """;
            AddParameter(update, "@id", assignment.LibraryId);
            AddParameter(update, "@qualityProfileId", assignment.ProfileId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> GetNextQualityProfileSortOrderAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sort_order), 0) + 1 FROM quality_profiles;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar ?? 1, CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HasUsersAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM users;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar ?? 0, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> RequiresBootstrapAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        return !await HasUsersAsync(connection, cancellationToken);
    }

    private static async Task<string?> ResolveQualityProfileIdAsync(
        System.Data.Common.DbConnection connection,
        string mediaType,
        string? requestedProfileId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedProfileId))
        {
            using var requested = connection.CreateCommand();
            requested.CommandText =
                """
                SELECT id
                FROM quality_profiles
                WHERE id = @id AND media_type = @mediaType
                LIMIT 1;
                """;
            AddParameter(requested, "@id", requestedProfileId);
            AddParameter(requested, "@mediaType", mediaType);
            var existing = await requested.ExecuteScalarAsync(cancellationToken) as string;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        using var fallback = connection.CreateCommand();
        fallback.CommandText =
            """
            SELECT id
            FROM quality_profiles
            WHERE media_type = @mediaType
            ORDER BY
                CASE
                    WHEN lower(name) LIKE '%standard%' THEN 0
                    ELSE 1
                END,
                name ASC
            LIMIT 1;
            """;
        AddParameter(fallback, "@mediaType", mediaType);
        return await fallback.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<QualityProfileItem?> GetQualityProfileAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, cutoff_quality, allowed_qualities, custom_format_ids,
                upgrade_until_cutoff, upgrade_unknown_items, allow_lower_quality_replacements,
                preset_id, preset_version, created_utc, updated_utc
            FROM quality_profiles
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadQualityProfile(reader) : null;
    }

    private static async Task<TagItem?> GetTagAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, color, description, created_utc, updated_utc
            FROM tags
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTag(reader) : null;
    }

    private static async Task<IntakeSourceItem?> GetIntakeSourceAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id, s.name, s.provider, s.feed_url, s.media_type,
                s.library_id, l.name, s.quality_profile_id, q.name,
                s.required_genres, s.minimum_rating, s.minimum_year, s.maximum_age_days,
                s.allowed_certifications, s.audience, s.sync_interval_hours,
                s.last_sync_utc, s.last_sync_status, s.last_sync_summary,
                s.search_on_add, s.is_enabled, s.created_utc, s.updated_utc
            FROM intake_sources s
            LEFT JOIN libraries l ON l.id = s.library_id
            LEFT JOIN quality_profiles q ON q.id = s.quality_profile_id
            WHERE s.id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadIntakeSource(reader) : null;
    }

    private static async Task<CustomFormatItem?> GetCustomFormatAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, score, trash_id, conditions, upgrade_allowed, created_utc, updated_utc
            FROM custom_formats
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCustomFormat(reader) : null;
    }

    private static async Task<DestinationRuleItem?> GetDestinationRuleAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, match_kind, match_value, root_path, folder_template,
                priority, is_enabled, created_utc, updated_utc
            FROM destination_rules
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDestinationRule(reader) : null;
    }

    private static async Task<PolicySetItem?> GetPolicySetAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                p.id, p.name, p.media_type,
                p.quality_profile_id, q.name,
                p.destination_rule_id, d.name,
                p.custom_format_ids, p.search_interval_override_hours, p.retry_delay_override_hours,
                p.upgrade_until_cutoff, p.is_enabled, p.notes, p.created_utc, p.updated_utc
            FROM policy_sets p
            LEFT JOIN quality_profiles q ON q.id = p.quality_profile_id
            LEFT JOIN destination_rules d ON d.id = p.destination_rule_id
            WHERE p.id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPolicySet(reader) : null;
    }

    private static async Task<UserItem?> GetUserAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, username, display_name, avatar_initials, security_stamp, created_utc
            FROM users
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    private static async Task<LibraryViewItem?> GetLibraryViewAsync(
        System.Data.Common.DbConnection connection,
        string userId,
        string id,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, user_id, variant, name, quick_filter, sort_field, sort_direction,
                view_mode, card_size, display_options_json, rules_json, created_utc, updated_utc
            FROM library_views
            WHERE user_id = @userId AND id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@userId", userId);
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLibraryView(reader) : null;
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

    private static string NormalizeDestinationMatchKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "tag" => "tag",
            "language" => "language",
            "quality" => "quality",
            "anime" => "anime",
            "certification" => "certification",
            "library" => "library",
            _ => "genre"
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

    private static string DefaultCutoffForMediaType(string? mediaType)
        => NormalizeMediaType(mediaType) == "tv" ? "WEB 1080p" : "WEB 1080p";

    private static string DefaultAllowedQualities(string? mediaType)
        => NormalizeMediaType(mediaType) == "tv"
            ? "WEB 720p, WEB 1080p, HDTV 1080p"
            : "WEB 1080p, Bluray 1080p, Remux 1080p";

    private static string NormalizeCsv(string? value)
    {
        return string.Join(
            ", ",
            (value ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static double? NormalizeNullableRating(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0, 10);
    }

    private static int? NormalizeNullableYear(int? value)
        => value is >= 1888 and <= 2100 ? value : null;

    private static int NormalizeSyncIntervalHours(int? value)
        => NormalizeSyncIntervalHours(value ?? 24);

    private static int NormalizeSyncIntervalHours(int value)
        => Math.Clamp(value <= 0 ? 24 : value, 1, 168);

    private static string NormalizeAudience(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "kids" => "kids",
            "adult" => "adult",
            _ => "any"
        };
    }

    private static string NormalizeIntakeSyncStatus(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "success" => "success",
            "partial" => "partial",
            "error" => "error",
            _ => "never"
        };
    }

    private static string NormalizeNeverGrabPatterns(string? value)
    {
        var defaultPatterns = new[] { "cam", "camrip", "telesync", "telecine", "workprint", "screener", "sample", "trailer", "extras" };
        var raw = string.IsNullOrWhiteSpace(value)
            ? defaultPatterns
            : value.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return string.Join(
            "\n",
            raw
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeUiTheme(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "system"
        };
    }

    private static string NormalizeUiDensity(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "compact" => "compact",
            "spacious" => "spacious",
            "expanded" => "expanded",
            _ => "comfortable"
        };
    }

    private static string NormalizeUiView(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "list" => "list",
            _ => "grid"
        };
    }

    private static string NormalizeImportWorkflow(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "refine-before-import" or "refine" or "processor" or "processing" => "refine-before-import",
            _ => "standard"
        };
    }

    private static string NormalizeProcessorFailureMode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "import-original" or "fallback-original" or "fallback" => "import-original",
            "manual-review" or "review" => "manual-review",
            _ => "block"
        };
    }

    private static string NormalizeMetadataProviderMode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "broker" or "cloud" or "managed" => "broker",
            "hybrid" or "broker-first" or "brokerfirst" => "hybrid",
            "direct" or "direct-only" or "directonly" or "self-hosted" => "direct",
            _ => "direct"
        };
    }

    private static string? NormalizeMetadataBrokerUrl(string? value)
    {
        var normalized = NormalizeName(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.TrimEnd('/');
    }

    private static string NormalizeCardSize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "sm" => "sm",
            "lg" => "lg",
            _ => "md"
        };
    }

    private static string NormalizeLibraryViewVariant(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "shows" => "shows",
            _ => "movies"
        };
    }

    private static string NormalizeSortDirection(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized == "desc" ? "desc" : "asc";
    }

    private static string NormalizeJson(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(value);
            return value;
        }
        catch
        {
            return fallback;
        }
    }

    private static int? NormalizeSearchWindowHour(int? value)
        => value is null ? null : Math.Clamp(value.Value, 0, 23);

    private static string NormalizeTagColor(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "emerald" => "emerald",
            "teal" => "teal",
            "blue" => "blue",
            "violet" => "violet",
            "amber" => "amber",
            "rose" => "rose",
            _ => "slate"
        };
    }

    private static string BuildAvatarInitials(string value)
    {
        var parts = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(part => char.ToUpperInvariant(part[0]))
            .ToArray();
        return parts.Length == 0 ? "OP" : new string(parts);
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iterations = 100_000;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string GenerateApiKey()
        => $"deluno_{Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}";

    private static string CreateSecurityStamp()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string BuildApiKeyPrefix(string apiKey)
    {
        var value = apiKey.Trim();
        return value.Length <= 18 ? value : $"{value[..14]}...";
    }

    private static string HashApiKey(string apiKey)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.Trim())));

    private static string NormalizeApiScopes(string? value)
    {
        var normalized = NormalizeCsv(value);
        return string.IsNullOrWhiteSpace(normalized) ? "all" : normalized;
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static int NormalizePositiveValue(int? value, int fallback)
    {
        return value is > 0 ? value.Value : fallback;
    }

    private static int NormalizePriorityValue(int value)
    {
        return value <= 0 ? 100 : value;
    }

    private static int? NormalizeNullablePositiveValue(int? value)
    {
        return value is > 0 ? value.Value : null;
    }

    private static async Task<IReadOnlyList<LibrarySourceLinkItem>> ReadLibrarySourceLinksAsync(
        System.Data.Common.DbConnection connection,
        string libraryId,
        CancellationToken cancellationToken)
    {
        var items = new List<LibrarySourceLinkItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                l.id, l.library_id, l.indexer_id, i.name, l.priority, l.required_tags, l.excluded_tags, l.created_utc, l.updated_utc
            FROM library_source_links l
            INNER JOIN indexer_sources i ON i.id = l.indexer_id
            WHERE l.library_id = @libraryId
            ORDER BY l.priority ASC, i.name ASC;
            """;

        AddParameter(command, "@libraryId", libraryId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LibrarySourceLinkItem(
                Id: reader.GetString(0),
                LibraryId: reader.GetString(1),
                IndexerId: reader.GetString(2),
                IndexerName: reader.GetString(3),
                Priority: reader.GetInt32(4),
                RequiredTags: reader.GetString(5),
                ExcludedTags: reader.GetString(6),
                CreatedUtc: ParseTimestamp(reader.GetString(7)),
                UpdatedUtc: ParseTimestamp(reader.GetString(8))));
        }

        return items;
    }

    private static LibraryItem ReadLibrary(System.Data.Common.DbDataReader reader)
    {
        return new LibraryItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            MediaType: reader.GetString(2),
            Purpose: reader.GetString(3),
            RootPath: reader.GetString(4),
            DownloadsPath: reader.IsDBNull(5) ? null : reader.GetString(5),
            QualityProfileId: reader.IsDBNull(6) ? null : reader.GetString(6),
            QualityProfileName: reader.IsDBNull(7) ? null : reader.GetString(7),
            CutoffQuality: reader.IsDBNull(8) ? null : reader.GetString(8),
            UpgradeUntilCutoff: reader.IsDBNull(9) || reader.GetInt64(9) == 1,
            UpgradeUnknownItems: !reader.IsDBNull(10) && reader.GetInt64(10) == 1,
            ImportWorkflow: reader.IsDBNull(11) ? "standard" : NormalizeImportWorkflow(reader.GetString(11)),
            ProcessorName: reader.IsDBNull(12) ? null : reader.GetString(12),
            ProcessorOutputPath: reader.IsDBNull(13) ? null : reader.GetString(13),
            ProcessorTimeoutMinutes: reader.IsDBNull(14) ? 360 : reader.GetInt32(14),
            ProcessorFailureMode: reader.IsDBNull(15) ? "block" : NormalizeProcessorFailureMode(reader.GetString(15)),
            AutoSearchEnabled: reader.GetInt64(16) == 1,
            MissingSearchEnabled: reader.GetInt64(17) == 1,
            UpgradeSearchEnabled: reader.GetInt64(18) == 1,
            SearchIntervalHours: reader.GetInt32(19),
            RetryDelayHours: reader.GetInt32(20),
            MaxItemsPerRun: reader.GetInt32(21),
            SearchWindowStartHour: reader.IsDBNull(22) ? null : reader.GetInt32(22),
            SearchWindowEndHour: reader.IsDBNull(23) ? null : reader.GetInt32(23),
            AutomationStatus: "idle",
            SearchRequested: false,
            LastSearchedUtc: null,
            NextSearchUtc: null,
            CreatedUtc: ParseTimestamp(reader.GetString(24)),
            UpdatedUtc: ParseTimestamp(reader.GetString(25)));
    }

    private static QualityProfileItem ReadQualityProfile(System.Data.Common.DbDataReader reader)
    {
        var presetId = reader.IsDBNull(9) ? null : reader.GetString(9);
        var presetVersion = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10);

        var presetDrifted = false;
        if (presetId is not null && presetVersion.HasValue)
        {
            var currentPreset = Presets.QualityProfilePresetCatalog.FindById(presetId);
            presetDrifted = currentPreset is null || currentPreset.Version != presetVersion.Value;
        }

        return new QualityProfileItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            MediaType: reader.GetString(2),
            CutoffQuality: reader.GetString(3),
            AllowedQualities: reader.GetString(4),
            CustomFormatIds: reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            UpgradeUntilCutoff: reader.GetInt64(6) == 1,
            UpgradeUnknownItems: reader.GetInt64(7) == 1,
            AllowLowerQualityReplacements: reader.GetInt64(8) == 1,
            PresetId: presetId,
            PresetVersion: presetVersion,
            PresetDrifted: presetDrifted,
            CreatedUtc: ParseTimestamp(reader.GetString(11)),
            UpdatedUtc: ParseTimestamp(reader.GetString(12)));
    }

    private static TagItem ReadTag(System.Data.Common.DbDataReader reader)
    {
        return new TagItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Color: reader.GetString(2),
            Description: reader.GetString(3),
            CreatedUtc: ParseTimestamp(reader.GetString(4)),
            UpdatedUtc: ParseTimestamp(reader.GetString(5)));
    }

    private static IntakeSourceItem ReadIntakeSource(System.Data.Common.DbDataReader reader)
    {
        return new IntakeSourceItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Provider: reader.GetString(2),
            FeedUrl: reader.GetString(3),
            MediaType: reader.GetString(4),
            LibraryId: reader.IsDBNull(5) ? null : reader.GetString(5),
            LibraryName: reader.IsDBNull(6) ? null : reader.GetString(6),
            QualityProfileId: reader.IsDBNull(7) ? null : reader.GetString(7),
            QualityProfileName: reader.IsDBNull(8) ? null : reader.GetString(8),
            RequiredGenres: reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            MinimumRating: reader.IsDBNull(10) ? null : reader.GetDouble(10),
            MinimumYear: reader.IsDBNull(11) ? null : reader.GetInt32(11),
            MaximumAgeDays: reader.IsDBNull(12) ? null : reader.GetInt32(12),
            AllowedCertifications: reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            Audience: reader.IsDBNull(14) ? "any" : NormalizeAudience(reader.GetString(14)),
            SyncIntervalHours: reader.IsDBNull(15) ? 24 : NormalizeSyncIntervalHours(reader.GetInt32(15)),
            LastSyncUtc: reader.IsDBNull(16) ? null : ParseTimestamp(reader.GetString(16)),
            LastSyncStatus: reader.IsDBNull(17) ? "never" : NormalizeIntakeSyncStatus(reader.GetString(17)),
            LastSyncSummary: reader.IsDBNull(18) ? null : reader.GetString(18),
            SearchOnAdd: reader.GetInt64(19) == 1,
            IsEnabled: reader.GetInt64(20) == 1,
            CreatedUtc: ParseTimestamp(reader.GetString(21)),
            UpdatedUtc: ParseTimestamp(reader.GetString(22)));
    }

    private static CustomFormatItem ReadCustomFormat(System.Data.Common.DbDataReader reader)
    {
        return new CustomFormatItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            MediaType: reader.GetString(2),
            Score: reader.GetInt32(3),
            TrashId: reader.IsDBNull(4) ? null : reader.GetString(4),
            Conditions: reader.GetString(5),
            UpgradeAllowed: reader.GetInt64(6) == 1,
            CreatedUtc: ParseTimestamp(reader.GetString(7)),
            UpdatedUtc: ParseTimestamp(reader.GetString(8)));
    }

    private static DestinationRuleItem ReadDestinationRule(System.Data.Common.DbDataReader reader)
    {
        return new DestinationRuleItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            MediaType: reader.GetString(2),
            MatchKind: reader.GetString(3),
            MatchValue: reader.GetString(4),
            RootPath: reader.GetString(5),
            FolderTemplate: reader.IsDBNull(6) ? null : reader.GetString(6),
            Priority: reader.GetInt32(7),
            IsEnabled: reader.GetInt64(8) == 1,
            CreatedUtc: ParseTimestamp(reader.GetString(9)),
            UpdatedUtc: ParseTimestamp(reader.GetString(10)));
    }

    private static PolicySetItem ReadPolicySet(System.Data.Common.DbDataReader reader)
    {
        return new PolicySetItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            MediaType: reader.GetString(2),
            QualityProfileId: reader.IsDBNull(3) ? null : reader.GetString(3),
            QualityProfileName: reader.IsDBNull(4) ? null : reader.GetString(4),
            DestinationRuleId: reader.IsDBNull(5) ? null : reader.GetString(5),
            DestinationRuleName: reader.IsDBNull(6) ? null : reader.GetString(6),
            CustomFormatIds: reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            SearchIntervalOverrideHours: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            RetryDelayOverrideHours: reader.IsDBNull(9) ? null : reader.GetInt32(9),
            UpgradeUntilCutoff: reader.GetInt64(10) == 1,
            IsEnabled: reader.GetInt64(11) == 1,
            Notes: reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedUtc: ParseTimestamp(reader.GetString(13)),
            UpdatedUtc: ParseTimestamp(reader.GetString(14)));
    }

    private static LibraryViewItem ReadLibraryView(System.Data.Common.DbDataReader reader)
    {
        return new LibraryViewItem(
            Id: reader.GetString(0),
            UserId: reader.GetString(1),
            Variant: reader.GetString(2),
            Name: reader.GetString(3),
            QuickFilter: reader.GetString(4),
            SortField: reader.GetString(5),
            SortDirection: reader.GetString(6),
            ViewMode: reader.GetString(7),
            CardSize: reader.GetString(8),
            DisplayOptionsJson: reader.GetString(9),
            RulesJson: reader.GetString(10),
            CreatedUtc: ParseTimestamp(reader.GetString(11)),
            UpdatedUtc: ParseTimestamp(reader.GetString(12)));
    }

    private static UserItem ReadUser(System.Data.Common.DbDataReader reader)
    {
        return new UserItem(
            Id: reader.GetString(0),
            Username: reader.GetString(1),
            DisplayName: reader.GetString(2),
            AvatarInitials: reader.GetString(3),
            SecurityStamp: ReadSecurityStamp(reader, 4),
            CreatedUtc: ParseTimestamp(reader.GetString(5)));
    }

    private static string ReadSecurityStamp(System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal))
            ? CreateSecurityStamp()
            : reader.GetString(ordinal);

    private static ApiKeyItem ReadApiKey(System.Data.Common.DbDataReader reader)
    {
        return new ApiKeyItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Prefix: reader.GetString(2),
            Scopes: reader.GetString(3),
            LastUsedUtc: reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
            CreatedUtc: ParseTimestamp(reader.GetString(5)),
            UpdatedUtc: ParseTimestamp(reader.GetString(6)));
    }

    private static async Task<IReadOnlyList<LibraryDownloadClientLinkItem>> ReadLibraryDownloadClientLinksAsync(
        System.Data.Common.DbConnection connection,
        string libraryId,
        CancellationToken cancellationToken)
    {
        var items = new List<LibraryDownloadClientLinkItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                l.id, l.library_id, l.download_client_id, d.name, l.priority, l.created_utc, l.updated_utc
            FROM library_download_client_links l
            INNER JOIN download_clients d ON d.id = l.download_client_id
            WHERE l.library_id = @libraryId
            ORDER BY l.priority ASC, d.name ASC;
            """;

        AddParameter(command, "@libraryId", libraryId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LibraryDownloadClientLinkItem(
                Id: reader.GetString(0),
                LibraryId: reader.GetString(1),
                DownloadClientId: reader.GetString(2),
                DownloadClientName: reader.GetString(3),
                Priority: reader.GetInt32(4),
                CreatedUtc: ParseTimestamp(reader.GetString(5)),
                UpdatedUtc: ParseTimestamp(reader.GetString(6))));
        }

        return items;
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

