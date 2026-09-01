using System.Globalization;
using Deluno.Infrastructure.Storage;
using Deluno.Quality.Contracts;
using Deluno.Quality.Presets;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;
using static Deluno.Contracts.DelunoValueNormalizers;

namespace Deluno.Quality.Data;

/// <summary>
/// Split out of SqlitePlatformSettingsRepository by ADR-001 Step 1; method
/// bodies are unchanged apart from access modifiers needed to expose the
/// seeding/resolution helpers Deluno.Platform still calls while seeding and
/// backfilling libraries (Libraries has not moved yet).
/// </summary>
public sealed class SqliteQualityRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IQualityRepository
{
    public async Task<IReadOnlyList<QualityProfileItem>> ListQualityProfilesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        // Listing profiles used to create four of them when the table was empty,
        // so a fresh install "had" profiles nobody chose — which is exactly what
        // migration V0013 exists to delete and what
        // Fresh_install_does_not_invent_libraries_or_quality_profiles asserts
        // against. Reading is a read: the setup guide and the Media plans page
        // are where profiles come from.

        var items = new List<QualityProfileItem>();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, name, media_type, cutoff_quality, allowed_qualities, custom_format_ids,
                upgrade_until_cutoff, upgrade_unknown_items, allow_lower_quality_replacements,
                preset_id, preset_version, release_preference_plan_json, created_utc, updated_utc
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
                p.quality_profile_id, q.name, q.release_preference_plan_json,
                p.destination_rule_id, d.name,
                p.custom_format_ids, p.search_interval_override_hours, p.retry_delay_override_hours,
                p.upgrade_until_cutoff, p.is_enabled, p.notes, p.automation_intent_json, p.created_utc, p.updated_utc
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

