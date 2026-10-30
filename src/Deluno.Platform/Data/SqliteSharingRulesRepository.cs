using Deluno.Infrastructure.Storage;
using Deluno.Platform.Contracts;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Platform.Data;

public sealed class SqliteSharingRulesRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : ISharingRulesRepository, ISharingActivityTracker
{
    public async Task<IReadOnlyList<SharingRuleItem>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var items = new List<SharingRuleItem>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, priority, category_pattern, tracker_pattern, tags_csv, tag_match_mode,
                mode, for_hours, until_ratio, stuck_action, stuck_after_days, completion_action,
                min_seeders, max_inactive_days, is_enabled, created_utc, updated_utc
            FROM sharing_rules
            ORDER BY priority ASC, created_utc ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
        }

        return items;
    }

    public async Task<SharingRuleItem> CreateAsync(CreateSharingRuleRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var priority = request.Priority is >= 0 ? request.Priority.Value : await NextPriorityAsync(connection, cancellationToken);
        var item = new SharingRuleItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Priority: priority,
            CategoryPattern: NormalizeBlankToNull(request.CategoryPattern),
            TrackerPattern: NormalizeBlankToNull(request.TrackerPattern),
            TagsCsv: NormalizeBlankToNull(request.TagsCsv),
            TagMatchMode: SharingRuleItem.NormalizeTagMatchMode(request.TagMatchMode),
            Mode: NormalizeBlankToNull(request.Mode),
            ForHours: request.ForHours,
            UntilRatio: request.UntilRatio,
            StuckAction: NormalizeBlankToNull(request.StuckAction),
            StuckAfterDays: request.StuckAfterDays,
            CompletionAction: NormalizeBlankToNull(request.CompletionAction),
            MinSeeders: request.MinSeeders,
            MaxInactiveDays: request.MaxInactiveDays,
            IsEnabled: request.IsEnabled,
            CreatedUtc: now,
            UpdatedUtc: now);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sharing_rules (
                id, priority, category_pattern, tracker_pattern, tags_csv, tag_match_mode,
                mode, for_hours, until_ratio, stuck_action, stuck_after_days, completion_action,
                min_seeders, max_inactive_days, is_enabled, created_utc, updated_utc
            ) VALUES (
                @id, @priority, @categoryPattern, @trackerPattern, @tagsCsv, @tagMatchMode,
                @mode, @forHours, @untilRatio, @stuckAction, @stuckAfterDays, @completionAction,
                @minSeeders, @maxInactiveDays, @isEnabled, @createdUtc, @updatedUtc
            );
            """;
        AddRuleParameters(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<SharingRuleItem?> UpdateAsync(string id, UpdateSharingRuleRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        var existing = (await ListAsync(cancellationToken)).FirstOrDefault(rule => string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return null;
        }

        var clearPolicy = request.ClearPolicy == true;
        var updated = existing with
        {
            Priority = request.Priority ?? existing.Priority,
            CategoryPattern = request.ClearCategoryPattern == true ? null : NormalizeBlankToNull(request.CategoryPattern) ?? existing.CategoryPattern,
            TrackerPattern = request.ClearTrackerPattern == true ? null : NormalizeBlankToNull(request.TrackerPattern) ?? existing.TrackerPattern,
            TagsCsv = request.ClearTags == true ? null : NormalizeBlankToNull(request.TagsCsv) ?? existing.TagsCsv,
            TagMatchMode = request.TagMatchMode is null ? existing.TagMatchMode : SharingRuleItem.NormalizeTagMatchMode(request.TagMatchMode),
            Mode = clearPolicy ? null : NormalizeBlankToNull(request.Mode) ?? existing.Mode,
            ForHours = clearPolicy ? null : request.ForHours ?? existing.ForHours,
            UntilRatio = clearPolicy ? null : request.UntilRatio ?? existing.UntilRatio,
            StuckAction = clearPolicy ? null : NormalizeBlankToNull(request.StuckAction) ?? existing.StuckAction,
            StuckAfterDays = clearPolicy ? null : request.StuckAfterDays ?? existing.StuckAfterDays,
            CompletionAction = clearPolicy ? null : NormalizeBlankToNull(request.CompletionAction) ?? existing.CompletionAction,
            MinSeeders = request.ClearMinSeeders == true ? null : request.MinSeeders ?? existing.MinSeeders,
            MaxInactiveDays = request.ClearMaxInactiveDays == true ? null : request.MaxInactiveDays ?? existing.MaxInactiveDays,
            IsEnabled = request.IsEnabled ?? existing.IsEnabled,
            UpdatedUtc = timeProvider.GetUtcNow()
        };

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE sharing_rules SET
                priority = @priority,
                category_pattern = @categoryPattern,
                tracker_pattern = @trackerPattern,
                tags_csv = @tagsCsv,
                tag_match_mode = @tagMatchMode,
                mode = @mode,
                for_hours = @forHours,
                until_ratio = @untilRatio,
                stuck_action = @stuckAction,
                stuck_after_days = @stuckAfterDays,
                completion_action = @completionAction,
                min_seeders = @minSeeders,
                max_inactive_days = @maxInactiveDays,
                is_enabled = @isEnabled,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddRuleParameters(command, updated);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return updated;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sharing_rules WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> RecordAndGetInactiveDaysAsync(
        string clientId,
        string queueItemId,
        bool isActiveNow,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = "SELECT last_active_utc FROM sharing_activity_tracking WHERE client_id = @clientId AND queue_item_id = @queueItemId;";
        AddParameter(readCommand, "@clientId", clientId);
        AddParameter(readCommand, "@queueItemId", queueItemId);
        var existingRaw = (string?)await readCommand.ExecuteScalarAsync(cancellationToken);

        // Active now, or never observed before: reset the clock. A download
        // just seen for the first time is not yet "inactive" — there is
        // nothing earlier to compare it to.
        var lastActiveUtc = isActiveNow || existingRaw is null
            ? nowUtc
            : ParseTimestamp(existingRaw);

        using var writeCommand = connection.CreateCommand();
        writeCommand.CommandText =
            """
            INSERT INTO sharing_activity_tracking (client_id, queue_item_id, last_active_utc, updated_utc)
            VALUES (@clientId, @queueItemId, @lastActiveUtc, @updatedUtc)
            ON CONFLICT (client_id, queue_item_id) DO UPDATE SET
                last_active_utc = excluded.last_active_utc,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(writeCommand, "@clientId", clientId);
        AddParameter(writeCommand, "@queueItemId", queueItemId);
        AddParameter(writeCommand, "@lastActiveUtc", lastActiveUtc.ToString("O"));
        AddParameter(writeCommand, "@updatedUtc", nowUtc.ToString("O"));
        await writeCommand.ExecuteNonQueryAsync(cancellationToken);

        return Math.Max(0, (int)(nowUtc - lastActiveUtc).TotalDays);
    }

    private async Task<int> NextPriorityAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(priority), 0) + 10 FROM sharing_rules;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static void AddRuleParameters(System.Data.Common.DbCommand command, SharingRuleItem item)
    {
        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@priority", item.Priority);
        AddParameter(command, "@categoryPattern", item.CategoryPattern);
        AddParameter(command, "@trackerPattern", item.TrackerPattern);
        AddParameter(command, "@tagsCsv", item.TagsCsv);
        AddParameter(command, "@tagMatchMode", item.TagMatchMode);
        AddParameter(command, "@mode", item.Mode);
        AddParameter(command, "@forHours", item.ForHours);
        AddParameter(command, "@untilRatio", item.UntilRatio);
        AddParameter(command, "@stuckAction", item.StuckAction);
        AddParameter(command, "@stuckAfterDays", item.StuckAfterDays);
        AddParameter(command, "@completionAction", item.CompletionAction);
        AddParameter(command, "@minSeeders", item.MinSeeders);
        AddParameter(command, "@maxInactiveDays", item.MaxInactiveDays);
        AddParameter(command, "@isEnabled", item.IsEnabled ? 1 : 0);
        AddParameter(command, "@createdUtc", item.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", item.UpdatedUtc.ToString("O"));
    }

    private static SharingRuleItem Read(System.Data.Common.DbDataReader reader) => new(
        Id: reader.GetString(0),
        Priority: reader.GetInt32(1),
        CategoryPattern: reader.IsDBNull(2) ? null : reader.GetString(2),
        TrackerPattern: reader.IsDBNull(3) ? null : reader.GetString(3),
        TagsCsv: reader.IsDBNull(4) ? null : reader.GetString(4),
        TagMatchMode: reader.GetString(5),
        Mode: reader.IsDBNull(6) ? null : reader.GetString(6),
        ForHours: reader.IsDBNull(7) ? null : reader.GetInt32(7),
        UntilRatio: reader.IsDBNull(8) ? null : reader.GetDouble(8),
        StuckAction: reader.IsDBNull(9) ? null : reader.GetString(9),
        StuckAfterDays: reader.IsDBNull(10) ? null : reader.GetInt32(10),
        CompletionAction: reader.IsDBNull(11) ? null : reader.GetString(11),
        MinSeeders: reader.IsDBNull(12) ? null : reader.GetInt32(12),
        MaxInactiveDays: reader.IsDBNull(13) ? null : reader.GetInt32(13),
        IsEnabled: reader.GetInt64(14) == 1,
        CreatedUtc: ParseTimestamp(reader.GetString(15)),
        UpdatedUtc: ParseTimestamp(reader.GetString(16)));

    private static string? NormalizeBlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
