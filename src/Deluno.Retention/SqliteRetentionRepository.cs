using System.Data.Common;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Retention;

public interface IRetentionRepository
{
    Task<IReadOnlyList<RetentionRule>> ListRulesAsync(CancellationToken cancellationToken);

    Task<RetentionRule?> GetRuleAsync(string id, CancellationToken cancellationToken);

    Task<RetentionRule?> SaveRuleAsync(string? id, SaveRetentionRuleRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes the rule, its marks and the keeps made against it. Its history stays.</summary>
    Task<bool> DeleteRuleAsync(string id, CancellationToken cancellationToken);

    Task MarkCheckedAsync(string ruleId, DateTimeOffset when, CancellationToken cancellationToken);

    Task SaveRunAsync(RetentionRun run, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionRun>> ListRunsAsync(string ruleId, int take, CancellationToken cancellationToken);

    Task<RetentionRun?> GetRunAsync(string runId, CancellationToken cancellationToken);

    /// <param name="ruleId">Null for every rule's marks.</param>
    Task<IReadOnlyList<RetentionMark>> ListMarksAsync(string? ruleId, CancellationToken cancellationToken);

    /// <summary>Marks on a movie or show, and on its seasons and episodes.</summary>
    Task<IReadOnlyList<RetentionMark>> ListMarksForTitleAsync(string entityId, CancellationToken cancellationToken);

    Task UpsertMarkAsync(RetentionMark mark, CancellationToken cancellationToken);

    Task DeleteMarkAsync(string ruleId, string entityId, CancellationToken cancellationToken);

    /// <summary>Every rule's mark on one title: it is gone, or kept from all rules.</summary>
    Task<int> DeleteMarksForEntityAsync(string entityId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionKeep>> ListKeepsAsync(CancellationToken cancellationToken);

    Task<RetentionKeep> SaveKeepAsync(string entityId, string level, string title, string? ruleId, string? note, CancellationToken cancellationToken);

    Task<RetentionKeep?> DeleteKeepAsync(string id, CancellationToken cancellationToken);

    Task SaveEpisodeRemovalAsync(RetentionEpisodeRemoval removal, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionEpisodeRemoval>> ListEpisodeRemovalsAsync(string seriesId, CancellationToken cancellationToken);

    Task<RetentionEpisodeRemoval?> DeleteEpisodeRemovalAsync(string episodeId, CancellationToken cancellationToken);
}

public sealed class SqliteRetentionRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IRetentionRepository
{
    private const string RuleColumns =
        "id, name, media_type, is_enabled, conditions_json, media_server_ids_json, remove_from_servers, created_utc, updated_utc, level, steps_json, wait_days, removal_mode, check_interval_hours, last_checked_utc";

    private const string RunColumns =
        "id, rule_id, rule_name, kind, trigger, preview_run_id, started_utc, finished_utc, matched_count, excluded_count, removed_count, failed_count, bytes, summary";

    private const string MarkColumns =
        "m.rule_id, COALESCE(r.name, 'Deleted rule'), m.entity_id, m.level, m.show_id, m.season_number, m.episode_ids_json, m.title, m.year, m.size_bytes, m.reasons_json, m.first_matched_utc, m.remove_after_utc, m.state, m.state_reason, m.updated_utc";

    private const string KeepColumns =
        "k.id, k.entity_id, k.level, k.title, k.rule_id, r.name, k.note, k.created_utc";

    // ------------------------------------------------------------------ rules
    public async Task<IReadOnlyList<RetentionRule>> ListRulesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RuleColumns} FROM retention_rules ORDER BY name COLLATE NOCASE;";
        var rules = new List<RetentionRule>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rules.Add(ReadRule(reader));
        }

        return rules;
    }

    public async Task<RetentionRule?> GetRuleAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RuleColumns} FROM retention_rules WHERE id = @id;";
        AddParameter(command, "@id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRule(reader) : null;
    }

