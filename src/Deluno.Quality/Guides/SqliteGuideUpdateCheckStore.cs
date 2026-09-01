using System.Data.Common;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Quality.ReleasePreferences;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Quality.Guides;

/// <summary>
/// Keeps one local, owner-owned guide-check preference and its last report.
/// This table records observations only; guide package versions remain the
/// separate preview-and-apply workflow.
/// </summary>
public sealed class SqliteGuideUpdateCheckStore(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IGuideUpdateCheckStore
{
    private static readonly JsonSerializerOptions JsonOptions = ReleasePreferenceJson.Options;

    public async Task<GuideUpdateCheckState> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_enabled, last_checked_utc, last_seen_revision, status, error, report_json, updated_utc FROM guide_update_check_state WHERE id = 1;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Read(reader)
            : Default(timeProvider.GetUtcNow());
    }

    public async Task<GuideUpdateCheckState> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        var updated = current with
        {
            IsEnabled = isEnabled,
            Status = isEnabled
                ? current.LastCheckedUtc is null
                    ? GuideUpdateCheckStatuses.NeverChecked
                    : current.Report is { Changes.Count: 0, AddedSources.Count: 0 }
                        ? GuideUpdateCheckStatuses.UpToDate
                        : GuideUpdateCheckStatuses.UpdateAvailable
                : GuideUpdateCheckStatuses.Disabled,
            Error = isEnabled ? current.Error : null,
            UpdatedUtc = timeProvider.GetUtcNow()
        };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<GuideUpdateCheckState> SaveSuccessAsync(
        GuideUpdateCheckReport report,
        CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        var updated = current with
        {
            LastCheckedUtc = report.CheckedUtc,
            LastSeenRevision = report.RemoteRevision,
            Status = report.Changes.Count == 0 && report.AddedSources.Count == 0
                ? GuideUpdateCheckStatuses.UpToDate
                : GuideUpdateCheckStatuses.UpdateAvailable,
            Error = null,
            Report = report,
            UpdatedUtc = timeProvider.GetUtcNow()
        };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<GuideUpdateCheckState> SaveFailureAsync(
        string error,
        DateTimeOffset checkedUtc,
        CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        var updated = current with
        {
            LastCheckedUtc = checkedUtc,
            Status = GuideUpdateCheckStatuses.Failed,
            Error = error.Trim(),
            UpdatedUtc = timeProvider.GetUtcNow()
        };
        await SaveAsync(updated, cancellationToken);
        return updated;
    }

    private async Task SaveAsync(GuideUpdateCheckState state, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Platform,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO guide_update_check_state (id, is_enabled, last_checked_utc, last_seen_revision, status, error, report_json, updated_utc) "
            + "VALUES (1, @enabled, @lastCheckedUtc, @lastSeenRevision, @status, @error, @reportJson, @updatedUtc) "
            + "ON CONFLICT(id) DO UPDATE SET is_enabled = excluded.is_enabled, last_checked_utc = excluded.last_checked_utc, last_seen_revision = excluded.last_seen_revision, status = excluded.status, error = excluded.error, report_json = excluded.report_json, updated_utc = excluded.updated_utc;";
        AddParameter(command, "@enabled", state.IsEnabled ? 1 : 0);
        AddParameter(command, "@lastCheckedUtc", state.LastCheckedUtc?.ToString("O"));
        AddParameter(command, "@lastSeenRevision", state.LastSeenRevision);
        AddParameter(command, "@status", state.Status);
        AddParameter(command, "@error", state.Error);
        AddParameter(command, "@reportJson", state.Report is null ? null : JsonSerializer.Serialize(state.Report, JsonOptions));
        AddParameter(command, "@updatedUtc", state.UpdatedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static GuideUpdateCheckState Read(DbDataReader reader)
    {
        var report = reader.IsDBNull(5)
            ? null
            : JsonSerializer.Deserialize<GuideUpdateCheckReport>(reader.GetString(5), JsonOptions);
        return new GuideUpdateCheckState(
            reader.GetInt32(0) == 1,
            reader.IsDBNull(1) ? null : ParseTimestamp(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            report,
            ParseTimestamp(reader.GetString(6)));
    }

    private static GuideUpdateCheckState Default(DateTimeOffset now)
        => new(false, null, null, GuideUpdateCheckStatuses.Disabled, null, null, now);
}
