using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;
using static Deluno.Contracts.DelunoValueNormalizers;

namespace Deluno.Libraries.Data;

public sealed class SqliteLibrariesRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : ILibrariesRepository
{
    public async Task<IReadOnlyList<LibraryItem>> ListLibrariesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        await BackfillLibraryQualityProfilesAsync(connection, cancellationToken);

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
                l.created_utc, l.updated_utc, l.default_policy_set_id, p.name,
                l.cleanup_mode, l.remove_empty_source_folders
            FROM libraries l
            LEFT JOIN quality_profiles q ON q.id = l.quality_profile_id
            LEFT JOIN policy_sets p ON p.id = l.default_policy_set_id
            ORDER BY l.media_type ASC, l.name ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadLibrary(reader));
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
                id, user_id, variant, library_id, name, quick_filter, sort_field, sort_direction,
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
            LibraryId: NormalizeOptionalId(request.LibraryId),
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
                id, user_id, variant, library_id, name, quick_filter, sort_field, sort_direction,
                view_mode, card_size, display_options_json, rules_json, created_utc, updated_utc
            )
            VALUES (
                @id, @userId, @variant, @libraryId, @name, @quickFilter, @sortField, @sortDirection,
                @viewMode, @cardSize, @displayOptionsJson, @rulesJson, @createdUtc, @updatedUtc
            );
            """;
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@userId", item.UserId);
        AddParameter(command, "@variant", item.Variant);
        AddParameter(command, "@libraryId", item.LibraryId);
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
            SET library_id = @libraryId,
                name = @name,
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
        AddParameter(command, "@libraryId", NormalizeOptionalId(request.LibraryId));
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
            UpdatedUtc: now,
            CleanupMode: NormalizeCleanupMode(request.CleanupMode),
            RemoveEmptySourceFolders: request.RemoveEmptySourceFolders);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var qualityProfileId = await SqliteQualityRepository.ResolveQualityProfileIdAsync(
            connection,
            mediaType,
            NormalizeName(request.QualityProfileId),
            cancellationToken);
        var profile = qualityProfileId is null
            ? null
            : await SqliteQualityRepository.GetQualityProfileAsync(connection, qualityProfileId, cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO libraries (
                id, name, media_type, purpose, root_path, downloads_path, quality_profile_id,
                import_workflow, processor_name, processor_output_path, processor_timeout_minutes, processor_failure_mode,
                cleanup_mode, remove_empty_source_folders,
                auto_search_enabled,
                missing_search_enabled, upgrade_search_enabled, search_interval_hours,
                retry_delay_hours, max_items_per_run,
                search_window_start_hour, search_window_end_hour,
                created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @purpose, @rootPath, @downloadsPath, @qualityProfileId,
                @importWorkflow, @processorName, @processorOutputPath, @processorTimeoutMinutes, @processorFailureMode,
                @cleanupMode, @removeEmptySourceFolders,
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
        AddParameter(command, "@cleanupMode", item.CleanupMode);
        AddParameter(command, "@removeEmptySourceFolders", item.RemoveEmptySourceFolders ? 1 : 0);
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


    public async Task<LibraryItem?> UpdateLibraryDetailsAsync(
        string id,
        UpdateLibraryDetailsRequest request,
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

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE libraries
            SET
                name = @name,
                root_path = @rootPath,
                downloads_path = @downloadsPath,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", NormalizeName(request.Name) ?? library.Name);
        AddParameter(command, "@rootPath", NormalizePath(request.RootPath) ?? library.RootPath);
        AddParameter(command, "@downloadsPath", NormalizePath(request.DownloadsPath));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

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

        var qualityProfileId = await SqliteQualityRepository.ResolveQualityProfileIdAsync(
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
                default_policy_set_id = NULL,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@qualityProfileId", qualityProfileId);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetLibraryAsync(connection, id, cancellationToken);
    }


    public async Task<LibraryItem?> UpdateLibraryMediaPlanAsync(
        string id,
        UpdateLibraryMediaPlanRequest request,
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

        var policySetId = NormalizeName(request.PolicySetId);
        PolicySetItem? policySet = null;
        if (!string.IsNullOrWhiteSpace(policySetId))
        {
            policySet = await SqliteQualityRepository.GetPolicySetAsync(connection, policySetId, cancellationToken);
            if (policySet is null || !string.Equals(policySet.MediaType, library.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE libraries
            SET
                default_policy_set_id = @policySetId,
                quality_profile_id = CASE
                    WHEN @qualityProfileId IS NULL THEN quality_profile_id
                    ELSE @qualityProfileId
                END,
                search_interval_hours = COALESCE(@searchIntervalHours, search_interval_hours),
                retry_delay_hours = COALESCE(@retryDelayHours, retry_delay_hours),
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@policySetId", policySet?.Id);
        AddParameter(command, "@qualityProfileId", policySet?.QualityProfileId);
        AddParameter(command, "@searchIntervalHours", policySet?.SearchIntervalOverrideHours);
        AddParameter(command, "@retryDelayHours", policySet?.RetryDelayOverrideHours);
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetLibraryAsync(connection, id, cancellationToken);
    }


    public async Task<int> ApplyMediaPlanToAssignedLibrariesAsync(
        string policySetId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var policySet = await SqliteQualityRepository.GetPolicySetAsync(connection, policySetId, cancellationToken);
        if (policySet is null)
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE libraries
            SET
                quality_profile_id = CASE
                    WHEN @qualityProfileId IS NULL THEN quality_profile_id
                    ELSE @qualityProfileId
                END,
                search_interval_hours = COALESCE(@searchIntervalHours, search_interval_hours),
                retry_delay_hours = COALESCE(@retryDelayHours, retry_delay_hours),
                updated_utc = @updatedUtc
            WHERE default_policy_set_id = @policySetId;
            """;

        AddParameter(command, "@policySetId", policySet.Id);
        AddParameter(command, "@qualityProfileId", policySet.QualityProfileId);
        AddParameter(command, "@searchIntervalHours", policySet.SearchIntervalOverrideHours);
        AddParameter(command, "@retryDelayHours", policySet.RetryDelayOverrideHours);
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
                cleanup_mode = COALESCE(@cleanupMode, cleanup_mode),
                remove_empty_source_folders = COALESCE(@removeEmptySourceFolders, remove_empty_source_folders),
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@importWorkflow", workflow);
        AddParameter(command, "@processorName", NormalizeName(request.ProcessorName));
        AddParameter(command, "@processorOutputPath", processorOutputPath);
        AddParameter(command, "@processorTimeoutMinutes", NormalizePositiveValue(request.ProcessorTimeoutMinutes, 360));
        AddParameter(command, "@processorFailureMode", NormalizeProcessorFailureMode(request.ProcessorFailureMode));
        AddParameter(command, "@cleanupMode", string.IsNullOrWhiteSpace(request.CleanupMode) ? null : NormalizeCleanupMode(request.CleanupMode));
        AddParameter(command, "@removeEmptySourceFolders", request.RemoveEmptySourceFolders.HasValue ? (request.RemoveEmptySourceFolders.Value ? 1 : 0) : null);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await GetLibraryAsync(connection, id, cancellationToken);
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
                    id, library_id, download_client_id, priority, category, created_utc, updated_utc
                )
                VALUES (
                    @id, @libraryId, @downloadClientId, @priority, @category, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(insertClient, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(insertClient, "@libraryId", libraryId);
            AddParameter(insertClient, "@downloadClientId", client.DownloadClientId);
            AddParameter(insertClient, "@priority", client.Priority is >= 1 ? client.Priority.Value : 100);
            AddParameter(insertClient, "@category", NormalizeCategory(client.Category));
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
                l.created_utc, l.updated_utc, l.default_policy_set_id, p.name,
                l.cleanup_mode, l.remove_empty_source_folders
            FROM libraries l
            LEFT JOIN quality_profiles q ON q.id = l.quality_profile_id
            LEFT JOIN policy_sets p ON p.id = l.default_policy_set_id
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
            var profileId = await SqliteQualityRepository.ResolveQualityProfileIdAsync(
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
                id, user_id, variant, library_id, name, quick_filter, sort_field, sort_direction,
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


    private static string NormalizeCleanupMode(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "remove-source-after-import" or "delete-source-after-import" or "remove" => "remove-source-after-import",
            _ => "keep-source"
        };
    }

    private static string? NormalizeOptionalId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();


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


    private static int NormalizePriorityValue(int value)
    {
        return value <= 0 ? 100 : value;
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
            UpdatedUtc: ParseTimestamp(reader.GetString(25)),
            DefaultPolicySetId: reader.IsDBNull(26) ? null : reader.GetString(26),
            DefaultPolicySetName: reader.IsDBNull(27) ? null : reader.GetString(27),
            CleanupMode: reader.IsDBNull(28) ? "keep-source" : NormalizeCleanupMode(reader.GetString(28)),
            RemoveEmptySourceFolders: !reader.IsDBNull(29) && reader.GetInt64(29) == 1);
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


    private static LibraryViewItem ReadLibraryView(System.Data.Common.DbDataReader reader)
    {
        return new LibraryViewItem(
            Id: reader.GetString(0),
            UserId: reader.GetString(1),
            Variant: reader.GetString(2),
            LibraryId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Name: reader.GetString(4),
            QuickFilter: reader.GetString(5),
            SortField: reader.GetString(6),
            SortDirection: reader.GetString(7),
            ViewMode: reader.GetString(8),
            CardSize: reader.GetString(9),
            DisplayOptionsJson: reader.GetString(10),
            RulesJson: reader.GetString(11),
            CreatedUtc: ParseTimestamp(reader.GetString(12)),
            UpdatedUtc: ParseTimestamp(reader.GetString(13)));
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
                l.id, l.library_id, l.download_client_id, d.name, l.priority, l.created_utc, l.updated_utc, l.category
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
                UpdatedUtc: ParseTimestamp(reader.GetString(6)),
                Category: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return items;
    }

    private static string? NormalizeCategory(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();


}
