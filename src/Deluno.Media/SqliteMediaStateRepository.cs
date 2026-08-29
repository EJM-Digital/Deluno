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
        var coveredCount = 0;
        var upcomingCount = 0;

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
                    SUM(CASE WHEN wanted_status = 'covered' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN wanted_status = 'upcoming' THEN 1 ELSE 0 END)
                FROM {map.WantedTable};
                """;

            using var reader = await totals.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                totalWanted = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                missingCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                upgradeCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                coveredCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                upcomingCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
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

        return new MediaWantedSummary(totalWanted, missingCount, upgradeCount, coveredCount, upcomingCount, items);
    }

    public async Task<IReadOnlyList<MediaWantedItem>> ListWantedByIdsAsync(
        MediaKind kind,
        IReadOnlyList<string> mediaIds,
        CancellationToken cancellationToken)
    {
        var ids = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);
        var items = new List<MediaWantedItem>(ids.Length);

        foreach (var chunk in ids.Chunk(400))
        {
            using var command = connection.CreateCommand();
            var parameters = new string[chunk.Length];
            for (var index = 0; index < chunk.Length; index++)
            {
                parameters[index] = $"@mediaId{index}";
                AddParameter(command, parameters[index], chunk[index]);
            }

            command.CommandText = $"""
                SELECT
                    {map.EntryAlias}.id, {map.EntryAlias}.title, {map.EntryAlias}.{map.YearColumn}, {map.EntryAlias}.imdb_id,
                    w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality,
                    w.quality_cutoff_met, w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc,
                    w.last_search_result, w.prevent_lower_quality_replacements, w.quality_delta_last_decision, w.updated_utc
                FROM {map.WantedTable} w
                INNER JOIN {map.EntryTable} {map.EntryAlias} ON {map.EntryAlias}.id = w.{map.WantedMediaIdColumn}
                WHERE w.{map.WantedMediaIdColumn} IN ({string.Join(", ", parameters)})
                ORDER BY {map.EntryAlias}.title ASC, {map.EntryAlias}.id ASC;
                """;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadWanted(reader));
            }
        }

        return items;
    }

    public async Task<IReadOnlyList<MediaWantedItem>> ListEligibleWantedAsync(
        MediaKind kind,
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken,
        string? wantedStatus = null)
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
        // Built from WantedStatuses.Searchable rather than spelled out, because
        // this used to read IN ('missing', 'upgrade') — the same rule as
        // IsSearchable, written a second time in a language that could not check
        // itself against the first. Adding a status to one and not the other
        // would have been silent, and silent in the bad direction: a title
        // nobody ever searches for again.
        var searchable = string.Join(", ", WantedStatuses.Searchable.Select(status => $"'{status}'"));

        // And the safety net. A download that never ends would otherwise hold a
        // title off the work list for ever, because nothing fails and nothing is
        // logged — the shape of the two worst defects this project has had. Past
        // StuckDownloadAfter, Deluno stops believing the download is still
        // happening and looks again.
        var stuckDownload =
            $"OR (w.wanted_status = '{WantedStatuses.Downloading}' " +
            "AND (w.downloading_since_utc IS NULL OR w.downloading_since_utc <= @downloadStale))";

        var statusFilter = string.IsNullOrWhiteSpace(wantedStatus)
            ? $"AND (w.wanted_status IN ({searchable}) {stuckDownload})"
            : "AND w.wanted_status = @wantedStatus";

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
              {statusFilter}
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
        AddParameter(command, "@downloadStale", now.Subtract(WantedStatuses.StuckDownloadAfter).ToString("O"));
        if (!string.IsNullOrWhiteSpace(wantedStatus))
        {
            AddParameter(command, "@wantedStatus", WantedStatuses.Normalize(wantedStatus));
        }

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
        CancellationToken cancellationToken,
        string? wantedStatus = null)
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
              AND (@wantedStatus IS NULL OR w.wanted_status = @wantedStatus)
              AND {map.EntryAlias}.monitored = 1
              AND w.next_eligible_search_utc IS NOT NULL
              AND w.next_eligible_search_utc > @now;
            """;
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@wantedStatus", string.IsNullOrWhiteSpace(wantedStatus) ? null : WantedStatuses.Normalize(wantedStatus));
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
        AddParameter(command, "@wantedStatus", WantedStatuses.Normalize(wantedStatus));
        AddParameter(command, "@wantedReason", wantedReason.Trim());
        AddParameter(command, "@hasFile", hasFile ? 1 : 0);
        AddParameter(command, "@qualityCutoffMet", qualityCutoffMet ? 1 : 0);
        AddParameter(command, "@currentQuality", currentQuality);
        AddParameter(command, "@targetQuality", targetQuality);
        AddParameter(command, "@missingSinceUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListDownloadingAsync(
        MediaKind kind,
        DateTimeOffset settledBefore,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT {map.WantedMediaIdColumn}
            FROM {map.WantedTable}
            WHERE wanted_status = @status
              AND (downloading_since_utc IS NULL OR downloading_since_utc <= @settledBefore);
            """;

        AddParameter(command, "@status", WantedStatuses.Downloading);
        AddParameter(command, "@settledBefore", settledBefore.ToString("O"));

        var ids = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public async Task SetDownloadingAsync(
        MediaKind kind,
        string mediaId,
        bool downloading,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();

        // Clearing is scoped to rows this actually set. A title that was
        // imported while the download was in flight has already had its status
        // rewritten by the import, and overwriting that with `missing` would
        // take a finished film off the shelf.
        command.CommandText = downloading
            ? $"""
                UPDATE {map.WantedTable}
                SET wanted_status = @status,
                    downloading_since_utc = @now,
                    updated_utc = @now
                WHERE {map.WantedMediaIdColumn} = @mediaId;
                """
            : $"""
                UPDATE {map.WantedTable}
                SET wanted_status = @status,
                    downloading_since_utc = NULL,
                    updated_utc = @now
                WHERE {map.WantedMediaIdColumn} = @mediaId
                  AND wanted_status = @downloadingStatus;
                """;

        AddParameter(command, "@mediaId", mediaId);
        AddParameter(command, "@status", downloading ? WantedStatuses.Downloading : WantedStatuses.Missing);
        AddParameter(command, "@now", now.ToString("O"));
        if (!downloading)
        {
            AddParameter(command, "@downloadingStatus", WantedStatuses.Downloading);
        }

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

    /// <summary>
    /// Whether a title is out yet, from the columns
    /// <see cref="MediaTableMap.ReleaseColumns"/> selects.
    ///
    /// A movie asks <see cref="MovieAvailability"/>, which is the same rule the
    /// catalogue uses to decide whether to search at all. A show is out once any
    /// episode has aired; with no aired episode and no air dates on record it
    /// counts as out, because refusing to search an unsynced show would be
    /// worse than searching one too early.
    /// </summary>
    private static bool IsReleased(MediaKind kind, System.Data.Common.DbDataReader reader, DateOnly today)
    {
        if (kind == MediaKind.Movie)
        {
            return MovieAvailability.IsAvailable(
                reader.IsDBNull(6) ? null : reader.GetString(6),
                ReadDateOnly(reader, 3),
                ReadDateOnly(reader, 4),
                ReadDateOnly(reader, 5),
                today);
        }

        var earliestAirDate = reader.IsDBNull(7) ? null : reader.GetString(7);
        if (earliestAirDate is null)
        {
            return true;
        }

        return DateTimeOffset.TryParse(
            earliestAirDate,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var aired)
            ? DateOnly.FromDateTime(aired.UtcDateTime) <= today
            : true;
    }

    private static DateOnly? ReadDateOnly(System.Data.Common.DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = reader.GetString(ordinal);
        return DateOnly.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    public async Task<int> ReevaluateLibraryWantedStateAsync(
        MediaKind kind,
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var items = new List<(string Id, bool HasFile, string? CurrentQuality, bool IsReleased)>();
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT w.{map.WantedMediaIdColumn}, w.has_file, w.current_quality,
                       {map.ReleaseColumns}
                FROM {map.WantedTable} w
                {map.ReleaseJoin}
                WHERE w.library_id = @libraryId;
                """;
            AddParameter(command, "@libraryId", libraryId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add((
                    reader.GetString(0),
                    reader.GetInt64(1) == 1,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    IsReleased(kind, reader, today)));
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
                upgradeUnknownItems,
                IsReleased: item.IsReleased));

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

    public async Task<MediaImportRecoverySummary> GetImportRecoverySummaryAsync(
        MediaKind kind,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM {map.RecoveryTable} WHERE status = 'open';";
        var openCount = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                id,
                title,
                failure_kind,
                status,
                summary,
                recommended_action,
                details_json,
                detected_utc,
                resolved_utc
            FROM {map.RecoveryTable}
            WHERE status = 'open'
            ORDER BY detected_utc DESC
            LIMIT 12;
            """;

        var cases = new List<MediaImportRecoveryCase>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cases.Add(new MediaImportRecoveryCase(
                Id: reader.GetString(0),
                Title: reader.GetString(1),
                FailureKind: reader.GetString(2),
                Status: reader.GetString(3),
                Summary: reader.GetString(4),
                RecommendedAction: reader.GetString(5),
                DetailsJson: reader.IsDBNull(6) ? null : reader.GetString(6),
                DetectedUtc: ParseTimestamp(reader.GetString(7)),
                ResolvedUtc: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8))));
        }

        return new MediaImportRecoverySummary(
            OpenCount: openCount,
            QualityCount: cases.Count(item => item.FailureKind == "quality"),
            UnmatchedCount: cases.Count(item => item.FailureKind == "unmatched"),
            CorruptCount: cases.Count(item => item.FailureKind == "corrupt"),
            DownloadFailedCount: cases.Count(item => item.FailureKind == "downloadFailed"),
            ImportFailedCount: cases.Count(item => item.FailureKind == "importFailed"),
            RecentCases: cases);
    }

    /// <summary>
    /// One <c>SET</c> line per rating column, generated from
    /// <see cref="RatingSources.All"/>.
    ///
    /// <para>Written out by hand this is eight near-identical lines that have
    /// to agree with eight near-identical parameters and two migrations. That
    /// is the shape that let <c>network</c> sit unwritten for four versions
    /// while the filter over it politely returned nothing. Generating both ends
    /// from the same list means a fifth source cannot be half-added.</para>
    /// </summary>
    private static string RatingAssignments()
    {
        var lines = new List<string>();

        foreach (var source in RatingSources.All)
        {
            lines.Add($"{source.ScoreColumn} = COALESCE(@{source.ScoreColumn}, {source.ScoreColumn})");
            if (source.VotesColumn is not null)
            {
                lines.Add($"{source.VotesColumn} = COALESCE(@{source.VotesColumn}, {source.VotesColumn})");
            }
        }

        return string.Join("," + Environment.NewLine + "                ", lines) + ",";
    }

    /// <summary>
    /// The probe's answer about the file, onto the row that holds that file.
    ///
    /// <para>COALESCE on every column: a probe that could not read the audio
    /// must not erase what the release name already said. The two sources land
    /// in one vocabulary — see <c>MediaProbedFacts</c> — so a filter matches
    /// whichever one supplied the value.</para>
    ///
    /// <para>Nothing here recomputes the cached entry columns. V0025's trigger
    /// on this table does that, which is the whole reason it is a trigger: this
    /// write did not exist when the trigger was written and did not have to
    /// know about it.</para>
    /// </summary>
    public async Task UpdateProbedFileFactsAsync(
        MediaKind kind,
        string mediaId,
        string filePath,
        ProbedFileFacts facts,
        CancellationToken cancellationToken)
    {
        if (facts.VideoCodec is null && facts.AudioCodec is null && facts.AudioChannels is null)
        {
            return;
        }

        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {map.WantedTable}
            SET video_codec = COALESCE(@videoCodec, video_codec),
                audio_codec = COALESCE(@audioCodec, audio_codec),
                audio_channels = COALESCE(@audioChannels, audio_channels),
                updated_utc = @updatedUtc
            WHERE {map.WantedMediaIdColumn} = @mediaId
              AND file_path = @filePath;
            """;

        AddParameter(command, "@mediaId", mediaId);
        AddParameter(command, "@filePath", filePath);
        AddParameter(command, "@videoCodec", NormalizeText(facts.VideoCodec));
        AddParameter(command, "@audioCodec", NormalizeText(facts.AudioCodec));
        AddParameter(command, "@audioChannels", NormalizeText(facts.AudioChannels));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UpdateMetadataAsync(
        MediaKind kind,
        MediaMetadataUpdate update,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var now = timeProvider.GetUtcNow().ToString("O");
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {map.EntryTable}
            SET
                imdb_id = COALESCE(@imdbId, imdb_id),
                metadata_provider = @metadataProvider,
                metadata_provider_id = @metadataProviderId,
                original_title = @originalTitle,
                overview = @overview,
                poster_url = @posterUrl,
                backdrop_url = @backdropUrl,
                rating = @rating,
                genres = @genres,
                external_url = @externalUrl,
                metadata_json = @metadataJson,
                runtime_minutes = COALESCE(@runtimeMinutes, runtime_minutes),
                popularity = COALESCE(@popularity, popularity),
                vote_count = COALESCE(@voteCount, vote_count),
                -- COALESCE, like the three above: a provider that does not
                -- answer for one of these must not blank what an earlier one
                -- did.
                status = COALESCE(@status, status),
                {map.MadeByColumn} = COALESCE(@madeBy, {map.MadeByColumn}),
                certification = COALESCE(@certification, certification),
                collection = COALESCE(@collection, collection),
                original_language = COALESCE(@originalLanguage, original_language),
                keywords = COALESCE(@keywords, keywords),
                {RatingAssignments()}
                metadata_updated_utc = @metadataUpdatedUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", update.Id);
        AddParameter(command, "@runtimeMinutes", update.RuntimeMinutes);
        AddParameter(command, "@popularity", update.Popularity);
        AddParameter(command, "@voteCount", update.VoteCount);
        AddParameter(command, "@status", NormalizeText(update.Status));
        AddParameter(command, "@madeBy", NormalizeText(update.MadeBy));
        AddParameter(command, "@imdbId", NormalizeExternalId(update.ImdbId));
        AddParameter(command, "@metadataProvider", NormalizeExternalId(update.MetadataProvider));
        AddParameter(command, "@metadataProviderId", NormalizeExternalId(update.MetadataProviderId));
        AddParameter(command, "@originalTitle", NormalizeText(update.OriginalTitle));
        AddParameter(command, "@overview", NormalizeText(update.Overview));
        AddParameter(command, "@posterUrl", NormalizeText(update.PosterUrl));
        AddParameter(command, "@backdropUrl", NormalizeText(update.BackdropUrl));
        AddParameter(command, "@rating", update.Rating);
        AddParameter(command, "@genres", NormalizeText(update.Genres));
        AddParameter(command, "@externalUrl", NormalizeText(update.ExternalUrl));
        AddParameter(command, "@metadataJson", NormalizeText(update.MetadataJson));
        AddParameter(command, "@certification", NormalizeText(update.Certification));
        AddParameter(command, "@collection", NormalizeText(update.Collection));
        AddParameter(command, "@originalLanguage", NormalizeText(update.OriginalLanguage));
        AddParameter(command, "@keywords", NormalizeText(update.Keywords));

        // Every source gets a parameter whether or not this provider answered
        // for it, because the statement names them all. A source the provider
        // is silent about arrives as null and the COALESCE leaves what an
        // earlier lookup found — the alternative is a Metacritic score that
        // disappears the moment TMDb answers without one.
        foreach (var source in RatingSources.All)
        {
            var fact = update.Ratings?.FirstOrDefault(rating =>
                string.Equals(rating.Source, source.Source, StringComparison.OrdinalIgnoreCase));

            AddParameter(command, $"@{source.ScoreColumn}", fact?.Score);
            if (source.VotesColumn is not null)
            {
                AddParameter(command, $"@{source.VotesColumn}", fact?.Votes);
            }
        }

        AddParameter(command, "@metadataUpdatedUtc", now);
        AddParameter(command, "@updatedUtc", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<string> AddAsync(
        MediaKind kind,
        MediaEntryCreate entry,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var title = entry.Title.Trim();
        if (title.Length == 0)
        {
            throw new ArgumentException("A media title is required.", nameof(entry));
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        var existingId = await FindEntryIdAsync(connection, map, entry, cancellationToken);
        if (existingId is not null)
        {
            return existingId;
        }

        var id = Guid.CreateVersion7().ToString("N");
        var now = timeProvider.GetUtcNow();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {map.EntryTable} (
                id,
                title,
                {map.YearColumn},
                imdb_id,
                monitored,
                metadata_provider,
                metadata_provider_id,
                original_title,
                overview,
                poster_url,
                backdrop_url,
                rating,
                genres,
                external_url,
                metadata_json,
                metadata_updated_utc,
                created_utc,
                updated_utc
            )
            VALUES (
                @id,
                @title,
                @year,
                @imdbId,
                @monitored,
                @metadataProvider,
                @metadataProviderId,
                @originalTitle,
                @overview,
                @posterUrl,
                @backdropUrl,
                @rating,
                @genres,
                @externalUrl,
                @metadataJson,
                @metadataUpdatedUtc,
                @createdUtc,
                @updatedUtc
            )
            ON CONFLICT DO NOTHING;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@title", title);
        AddParameter(command, "@year", entry.Year);
        AddParameter(command, "@imdbId", NormalizeExternalId(entry.ImdbId));
        AddParameter(command, "@monitored", entry.Monitored ? 1 : 0);
        AddParameter(command, "@metadataProvider", NormalizeExternalId(entry.MetadataProvider));
        AddParameter(command, "@metadataProviderId", NormalizeExternalId(entry.MetadataProviderId));
        AddParameter(command, "@originalTitle", NormalizeText(entry.OriginalTitle));
        AddParameter(command, "@overview", NormalizeText(entry.Overview));
        AddParameter(command, "@posterUrl", NormalizeText(entry.PosterUrl));
        AddParameter(command, "@backdropUrl", NormalizeText(entry.BackdropUrl));
        AddParameter(command, "@rating", entry.Rating);
        AddParameter(command, "@genres", NormalizeText(entry.Genres));
        AddParameter(command, "@externalUrl", NormalizeText(entry.ExternalUrl));
        AddParameter(command, "@metadataJson", NormalizeText(entry.MetadataJson));
        AddParameter(
            command,
            "@metadataUpdatedUtc",
            string.IsNullOrWhiteSpace(entry.MetadataProviderId) ? null : now.ToString("O"));
        AddParameter(command, "@createdUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await FindEntryIdAsync(connection, map, entry, cancellationToken)
            ?? throw new InvalidOperationException("The media entry could not be read after insertion.");
    }

    public async Task<MediaImportResult> ImportExistingAsync(
        MediaKind kind,
        string libraryId,
        MediaExistingImportRequest request,
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        var normalizedTitle = request.Title.Trim();
        if (normalizedTitle.Length == 0)
        {
            throw new ArgumentException("A media title is required.", nameof(request));
        }

        var normalizedFilePath = NormalizeText(request.FilePath);
        var fileFacts = MediaFileNameFacts.Parse(request.FilePath);
        var now = timeProvider.GetUtcNow().ToString("O");
        string? mediaId;

        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = $"""
                SELECT id
                FROM {map.EntryTable}
                WHERE lower(title) = lower(@title)
                  AND (({map.YearColumn} IS NULL AND @year IS NULL) OR {map.YearColumn} = @year)
                LIMIT 1;
                """;
            AddParameter(lookup, "@title", normalizedTitle);
            AddParameter(lookup, "@year", request.Year);
            mediaId = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        }

        var created = false;
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            mediaId = Guid.CreateVersion7().ToString("N");
            created = true;

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                INSERT INTO {map.EntryTable} (
                    id, title, {map.YearColumn}, imdb_id, monitored, created_utc, updated_utc
                )
                VALUES (
                    @id, @title, @year, NULL, @monitored, @createdUtc, @updatedUtc
                );
                """;
            AddParameter(insert, "@id", mediaId);
            AddParameter(insert, "@title", normalizedTitle);
            AddParameter(insert, "@year", request.Year);
            // Reaching the cutoff stops upgrade searches; it must not stop
            // monitoring. The user's monitoring choice is independent.
            AddParameter(insert, "@monitored", 1);
            AddParameter(insert, "@createdUtc", now);
            AddParameter(insert, "@updatedUtc", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        using var wanted = connection.CreateCommand();
        wanted.Transaction = transaction;
        wanted.CommandText = $"""
            INSERT INTO {map.WantedTable} (
                {map.WantedMediaIdColumn}, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, file_path, file_size_bytes, imported_utc, last_verified_utc,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc,
                prevent_lower_quality_replacements, quality_delta_last_decision,
                video_codec, audio_codec, audio_channels, release_group
            )
            VALUES (
                @mediaId, @libraryId, @wantedStatus, @wantedReason, 1, @qualityCutoffMet,
                @currentQuality, @targetQuality, @filePath, @fileSizeBytes, @importedUtc, @lastVerifiedUtc,
                NULL, NULL, NULL, 'Imported from your existing library.', @updatedUtc,
                1, 0,
                @videoCodec, @audioCodec, @audioChannels, @releaseGroup
            )
            ON CONFLICT({map.WantedMediaIdColumn}, library_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                has_file = 1,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met,
                file_path = excluded.file_path,
                file_size_bytes = excluded.file_size_bytes,
                imported_utc = COALESCE({map.WantedTable}.imported_utc, excluded.imported_utc),
                last_verified_utc = excluded.last_verified_utc,
                missing_detected_utc = NULL,
                last_search_result = excluded.last_search_result,
                video_codec = excluded.video_codec,
                audio_codec = excluded.audio_codec,
                audio_channels = excluded.audio_channels,
                release_group = excluded.release_group,
                updated_utc = excluded.updated_utc;
            """;
        AddParameter(wanted, "@mediaId", mediaId);
        AddParameter(wanted, "@libraryId", libraryId);
        AddParameter(wanted, "@wantedStatus", WantedStatuses.Normalize(request.WantedStatus));
        AddParameter(wanted, "@wantedReason", request.WantedReason.Trim());
        AddParameter(wanted, "@currentQuality", request.CurrentQuality);
        AddParameter(wanted, "@targetQuality", request.TargetQuality);
        AddParameter(wanted, "@qualityCutoffMet", request.QualityCutoffMet ? 1 : 0);
        AddParameter(wanted, "@filePath", normalizedFilePath);
        AddParameter(wanted, "@fileSizeBytes", request.FileSizeBytes);
        AddParameter(wanted, "@importedUtc", normalizedFilePath is null ? null : now);
        AddParameter(wanted, "@lastVerifiedUtc", normalizedFilePath is null ? null : now);
        AddParameter(wanted, "@updatedUtc", now);
        AddParameter(wanted, "@videoCodec", fileFacts.VideoCodec);
        AddParameter(wanted, "@audioCodec", fileFacts.AudioCodec);
        AddParameter(wanted, "@audioChannels", fileFacts.AudioChannels);
        AddParameter(wanted, "@releaseGroup", fileFacts.ReleaseGroup);
        await wanted.ExecuteNonQueryAsync(cancellationToken);

        return new MediaImportResult(mediaId, created);
    }

    public async IAsyncEnumerable<MediaTrackedFileItem> StreamTrackedFilesAsync(
        MediaKind kind,
        string libraryId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                w.{map.WantedMediaIdColumn},
                w.library_id,
                {map.EntryAlias}.title,
                {map.EntryAlias}.{map.YearColumn},
                w.file_path,
                w.file_size_bytes,
                w.imported_utc,
                w.last_verified_utc
            FROM {map.WantedTable} w
            INNER JOIN {map.EntryTable} {map.EntryAlias}
                ON {map.EntryAlias}.id = w.{map.WantedMediaIdColumn}
            WHERE w.library_id = @libraryId
              AND w.has_file = 1
              AND w.file_path IS NOT NULL
            ORDER BY {map.EntryAlias}.title COLLATE NOCASE, {map.EntryAlias}.id;
            """;
        AddParameter(command, "@libraryId", libraryId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new MediaTrackedFileItem(
                MediaId: reader.GetString(0),
                LibraryId: reader.GetString(1),
                Title: reader.GetString(2),
                Year: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                FilePath: reader.GetString(4),
                FileSizeBytes: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                ImportedUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                LastVerifiedUtc: reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)));
        }
    }

    public async Task<MediaEntryDetails?> GetByIdAsync(
        MediaKind kind,
        string id,
        CancellationToken cancellationToken)
    {
        var map = MediaTableMap.For(kind);
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            map.DatabaseName,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {map.EntryAlias}.id,
                {map.EntryAlias}.title,
                {map.EntryAlias}.{map.YearColumn},
                {map.EntryAlias}.imdb_id,
                {map.EntryAlias}.monitored,
                {CatalogueWantedState.HasFileColumn},
                {map.EntryAlias}.metadata_provider,
                {map.EntryAlias}.metadata_provider_id,
                {map.EntryAlias}.original_title,
                {map.EntryAlias}.overview,
                {map.EntryAlias}.poster_url,
                {map.EntryAlias}.backdrop_url,
                {map.EntryAlias}.rating,
                {map.EntryAlias}.genres,
                {map.EntryAlias}.external_url,
                {map.EntryAlias}.metadata_json,
                {map.EntryAlias}.metadata_updated_utc,
                {map.EntryAlias}.created_utc,
                {map.EntryAlias}.updated_utc,
                ws.current_quality,
            {CatalogueWantedState.PageColumns}
            FROM {map.EntryTable} {map.EntryAlias}
            {CatalogueWantedState.Join(map.EntryAlias, map.WantedTable, map.WantedMediaIdColumn, scopedToLibrary: false)}
            WHERE {map.EntryAlias}.id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        // Ordinal 19 is the current quality; the seven search-state columns
        // follow it, in the order CatalogueWantedState.PageColumns declares.
        var wanted = CatalogueWantedState.Read(reader, 20);

        return new MediaEntryDetails(
            reader.GetString(0),
            reader.GetString(1),
            ReadNullableInt(reader, 2),
            ReadNullableString(reader, 3),
            reader.GetInt64(4) == 1,
            reader.GetInt64(5) == 1,
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            ReadNullableString(reader, 9),
            ReadNullableString(reader, 10),
            ReadNullableString(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetDouble(12),
            ReadNullableString(reader, 13),
            ReadNullableString(reader, 14),
            ReadNullableString(reader, 15),
            ReadNullableTimestamp(reader, 16),
            ParseTimestamp(reader.GetString(17)),
            ParseTimestamp(reader.GetString(18)),
            CurrentQuality: ReadNullableString(reader, 19),
            LibraryId: wanted.LibraryId,
            WantedStatus: wanted.WantedStatus,
            WantedReason: wanted.WantedReason,
            TargetQuality: wanted.TargetQuality,
            QualityCutoffMet: wanted.QualityCutoffMet,
            LastSearchUtc: wanted.LastSearchUtc,
            NextEligibleSearchUtc: wanted.NextEligibleSearchUtc);
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

    private static string? NormalizeExternalId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static async Task<string?> FindEntryIdAsync(
        DbConnection connection,
        MediaTableMap map,
        MediaEntryCreate entry,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id
            FROM {map.EntryTable}
            WHERE
                (@imdbId IS NOT NULL AND imdb_id = @imdbId)
                OR (
                    @metadataProvider IS NOT NULL
                    AND @metadataProviderId IS NOT NULL
                    AND metadata_provider = @metadataProvider
                    AND metadata_provider_id = @metadataProviderId
                )
                OR (
                    lower(title) = lower(@title)
                    AND COALESCE({map.YearColumn}, -1) = COALESCE(@year, -1)
                )
            ORDER BY created_utc ASC
            LIMIT 1;
            """;
        AddParameter(command, "@imdbId", NormalizeExternalId(entry.ImdbId));
        AddParameter(command, "@metadataProvider", NormalizeExternalId(entry.MetadataProvider));
        AddParameter(command, "@metadataProviderId", NormalizeExternalId(entry.MetadataProviderId));
        AddParameter(command, "@title", entry.Title.Trim());
        AddParameter(command, "@year", entry.Year);
        return await command.ExecuteScalarAsync(cancellationToken) is string id ? id : null;
    }

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
