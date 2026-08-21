using System.Data.Common;
using System.Globalization;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Quality;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Media;

/// <summary>
/// Shared persistence for the wanted-state and search-history behaviour that
/// is identical between movies and series. The table map is deliberately
/// closed over an enum rather than accepting table names from callers.
///
/// Episode inventory and movie availability remain in their owning engines;
/// this store only owns the common media-level operations.
/// </summary>
public sealed class SqliteMediaStateRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider) : IMediaStateRepository
{
    public async Task<MediaWantedSummary> GetWantedSummaryAsync(
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var items = new List<MediaWantedItem>();
        var totalWanted = 0;
        var missingCount = 0;
        var upgradeCount = 0;
        var waitingCount = 0;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using (var totals = connection.CreateCommand())
        {
            totals.CommandText = $"""
                SELECT
                    COUNT(*),
                    SUM(CASE WHEN wanted_status = 'missing' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN wanted_status = 'upgrade' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN wanted_status = 'waiting' THEN 1 ELSE 0 END)
                FROM {map.WantedTable};
                """;

            using var reader = await totals.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                totalWanted = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                missingCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                upgradeCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                waitingCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {map.EntryAlias}.id, {map.EntryAlias}.title, {map.EntryAlias}.{map.YearColumn}, {map.EntryAlias}.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality,
                w.quality_cutoff_met, w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc,
                w.last_search_result, w.prevent_lower_quality_replacements, w.quality_delta_last_decision, w.updated_utc
            FROM {map.WantedTable} w
            INNER JOIN {map.EntryTable} {map.EntryAlias} ON {map.EntryAlias}.id = w.{map.WantedMediaIdColumn}
            ORDER BY w.updated_utc DESC, {map.EntryAlias}.title ASC
            LIMIT 25;
            """;

        using var recentReader = await command.ExecuteReaderAsync(cancellationToken);
        while (await recentReader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWanted(recentReader));
        }

        return new MediaWantedSummary(totalWanted, missingCount, upgradeCount, waitingCount, items);
    }

    public async Task<IReadOnlyList<MediaWantedItem>> ListEligibleWantedAsync(
        MediaKind kind,
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        var availability = kind == MediaKind.Movie && !ignoreRetryWindow
            ? """
              AND (
                  m.minimum_availability = 'announced'
                  OR (m.minimum_availability = 'inCinemas' AND (
                      m.in_cinemas_date IS NULL AND m.digital_release_date IS NULL AND m.physical_release_date IS NULL
                      OR COALESCE(m.in_cinemas_date, m.digital_release_date, m.physical_release_date) <= @today))
                  OR (m.minimum_availability NOT IN ('announced', 'inCinemas') AND (
                      m.digital_release_date IS NULL AND m.physical_release_date IS NULL
                      OR MIN(COALESCE(m.digital_release_date, m.physical_release_date), COALESCE(m.physical_release_date, m.digital_release_date)) <= @today))
              )
              """
            : string.Empty;
        var monitored = ignoreRetryWindow ? string.Empty : $"AND {map.EntryAlias}.monitored = 1";
        var retry = ignoreRetryWindow
            ? string.Empty
            : "AND (w.next_eligible_search_utc IS NULL OR w.next_eligible_search_utc <= @now)";

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {map.EntryAlias}.id, {map.EntryAlias}.title, {map.EntryAlias}.{map.YearColumn}, {map.EntryAlias}.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality,
                w.quality_cutoff_met, w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc,
                w.last_search_result, w.prevent_lower_quality_replacements, w.quality_delta_last_decision, w.updated_utc
            FROM {map.WantedTable} w
            INNER JOIN {map.EntryTable} {map.EntryAlias} ON {map.EntryAlias}.id = w.{map.WantedMediaIdColumn}
            WHERE w.library_id = @libraryId
              AND w.wanted_status IN ('missing', 'upgrade')
              {monitored}
              {retry}
              {availability}
            ORDER BY
                CASE w.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                COALESCE(w.last_search_utc, w.missing_since_utc, w.updated_utc) ASC,
                {map.EntryAlias}.title ASC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@today", DateOnly.FromDateTime(now.UtcDateTime).ToString("yyyy-MM-dd"));
        AddParameter(command, "@take", Math.Clamp(take, 1, 500));

        var items = new List<MediaWantedItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWanted(reader));
        }

        return items;
    }

    public async Task<int> CountRetryDelayedWantedAsync(
        MediaKind kind,
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM {map.WantedTable} w
            INNER JOIN {map.EntryTable} {map.EntryAlias} ON {map.EntryAlias}.id = w.{map.WantedMediaIdColumn}
            WHERE w.library_id = @libraryId
              AND w.wanted_status IN ('missing', 'upgrade')
              AND {map.EntryAlias}.monitored = 1
              AND w.next_eligible_search_utc IS NOT NULL
              AND w.next_eligible_search_utc > @now;
            """;
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task EnsureWantedStateAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        string wantedStatus,
        string wantedReason,
        bool hasFile,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {map.WantedTable} (
                {map.WantedMediaIdColumn}, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, missing_since_utc, last_search_utc, next_eligible_search_utc,
                last_search_result, updated_utc, prevent_lower_quality_replacements, quality_delta_last_decision
            )
            VALUES (
                @mediaId, @libraryId, @wantedStatus, @wantedReason, @hasFile, @qualityCutoffMet,
                @currentQuality, @targetQuality, @missingSinceUtc, NULL, NULL, NULL, @updatedUtc, 1, 0
            )
            ON CONFLICT({map.WantedMediaIdColumn}, library_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                has_file = excluded.has_file,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@mediaId", mediaId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@wantedStatus", NormalizeWantedStatus(wantedStatus));
        AddParameter(command, "@wantedReason", wantedReason.Trim());
        AddParameter(command, "@hasFile", hasFile ? 1 : 0);
        AddParameter(command, "@qualityCutoffMet", qualityCutoffMet ? 1 : 0);
        AddParameter(command, "@currentQuality", currentQuality);
        AddParameter(command, "@targetQuality", targetQuality);
        AddParameter(command, "@missingSinceUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<bool> DeferWantedSearchAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        DateTimeOffset deferredUntilUtc,
        CancellationToken cancellationToken)
        => UpdateWantedFlagAsync(
            kind,
            mediaId,
            libraryId,
            "next_eligible_search_utc = @deferredUntilUtc, last_search_result = 'Deferred by user.'",
            (command, now) =>
            {
                AddParameter(command, "@deferredUntilUtc", deferredUntilUtc.ToString("O"));
                AddParameter(command, "@updatedUtc", now);
            },
            cancellationToken);

    public Task<bool> SkipNextWantedSearchAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        CancellationToken cancellationToken)
        => UpdateWantedFlagAsync(
            kind,
            mediaId,
            libraryId,
            "skip_next_automation_search = 1, last_search_result = 'Will skip the next scheduled search by user request.'",
            static (command, now) => AddParameter(command, "@updatedUtc", now),
            cancellationToken);

    public Task<bool> ConsumeSkipNextWantedSearchAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        CancellationToken cancellationToken)
        => UpdateWantedFlagAsync(
            kind,
            mediaId,
            libraryId,
            "skip_next_automation_search = 0",
            static (command, now) => AddParameter(command, "@updatedUtc", now),
            cancellationToken,
            "AND skip_next_automation_search = 1");

