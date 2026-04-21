using System.Globalization;
using Deluno.Infrastructure.Storage;
using Deluno.Series.Contracts;

namespace Deluno.Series.Data;

public sealed class SqliteSeriesCatalogRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : ISeriesCatalogRepository
{
    public async Task<SeriesListItem> AddAsync(CreateSeriesRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var series = new SeriesListItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Title: request.Title!.Trim(),
            StartYear: request.StartYear,
            ImdbId: NormalizeExternalId(request.ImdbId),
            Monitored: request.Monitored,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series_entries (
                id,
                title,
                start_year,
                imdb_id,
                monitored,
                created_utc,
                updated_utc
            )
            VALUES (
                @id,
                @title,
                @startYear,
                @imdbId,
                @monitored,
                @createdUtc,
                @updatedUtc
            );
            """;

        AddParameter(command, "@id", series.Id);
        AddParameter(command, "@title", series.Title);
        AddParameter(command, "@startYear", series.StartYear);
        AddParameter(command, "@imdbId", series.ImdbId);
        AddParameter(command, "@monitored", series.Monitored ? 1 : 0);
        AddParameter(command, "@createdUtc", series.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", series.UpdatedUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
        return series;
    }

    public async Task<SeriesListItem?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                title,
                start_year,
                imdb_id,
                monitored,
                created_utc,
                updated_utc
            FROM series_entries
            WHERE id = @id
            LIMIT 1;
            """;

        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSeries(reader)
            : null;
    }

