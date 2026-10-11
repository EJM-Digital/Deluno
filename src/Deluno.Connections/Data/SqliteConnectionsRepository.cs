using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
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
    ISecretProtector secretProtector,
    ITrackerDefinitionNames? trackerDefinitionNames = null)
    : IConnectionsRepository
{
    // A name the person gave (or Prowlarr or Jackett gave the tracker) comes first, then the built-in tracker's own
    // name, and only an indexer with neither is named after its address's host (#1076, #1248).
    private string IndexerBaseName(string? definitionId, string? baseUrl, string? givenName = null)
        => (string.IsNullOrWhiteSpace(givenName) ? null : givenName.Trim())
           ?? (string.IsNullOrWhiteSpace(definitionId) ? null : trackerDefinitionNames?.DisplayName(definitionId))
           ?? ConnectionNames.SiteName(baseUrl);

    /// <summary>The name Prowlarr or Jackett gave this indexer, when it was given for the address the indexer has now.</summary>
    private static async Task<string?> DiscoveredNameAsync(System.Data.Common.DbConnection connection, string id, string? baseUrl, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT discovered_name FROM indexer_sources WHERE id = @id AND discovered_for_url = @url COLLATE NOCASE;";
        AddParameter(command, "@id", id);
        AddParameter(command, "@url", baseUrl ?? string.Empty);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    /// <summary>Whether the indexer's name is one it was given (or its aggregator gave it), not one worked out from its address.</summary>
    private static async Task<bool> IsGivenNameAsync(System.Data.Common.DbConnection connection, string id, string name, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM indexer_sources WHERE id = @id AND discovered_name = @name COLLATE NOCASE;";
        AddParameter(command, "@id", id);
        AddParameter(command, "@name", name);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task StoreTypedIndexerNameAsync(System.Data.Common.DbConnection connection, string id, string name, string? baseUrl, CancellationToken cancellationToken)
    {
        using var store = connection.CreateCommand();
        store.CommandText = "UPDATE indexer_sources SET discovered_name = @name, discovered_for_url = @url WHERE id = @id;";
        AddParameter(store, "@id", id);
        AddParameter(store, "@name", name);
        AddParameter(store, "@url", baseUrl ?? string.Empty);
        await store.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IndexerItem?> SetDiscoveredIndexerNameAsync(string id, string discoveredName, CancellationToken cancellationToken)
    {
        var existing = (await ListIndexersAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null || string.IsNullOrWhiteSpace(discoveredName))
        {
            return existing;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken);
        var name = ConnectionNames.Unique(
            IndexerBaseName(existing.DefinitionId, existing.BaseUrl, discoveredName.Trim()),
            ConnectionNames.PortOf(existing.BaseUrl),
            id,
            await ConnectionNameRows.OtherNamesAsync(connection, "indexer_sources", id, cancellationToken));
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE indexer_sources SET name = @name, discovered_name = @discovered, discovered_for_url = @url WHERE id = @id;";
        AddParameter(update, "@name", name);
        AddParameter(update, "@discovered", discoveredName.Trim());
        AddParameter(update, "@url", existing.BaseUrl ?? string.Empty);
        AddParameter(update, "@id", id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        return existing with { Name = name };
    }

    private static string DownloadClientBaseName(string? protocol, string? host, string? endpointUrl)
        => ConnectionNames.AppOnHost(ConnectionNames.DownloadClientApp(protocol), string.IsNullOrWhiteSpace(host) ? endpointUrl : host);

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

    public async Task<int> ApplyAutomaticIndexerNamesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var rows = new List<(string Id, string Name, string? BaseUrl, string? DefinitionId, string? Discovered)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT id, name, base_url, definition_id, CASE WHEN discovered_for_url = base_url COLLATE NOCASE THEN discovered_name END FROM indexer_sources ORDER BY created_utc, id;";
            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        // In the order they were added, the way saving each one would name it: the first to hold a
        // name keeps it, a later one takes its port and then a short suffix.
        var taken = new List<string>();
        var renamed = 0;
        foreach (var (id, name, baseUrl, definitionId, discovered) in rows)
        {
            var wanted = ConnectionNames.Unique(IndexerBaseName(definitionId, baseUrl, discovered), ConnectionNames.PortOf(baseUrl), id, taken);
            taken.Add(wanted);
            if (string.Equals(wanted, name, StringComparison.Ordinal))
            {
                continue;
            }

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE indexer_sources SET name = @name WHERE id = @id;";
            AddParameter(update, "@name", wanted);
            AddParameter(update, "@id", id);
            renamed += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return renamed;
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
                consecutive_failures, rate_limited_until_utc, disabled_reason, request_interval_seconds,
                created_utc, updated_utc,
                sharing_mode, sharing_for_hours, sharing_until_ratio, sharing_stuck_action, sharing_stuck_after_days,
                minimum_age_minutes, retention_days, maximum_size_mb, prefer_indexer_flags, availability_delay_days,
                rss_enabled, automatic_search_enabled, interactive_search_enabled,
                last_health_failure_json, definition_id, definition_settings_json, definition_secrets,
                api_path, additional_parameters, anime_categories, minimum_seeders,
                required_words, ignored_words, multi_languages,
                vip_expires_utc, vip_warning_acknowledged_expiry_utc,
                query_limit, grab_limit, limits_unit, prefer_magnet_url, season_pack_search_mode,
                season_pack_seed_for_hours, proxy_id
            FROM indexer_sources
            ORDER BY priority ASC, name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var apiKey = reader.IsDBNull(5) ? SecretReadResult.None : secretProtector.TryUnprotect("indexer:api-key", reader.GetString(5));
            var trackerSecrets = reader.IsDBNull(38) ? SecretReadResult.None : secretProtector.TryUnprotect("indexer:tracker-secrets", reader.GetString(38));
            var indexerIsEnabled = reader.GetInt64(10) == 1;
            var indexerProblem = apiKey.Unreadable
                ? SecretUnreadable.Message("API key")
                : trackerSecrets.Unreadable ? SecretUnreadable.Message("login details") : null;
            items.Add(new IndexerItem(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                Protocol: reader.GetString(2),
                Privacy: reader.GetString(3),
                BaseUrl: reader.GetString(4),
                ApiKey: apiKey.Value,
                Priority: reader.GetInt32(6),
                Categories: reader.GetString(7),
                Tags: reader.GetString(8),
                MediaScope: reader.IsDBNull(9) ? "both" : reader.GetString(9),
                IsEnabled: indexerIsEnabled,
                HealthStatus: indexerProblem is not null && indexerIsEnabled ? "degraded" : reader.GetString(11),
                LastHealthMessage: indexerProblem ?? (reader.IsDBNull(12) ? null : reader.GetString(12)),
                LastHealthFailureCategory: indexerProblem is not null ? "auth" : reader.IsDBNull(13) ? null : reader.GetString(13),
                LastHealthLatencyMs: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                LastHealthTestUtc: reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)),
                ConsecutiveFailures: reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                RateLimitedUntilUtc: reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
                DisabledReason: reader.IsDBNull(18) ? null : reader.GetString(18),
                CreatedUtc: ParseTimestamp(reader.GetString(20)),
                UpdatedUtc: ParseTimestamp(reader.GetString(21)))
            {
                SecretUnreadable = indexerProblem is not null,
                SecretProblem = indexerProblem,
                RequestIntervalSeconds = reader.IsDBNull(19) ? null : reader.GetInt32(19),
                // All null on a source that has never been given its own rule,
                // which is the common case: it inherits the global one.
                SharingMode = reader.IsDBNull(22) ? null : reader.GetString(22),
                SharingForHours = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                SharingUntilRatio = reader.IsDBNull(24) ? null : reader.GetDouble(24),
                SharingStuckAction = reader.IsDBNull(25) ? null : reader.GetString(25),
                SharingStuckAfterDays = reader.IsDBNull(26) ? null : reader.GetInt32(26),
                MinimumAgeMinutes = reader.IsDBNull(27) ? null : reader.GetInt32(27),
                RetentionDays = reader.IsDBNull(28) ? null : reader.GetInt32(28),
                MaximumSizeMb = reader.IsDBNull(29) ? null : reader.GetInt32(29),
                PreferIndexerFlags = reader.IsDBNull(30) ? null : reader.GetString(30),
                AvailabilityDelayDays = reader.IsDBNull(31) ? null : reader.GetInt32(31),
                RssEnabled = reader.IsDBNull(32) || reader.GetInt64(32) == 1,
                AutomaticSearchEnabled = reader.IsDBNull(33) || reader.GetInt64(33) == 1,
                InteractiveSearchEnabled = reader.IsDBNull(34) || reader.GetInt64(34) == 1,
                LastHealthFailure = reader.IsDBNull(35) ? null : DeserializeFailure(reader.GetString(35)),
                DefinitionId = reader.IsDBNull(36) ? null : reader.GetString(36),
                DefinitionSettings = reader.IsDBNull(37) ? null : ReadStringMap(reader.GetString(37)),
                DefinitionSecrets = trackerSecrets.Value is null ? null : ReadStringMap(trackerSecrets.Value),
                ApiPath = reader.IsDBNull(39) ? null : reader.GetString(39),
                AdditionalParameters = reader.IsDBNull(40) ? null : reader.GetString(40),
                AnimeCategories = reader.IsDBNull(41) ? null : reader.GetString(41),
                MinimumSeeders = reader.IsDBNull(42) ? null : reader.GetInt32(42),
                RequiredWords = reader.IsDBNull(43) ? null : reader.GetString(43),
                IgnoredWords = reader.IsDBNull(44) ? null : reader.GetString(44),
                MultiLanguages = reader.IsDBNull(45) ? null : reader.GetString(45),
                VipExpiresUtc = reader.IsDBNull(46) ? null : ParseTimestamp(reader.GetString(46)),
                VipWarningAcknowledgedExpiryUtc = reader.IsDBNull(47) ? null : ParseTimestamp(reader.GetString(47)),
                QueryLimit = reader.IsDBNull(48) ? null : reader.GetInt32(48),
                GrabLimit = reader.IsDBNull(49) ? null : reader.GetInt32(49),
                LimitsUnit = reader.IsDBNull(50) ? null : reader.GetString(50),
                PreferMagnetUrl = !reader.IsDBNull(51) && reader.GetInt64(51) == 1,
                SeasonPackSearchMode = reader.IsDBNull(52) ? null : reader.GetString(52),
                SeasonPackSeedForHours = reader.IsDBNull(53) ? null : reader.GetInt32(53),
                ProxyId = reader.IsDBNull(54) ? null : reader.GetString(54)
            });
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
                created_utc, updated_utc,
                last_health_failure_json, use_ssl, url_base, add_paused, tags,
                remove_completed, remove_failed,
                skip_hash_check, sequential_download, first_last_piece_prio,
                share_ratio_limit, share_seeding_time_limit_minutes, share_inactive_seeding_time_limit_minutes,
                location, content_layout
            FROM download_clients
            ORDER BY priority ASC, name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var clientSecret = reader.IsDBNull(6) ? SecretReadResult.None : secretProtector.TryUnprotect("download-client:secret", reader.GetString(6));
            var clientProblem = clientSecret.Unreadable ? SecretUnreadable.Message("password") : null;
            var clientIsEnabled = reader.GetInt64(12) == 1;
            items.Add(new DownloadClientItem(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                Protocol: reader.GetString(2),
                Host: reader.IsDBNull(3) ? null : reader.GetString(3),
                Port: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Username: reader.IsDBNull(5) ? null : reader.GetString(5),
                Secret: clientSecret.Value,
                EndpointUrl: reader.IsDBNull(7) ? null : reader.GetString(7),
                UseSsl: reader.GetInt64(21) == 1,
                UrlBase: reader.IsDBNull(22) ? null : reader.GetString(22),
                MoviesCategory: reader.IsDBNull(8) ? null : reader.GetString(8),
                TvCategory: reader.IsDBNull(9) ? null : reader.GetString(9),
                CategoryTemplate: reader.IsDBNull(10) ? null : reader.GetString(10),
                Priority: reader.GetInt32(11),
                IsEnabled: clientIsEnabled,
                HealthStatus: clientProblem is not null && clientIsEnabled ? "degraded" : reader.GetString(13),
                LastHealthMessage: clientProblem ?? (reader.IsDBNull(14) ? null : reader.GetString(14)),
                LastHealthFailureCategory: clientProblem is not null ? "auth" : reader.IsDBNull(15) ? null : reader.GetString(15),
                LastHealthLatencyMs: reader.IsDBNull(16) ? null : reader.GetInt32(16),
                LastHealthTestUtc: reader.IsDBNull(17) ? null : ParseTimestamp(reader.GetString(17)),
                CreatedUtc: ParseTimestamp(reader.GetString(18)),
                UpdatedUtc: ParseTimestamp(reader.GetString(19)),
                AddPaused: reader.GetInt64(23) == 1,
                Tags: reader.GetString(24),
                RemoveCompleted: reader.GetInt64(25) == 1,
                RemoveFailed: reader.GetInt64(26) == 1,
                SkipHashCheck: reader.GetInt64(27) == 1,
                SequentialDownload: reader.GetInt64(28) == 1,
                FirstLastPiecePrio: reader.GetInt64(29) == 1,
                ShareRatioLimit: reader.IsDBNull(30) ? null : reader.GetDouble(30),
                ShareSeedingTimeLimitMinutes: reader.IsDBNull(31) ? null : reader.GetInt32(31),
                ShareInactiveSeedingTimeLimitMinutes: reader.IsDBNull(32) ? null : reader.GetInt32(32),
                Location: reader.IsDBNull(33) ? null : reader.GetString(33),
                ContentLayout: ContentLayouts.Normalize(reader.IsDBNull(34) ? null : reader.GetString(34)))
            {
                LastHealthFailure = reader.IsDBNull(20) ? null : DeserializeFailure(reader.GetString(20)),
                SecretUnreadable = clientProblem is not null,
                SecretProblem = clientProblem
            });
        }

        return items;
    }

    public async Task<IndexerItem> CreateIndexerAsync(
        CreateIndexerRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new IndexerItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: string.Empty,
            Protocol: IndexerProtocols.Normalize(request.Protocol),
            // What the person said wins; otherwise a site added from a tracker definition keeps what the definition says
            // about itself (#1240), and a hand-added Newznab or Torznab source stays "not set": nothing in it says.
            Privacy: IndexerPrivacy.Normalize(request.Privacy) is var said && said != IndexerPrivacy.Unknown
                ? said
                : (string.IsNullOrWhiteSpace(request.DefinitionId) ? null : trackerDefinitionNames?.Privacy(request.DefinitionId)) is { } fromDefinition
                    ? IndexerPrivacy.Normalize(fromDefinition)
                    : IndexerPrivacy.Unknown,
            BaseUrl: IndexerApiEndpoint.NormalizeBaseUrl(NormalizePath(request.BaseUrl)) ?? string.Empty,
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
            UpdatedUtc: now)
        {
            RequestIntervalSeconds = request.RequestIntervalSeconds,
            ApiPath = NormalizeBlankToNull(request.ApiPath),
            AdditionalParameters = NormalizeBlankToNull(request.AdditionalParameters),
            AnimeCategories = NormalizeOptionalCsv(request.AnimeCategories),
            SharingMode = request.SharingMode,
            SharingForHours = request.SharingForHours,
            SharingUntilRatio = request.SharingUntilRatio,
            SharingStuckAction = request.SharingStuckAction,
            SharingStuckAfterDays = request.SharingStuckAfterDays,
            SeasonPackSeedForHours = request.SeasonPackSeedForHours,
            MinimumAgeMinutes = request.MinimumAgeMinutes,
            RetentionDays = request.RetentionDays,
            MaximumSizeMb = request.MaximumSizeMb,
            MinimumSeeders = request.MinimumSeeders,
            PreferIndexerFlags = request.PreferIndexerFlags,
            RequiredWords = NormalizeBlankToNull(request.RequiredWords),
            IgnoredWords = NormalizeBlankToNull(request.IgnoredWords),
            MultiLanguages = NormalizeBlankToNull(request.MultiLanguages),
            VipExpiresUtc = request.VipExpiresUtc,
            QueryLimit = request.QueryLimit,
            GrabLimit = request.GrabLimit,
            LimitsUnit = string.IsNullOrWhiteSpace(request.LimitsUnit) ? null : IndexerLimitsUnits.Normalize(request.LimitsUnit),
            PreferMagnetUrl = request.PreferMagnetUrl,
            SeasonPackSearchMode = request.SeasonPackSearchMode is null || IndexerSeasonPackSearchModes.Normalize(request.SeasonPackSearchMode) == IndexerSeasonPackSearchModes.Both
                ? null
                : IndexerSeasonPackSearchModes.Normalize(request.SeasonPackSearchMode),
            AvailabilityDelayDays = request.AvailabilityDelayDays,
            RssEnabled = request.RssEnabled,
            AutomaticSearchEnabled = request.AutomaticSearchEnabled,
            InteractiveSearchEnabled = request.InteractiveSearchEnabled,
            DefinitionId = string.IsNullOrWhiteSpace(request.DefinitionId) ? null : request.DefinitionId.Trim().ToLowerInvariant(),
            DefinitionSettings = WithoutBlanks(request.DefinitionSettings),
            DefinitionSecrets = WithoutBlanks(request.DefinitionSecrets),
            ProxyId = NormalizeBlankToNull(request.ProxyId)
        };

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        // A name given is kept, whatever the kind and whoever gave it: the screen, the API or an import (#1248). It is stored
        // where a name Prowlarr or Jackett gave an indexer is, so every later rename pass keeps it while the address stays.
        var typedName = NormalizeBlankToNull(request.Name)?.Trim();
        item = item with
        {
            Name = ConnectionNames.Unique(
                IndexerBaseName(item.DefinitionId, item.BaseUrl, typedName),
                ConnectionNames.PortOf(item.BaseUrl),
                item.Id,
                await ConnectionNameRows.OtherNamesAsync(connection, "indexer_sources", item.Id, cancellationToken))
        };

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO indexer_sources (
                id, name, protocol, privacy, base_url, api_key, priority, categories, tags,
                media_scope, is_enabled, health_status, last_health_message, request_interval_seconds, created_utc, updated_utc,
                sharing_mode, sharing_for_hours, sharing_until_ratio, sharing_stuck_action, sharing_stuck_after_days,
                minimum_age_minutes, retention_days, maximum_size_mb, prefer_indexer_flags, availability_delay_days,
                rss_enabled, automatic_search_enabled, interactive_search_enabled, definition_id,
                definition_settings_json, definition_secrets, api_path, additional_parameters, anime_categories,
                minimum_seeders, required_words, ignored_words, multi_languages,
                vip_expires_utc, query_limit, grab_limit, limits_unit, prefer_magnet_url,
                season_pack_search_mode, season_pack_seed_for_hours, proxy_id
            )
            VALUES (
                @id, @name, @protocol, @privacy, @baseUrl, @apiKey, @priority, @categories, @tags,
                @mediaScope, @isEnabled, @healthStatus, @lastHealthMessage, @requestIntervalSeconds, @createdUtc, @updatedUtc,
                @sharingMode, @sharingForHours, @sharingUntilRatio, @sharingStuckAction, @sharingStuckAfterDays,
                @minimumAgeMinutes, @retentionDays, @maximumSizeMb, @preferIndexerFlags, @availabilityDelayDays,
                @rssEnabled, @automaticSearchEnabled, @interactiveSearchEnabled, @definitionId,
                @definitionSettings, @definitionSecrets, @apiPath, @additionalParameters, @animeCategories,
                @minimumSeeders, @requiredWords, @ignoredWords, @multiLanguages,
                @vipExpiresUtc, @queryLimit, @grabLimit, @limitsUnit, @preferMagnetUrl,
                @seasonPackSearchMode, @seasonPackSeedForHours, @proxyId
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@sharingMode", item.SharingMode);
        AddParameter(command, "@sharingForHours", item.SharingForHours);
        AddParameter(command, "@sharingUntilRatio", item.SharingUntilRatio);
        AddParameter(command, "@sharingStuckAction", item.SharingStuckAction);
        AddParameter(command, "@sharingStuckAfterDays", item.SharingStuckAfterDays);
        AddParameter(command, "@seasonPackSeedForHours", item.SeasonPackSeedForHours);
        AddParameter(command, "@minimumAgeMinutes", item.MinimumAgeMinutes);
        AddParameter(command, "@retentionDays", item.RetentionDays);
        AddParameter(command, "@maximumSizeMb", item.MaximumSizeMb);
        AddParameter(command, "@preferIndexerFlags", item.PreferIndexerFlags);
        AddParameter(command, "@availabilityDelayDays", item.AvailabilityDelayDays);
        AddParameter(command, "@rssEnabled", item.RssEnabled ? 1 : 0);
        AddParameter(command, "@automaticSearchEnabled", item.AutomaticSearchEnabled ? 1 : 0);
        AddParameter(command, "@interactiveSearchEnabled", item.InteractiveSearchEnabled ? 1 : 0);
        AddParameter(command, "@definitionId", item.DefinitionId);
        AddDefinitionParameters(command, item.DefinitionSettings, item.DefinitionSecrets);
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
        AddParameter(command, "@requestIntervalSeconds", item.RequestIntervalSeconds);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        AddParameter(command, "@apiPath", item.ApiPath);
        AddParameter(command, "@additionalParameters", item.AdditionalParameters);
        AddParameter(command, "@animeCategories", item.AnimeCategories);
        AddParameter(command, "@minimumSeeders", item.MinimumSeeders);
        AddParameter(command, "@requiredWords", item.RequiredWords);
        AddParameter(command, "@ignoredWords", item.IgnoredWords);
        AddParameter(command, "@multiLanguages", item.MultiLanguages);
        AddParameter(command, "@vipExpiresUtc", item.VipExpiresUtc?.ToString("O"));
        AddParameter(command, "@queryLimit", item.QueryLimit);
        AddParameter(command, "@grabLimit", item.GrabLimit);
        AddParameter(command, "@limitsUnit", item.LimitsUnit);
        AddParameter(command, "@preferMagnetUrl", item.PreferMagnetUrl ? 1 : 0);
        AddParameter(command, "@seasonPackSearchMode", item.SeasonPackSearchMode);
        AddParameter(command, "@proxyId", item.ProxyId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (typedName is not null)
        {
            await StoreTypedIndexerNameAsync(connection, item.Id, typedName, item.BaseUrl, cancellationToken);
        }

        if (request.RouteToLibraries)
        {
            await RouteNewIndexerAsync(connection, item, cancellationToken);
        }

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

        var newProtocol = request.Protocol is not null ? IndexerProtocols.Normalize(request.Protocol) : existing.Protocol;
        var newPrivacy  = request.Privacy is not null ? IndexerPrivacy.Normalize(request.Privacy) : existing.Privacy;
        var newBaseUrl  = IndexerApiEndpoint.NormalizeBaseUrl(NormalizePath(request.BaseUrl)) ?? existing.BaseUrl;
        // A name that differs from the one it has is a rename. The form sends the current name back on every save: that is
        // a rename only when the name was a given one, which then goes with the indexer to its new address (#1248).
        var typedName = !string.IsNullOrWhiteSpace(request.Name)
            && (!string.Equals(request.Name.Trim(), existing.Name, StringComparison.OrdinalIgnoreCase)
                || await IsGivenNameAsync(connection, id, existing.Name, cancellationToken))
            ? request.Name.Trim()
            : null;
        var newName     = ConnectionNames.Unique(
            IndexerBaseName(existing.DefinitionId, newBaseUrl, typedName ?? await DiscoveredNameAsync(connection, id, newBaseUrl, cancellationToken)),
            ConnectionNames.PortOf(newBaseUrl),
            id,
            await ConnectionNameRows.OtherNamesAsync(connection, "indexer_sources", id, cancellationToken));
        var newApiKey   = request.ApiKey is not null ? NormalizeName(request.ApiKey) : existing.ApiKey;
        var newPriority = request.Priority is >= 1 ? request.Priority.Value : existing.Priority;
        var newCats     = request.Categories is not null ? NormalizeCsv(request.Categories) : existing.Categories;
        var newTags     = request.Tags is not null ? NormalizeCsv(request.Tags) : existing.Tags;
        var newScope    = request.MediaScope is not null ? NormalizeMediaScope(request.MediaScope) : existing.MediaScope;
        var newEnabled  = request.IsEnabled ?? existing.IsEnabled;
        var newRequestInterval = request.ClearRequestInterval == true
            ? null
            : request.RequestIntervalSeconds ?? existing.RequestIntervalSeconds;
        var newApiPath = request.ClearApiPath == true
            ? null
            : NormalizeBlankToNull(request.ApiPath) ?? existing.ApiPath;
        var newAdditionalParameters = request.ClearAdditionalParameters == true
            ? null
            : NormalizeBlankToNull(request.AdditionalParameters) ?? existing.AdditionalParameters;
        var newAnimeCategories = request.ClearAnimeCategories == true
            ? null
            : NormalizeOptionalCsv(request.AnimeCategories) ?? existing.AnimeCategories;
        var newProxyId = request.ClearProxy == true
            ? null
            : NormalizeBlankToNull(request.ProxyId) ?? existing.ProxyId;

        // Clearing drops the whole override at once rather than field by field:
        // "this source is no different from the rest" is a single intent, and
        // half-cleared overrides are exactly the state nobody can reason about.
        var clearSharing = request.ClearSharingPolicy == true;
        var newSharingMode        = clearSharing ? null : request.SharingMode ?? existing.SharingMode;
        var newSharingForHours    = clearSharing ? null : request.SharingForHours ?? existing.SharingForHours;
        var newSharingUntilRatio  = clearSharing ? null : request.SharingUntilRatio ?? existing.SharingUntilRatio;
        var newSharingStuckAction = clearSharing ? null : request.SharingStuckAction ?? existing.SharingStuckAction;
        var newSharingStuckDays   = clearSharing ? null : request.SharingStuckAfterDays ?? existing.SharingStuckAfterDays;
        var newSeasonPackSeedForHours = request.ClearSeasonPackSeedForHours == true
            ? null
            : request.SeasonPackSeedForHours ?? existing.SeasonPackSeedForHours;
        var newMinimumAge = request.ClearMinimumAge == true ? null : request.MinimumAgeMinutes ?? existing.MinimumAgeMinutes;
        var newRetention = request.ClearRetention == true ? null : request.RetentionDays ?? existing.RetentionDays;
        var newMaximumSize = request.ClearMaximumSize == true ? null : request.MaximumSizeMb ?? existing.MaximumSizeMb;
        var newMinimumSeeders = request.ClearMinimumSeeders == true ? null : request.MinimumSeeders ?? existing.MinimumSeeders;
        var newPreferIndexerFlags = request.ClearPreferIndexerFlags == true
            ? null
            : request.PreferIndexerFlags ?? existing.PreferIndexerFlags;
        var newRequiredWords = request.ClearRequiredWords == true
            ? null
            : NormalizeBlankToNull(request.RequiredWords) ?? existing.RequiredWords;
        var newIgnoredWords = request.ClearIgnoredWords == true
            ? null
            : NormalizeBlankToNull(request.IgnoredWords) ?? existing.IgnoredWords;
        var newMultiLanguages = request.ClearMultiLanguages == true
            ? null
            : NormalizeBlankToNull(request.MultiLanguages) ?? existing.MultiLanguages;
        var newVipExpiresUtc = request.ClearVipExpiry == true
            ? null
            : request.VipExpiresUtc ?? existing.VipExpiresUtc;
        // Acknowledging silences the warning for the value it names — the
        // just-updated date when given alongside it, or the still-current
        // one otherwise. Clearing the date drops the marker with it, and any
        // other edit leaves a standing acknowledgement alone.
        var newVipWarningAcknowledged = request.ClearVipExpiry == true
            ? null
            : request.AcknowledgeVipExpiry == true
                ? newVipExpiresUtc
                : existing.VipWarningAcknowledgedExpiryUtc;
        var newQueryLimit = request.ClearQueryLimit == true ? null : request.QueryLimit ?? existing.QueryLimit;
        var newGrabLimit = request.ClearGrabLimit == true ? null : request.GrabLimit ?? existing.GrabLimit;
        var newLimitsUnit = string.IsNullOrWhiteSpace(request.LimitsUnit)
            ? existing.LimitsUnit
            : IndexerLimitsUnits.Normalize(request.LimitsUnit);
        var newPreferMagnetUrl = request.PreferMagnetUrl ?? existing.PreferMagnetUrl;
        var newSeasonPackSearchMode = request.SeasonPackSearchMode is null
            ? existing.SeasonPackSearchMode
            : IndexerSeasonPackSearchModes.Normalize(request.SeasonPackSearchMode) == IndexerSeasonPackSearchModes.Both
                ? null
                : IndexerSeasonPackSearchModes.Normalize(request.SeasonPackSearchMode);
        var newAvailabilityDelay = request.ClearAvailabilityDelay == true
            ? null
            : request.AvailabilityDelayDays ?? existing.AvailabilityDelayDays;
        var newRssEnabled = request.RssEnabled ?? existing.RssEnabled;
        var newAutomaticSearchEnabled = request.AutomaticSearchEnabled ?? existing.AutomaticSearchEnabled;
        var newInteractiveSearchEnabled = request.InteractiveSearchEnabled ?? existing.InteractiveSearchEnabled;
        var newDefinitionSettings = request.DefinitionSettings is not null ? WithoutBlanks(request.DefinitionSettings) : existing.DefinitionSettings;
        var newDefinitionSecrets = MergeSecrets(existing.DefinitionSecrets, request.DefinitionSecrets);

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
                request_interval_seconds = @requestIntervalSeconds,
                api_path = @apiPath,
                additional_parameters = @additionalParameters,
                anime_categories = @animeCategories,
                proxy_id = @proxyId,
                sharing_mode = @sharingMode,
                sharing_for_hours = @sharingForHours,
                sharing_until_ratio = @sharingUntilRatio,
                sharing_stuck_action = @sharingStuckAction,
                sharing_stuck_after_days = @sharingStuckAfterDays,
                season_pack_seed_for_hours = @seasonPackSeedForHours,
                minimum_age_minutes = @minimumAgeMinutes,
                retention_days = @retentionDays,
                maximum_size_mb = @maximumSizeMb,
                minimum_seeders = @minimumSeeders,
                required_words = @requiredWords,
                ignored_words = @ignoredWords,
                multi_languages = @multiLanguages,
                vip_expires_utc = @vipExpiresUtc,
                vip_warning_acknowledged_expiry_utc = @vipWarningAcknowledgedExpiryUtc,
                query_limit = @queryLimit,
                grab_limit = @grabLimit,
                limits_unit = @limitsUnit,
                prefer_magnet_url = @preferMagnetUrl,
                season_pack_search_mode = @seasonPackSearchMode,
                prefer_indexer_flags = @preferIndexerFlags,
                availability_delay_days = @availabilityDelayDays,
                rss_enabled = @rssEnabled,
                automatic_search_enabled = @automaticSearchEnabled,
                interactive_search_enabled = @interactiveSearchEnabled,
                definition_settings_json = @definitionSettings,
                definition_secrets = @definitionSecrets,
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
        AddParameter(command, "@requestIntervalSeconds", newRequestInterval);
        AddParameter(command, "@apiPath", newApiPath);
        AddParameter(command, "@additionalParameters", newAdditionalParameters);
        AddParameter(command, "@animeCategories", newAnimeCategories);
        AddParameter(command, "@proxyId", newProxyId);
        AddParameter(command, "@sharingMode", newSharingMode);
        AddParameter(command, "@sharingForHours", newSharingForHours);
        AddParameter(command, "@sharingUntilRatio", newSharingUntilRatio);
        AddParameter(command, "@sharingStuckAction", newSharingStuckAction);
        AddParameter(command, "@sharingStuckAfterDays", newSharingStuckDays);
        AddParameter(command, "@seasonPackSeedForHours", newSeasonPackSeedForHours);
        AddParameter(command, "@minimumAgeMinutes", newMinimumAge);
        AddParameter(command, "@retentionDays", newRetention);
        AddParameter(command, "@maximumSizeMb", newMaximumSize);
        AddParameter(command, "@minimumSeeders", newMinimumSeeders);
        AddParameter(command, "@requiredWords", newRequiredWords);
        AddParameter(command, "@ignoredWords", newIgnoredWords);
        AddParameter(command, "@multiLanguages", newMultiLanguages);
        AddParameter(command, "@vipExpiresUtc", newVipExpiresUtc?.ToString("O"));
        AddParameter(command, "@vipWarningAcknowledgedExpiryUtc", newVipWarningAcknowledged?.ToString("O"));
        AddParameter(command, "@queryLimit", newQueryLimit);
        AddParameter(command, "@grabLimit", newGrabLimit);
        AddParameter(command, "@limitsUnit", newLimitsUnit);
        AddParameter(command, "@preferMagnetUrl", newPreferMagnetUrl ? 1 : 0);
        AddParameter(command, "@seasonPackSearchMode", newSeasonPackSearchMode);
        AddParameter(command, "@preferIndexerFlags", newPreferIndexerFlags);
        AddParameter(command, "@availabilityDelayDays", newAvailabilityDelay);
        AddParameter(command, "@rssEnabled", newRssEnabled ? 1 : 0);
        AddParameter(command, "@automaticSearchEnabled", newAutomaticSearchEnabled ? 1 : 0);
        AddParameter(command, "@interactiveSearchEnabled", newInteractiveSearchEnabled ? 1 : 0);
        AddDefinitionParameters(command, newDefinitionSettings, newDefinitionSecrets);
        AddParameter(command, "@isEnabled", newEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", newHealth);
        AddParameter(command, "@lastHealthMessage", newMsg);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;

        if (typedName is not null)
        {
            await StoreTypedIndexerNameAsync(connection, id, typedName, newBaseUrl, cancellationToken);
        }

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
            RequestIntervalSeconds = newRequestInterval,
            ApiPath = newApiPath,
            AdditionalParameters = newAdditionalParameters,
            AnimeCategories = newAnimeCategories,
            ProxyId = newProxyId,
            SharingMode = newSharingMode,
            SharingForHours = newSharingForHours,
            SharingUntilRatio = newSharingUntilRatio,
            SharingStuckAction = newSharingStuckAction,
            SharingStuckAfterDays = newSharingStuckDays,
            SeasonPackSeedForHours = newSeasonPackSeedForHours,
            MinimumAgeMinutes = newMinimumAge,
            RetentionDays = newRetention,
            MaximumSizeMb = newMaximumSize,
            MinimumSeeders = newMinimumSeeders,
            RequiredWords = newRequiredWords,
            IgnoredWords = newIgnoredWords,
            MultiLanguages = newMultiLanguages,
            VipExpiresUtc = newVipExpiresUtc,
            VipWarningAcknowledgedExpiryUtc = newVipWarningAcknowledged,
            QueryLimit = newQueryLimit,
            GrabLimit = newGrabLimit,
            LimitsUnit = newLimitsUnit,
            PreferMagnetUrl = newPreferMagnetUrl,
            SeasonPackSearchMode = newSeasonPackSearchMode,
            PreferIndexerFlags = newPreferIndexerFlags,
            AvailabilityDelayDays = newAvailabilityDelay,
            RssEnabled = newRssEnabled,
            AutomaticSearchEnabled = newAutomaticSearchEnabled,
            InteractiveSearchEnabled = newInteractiveSearchEnabled,
            DefinitionSettings = newDefinitionSettings,
            DefinitionSecrets = newDefinitionSecrets,
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
            Name: string.Empty,
            Protocol: NormalizeDownloadProtocol(request.Protocol),
            Host: NormalizeName(request.Host),
            Port: NormalizeNullablePositiveValue(request.Port),
            Username: NormalizeName(request.Username),
            Secret: NormalizeName(request.Password),
            EndpointUrl: NormalizePath(request.EndpointUrl),
            UseSsl: request.UseSsl,
            UrlBase: NormalizePath(request.UrlBase),
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
            UpdatedUtc: now,
            AddPaused: request.AddPaused,
            Tags: NormalizeCsv(request.Tags),
            RemoveCompleted: request.RemoveCompleted,
            RemoveFailed: request.RemoveFailed,
            SkipHashCheck: request.SkipHashCheck,
            SequentialDownload: request.SequentialDownload,
            FirstLastPiecePrio: request.FirstLastPiecePrio,
            ShareRatioLimit: request.ShareRatioLimit,
            ShareSeedingTimeLimitMinutes: request.ShareSeedingTimeLimitMinutes,
            ShareInactiveSeedingTimeLimitMinutes: request.ShareInactiveSeedingTimeLimitMinutes,
            Location: NormalizePath(request.Location),
            ContentLayout: ContentLayouts.Normalize(request.ContentLayout));

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        item = item with
        {
            Name = ConnectionNames.Unique(
                DownloadClientBaseName(item.Protocol, item.Host, item.EndpointUrl),
                item.Port,
                item.Id,
                await ConnectionNameRows.OtherNamesAsync(connection, "download_clients", item.Id, cancellationToken))
        };

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_clients (
                id, name, protocol, host, port, username, secret, endpoint_url,
                use_ssl, url_base, add_paused,
                movies_category, tv_category, category_template, priority,
                is_enabled, health_status, last_health_message, created_utc, updated_utc, tags,
                remove_completed, remove_failed,
                skip_hash_check, sequential_download, first_last_piece_prio,
                share_ratio_limit, share_seeding_time_limit_minutes, share_inactive_seeding_time_limit_minutes,
                location, content_layout
            )
            VALUES (
                @id, @name, @protocol, @host, @port, @username, @secret, @endpointUrl,
                @useSsl, @urlBase, @addPaused,
                @moviesCategory, @tvCategory, @categoryTemplate, @priority,
                @isEnabled, @healthStatus, @lastHealthMessage, @createdUtc, @updatedUtc, @tags,
                @removeCompleted, @removeFailed,
                @skipHashCheck, @sequentialDownload, @firstLastPiecePrio,
                @shareRatioLimit, @shareSeedingTimeLimitMinutes, @shareInactiveSeedingTimeLimitMinutes,
                @location, @contentLayout
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
        AddParameter(command, "@useSsl", item.UseSsl ? 1 : 0);
        AddParameter(command, "@urlBase", item.UrlBase);
        AddParameter(command, "@addPaused", item.AddPaused ? 1 : 0);
        AddParameter(command, "@moviesCategory", item.MoviesCategory);
        AddParameter(command, "@tvCategory", item.TvCategory);
        AddParameter(command, "@categoryTemplate", item.CategoryTemplate);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", item.HealthStatus);
        AddParameter(command, "@lastHealthMessage", item.LastHealthMessage);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        AddParameter(command, "@tags", item.Tags);
        AddParameter(command, "@removeCompleted", item.RemoveCompleted ? 1 : 0);
        AddParameter(command, "@removeFailed", item.RemoveFailed ? 1 : 0);
        AddParameter(command, "@skipHashCheck", item.SkipHashCheck ? 1 : 0);
        AddParameter(command, "@sequentialDownload", item.SequentialDownload ? 1 : 0);
        AddParameter(command, "@firstLastPiecePrio", item.FirstLastPiecePrio ? 1 : 0);
        AddParameter(command, "@shareRatioLimit", item.ShareRatioLimit);
        AddParameter(command, "@shareSeedingTimeLimitMinutes", item.ShareSeedingTimeLimitMinutes);
        AddParameter(command, "@shareInactiveSeedingTimeLimitMinutes", item.ShareInactiveSeedingTimeLimitMinutes);
        AddParameter(command, "@location", item.Location);
        AddParameter(command, "@contentLayout", item.ContentLayout);
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (request.RouteToLibraries)
        {
            await RouteNewDownloadClientAsync(connection, item, cancellationToken);
        }

        return item;
    }

    public async Task<IReadOnlyList<DownloadClientPathMappingItem>> ListDownloadClientPathMappingsAsync(
        string? downloadClientId,
        CancellationToken cancellationToken)
    {
        // A mapping applies to every client at its host (#809), not only the
        // client it was created under, so listing "for client X" also has to
        // surface mappings created under a different client at X's own host.
        // A client with no host recorded falls back to exactly today's
        // behaviour: only the mappings created under its own id.
        string? host = null;
        if (!string.IsNullOrWhiteSpace(downloadClientId))
        {
            var owner = (await ListDownloadClientsAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, downloadClientId, StringComparison.OrdinalIgnoreCase));
            host = NormalizeHost(owner?.Host);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<DownloadClientPathMappingItem>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, download_client_id, remote_path, local_path, is_enabled, priority, created_utc, updated_utc, host
            FROM download_client_path_mappings
            WHERE @downloadClientId IS NULL
               OR download_client_id = @downloadClientId
               OR (@host IS NOT NULL AND host = @host)
            ORDER BY priority ASC, LENGTH(remote_path) DESC, remote_path ASC;
            """;
        AddParameter(command, "@downloadClientId", string.IsNullOrWhiteSpace(downloadClientId) ? null : downloadClientId.Trim());
        AddParameter(command, "@host", host);

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
                ParseTimestamp(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
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

        // Left blank, a mapping takes the host of the client it is added
        // under (#809) - that is what makes it reusable for any other client
        // sharing that host without anyone having to type the host twice.
        var host = NormalizeHost(request.Host) ?? NormalizeHost(client.Host);

        var now = timeProvider.GetUtcNow();
        var item = new DownloadClientPathMappingItem(
            Guid.CreateVersion7().ToString("N"),
            client.Id,
            remotePath,
            localPath,
            request.IsEnabled,
            request.Priority is >= 1 ? request.Priority.Value : 10,
            now,
            now,
            host);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO download_client_path_mappings (
                id, download_client_id, remote_path, local_path, is_enabled, priority, created_utc, updated_utc, host)
            VALUES (@id, @downloadClientId, @remotePath, @localPath, @isEnabled, @priority, @createdUtc, @updatedUtc, @host);
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@downloadClientId", item.DownloadClientId);
        AddParameter(command, "@remotePath", item.RemotePath);
        AddParameter(command, "@localPath", item.LocalPath);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        AddParameter(command, "@host", item.Host);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return item;
    }

    /// <summary>Trimmed and lowercased, or null for blank — the shape a host is stored and compared in (#809).</summary>
    private static string? NormalizeHost(string? host)
        => string.IsNullOrWhiteSpace(host) ? null : host.Trim().ToLowerInvariant();

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

        var newProtocol = request.Protocol is not null ? NormalizeDownloadProtocol(request.Protocol) : existing.Protocol;
        var newHost     = request.Host is not null ? NormalizeName(request.Host) : existing.Host;
        var newPort     = request.Port is >= 1 ? request.Port : existing.Port;
        var newUsername = request.Username is not null ? NormalizeName(request.Username) : existing.Username;
        var newSecret   = request.Password is not null ? NormalizeName(request.Password) : existing.Secret;
        var newEndpoint = request.EndpointUrl is not null ? NormalizePath(request.EndpointUrl) : existing.EndpointUrl;
        var newName     = ConnectionNames.Unique(
            DownloadClientBaseName(newProtocol, newHost, newEndpoint),
            newPort,
            id,
            await ConnectionNameRows.OtherNamesAsync(connection, "download_clients", id, cancellationToken));
        var newUseSsl   = request.UseSsl ?? existing.UseSsl;
        var newUrlBase  = request.UrlBase is not null ? NormalizePath(request.UrlBase) : existing.UrlBase;
        var newAddPaused = request.AddPaused ?? existing.AddPaused;
        var newMovieCat = request.MoviesCategory is not null ? NormalizeName(request.MoviesCategory) : existing.MoviesCategory;
        var newTvCat    = request.TvCategory is not null ? NormalizeName(request.TvCategory) : existing.TvCategory;
        var newCatTmpl  = request.CategoryTemplate is not null ? NormalizeName(request.CategoryTemplate) : existing.CategoryTemplate;
        var newPriority = request.Priority is >= 1 ? request.Priority.Value : existing.Priority;
        var newEnabled  = request.IsEnabled ?? existing.IsEnabled;
        var newTags     = request.Tags is not null ? NormalizeCsv(request.Tags) : existing.Tags;
        var newRemoveCompleted = request.RemoveCompleted ?? existing.RemoveCompleted;
        var newRemoveFailed    = request.RemoveFailed ?? existing.RemoveFailed;
        var newSkipHashCheck = request.SkipHashCheck ?? existing.SkipHashCheck;
        var newSequentialDownload = request.SequentialDownload ?? existing.SequentialDownload;
        var newFirstLastPiecePrio = request.FirstLastPiecePrio ?? existing.FirstLastPiecePrio;
        var newShareRatioLimit = request.ClearShareRatioLimit ? null : request.ShareRatioLimit ?? existing.ShareRatioLimit;
        var newShareSeedingTimeLimitMinutes = request.ClearShareSeedingTimeLimitMinutes ? null : request.ShareSeedingTimeLimitMinutes ?? existing.ShareSeedingTimeLimitMinutes;
        var newShareInactiveSeedingTimeLimitMinutes = request.ClearShareInactiveSeedingTimeLimitMinutes ? null : request.ShareInactiveSeedingTimeLimitMinutes ?? existing.ShareInactiveSeedingTimeLimitMinutes;
        var newContentLayout = request.ContentLayout is null ? existing.ContentLayout : ContentLayouts.Normalize(request.ContentLayout);
        var newLocation = request.ClearLocation ? null : request.Location is not null ? NormalizePath(request.Location) : existing.Location;

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
                use_ssl = @useSsl,
                url_base = @urlBase,
                add_paused = @addPaused,
                movies_category = @moviesCategory,
                tv_category = @tvCategory,
                category_template = @categoryTemplate,
                priority = @priority,
                is_enabled = @isEnabled,
                health_status = @healthStatus,
                last_health_message = @lastHealthMessage,
                updated_utc = @updatedUtc,
                tags = @tags,
                remove_completed = @removeCompleted,
                remove_failed = @removeFailed,
                skip_hash_check = @skipHashCheck,
                sequential_download = @sequentialDownload,
                first_last_piece_prio = @firstLastPiecePrio,
                share_ratio_limit = @shareRatioLimit,
                share_seeding_time_limit_minutes = @shareSeedingTimeLimitMinutes,
                share_inactive_seeding_time_limit_minutes = @shareInactiveSeedingTimeLimitMinutes,
                location = @location,
                content_layout = @contentLayout
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
        AddParameter(command, "@useSsl", newUseSsl ? 1 : 0);
        AddParameter(command, "@urlBase", newUrlBase);
        AddParameter(command, "@addPaused", newAddPaused ? 1 : 0);
        AddParameter(command, "@moviesCategory", newMovieCat);
        AddParameter(command, "@tvCategory", newTvCat);
        AddParameter(command, "@categoryTemplate", newCatTmpl);
        AddParameter(command, "@priority", newPriority);
        AddParameter(command, "@isEnabled", newEnabled ? 1 : 0);
        AddParameter(command, "@healthStatus", newHealth);
        AddParameter(command, "@lastHealthMessage", newMsg);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        AddParameter(command, "@tags", newTags);
        AddParameter(command, "@removeCompleted", newRemoveCompleted ? 1 : 0);
        AddParameter(command, "@removeFailed", newRemoveFailed ? 1 : 0);
        AddParameter(command, "@skipHashCheck", newSkipHashCheck ? 1 : 0);
        AddParameter(command, "@sequentialDownload", newSequentialDownload ? 1 : 0);
        AddParameter(command, "@firstLastPiecePrio", newFirstLastPiecePrio ? 1 : 0);
        AddParameter(command, "@shareRatioLimit", newShareRatioLimit);
        AddParameter(command, "@shareSeedingTimeLimitMinutes", newShareSeedingTimeLimitMinutes);
        AddParameter(command, "@shareInactiveSeedingTimeLimitMinutes", newShareInactiveSeedingTimeLimitMinutes);
        AddParameter(command, "@location", newLocation);
        AddParameter(command, "@contentLayout", newContentLayout);

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
            UseSsl           = newUseSsl,
            UrlBase          = newUrlBase,
            AddPaused        = newAddPaused,
            MoviesCategory   = newMovieCat,
            TvCategory       = newTvCat,
            CategoryTemplate = newCatTmpl,
            Priority         = newPriority,
            IsEnabled        = newEnabled,
            HealthStatus     = newHealth,
            LastHealthMessage = newMsg,
            Tags             = newTags,
            RemoveCompleted  = newRemoveCompleted,
            RemoveFailed     = newRemoveFailed,
            SkipHashCheck    = newSkipHashCheck,
            SequentialDownload = newSequentialDownload,
            FirstLastPiecePrio = newFirstLastPiecePrio,
            ShareRatioLimit  = newShareRatioLimit,
            ShareSeedingTimeLimitMinutes = newShareSeedingTimeLimitMinutes,
            ShareInactiveSeedingTimeLimitMinutes = newShareInactiveSeedingTimeLimitMinutes,
            Location         = newLocation,
            ContentLayout    = newContentLayout,
            UpdatedUtc       = now
        };
    }

    public async Task<IndexerTestResult?> UpdateIndexerHealthAsync(
        string id,
        string healthStatus,
        string message,
        string? failureCategory,
        int? latencyMs,
        CancellationToken cancellationToken,
        IntegrationFailure? failure = null)
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
                last_health_failure_json = @lastHealthFailureJson,
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
        AddParameter(command, "@lastHealthFailureJson", failure is null ? null : JsonSerializer.Serialize(failure));
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
            TestedUtc: now)
        {
            Failure = failure
        };
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
                last_health_failure_category = NULL,
                last_health_failure_json = NULL,
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
            UPDATE download_clients
            SET
                health_status = @healthStatus,
                last_health_message = @lastHealthMessage,
                last_health_failure_category = @lastHealthFailureCategory,
                last_health_failure_json = @lastHealthFailureJson,
                last_health_latency_ms = @lastHealthLatencyMs,
                last_health_test_utc = @lastHealthTestUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@healthStatus", healthStatus);
        AddParameter(command, "@lastHealthMessage", message);
        AddParameter(command, "@lastHealthFailureCategory", failureCategory);
        AddParameter(command, "@lastHealthFailureJson", failure is null ? null : JsonSerializer.Serialize(failure));
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
            TestedUtc: now)
        {
            Failure = failure
        };
    }

    /// <summary>A tracker's settings or secrets, stored as a JSON object of strings (#606).</summary>
    private static IReadOnlyDictionary<string, string>? ReadStringMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? WithoutBlanks(IReadOnlyDictionary<string, string>? values)
    {
        var kept = values?.Where(pair => pair.Key.Length > 0 && !string.IsNullOrEmpty(pair.Value)).ToDictionary(pair => pair.Key.Trim(), pair => pair.Value, StringComparer.Ordinal);
        return kept is { Count: > 0 } ? kept : null;
    }

    /// <summary>Secrets named in an update change; an empty value removes one; the rest stay.</summary>
    private static IReadOnlyDictionary<string, string>? MergeSecrets(IReadOnlyDictionary<string, string>? existing, IReadOnlyDictionary<string, string>? changes)
    {
        if (changes is null)
        {
            return existing;
        }

        var merged = new Dictionary<string, string>(existing ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (name, value) in changes)
        {
            if (string.IsNullOrEmpty(value))
            {
                merged.Remove(name);
            }
            else
            {
                merged[name] = value;
            }
        }

        return merged.Count > 0 ? merged : null;
    }

    private void AddDefinitionParameters(System.Data.Common.DbCommand command, IReadOnlyDictionary<string, string>? settings, IReadOnlyDictionary<string, string>? secrets)
    {
        AddParameter(command, "@definitionSettings", settings is null ? null : JsonSerializer.Serialize(settings));
        AddParameter(command, "@definitionSecrets", secrets is null ? null : secretProtector.Protect("indexer:tracker-secrets", JsonSerializer.Serialize(secrets)));
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

    public async Task<IReadOnlyList<ProxyItem>> ListProxiesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<ProxyItem>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT id, name, proxy_type, address, username, password, is_enabled, created_utc, updated_utc
                FROM indexer_proxies
                ORDER BY name ASC;
                """;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var proxyPassword = reader.IsDBNull(5) ? SecretReadResult.None : secretProtector.TryUnprotect("indexer-proxy:password", reader.GetString(5));
                items.Add(new ProxyItem(
                    Id: reader.GetString(0),
                    Name: reader.GetString(1),
                    ProxyType: reader.GetString(2),
                    Address: reader.GetString(3),
                    Username: reader.IsDBNull(4) ? null : reader.GetString(4),
                    Password: proxyPassword.Value,
                    IsEnabled: reader.GetInt64(6) == 1,
                    CreatedUtc: ParseTimestamp(reader.GetString(7)),
                    UpdatedUtc: ParseTimestamp(reader.GetString(8)))
                {
                    SecretUnreadable = proxyPassword.Unreadable,
                    SecretProblem = proxyPassword.Unreadable ? SecretUnreadable.Message("password") : null
                });
            }
        }

        if (items.Count == 0)
        {
            return items;
        }

        var assignedNames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT proxy_id, name FROM indexer_sources WHERE proxy_id IS NOT NULL ORDER BY name ASC;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var proxyId = reader.GetString(0);
                if (!assignedNames.TryGetValue(proxyId, out var names))
                {
                    names = [];
                    assignedNames[proxyId] = names;
                }

                names.Add(reader.GetString(1));
            }
        }

        return items
            .Select(item => assignedNames.TryGetValue(item.Id, out var names)
                ? item with { AssignedIndexerNames = names }
                : item)
            .ToArray();
    }

    public async Task<ProxyItem> CreateProxyAsync(CreateProxyRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new ProxyItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Name: string.Empty,
            ProxyType: ProxyTypes.Normalize(request.ProxyType),
            Address: NormalizePath(request.Address) ?? string.Empty,
            Username: NormalizeBlankToNull(request.Username),
            Password: NormalizeBlankToNull(request.Password),
            IsEnabled: request.IsEnabled,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var (baseName, port) = ProxyTypes.AutomaticName(item.ProxyType, item.Address);
        item = item with
        {
            Name = ConnectionNames.Unique(baseName, port, item.Id, await ConnectionNameRows.OtherNamesAsync(connection, "indexer_proxies", item.Id, cancellationToken))
        };

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO indexer_proxies (
                id, name, proxy_type, address, username, password, is_enabled, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @proxyType, @address, @username, @password, @isEnabled, @createdUtc, @updatedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@name", item.Name);
        AddParameter(command, "@proxyType", item.ProxyType);
        AddParameter(command, "@address", item.Address);
        AddParameter(command, "@username", item.Username);
        AddParameter(
            command,
            "@password",
            string.IsNullOrEmpty(item.Password) ? null : secretProtector.Protect("indexer-proxy:password", item.Password));
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<ProxyItem?> UpdateProxyAsync(string id, UpdateProxyRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var existing = (await ListProxiesAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return null;

        var newType = request.ProxyType is not null ? ProxyTypes.Normalize(request.ProxyType) : existing.ProxyType;
        var newAddress = NormalizeBlankToNull(request.Address) ?? existing.Address;
        var newUsername = request.Username is not null ? NormalizeBlankToNull(request.Username) : existing.Username;
        var newPassword = request.ClearPassword == true
            ? null
            : request.Password is not null ? NormalizeBlankToNull(request.Password) : existing.Password;
        var newEnabled = request.IsEnabled ?? existing.IsEnabled;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var (baseName, port) = ProxyTypes.AutomaticName(newType, newAddress);
        var newName = ConnectionNames.Unique(baseName, port, id, await ConnectionNameRows.OtherNamesAsync(connection, "indexer_proxies", id, cancellationToken));

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE indexer_proxies
            SET
                name = @name,
                proxy_type = @proxyType,
                address = @address,
                username = @username,
                password = @password,
                is_enabled = @isEnabled,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", newName);
        AddParameter(command, "@proxyType", newType);
        AddParameter(command, "@address", newAddress);
        AddParameter(command, "@username", newUsername);
        AddParameter(
            command,
            "@password",
            string.IsNullOrEmpty(newPassword) ? null : secretProtector.Protect("indexer-proxy:password", newPassword));
        AddParameter(command, "@isEnabled", newEnabled ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) return null;

        return existing with
        {
            Name = newName,
            ProxyType = newType,
            Address = newAddress,
            Username = newUsername,
            Password = newPassword,
            IsEnabled = newEnabled,
            UpdatedUtc = now
        };
    }

    public async Task<bool> DeleteProxyAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        // Unassign first so no indexer is left pointing at a proxy that no
        // longer exists; a different indexer's proxy is left untouched.
        using (var unassign = connection.CreateCommand())
        {
            unassign.CommandText = "UPDATE indexer_sources SET proxy_id = NULL WHERE proxy_id = @id;";
            AddParameter(unassign, "@id", id);
            await unassign.ExecuteNonQueryAsync(cancellationToken);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM indexer_proxies WHERE id = @id;";
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

    public async Task<int> RouteUnroutedDownloadClientAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM library_download_client_links WHERE download_client_id = @id;";
            AddParameter(check, "@id", id);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                return 0;
            }
        }

        var client = (await ListDownloadClientsAsync(cancellationToken)).FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return 0;
        }

        await RouteClientToLibrariesAsync(connection, client, onlyUnservedLibraries: false, cancellationToken);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM library_download_client_links WHERE download_client_id = @id;";
        AddParameter(count, "@id", id);
        return (int)Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
    }



    /// <summary>
    /// #1135: a new indexer is switched on for every library of the media types it searches, so the
    /// "Used for" answer is not asked again in Library Routing. It runs for every way an indexer is
    /// created (the drawer, the API, an import). A library that already searches it is left alone.
    /// </summary>
    private async Task RouteNewIndexerAsync(System.Data.Common.DbConnection connection, IndexerItem item, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToString("O");
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO library_source_links (id, library_id, indexer_id, priority, required_tags, excluded_tags, created_utc, updated_utc)
            SELECT lower(hex(randomblob(16))), l.id, @indexerId,
                   COALESCE((SELECT MAX(priority) FROM library_source_links WHERE library_id = l.id), 0) + 1,
                   '', '', @now, @now
            FROM libraries l
            WHERE (@scope = 'both' OR lower(l.media_type) = @scope);
            """;
        AddParameter(command, "@indexerId", item.Id);
        AddParameter(command, "@scope", item.MediaScope);
        AddParameter(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// #1135: the first download client connected for a library type is routed to that library. A
    /// client serves the types its categories name (both when it names none, as Transmission). A
    /// library that already has a client is left alone: a second client is the person's choice.
    /// </summary>
    private Task RouteNewDownloadClientAsync(System.Data.Common.DbConnection connection, DownloadClientItem item, CancellationToken cancellationToken)
        => RouteClientToLibrariesAsync(connection, item, onlyUnservedLibraries: true, cancellationToken);

    private async Task RouteClientToLibrariesAsync(System.Data.Common.DbConnection connection, DownloadClientItem item, bool onlyUnservedLibraries, CancellationToken cancellationToken)
    {
        var movies = !string.IsNullOrWhiteSpace(item.MoviesCategory) || !string.IsNullOrWhiteSpace(item.CategoryTemplate);
        var tv = !string.IsNullOrWhiteSpace(item.TvCategory);
        if (!movies && !tv)
        {
            movies = tv = true;
        }

        var now = timeProvider.GetUtcNow().ToString("O");
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO library_download_client_links (id, library_id, download_client_id, priority, category, created_utc, updated_utc)
            SELECT lower(hex(randomblob(16))), l.id, @clientId, 1, NULL, @now, @now
            FROM libraries l
            WHERE ((lower(l.media_type) = 'tv' AND @tv = 1) OR (lower(l.media_type) <> 'tv' AND @movies = 1))
              AND (@onlyUnserved = 0 OR NOT EXISTS (SELECT 1 FROM library_download_client_links k WHERE k.library_id = l.id));
            """;
        AddParameter(command, "@clientId", item.Id);
        AddParameter(command, "@movies", movies ? 1 : 0);
        AddParameter(command, "@tv", tv ? 1 : 0);
        AddParameter(command, "@onlyUnserved", onlyUnservedLibraries ? 1 : 0);
        AddParameter(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static string NormalizeDownloadProtocol(string? value)
        => DownloadClientProtocolCatalog.NormalizeOrThrow(value);

    /// <summary>A blank optional text field is stored as null, not as an empty string.</summary>
    private static string? NormalizeBlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>An optional comma-separated numbers field (#742), stored as null when blank.</summary>
    private static string? NormalizeOptionalCsv(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizeCsv(value);
}