    public async Task<int> ReevaluateLibraryWantedStateAsync(
        MediaKind kind,
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var items = new List<(string Id, bool HasFile, string? CurrentQuality)>();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {map.WantedMediaIdColumn}, has_file, current_quality
                FROM {map.WantedTable}
                WHERE library_id = @libraryId;
                """;
            AddParameter(command, "@libraryId", libraryId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add((reader.GetString(0), reader.GetInt64(1) == 1, reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var updated = 0;
        foreach (var item in items)
        {
            var decision = MediaDecisionRules.DecideWantedState(new MediaWantedDecisionInput(
                kind == MediaKind.Movie ? "movie" : "tv",
                item.HasFile,
                item.CurrentQuality,
                cutoffQuality,
                upgradeUntilCutoff,
                upgradeUnknownItems));

            using var update = connection.CreateCommand();
            update.CommandText = $"""
                UPDATE {map.WantedTable}
                SET wanted_status = @wantedStatus,
                    wanted_reason = @wantedReason,
                    target_quality = @targetQuality,
                    quality_cutoff_met = @qualityCutoffMet,
                    updated_utc = @updatedUtc
                WHERE {map.WantedMediaIdColumn} = @mediaId
                  AND library_id = @libraryId;
                """;
            AddParameter(update, "@mediaId", item.Id);
            AddParameter(update, "@libraryId", libraryId);
            AddParameter(update, "@wantedStatus", decision.WantedStatus);
            AddParameter(update, "@wantedReason", decision.WantedReason);
            AddParameter(update, "@targetQuality", decision.TargetQuality);
            AddParameter(update, "@qualityCutoffMet", decision.QualityCutoffMet ? 1 : 0);
            AddParameter(update, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
            updated += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return updated;
    }

    public async Task<IReadOnlyList<MediaSearchHistoryItem>> ListSearchHistoryAsync(
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = kind == MediaKind.Movie
            ? $"""
              SELECT id, movie_id, NULL, NULL, NULL, library_id, trigger_kind, outcome,
                     release_name, indexer_name, details_json, created_utc
              FROM {map.HistoryTable}
              ORDER BY created_utc DESC
              LIMIT 20;
              """
            : $"""
              SELECT COALESCE(h.id, ''), COALESCE(h.series_id, ''), h.episode_id,
                     e.season_number, e.episode_number, COALESCE(h.library_id, ''),
                     COALESCE(h.trigger_kind, 'manual'), COALESCE(h.outcome, 'unknown'),
                     h.release_name, h.indexer_name, h.details_json, COALESCE(h.created_utc, '')
              FROM {map.HistoryTable} h
              LEFT JOIN episode_entries e ON e.id = h.episode_id
              ORDER BY h.created_utc DESC
              LIMIT 20;
              """;

        var items = new List<MediaSearchHistoryItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MediaSearchHistoryItem(
                Id: ReadStringOrFallback(reader, 0, Guid.CreateVersion7().ToString("N")),
                MediaId: ReadStringOrFallback(reader, 1, string.Empty),
                EpisodeId: ReadNullableString(reader, 2),
                SeasonNumber: ReadNullableInt(reader, 3),
                EpisodeNumber: ReadNullableInt(reader, 4),
                LibraryId: ReadStringOrFallback(reader, 5, string.Empty),
                TriggerKind: ReadStringOrFallback(reader, 6, "manual"),
                Outcome: ReadStringOrFallback(reader, 7, "unknown"),
                ReleaseName: ReadNullableString(reader, 8),
                IndexerName: ReadNullableString(reader, 9),
                DetailsJson: ReadNullableString(reader, 10),
                CreatedUtc: ReadTimestampOrFallback(reader, 11, DateTimeOffset.UnixEpoch)));
        }

        return items;
    }

    public async Task<MediaDailyMetrics> GetDailyMetricsAsync(
        MediaKind kind,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var from = fromDate.ToString("yyyy-MM-dd");
        var toExclusive = toDate.AddDays(1).ToString("yyyy-MM-dd");
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        var added = await ReadDailyAsync(
            connection,
            $"SELECT substr(created_utc, 1, 10), COUNT(*) FROM {map.EntryTable} WHERE created_utc >= @from AND created_utc < @to GROUP BY substr(created_utc, 1, 10);",
            from,
            toExclusive,
            cancellationToken);
        var before = await ReadScalarAsync(
            connection,
            $"SELECT COUNT(*) FROM {map.EntryTable} WHERE created_utc < @from;",
            from,
            cancellationToken);
        var matched = await ReadDailyAsync(
            connection,
            $"SELECT substr(created_utc, 1, 10), COUNT(*) FROM {map.HistoryTable} WHERE outcome = 'matched' AND created_utc >= @from AND created_utc < @to GROUP BY substr(created_utc, 1, 10);",
            from,
            toExclusive,
            cancellationToken);
        var unmatched = await ReadDailyAsync(
            connection,
            $"SELECT substr(created_utc, 1, 10), COUNT(*) FROM {map.HistoryTable} WHERE outcome <> 'matched' AND created_utc >= @from AND created_utc < @to GROUP BY substr(created_utc, 1, 10);",
            from,
            toExclusive,
            cancellationToken);
        var failures = await ReadDailyAsync(
            connection,
            $"SELECT substr(detected_utc, 1, 10), COUNT(*) FROM {map.RecoveryTable} WHERE detected_utc >= @from AND detected_utc < @to GROUP BY substr(detected_utc, 1, 10);",
            from,
            toExclusive,
            cancellationToken);

        return new MediaDailyMetrics(before, added, matched, unmatched, failures);
    }

    private async Task<bool> UpdateWantedFlagAsync(
        MediaKind kind,
        string mediaId,
        string libraryId,
        string assignments,
        Action<DbCommand, string> addExtraParameters,
        CancellationToken cancellationToken,
        string additionalPredicate = "")
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {map.WantedTable}
            SET {assignments}, updated_utc = @updatedUtc
            WHERE {map.WantedMediaIdColumn} = @mediaId
              AND library_id = @libraryId
              AND wanted_status IN ('missing', 'upgrade')
              {additionalPredicate};
            """;
        AddParameter(command, "@mediaId", mediaId);
        AddParameter(command, "@libraryId", libraryId);
        var now = timeProvider.GetUtcNow().ToString("O");
        addExtraParameters(command, now);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static MediaWantedItem ReadWanted(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7) == 1,
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt64(10) == 1,
            ReadNullableTimestamp(reader, 11),
            ReadNullableTimestamp(reader, 12),
            ReadNullableTimestamp(reader, 13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetInt64(15) == 1,
            reader.IsDBNull(16) ? null : reader.GetInt32(16),
            ParseTimestamp(reader.GetString(17)));

    private static string? ReadNullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string ReadStringOrFallback(DbDataReader reader, int ordinal, string fallback)
        => reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal))
            ? fallback
            : reader.GetString(ordinal);

    private static int? ReadNullableInt(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static DateTimeOffset? ReadNullableTimestamp(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static DateTimeOffset ReadTimestampOrFallback(DbDataReader reader, int ordinal, DateTimeOffset fallback)
        => reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal))
            ? fallback
            : ParseTimestamp(reader.GetString(ordinal));

    private static string NormalizeWantedStatus(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "upgrade" => "upgrade",
            "waiting" => "waiting",
            _ => "missing"
        };

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async Task<IReadOnlyDictionary<string, int>> ReadDailyAsync(
        DbConnection connection,
        string sql,
        string from,
        string toExclusive,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@from", from);
        AddParameter(command, "@to", toExclusive);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                counts[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return counts;
    }

    private static async Task<int> ReadScalarAsync(
        DbConnection connection,
        string sql,
        string from,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@from", from);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
