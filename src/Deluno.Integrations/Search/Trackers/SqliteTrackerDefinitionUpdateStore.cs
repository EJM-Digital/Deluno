using System.Data.Common;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Integrations.Search.Trackers;

/// <summary>
/// Keeps one local, owner-owned tracker-definition-update preference and its
/// last outcome (#756) -- the same one-row-by-id shape
/// SqliteGuideUpdateCheckStore uses for the TRaSH update check.
/// </summary>
public sealed class SqliteTrackerDefinitionUpdateStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : ITrackerDefinitionUpdateStore
{
    public async Task<TrackerDefinitionUpdateState> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_enabled, interval_hours, last_checked_utc, last_updated_utc, status, error, updated_ids_json, updated_utc FROM tracker_definition_update_state WHERE id = 1;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Read(reader)
            : Default(timeProvider.GetUtcNow());
    }

    public async Task<TrackerDefinitionUpdateState> SetSettingsAsync(bool isEnabled, int intervalHours, CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        var updated = current with
        {
            IsEnabled = isEnabled,
            IntervalHours = Math.Clamp(intervalHours, 1, 168),
            Status = isEnabled
                ? current.LastCheckedUtc is null
                    ? TrackerDefinitionUpdateStatuses.NeverChecked
                    : current.UpdatedTrackerIds.Count > 0
                        ? TrackerDefinitionUpdateStatuses.Updated
                        : TrackerDefinitionUpdateStatuses.UpToDate
                : TrackerDefinitionUpdateStatuses.Disabled,
            Error = isEnabled ? current.Error : null,
            UpdatedUtc = timeProvider.GetUtcNow()
        };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<TrackerDefinitionUpdateState> SaveSuccessAsync(
        DateTimeOffset checkedUtc,
        IReadOnlyList<string> updatedTrackerIds,
        CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        var updated = current with
        {
            LastCheckedUtc = checkedUtc,
            LastUpdatedUtc = updatedTrackerIds.Count > 0 ? checkedUtc : current.LastUpdatedUtc,
            Status = updatedTrackerIds.Count > 0 ? TrackerDefinitionUpdateStatuses.Updated : TrackerDefinitionUpdateStatuses.UpToDate,
            Error = null,
            UpdatedTrackerIds = updatedTrackerIds,
            UpdatedUtc = timeProvider.GetUtcNow()
        };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<TrackerDefinitionUpdateState> SaveFailureAsync(
        string error,
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        var updated = current with
        {
            LastCheckedUtc = checkedUtc,
            Status = TrackerDefinitionUpdateStatuses.Failed,
            Error = error.Trim(),
            UpdatedTrackerIds = [],
            UpdatedUtc = timeProvider.GetUtcNow()
        };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    private async Task SaveAsync(TrackerDefinitionUpdateState state, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO tracker_definition_update_state (id, is_enabled, interval_hours, last_checked_utc, last_updated_utc, status, error, updated_ids_json, updated_utc) "
            + "VALUES (1, @enabled, @intervalHours, @lastCheckedUtc, @lastUpdatedUtc, @status, @error, @updatedIdsJson, @updatedUtc) "
            + "ON CONFLICT(id) DO UPDATE SET is_enabled = excluded.is_enabled, interval_hours = excluded.interval_hours, last_checked_utc = excluded.last_checked_utc, last_updated_utc = excluded.last_updated_utc, status = excluded.status, error = excluded.error, updated_ids_json = excluded.updated_ids_json, updated_utc = excluded.updated_utc;";
        AddParameter(command, "@enabled", state.IsEnabled ? 1 : 0);
        AddParameter(command, "@intervalHours", state.IntervalHours);
        AddParameter(command, "@lastCheckedUtc", state.LastCheckedUtc?.ToString("O"));
        AddParameter(command, "@lastUpdatedUtc", state.LastUpdatedUtc?.ToString("O"));
        AddParameter(command, "@status", state.Status);
        AddParameter(command, "@error", state.Error);
        AddParameter(command, "@updatedIdsJson", JsonSerializer.Serialize(state.UpdatedTrackerIds));
        AddParameter(command, "@updatedUtc", state.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static TrackerDefinitionUpdateState Read(DbDataReader reader)
    {
        var updatedIds = reader.IsDBNull(6)
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(6)) ?? [];
        return new TrackerDefinitionUpdateState(
            reader.GetInt32(0) == 1,
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)),
            reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            updatedIds,
            ParseTimestamp(reader.GetString(7)));
    }

    private static TrackerDefinitionUpdateState Default(DateTimeOffset now)
        => new(false, 24, null, null, TrackerDefinitionUpdateStatuses.Disabled, null, [], now);
}