    public async Task<RetentionRule?> SaveRuleAsync(string? id, SaveRetentionRuleRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToString("O");
        var existing = id is null ? null : await GetRuleAsync(id, cancellationToken);
        if (id is not null && existing is null)
        {
            return null;
        }

        var rowId = existing?.Id ?? Guid.CreateVersion7().ToString("N");
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = existing is null
            ? """
              INSERT INTO retention_rules (id, name, media_type, is_enabled, conditions_json, media_server_ids_json, remove_from_servers, created_utc, updated_utc, level, steps_json, wait_days, removal_mode, check_interval_hours)
              VALUES (@id, @name, @mediaType, @isEnabled, '[]', @servers, @removeFromServers, @now, @now, @level, @steps, @wait, @removal, @interval);
              """
            : """
              UPDATE retention_rules
              SET name = @name, media_type = @mediaType, is_enabled = @isEnabled, conditions_json = '[]', level = @level, steps_json = @steps,
                  media_server_ids_json = @servers, remove_from_servers = @removeFromServers,
                  wait_days = @wait, removal_mode = @removal, check_interval_hours = @interval, updated_utc = @now
              WHERE id = @id;
              """;
        var level = RetentionLevels.Normalize(request.Level, request.MediaType);
        AddParameter(command, "@id", rowId);
        AddParameter(command, "@name", request.Name!.Trim());
        AddParameter(command, "@mediaType", RetentionLevels.MediaType(level));
        AddParameter(command, "@level", level);
        AddParameter(command, "@steps", RetentionStepList.Serialize(request.Steps ?? RetentionStepList.FromConditions(request.Conditions ?? [])));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@servers", JsonSerializer.Serialize((request.MediaServerIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase)));
        AddParameter(command, "@removeFromServers", request.RemoveFromServers ? 1 : 0);
        AddParameter(command, "@wait", Math.Clamp(request.WaitDays ?? RetentionLimits.DefaultWaitDays, RetentionLimits.MinWaitDays, RetentionLimits.MaxWaitDays));
        AddParameter(command, "@removal", RetentionRemovalModes.Normalize(request.RemovalMode));
        AddParameter(command, "@interval", Math.Clamp(request.CheckIntervalHours ?? RetentionLimits.DefaultCheckIntervalHours, RetentionLimits.MinCheckIntervalHours, RetentionLimits.MaxCheckIntervalHours));
        AddParameter(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetRuleAsync(rowId, cancellationToken);
    }

    public async Task<bool> DeleteRuleAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        // History outlives the rule: what a rule removed is still a fact after
        // somebody deletes the rule. What it had marked is not.
        command.CommandText =
            """
            DELETE FROM retention_marks WHERE rule_id = @id;
            DELETE FROM retention_keeps WHERE rule_id = @id;
            DELETE FROM retention_rules WHERE id = @id;
            """;
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task MarkCheckedAsync(string ruleId, DateTimeOffset when, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE retention_rules SET last_checked_utc = @when WHERE id = @id;";
        AddParameter(command, "@id", ruleId);
        AddParameter(command, "@when", when.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------- runs
    public async Task SaveRunAsync(RetentionRun run, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"""
                INSERT OR REPLACE INTO retention_runs ({RunColumns})
                VALUES (@id, @ruleId, @ruleName, @kind, @trigger, @previewRunId, @started, @finished, @matched, @excluded, @removed, @failed, @bytes, @summary);
                """;
            AddParameter(command, "@id", run.Id);
            AddParameter(command, "@ruleId", run.RuleId);
            AddParameter(command, "@ruleName", run.RuleName);
            AddParameter(command, "@kind", run.Kind);
            AddParameter(command, "@trigger", run.Trigger);
            AddParameter(command, "@previewRunId", run.PreviewRunId);
            AddParameter(command, "@started", run.StartedUtc.ToString("O"));
            AddParameter(command, "@finished", run.FinishedUtc?.ToString("O"));
            AddParameter(command, "@matched", run.MatchedCount);
            AddParameter(command, "@excluded", run.ExcludedCount);
            AddParameter(command, "@removed", run.RemovedCount);
            AddParameter(command, "@failed", run.FailedCount);
            AddParameter(command, "@bytes", run.Bytes);
            AddParameter(command, "@summary", run.Summary);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM retention_run_items WHERE run_id = @id;";
            AddParameter(clear, "@id", run.Id);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in run.Items ?? [])
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR REPLACE INTO retention_run_items (run_id, entity_id, media_type, title, year, size_bytes, reasons_json, outcome, message)
                VALUES (@runId, @entityId, @mediaType, @title, @year, @size, @reasons, @outcome, @message);
                """;
            AddParameter(insert, "@runId", run.Id);
            AddParameter(insert, "@entityId", item.EntityId);
            AddParameter(insert, "@mediaType", item.MediaType);
            AddParameter(insert, "@title", item.Title);
            AddParameter(insert, "@year", item.Year);
            AddParameter(insert, "@size", item.SizeBytes);
            AddParameter(insert, "@reasons", JsonSerializer.Serialize(item.Reasons));
            AddParameter(insert, "@outcome", item.Outcome);
            AddParameter(insert, "@message", item.Message);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetentionRun>> ListRunsAsync(string ruleId, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RunColumns} FROM retention_runs WHERE rule_id = @ruleId ORDER BY started_utc DESC LIMIT @take;";
        AddParameter(command, "@ruleId", ruleId);
        AddParameter(command, "@take", Math.Clamp(take, 1, 200));
        var runs = new List<RetentionRun>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public async Task<RetentionRun?> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        RetentionRun? run;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {RunColumns} FROM retention_runs WHERE id = @id;";
            AddParameter(command, "@id", runId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            run = await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
        }

        if (run is null)
        {
            return null;
        }

        var items = new List<RetentionRunItem>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT entity_id, media_type, title, year, size_bytes, reasons_json, outcome, message FROM retention_run_items WHERE run_id = @id ORDER BY title COLLATE NOCASE;";
            AddParameter(command, "@id", runId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new RetentionRunItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        return run with { Items = items };
    }

    // ------------------------------------------------------------------ marks
    public async Task<IReadOnlyList<RetentionMark>> ListMarksAsync(string? ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {MarkColumns}
            FROM retention_marks m LEFT JOIN retention_rules r ON r.id = m.rule_id
            WHERE @ruleId IS NULL OR m.rule_id = @ruleId
            ORDER BY m.remove_after_utc, m.title COLLATE NOCASE;
            """;
        AddParameter(command, "@ruleId", ruleId);
        return await ReadMarksAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<RetentionMark>> ListMarksForTitleAsync(string entityId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {MarkColumns}
            FROM retention_marks m LEFT JOIN retention_rules r ON r.id = m.rule_id
            WHERE m.entity_id = @id OR m.show_id = @id
            ORDER BY m.remove_after_utc, m.title COLLATE NOCASE;
            """;
        AddParameter(command, "@id", entityId);
        return await ReadMarksAsync(command, cancellationToken);
    }

    public async Task UpsertMarkAsync(RetentionMark mark, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO retention_marks (rule_id, entity_id, level, show_id, season_number, episode_ids_json, title, year, size_bytes, reasons_json, first_matched_utc, remove_after_utc, state, state_reason, updated_utc)
            VALUES (@ruleId, @entityId, @level, @showId, @season, @episodes, @title, @year, @size, @reasons, @first, @removeAfter, @state, @stateReason, @updated)
            ON CONFLICT (rule_id, entity_id) DO UPDATE SET
                level = excluded.level, show_id = excluded.show_id, season_number = excluded.season_number,
                episode_ids_json = excluded.episode_ids_json, title = excluded.title, year = excluded.year,
                size_bytes = excluded.size_bytes, reasons_json = excluded.reasons_json,
                remove_after_utc = excluded.remove_after_utc, state = excluded.state,
                state_reason = excluded.state_reason, updated_utc = excluded.updated_utc;
            """;
        AddParameter(command, "@ruleId", mark.RuleId);
        AddParameter(command, "@entityId", mark.EntityId);
        AddParameter(command, "@level", mark.Level);
        AddParameter(command, "@showId", mark.ShowId);
        AddParameter(command, "@season", mark.SeasonNumber);
        AddParameter(command, "@episodes", JsonSerializer.Serialize(mark.EpisodeIds));
        AddParameter(command, "@title", mark.Title);
        AddParameter(command, "@year", mark.Year);
        AddParameter(command, "@size", mark.SizeBytes);
        AddParameter(command, "@reasons", JsonSerializer.Serialize(mark.Reasons));
        AddParameter(command, "@first", mark.FirstMatchedUtc.ToString("O"));
        AddParameter(command, "@removeAfter", mark.RemoveAfterUtc.ToString("O"));
        AddParameter(command, "@state", mark.State);
        AddParameter(command, "@stateReason", mark.StateReason);
        AddParameter(command, "@updated", mark.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteMarkAsync(string ruleId, string entityId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM retention_marks WHERE rule_id = @ruleId AND entity_id = @entityId;";
        AddParameter(command, "@ruleId", ruleId);
        AddParameter(command, "@entityId", entityId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> DeleteMarksForEntityAsync(string entityId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM retention_marks WHERE entity_id = @entityId;";
        AddParameter(command, "@entityId", entityId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ keeps
    public async Task<IReadOnlyList<RetentionKeep>> ListKeepsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {KeepColumns} FROM retention_keeps k LEFT JOIN retention_rules r ON r.id = k.rule_id ORDER BY k.created_utc DESC;";
        var keeps = new List<RetentionKeep>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keeps.Add(ReadKeep(reader));
        }

        return keeps;
    }

    public async Task<RetentionKeep> SaveKeepAsync(string entityId, string level, string title, string? ruleId, string? note, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var id = Guid.CreateVersion7().ToString("N");
        using (var command = connection.CreateCommand())
        {
            // Keeping the same title from the same rule again updates the note.
            command.CommandText =
                """
                INSERT INTO retention_keeps (id, entity_id, level, title, rule_id, note, created_utc)
                VALUES (@id, @entityId, @level, @title, @ruleId, @note, @now)
                ON CONFLICT (entity_id, IFNULL(rule_id, '')) DO UPDATE SET note = excluded.note, title = excluded.title;
                """;
            AddParameter(command, "@id", id);
            AddParameter(command, "@entityId", entityId);
            AddParameter(command, "@level", level);
            AddParameter(command, "@title", title);
            AddParameter(command, "@ruleId", ruleId);
            AddParameter(command, "@note", string.IsNullOrWhiteSpace(note) ? null : note.Trim());
            AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {KeepColumns} FROM retention_keeps k LEFT JOIN retention_rules r ON r.id = k.rule_id WHERE k.entity_id = @entityId AND IFNULL(k.rule_id, '') = @rule;";
            AddParameter(command, "@entityId", entityId);
            AddParameter(command, "@rule", ruleId ?? string.Empty);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return ReadKeep(reader);
        }
    }

    public async Task<RetentionKeep?> DeleteKeepAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        RetentionKeep? keep = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {KeepColumns} FROM retention_keeps k LEFT JOIN retention_rules r ON r.id = k.rule_id WHERE k.id = @id;";
            AddParameter(command, "@id", id);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) keep = ReadKeep(reader);
        }

        if (keep is null)
        {
            return null;
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM retention_keeps WHERE id = @id;";
            AddParameter(command, "@id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return keep;
    }

    // ------------------------------------------------------- episode removals
    public async Task SaveEpisodeRemovalAsync(RetentionEpisodeRemoval removal, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO retention_episode_removals (episode_id, series_id, season_number, episode_number, rule_id, rule_name, reason, removed_utc)
            VALUES (@episodeId, @seriesId, @season, @episode, @ruleId, @ruleName, @reason, @removed);
            """;
        AddParameter(command, "@episodeId", removal.EpisodeId);
        AddParameter(command, "@seriesId", removal.SeriesId);
        AddParameter(command, "@season", removal.SeasonNumber);
        AddParameter(command, "@episode", removal.EpisodeNumber);
        AddParameter(command, "@ruleId", removal.RuleId);
        AddParameter(command, "@ruleName", removal.RuleName);
        AddParameter(command, "@reason", removal.Reason);
        AddParameter(command, "@removed", removal.RemovedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetentionEpisodeRemoval>> ListEpisodeRemovalsAsync(string seriesId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT episode_id, series_id, season_number, episode_number, rule_id, rule_name, reason, removed_utc FROM retention_episode_removals WHERE series_id = @seriesId ORDER BY season_number, episode_number;";
        AddParameter(command, "@seriesId", seriesId);
        var removals = new List<RetentionEpisodeRemoval>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            removals.Add(ReadEpisodeRemoval(reader));
        }

        return removals;
    }

    public async Task<RetentionEpisodeRemoval?> DeleteEpisodeRemovalAsync(string episodeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        RetentionEpisodeRemoval? removal = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT episode_id, series_id, season_number, episode_number, rule_id, rule_name, reason, removed_utc FROM retention_episode_removals WHERE episode_id = @id;";
            AddParameter(command, "@id", episodeId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) removal = ReadEpisodeRemoval(reader);
        }

        if (removal is null)
        {
            return null;
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM retention_episode_removals WHERE episode_id = @id;";
            AddParameter(command, "@id", episodeId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return removal;
    }

    // ---------------------------------------------------------------- reading
    private Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
        => databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken).AsTask();

    private static async Task<IReadOnlyList<RetentionMark>> ReadMarksAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var marks = new List<RetentionMark>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            marks.Add(new RetentionMark(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                JsonSerializer.Deserialize<string[]>(reader.GetString(6)) ?? [],
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                JsonSerializer.Deserialize<string[]>(reader.GetString(10)) ?? [],
                DateTimeOffset.Parse(reader.GetString(11)),
                DateTimeOffset.Parse(reader.GetString(12)),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                DateTimeOffset.Parse(reader.GetString(15))));
        }

        return marks;
    }

    private static RetentionKeep ReadKeep(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)));

    private static RetentionEpisodeRemoval ReadEpisodeRemoval(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)));

    private static RetentionRule ReadRule(DbDataReader reader)
    {
        var mediaType = reader.GetString(2);
        // A rule saved before steps existed keeps its meaning: its flat
        // conditions all had to hold, which is a run of Keep only.
        var steps = reader.IsDBNull(10)
            ? RetentionStepList.FromConditions((JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? []).Select(RetentionFieldCatalog.Upgrade))
            : RetentionStepList.Deserialize(reader.GetString(10));
        return new RetentionRule(
            reader.GetString(0),
            reader.GetString(1),
            RetentionLevels.Normalize(reader.IsDBNull(9) ? null : reader.GetString(9), mediaType),
            reader.GetInt32(3) == 1,
            steps,
            JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
            reader.GetInt32(6) == 1,
            reader.GetInt32(11),
            RetentionRemovalModes.Normalize(reader.GetString(12)),
            reader.GetInt32(13),
            reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14)),
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)));
    }

    private static RetentionRun ReadRun(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            reader.GetInt64(12),
            reader.GetString(13));
}
