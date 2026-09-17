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

    Task<bool> DeleteRuleAsync(string id, CancellationToken cancellationToken);

    Task MarkScheduledRunAsync(string ruleId, DateTimeOffset when, CancellationToken cancellationToken);

    Task SaveRunAsync(RetentionRun run, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionRun>> ListRunsAsync(string ruleId, int take, CancellationToken cancellationToken);

    Task<RetentionRun?> GetRunAsync(string runId, CancellationToken cancellationToken);
}

public sealed class SqliteRetentionRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IRetentionRepository
{
    private const string RuleColumns =
        "id, name, media_type, is_enabled, conditions_json, media_server_ids_json, remove_from_servers, schedule_mode, schedule_interval_hours, last_scheduled_run_utc, created_utc, updated_utc, level, steps_json";

    private const string RunColumns =
        "id, rule_id, rule_name, kind, trigger, preview_run_id, started_utc, finished_utc, matched_count, excluded_count, removed_count, failed_count, bytes, summary";

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
              INSERT INTO retention_rules (id, name, media_type, is_enabled, conditions_json, media_server_ids_json, remove_from_servers, schedule_mode, schedule_interval_hours, created_utc, updated_utc, level, steps_json)
              VALUES (@id, @name, @mediaType, @isEnabled, '[]', @servers, @removeFromServers, @scheduleMode, @interval, @now, @now, @level, @steps);
              """
            : """
              UPDATE retention_rules
              SET name = @name, media_type = @mediaType, is_enabled = @isEnabled, conditions_json = '[]', level = @level, steps_json = @steps,
                  media_server_ids_json = @servers, remove_from_servers = @removeFromServers,
                  schedule_mode = @scheduleMode, schedule_interval_hours = @interval, updated_utc = @now
              WHERE id = @id;
              """;
        AddParameter(command, "@id", rowId);
        AddParameter(command, "@name", request.Name!.Trim());
        var level = RetentionLevels.Normalize(request.Level, request.MediaType);
        AddParameter(command, "@mediaType", RetentionLevels.MediaType(level));
        AddParameter(command, "@level", level);
        AddParameter(command, "@steps", RetentionStepList.Serialize(request.Steps ?? RetentionStepList.FromConditions(request.Conditions ?? [])));
        AddParameter(command, "@isEnabled", request.IsEnabled ? 1 : 0);
        AddParameter(command, "@servers", JsonSerializer.Serialize((request.MediaServerIds ?? []).Distinct(StringComparer.OrdinalIgnoreCase)));
        AddParameter(command, "@removeFromServers", request.RemoveFromServers ? 1 : 0);
        AddParameter(command, "@scheduleMode", RetentionScheduleModes.Normalize(request.ScheduleMode));
        AddParameter(command, "@interval", Math.Clamp(request.ScheduleIntervalHours ?? 24, 1, 24 * 30));
        AddParameter(command, "@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetRuleAsync(rowId, cancellationToken);
    }

    public async Task<bool> DeleteRuleAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        // History outlives the rule: what a rule removed is still a fact after
        // somebody deletes the rule.
        command.CommandText = "DELETE FROM retention_rules WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task MarkScheduledRunAsync(string ruleId, DateTimeOffset when, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE retention_rules SET last_scheduled_run_utc = @when WHERE id = @id;";
        AddParameter(command, "@id", ruleId);
        AddParameter(command, "@when", when.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

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

    private Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
        => databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Platform, cancellationToken).AsTask();

    private static RetentionRule ReadRule(DbDataReader reader)
    {
        var mediaType = reader.GetString(2);
        // A rule saved before steps existed keeps its meaning: its flat
        // conditions all had to hold, which is a run of Keep only.
        var steps = reader.IsDBNull(13)
            ? RetentionStepList.FromConditions((JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? []).Select(RetentionFieldCatalog.Upgrade))
            : RetentionStepList.Deserialize(reader.GetString(13));
        return new RetentionRule(
            reader.GetString(0),
            reader.GetString(1),
            mediaType,
            reader.GetInt32(3) == 1,
            JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
            reader.GetInt32(6) == 1,
            RetentionScheduleModes.Normalize(reader.GetString(7)),
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            DateTimeOffset.Parse(reader.GetString(10)),
            DateTimeOffset.Parse(reader.GetString(11)),
            RetentionLevels.Normalize(reader.IsDBNull(12) ? null : reader.GetString(12), mediaType),
            steps);
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