    public async Task<IReadOnlyList<MediaPlanVersionItem>> ListMediaPlanVersionsAsync(
        string planId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return [];
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT plan_id, version, plan_hash, change_kind, snapshot_json, created_utc FROM media_plan_versions WHERE plan_id = @planId ORDER BY version DESC;";
        AddParameter(command, "@planId", planId.Trim());

        var items = new List<MediaPlanVersionItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadMediaPlanVersion(reader));
        }

        return items;
    }

    public async Task<MediaPlanVersionItem?> GetMediaPlanVersionAsync(
        string planId,
        int version,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(planId) || version <= 0)
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT plan_id, version, plan_hash, change_kind, snapshot_json, created_utc FROM media_plan_versions WHERE plan_id = @planId AND version = @version LIMIT 1;";
        AddParameter(command, "@planId", planId.Trim());
        AddParameter(command, "@version", version);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMediaPlanVersion(reader)
            : null;
    }

    public async Task<MediaPlanVersionItem?> GetLatestMediaPlanVersionAsync(
        string planId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            return null;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        return await GetLatestMediaPlanVersionAsync(connection, planId, cancellationToken);
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
            UpdatedUtc: now,
            ReleasePreferencePlan: ReleasePreferencePlanReference.Normalize(request.ReleasePreferencePlan));

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
        var preset = QualityProfilePresetCatalog.FindById(presetId)
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
                preset_id, preset_version, release_preference_plan_json, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @sortOrder, @cutoffQuality, @allowedQualities, @customFormatIds,
                @upgradeUntilCutoff, @upgradeUnknownItems, @allowLowerQualityReplacements,
                @presetId, @presetVersion, @releasePreferencePlan, @createdUtc, @updatedUtc
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
        AddParameter(command, "@releasePreferencePlan", ReleasePreferencePlanReferenceCodec.Serialize(item.ReleasePreferencePlan));
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CustomFormatItem> CreateCustomFormatAsync(
        CreateCustomFormatRequest request,
        CancellationToken cancellationToken,
        string? preferredId = null)
    {
        var now = timeProvider.GetUtcNow();
        var item = new CustomFormatItem(
            Id: NormalizeName(preferredId) ?? Guid.CreateVersion7().ToString("N"),
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
            AutomationIntent: MediaPlanAutomationIntentCodec.Normalize(request.AutomationIntent),
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO policy_sets (
                id, name, media_type, quality_profile_id, destination_rule_id, custom_format_ids,
                search_interval_override_hours, retry_delay_override_hours,
                upgrade_until_cutoff, is_enabled, notes, automation_intent_json, created_utc, updated_utc
            )
            VALUES (
                @id, @name, @mediaType, @qualityProfileId, @destinationRuleId, @customFormatIds,
                @searchIntervalOverrideHours, @retryDelayOverrideHours,
                @upgradeUntilCutoff, @isEnabled, @notes, @automationIntent, @createdUtc, @updatedUtc
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
        AddParameter(command, "@automationIntent", MediaPlanAutomationIntentCodec.Serialize(item.AutomationIntent));
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Read the joined policy-set projection before capturing its first
        // version. The effective release-preference reference belongs to the
        // selected quality profile, so the constructor value above cannot
        // carry it into immutable Media Plan history.
        var created = await GetPolicySetAsync(connection, item.Id, cancellationToken, transaction);
        await AppendMediaPlanVersionAsync(connection, created ?? item, "create", cancellationToken, transaction);
        await transaction.CommitAsync(cancellationToken);
        return created!;
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

        var nextName = NormalizeName(request.Name) ?? current.Name;
        var nextCutoffQuality = NormalizeName(request.CutoffQuality) ?? current.CutoffQuality;
        var nextAllowedQualities = string.IsNullOrWhiteSpace(request.AllowedQualities)
            ? current.AllowedQualities
            : NormalizeCsv(request.AllowedQualities);
        var nextCustomFormatIds = string.IsNullOrWhiteSpace(request.CustomFormatIds)
            ? string.Empty
            : NormalizeCsv(request.CustomFormatIds);
        var semanticProfileChanged = !string.Equals(nextCutoffQuality, current.CutoffQuality, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(nextAllowedQualities, current.AllowedQualities, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(nextCustomFormatIds, current.CustomFormatIds, StringComparison.OrdinalIgnoreCase)
            || request.UpgradeUntilCutoff != current.UpgradeUntilCutoff
            || request.UpgradeUnknownItems != current.UpgradeUnknownItems;
        var nextReleasePreferencePlan = request.ReleasePreferencePlan is not null
            ? ReleasePreferencePlanReference.Normalize(request.ReleasePreferencePlan)
            : semanticProfileChanged
                ? null
                : current.ReleasePreferencePlan;

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
                release_preference_plan_json = @releasePreferencePlan,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@name", nextName);
        AddParameter(command, "@cutoffQuality", nextCutoffQuality);
        AddParameter(command, "@allowedQualities", nextAllowedQualities);
        AddParameter(command, "@customFormatIds", nextCustomFormatIds);
        AddParameter(command, "@upgradeUntilCutoff", request.UpgradeUntilCutoff ? 1 : 0);
        AddParameter(command, "@upgradeUnknownItems", request.UpgradeUnknownItems ? 1 : 0);
        AddParameter(command, "@releasePreferencePlan", ReleasePreferencePlanReferenceCodec.Serialize(nextReleasePreferencePlan));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetQualityProfileAsync(connection, id, cancellationToken);
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

    public async Task<PolicySetItem?> UpdatePolicySetAsync(
        string id,
        UpdatePolicySetRequest request,
        CancellationToken cancellationToken,
        string changeKind = "update",
        string? expectedPlanHash = null)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var current = await GetPolicySetAsync(connection, id, cancellationToken, transaction);
        if (current is null)
        {
            return null;
        }

        // A reviewed update uses updated_utc as its compare-and-swap witness.
        // Keep it strictly monotonic for one plan even under a fixed/coarse
        // clock, otherwise two writes in the same clock tick could both look
        // like they still match the reviewed snapshot.
        var updatedUtc = now <= current.UpdatedUtc
            ? current.UpdatedUtc.AddTicks(1)
            : now;
        var currentPlanHash = MediaPlanVersionCodec.ComputeHash(MediaPlanSnapshot.From(current));
        if (!string.IsNullOrWhiteSpace(expectedPlanHash)
            && !string.Equals(expectedPlanHash.Trim(), currentPlanHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new MediaPlanVersionConflictException(id, expectedPlanHash.Trim(), currentPlanHash);
        }

        // Plans created before version history was introduced still receive a
        // baseline before their first edit, so the first update is reversible.
        if (await GetLatestMediaPlanVersionAsync(connection, id, cancellationToken, transaction) is null)
        {
            await AppendMediaPlanVersionAsync(connection, current, "baseline", cancellationToken, transaction);
        }

        // A full PUT is allowed to repeat after a caller retries a timed-out
        // request. Do not manufacture a new Media Plan version when the
        // normalized effective snapshot is unchanged: version history is an
        // audit trail of behavior changes, not a log of HTTP retries.
        var requestedQualityProfileId = NormalizeName(request.QualityProfileId);
        var requestedQualityProfile = string.IsNullOrWhiteSpace(requestedQualityProfileId)
            ? null
            : await GetQualityProfileAsync(connection, requestedQualityProfileId, cancellationToken, transaction);
        var proposed = current with
        {
            Name = NormalizeName(request.Name) ?? current.Name,
            MediaType = NormalizeMediaType(request.MediaType ?? current.MediaType),
            QualityProfileId = requestedQualityProfileId,
            QualityProfileName = requestedQualityProfile?.Name,
            DestinationRuleId = NormalizeName(request.DestinationRuleId),
            DestinationRuleName = null,
            CustomFormatIds = NormalizeCsv(request.CustomFormatIds),
            SearchIntervalOverrideHours = NormalizeNullablePositiveValue(request.SearchIntervalOverrideHours),
            RetryDelayOverrideHours = NormalizeNullablePositiveValue(request.RetryDelayOverrideHours),
            UpgradeUntilCutoff = request.UpgradeUntilCutoff,
            IsEnabled = request.IsEnabled,
            Notes = NormalizeName(request.Notes),
            AutomationIntent = MediaPlanAutomationIntentCodec.Normalize(
                request.AutomationIntent ?? current.AutomationIntent),
            ReleasePreferencePlan = requestedQualityProfile?.ReleasePreferencePlan
        };
        if (string.Equals(
                MediaPlanVersionCodec.ComputeHash(MediaPlanSnapshot.From(current)),
                MediaPlanVersionCodec.ComputeHash(MediaPlanSnapshot.From(proposed)),
                StringComparison.OrdinalIgnoreCase))
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
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
                automation_intent_json = @automationIntent,
                updated_utc = @updatedUtc
            WHERE id = @id
              AND (@expectedUpdatedUtc IS NULL OR updated_utc = @expectedUpdatedUtc);
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
        AddParameter(command, "@automationIntent", MediaPlanAutomationIntentCodec.Serialize(
            request.AutomationIntent ?? current.AutomationIntent));
        AddParameter(command, "@updatedUtc", updatedUtc.ToString("O"));
        AddParameter(command, "@expectedUpdatedUtc", string.IsNullOrWhiteSpace(expectedPlanHash)
            ? null
            : current.UpdatedUtc.ToString("O"));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(expectedPlanHash) && affected == 0)
        {
            throw new MediaPlanVersionConflictException(id, expectedPlanHash.Trim(), currentPlanHash);
        }

        var updated = await GetPolicySetAsync(connection, id, cancellationToken, transaction);
        if (updated is not null)
        {
            await AppendMediaPlanVersionAsync(
                connection,
                updated,
                string.IsNullOrWhiteSpace(changeKind) ? "update" : changeKind.Trim().ToLowerInvariant(),
                cancellationToken,
                transaction);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
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

    public async Task<bool> DeletePolicySetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using (var unlink = connection.CreateCommand())
        {
            unlink.CommandText = "UPDATE libraries SET default_policy_set_id = NULL WHERE default_policy_set_id = @id;";
            AddParameter(unlink, "@id", id);
            await unlink.ExecuteNonQueryAsync(cancellationToken);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM policy_sets WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Public because Deluno.Platform's library seeding calls this before a
    /// fresh library can be assigned a default quality profile. Libraries has
    /// not moved out of Platform yet (see ADR-001 Step 1 progress table).
    /// </summary>
    public static async Task EnsureSeedQualityProfilesAsync(
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

    private static async Task<int> GetNextQualityProfileSortOrderAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sort_order), 0) + 1 FROM quality_profiles;";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar ?? 1, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Public for the same reason as <see cref="EnsureSeedQualityProfilesAsync"/> --
    /// Platform's library backfill needs to resolve a default profile id.
    /// </summary>
    public static async Task<string?> ResolveQualityProfileIdAsync(
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

    /// <summary>
    /// Public for the same reason as <see cref="EnsureSeedQualityProfilesAsync"/> --
    /// Platform's library create/update endpoints read back the resolved
    /// profile to embed in the created library.
    /// </summary>
    public static async Task<QualityProfileItem?> GetQualityProfileAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                id, name, media_type, cutoff_quality, allowed_qualities, custom_format_ids,
                upgrade_until_cutoff, upgrade_unknown_items, allow_lower_quality_replacements,
                preset_id, preset_version, release_preference_plan_json, created_utc, updated_utc
            FROM quality_profiles
            WHERE id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadQualityProfile(reader) : null;
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

    /// <summary>
    /// Public for the same reason as <see cref="EnsureSeedQualityProfilesAsync"/> --
    /// Platform's library media-plan endpoints still resolve a policy set
    /// while assigning it to a library.
    /// </summary>
    public static async Task<PolicySetItem?> GetPolicySetAsync(
        System.Data.Common.DbConnection connection,
        string id,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                p.id, p.name, p.media_type,
                p.quality_profile_id, q.name, q.release_preference_plan_json,
                p.destination_rule_id, d.name,
                p.custom_format_ids, p.search_interval_override_hours, p.retry_delay_override_hours,
                p.upgrade_until_cutoff, p.is_enabled, p.notes, p.automation_intent_json, p.created_utc, p.updated_utc
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

    private static async Task<MediaPlanVersionItem?> GetLatestMediaPlanVersionAsync(
        System.Data.Common.DbConnection connection,
        string planId,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT plan_id, version, plan_hash, change_kind, snapshot_json, created_utc FROM media_plan_versions WHERE plan_id = @planId ORDER BY version DESC LIMIT 1;";
        AddParameter(command, "@planId", planId.Trim());

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMediaPlanVersion(reader)
            : null;
    }

    private async Task AppendMediaPlanVersionAsync(
        System.Data.Common.DbConnection connection,
        PolicySetItem item,
        string changeKind,
        CancellationToken cancellationToken,
        System.Data.Common.DbTransaction? transaction = null)
    {
        var snapshot = MediaPlanSnapshot.From(item);
        var json = MediaPlanVersionCodec.Serialize(snapshot);
        var hash = MediaPlanVersionCodec.ComputeHash(snapshot);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO media_plan_versions (plan_id, version, plan_hash, change_kind, snapshot_json, created_utc) "
            + "VALUES (@planId, COALESCE((SELECT MAX(version) + 1 FROM media_plan_versions WHERE plan_id = @planId), 1), @planHash, @changeKind, @snapshotJson, @createdUtc);";
        AddParameter(command, "@planId", item.Id);
        AddParameter(command, "@planHash", hash);
        AddParameter(command, "@changeKind", string.IsNullOrWhiteSpace(changeKind) ? "update" : changeKind.Trim().ToLowerInvariant());
        AddParameter(command, "@snapshotJson", json);
        AddParameter(command, "@createdUtc", item.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string DefaultCutoffForMediaType(string? mediaType)
        => NormalizeMediaType(mediaType) == "tv" ? "WEB 1080p" : "WEB 1080p";

    private static string DefaultAllowedQualities(string? mediaType)
        => NormalizeMediaType(mediaType) == "tv"
            ? "WEB 720p, WEB 1080p, HDTV 1080p"
            : "WEB 1080p, Bluray 1080p, Remux 1080p";

    private static QualityProfileItem ReadQualityProfile(System.Data.Common.DbDataReader reader)
    {
        var presetId = reader.IsDBNull(9) ? null : reader.GetString(9);
        var presetVersion = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10);
        var releasePreferencePlan = reader.IsDBNull(11)
            ? null
            : ReleasePreferencePlanReferenceCodec.Deserialize(reader.GetString(11));

        var presetDrifted = false;
        if (presetId is not null && presetVersion.HasValue)
        {
            var currentPreset = QualityProfilePresetCatalog.FindById(presetId);
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
            CreatedUtc: ParseTimestamp(reader.GetString(12)),
            UpdatedUtc: ParseTimestamp(reader.GetString(13)),
            ReleasePreferencePlan: releasePreferencePlan);
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

    private static PolicySetItem ReadPolicySet(System.Data.Common.DbDataReader reader)
    {
        var releasePreferencePlan = reader.IsDBNull(5)
            ? null
            : ReleasePreferencePlanReferenceCodec.Deserialize(reader.GetString(5));

        return new PolicySetItem(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            MediaType: reader.GetString(2),
            QualityProfileId: reader.IsDBNull(3) ? null : reader.GetString(3),
            QualityProfileName: reader.IsDBNull(4) ? null : reader.GetString(4),
            DestinationRuleId: reader.IsDBNull(6) ? null : reader.GetString(6),
            DestinationRuleName: reader.IsDBNull(7) ? null : reader.GetString(7),
            CustomFormatIds: reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            SearchIntervalOverrideHours: reader.IsDBNull(9) ? null : reader.GetInt32(9),
            RetryDelayOverrideHours: reader.IsDBNull(10) ? null : reader.GetInt32(10),
            UpgradeUntilCutoff: reader.GetInt64(11) == 1,
            IsEnabled: reader.GetInt64(12) == 1,
            Notes: reader.IsDBNull(13) ? null : reader.GetString(13),
            AutomationIntent: MediaPlanAutomationIntentCodec.Deserialize(reader.IsDBNull(14) ? null : reader.GetString(14)),
            CreatedUtc: ParseTimestamp(reader.GetString(15)),
            UpdatedUtc: ParseTimestamp(reader.GetString(16)),
            ReleasePreferencePlan: releasePreferencePlan);
    }

    private static MediaPlanVersionItem ReadMediaPlanVersion(System.Data.Common.DbDataReader reader)
    {
        var planId = reader.GetString(0);
        var version = reader.GetInt32(1);
        var hash = reader.GetString(2);
        var snapshot = MediaPlanVersionCodec.Deserialize(reader.GetString(4));
        var computedHash = MediaPlanVersionCodec.ComputeHash(snapshot);
        if (!string.Equals(hash, computedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Media plan '{planId}' version {version.ToString(CultureInfo.InvariantCulture)} has an invalid content hash.");
        }

        return new MediaPlanVersionItem(
            planId,
            version,
            hash,
            reader.GetString(3),
            snapshot,
            ParseTimestamp(reader.GetString(5)));
    }
}