    public async Task<IReadOnlyList<SeriesListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<SeriesListItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                title,
                start_year,
                imdb_id,
                monitored,
                created_utc,
                updated_utc
            FROM series_entries
            ORDER BY created_utc DESC, title ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSeries(reader));
        }

        return items;
    }

    public async Task<SeriesWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken)
    {
        var items = new List<SeriesWantedItem>();
        var totalWanted = 0;
        var missingCount = 0;
        var upgradeCount = 0;
        var waitingCount = 0;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using (var totals = connection.CreateCommand())
        {
            totals.CommandText =
                """
                SELECT
                    COUNT(*),
                    SUM(CASE WHEN wanted_status = 'missing' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN wanted_status = 'upgrade' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN wanted_status = 'waiting' THEN 1 ELSE 0 END)
                FROM series_wanted_state;
                """;

            using var totalsReader = await totals.ExecuteReaderAsync(cancellationToken);
            if (await totalsReader.ReadAsync(cancellationToken))
            {
                totalWanted = totalsReader.IsDBNull(0) ? 0 : totalsReader.GetInt32(0);
                missingCount = totalsReader.IsDBNull(1) ? 0 : totalsReader.GetInt32(1);
                upgradeCount = totalsReader.IsDBNull(2) ? 0 : totalsReader.GetInt32(2);
                waitingCount = totalsReader.IsDBNull(3) ? 0 : totalsReader.GetInt32(3);
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id, s.title, s.start_year, s.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.quality_cutoff_met,
                w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
            FROM series_wanted_state w
            INNER JOIN series_entries s ON s.id = w.series_id
            ORDER BY w.updated_utc DESC, s.title ASC
            LIMIT 25;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWantedSeries(reader));
        }

        return new SeriesWantedSummary(
            TotalWanted: totalWanted,
            MissingCount: missingCount,
            UpgradeCount: upgradeCount,
            WaitingCount: waitingCount,
            RecentItems: items);
    }

    public async Task<IReadOnlyList<SeriesWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var items = new List<SeriesWantedItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id, s.title, s.start_year, s.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.quality_cutoff_met,
                w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
            FROM series_wanted_state w
            INNER JOIN series_entries s ON s.id = w.series_id
            WHERE w.library_id = @libraryId
              AND w.wanted_status IN ('missing', 'upgrade')
              AND (w.next_eligible_search_utc IS NULL OR w.next_eligible_search_utc <= @now)
            ORDER BY
                CASE w.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                COALESCE(w.last_search_utc, w.missing_since_utc, w.updated_utc) ASC,
                s.title ASC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWantedSeries(reader));
        }

        return items;
    }

    public async Task EnsureWantedStateAsync(
        string seriesId,
        string libraryId,
        string wantedStatus,
        string wantedReason,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series_wanted_state (
                series_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            )
            VALUES (
                @seriesId, @libraryId, @wantedStatus, @wantedReason, 0, 0,
                @missingSinceUtc, NULL, NULL, NULL, @updatedUtc
            )
            ON CONFLICT(series_id) DO UPDATE SET
                library_id = excluded.library_id,
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@wantedStatus", NormalizeWantedStatus(wantedStatus));
        AddParameter(command, "@wantedReason", wantedReason.Trim());
        AddParameter(command, "@missingSinceUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ImportExistingAsync(
        string libraryId,
        string title,
        int? startYear,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var normalizedTitle = title.Trim();
        var now = timeProvider.GetUtcNow();
        string? seriesId = null;

        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText =
                """
                SELECT id
                FROM series_entries
                WHERE lower(title) = lower(@title)
                  AND ((start_year IS NULL AND @startYear IS NULL) OR start_year = @startYear)
                LIMIT 1;
                """;

            AddParameter(lookup, "@title", normalizedTitle);
            AddParameter(lookup, "@startYear", startYear);

            seriesId = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        }

        var created = false;
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            seriesId = Guid.CreateVersion7().ToString("N");
            created = true;

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO series_entries (
                    id, title, start_year, imdb_id, monitored, created_utc, updated_utc
                )
                VALUES (
                    @id, @title, @startYear, NULL, 1, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(insert, "@id", seriesId);
            AddParameter(insert, "@title", normalizedTitle);
            AddParameter(insert, "@startYear", startYear);
            AddParameter(insert, "@createdUtc", now.ToString("O"));
            AddParameter(insert, "@updatedUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        using var wanted = connection.CreateCommand();
        wanted.CommandText =
            """
            INSERT INTO series_wanted_state (
                series_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            )
            VALUES (
                @seriesId, @libraryId, 'waiting', 'Already in your library.', 1, 0,
                NULL, NULL, NULL, 'Imported from your existing library.', @updatedUtc
            )
            ON CONFLICT(series_id) DO UPDATE SET
                library_id = excluded.library_id,
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                has_file = 1,
                last_search_result = excluded.last_search_result,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(wanted, "@seriesId", seriesId);
        AddParameter(wanted, "@libraryId", libraryId);
        AddParameter(wanted, "@updatedUtc", now.ToString("O"));
        await wanted.ExecuteNonQueryAsync(cancellationToken);

        return created;
    }

    public async Task RecordSearchAttemptAsync(
        string seriesId,
        string libraryId,
        string triggerKind,
        string outcome,
        DateTimeOffset now,
        DateTimeOffset? nextEligibleSearchUtc,
        string? lastSearchResult,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText =
                """
                INSERT INTO series_search_history (
                    id, series_id, episode_id, library_id, trigger_kind, outcome, release_name, indexer_name, details_json, created_utc
                )
                VALUES (
                    @id, @seriesId, NULL, @libraryId, @triggerKind, @outcome, NULL, NULL, NULL, @createdUtc
                );
                """;

            AddParameter(history, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(history, "@seriesId", seriesId);
            AddParameter(history, "@libraryId", libraryId);
            AddParameter(history, "@triggerKind", triggerKind);
            AddParameter(history, "@outcome", outcome);
            AddParameter(history, "@createdUtc", now.ToString("O"));
            await history.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE series_wanted_state
                SET
                    last_search_utc = @lastSearchUtc,
                    next_eligible_search_utc = @nextEligibleSearchUtc,
                    last_search_result = @lastSearchResult,
                    updated_utc = @updatedUtc
                WHERE series_id = @seriesId
                  AND library_id = @libraryId;
                """;

            AddParameter(update, "@seriesId", seriesId);
            AddParameter(update, "@libraryId", libraryId);
            AddParameter(update, "@lastSearchUtc", now.ToString("O"));
            AddParameter(update, "@nextEligibleSearchUtc", nextEligibleSearchUtc?.ToString("O"));
            AddParameter(update, "@lastSearchResult", lastSearchResult);
            AddParameter(update, "@updatedUtc", now.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SeriesImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken)
    {
        var cases = new List<SeriesImportRecoveryCase>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    id,
                    title,
                    failure_kind,
                    summary,
                    recommended_action,
                    detected_utc
                FROM series_import_recovery_cases
                ORDER BY detected_utc DESC
                LIMIT 12;
                """;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cases.Add(new SeriesImportRecoveryCase(
                    Id: reader.GetString(0),
                    Title: reader.GetString(1),
                    FailureKind: reader.GetString(2),
                    Summary: reader.GetString(3),
                    RecommendedAction: reader.GetString(4),
                    DetectedUtc: ParseTimestamp(reader.GetString(5))));
            }
        }

        return new SeriesImportRecoverySummary(
            OpenCount: cases.Count,
            QualityCount: cases.Count(item => item.FailureKind == "quality"),
            UnmatchedCount: cases.Count(item => item.FailureKind == "unmatched"),
            CorruptCount: cases.Count(item => item.FailureKind == "corrupt"),
            DownloadFailedCount: cases.Count(item => item.FailureKind == "downloadFailed"),
            ImportFailedCount: cases.Count(item => item.FailureKind == "importFailed"),
            RecentCases: cases);
    }

    public async Task<SeriesImportRecoveryCase> AddImportRecoveryCaseAsync(
        CreateSeriesImportRecoveryCaseRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new SeriesImportRecoveryCase(
            Id: Guid.CreateVersion7().ToString("N"),
            Title: request.Title!.Trim(),
            FailureKind: NormalizeFailureKind(request.FailureKind),
            Summary: request.Summary!.Trim(),
            RecommendedAction: string.IsNullOrWhiteSpace(request.RecommendedAction)
                ? "Review this import and decide whether Deluno should retry, rematch, or remove it."
                : request.RecommendedAction.Trim(),
            DetectedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series_import_recovery_cases (
                id,
                title,
                failure_kind,
                summary,
                recommended_action,
                detected_utc
            )
            VALUES (
                @id,
                @title,
                @failureKind,
                @summary,
                @recommendedAction,
                @detectedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@title", item.Title);
        AddParameter(command, "@failureKind", item.FailureKind);
        AddParameter(command, "@summary", item.Summary);
        AddParameter(command, "@recommendedAction", item.RecommendedAction);
        AddParameter(command, "@detectedUtc", item.DetectedUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return item;
    }

    public async Task<bool> DeleteImportRecoveryCaseAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM series_import_recovery_cases WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static SeriesListItem ReadSeries(System.Data.Common.DbDataReader reader)
    {
        return new SeriesListItem(
            Id: reader.GetString(0),
            Title: reader.GetString(1),
            StartYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ImdbId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Monitored: reader.GetInt32(4) == 1,
            CreatedUtc: ParseTimestamp(reader.GetString(5)),
            UpdatedUtc: ParseTimestamp(reader.GetString(6)));
    }

    private static SeriesWantedItem ReadWantedSeries(System.Data.Common.DbDataReader reader)
    {
        return new SeriesWantedItem(
            SeriesId: reader.GetString(0),
            Title: reader.GetString(1),
            StartYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ImdbId: reader.IsDBNull(3) ? null : reader.GetString(3),
            LibraryId: reader.GetString(4),
            WantedStatus: reader.GetString(5),
            WantedReason: reader.GetString(6),
            HasFile: reader.GetInt64(7) == 1,
            QualityCutoffMet: reader.GetInt64(8) == 1,
            MissingSinceUtc: reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
            LastSearchUtc: reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
            NextEligibleSearchUtc: reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
            LastSearchResult: reader.IsDBNull(12) ? null : reader.GetString(12),
            UpdatedUtc: ParseTimestamp(reader.GetString(13)));
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? NormalizeExternalId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeFailureKind(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "quality" => "quality",
            "unmatched" => "unmatched",
            "corrupt" => "corrupt",
            "downloadfailed" => "downloadFailed",
            "download failed" => "downloadFailed",
            "importfailed" => "importFailed",
            "import failed" => "importFailed",
            _ => "importFailed"
        };
    }

    private static string NormalizeWantedStatus(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "upgrade" => "upgrade",
            "waiting" => "waiting",
            _ => "missing"
        };
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
