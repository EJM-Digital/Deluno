using System.Globalization;
using Deluno.Infrastructure.Storage;
using Deluno.Movies.Contracts;

namespace Deluno.Movies.Data;

public sealed class SqliteMovieCatalogRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider)
    : IMovieCatalogRepository
{
    public async Task<MovieListItem> AddAsync(CreateMovieRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var movie = new MovieListItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Title: request.Title!.Trim(),
            ReleaseYear: request.ReleaseYear,
            ImdbId: NormalizeExternalId(request.ImdbId),
            Monitored: request.Monitored,
            CreatedUtc: now,
            UpdatedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO movie_entries (
                id,
                title,
                release_year,
                imdb_id,
                monitored,
                created_utc,
                updated_utc
            )
            VALUES (
                @id,
                @title,
                @releaseYear,
                @imdbId,
                @monitored,
                @createdUtc,
                @updatedUtc
            );
            """;

        AddParameter(command, "@id", movie.Id);
        AddParameter(command, "@title", movie.Title);
        AddParameter(command, "@releaseYear", movie.ReleaseYear);
        AddParameter(command, "@imdbId", movie.ImdbId);
        AddParameter(command, "@monitored", movie.Monitored ? 1 : 0);
        AddParameter(command, "@createdUtc", movie.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", movie.UpdatedUtc.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
        return movie;
    }

    public async Task<MovieListItem?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                title,
                release_year,
                imdb_id,
                monitored,
                created_utc,
                updated_utc
            FROM movie_entries
            WHERE id = @id
            LIMIT 1;
            """;

        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMovie(reader)
            : null;
    }

    public async Task<IReadOnlyList<MovieListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var movies = new List<MovieListItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                title,
                release_year,
                imdb_id,
                monitored,
                created_utc,
                updated_utc
            FROM movie_entries
            ORDER BY created_utc DESC, title ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            movies.Add(ReadMovie(reader));
        }

        return movies;
    }

    public async Task<MovieWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken)
    {
        var items = new List<MovieWantedItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                m.id, m.title, m.release_year, m.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.quality_cutoff_met,
                w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
            FROM movie_wanted_state w
            INNER JOIN movie_entries m ON m.id = w.movie_id
            ORDER BY w.updated_utc DESC, m.title ASC
            LIMIT 25;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWantedMovie(reader));
        }

        return new MovieWantedSummary(
            TotalWanted: items.Count,
            MissingCount: items.Count(item => item.WantedStatus == "missing"),
            UpgradeCount: items.Count(item => item.WantedStatus == "upgrade"),
            WaitingCount: items.Count(item => item.WantedStatus == "waiting"),
            RecentItems: items);
    }

    public async Task<IReadOnlyList<MovieWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var items = new List<MovieWantedItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                m.id, m.title, m.release_year, m.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.quality_cutoff_met,
                w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
            FROM movie_wanted_state w
            INNER JOIN movie_entries m ON m.id = w.movie_id
            WHERE w.library_id = @libraryId
              AND w.wanted_status IN ('missing', 'upgrade')
              AND (w.next_eligible_search_utc IS NULL OR w.next_eligible_search_utc <= @now)
            ORDER BY
                CASE w.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                COALESCE(w.last_search_utc, w.missing_since_utc, w.updated_utc) ASC,
                m.title ASC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWantedMovie(reader));
        }

        return items;
    }

    public async Task EnsureWantedStateAsync(
        string movieId,
        string libraryId,
        string wantedStatus,
        string wantedReason,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO movie_wanted_state (
                movie_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            )
            VALUES (
                @movieId, @libraryId, @wantedStatus, @wantedReason, 0, 0,
                @missingSinceUtc, NULL, NULL, NULL, @updatedUtc
            )
            ON CONFLICT(movie_id) DO UPDATE SET
                library_id = excluded.library_id,
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@wantedStatus", NormalizeWantedStatus(wantedStatus));
        AddParameter(command, "@wantedReason", wantedReason.Trim());
        AddParameter(command, "@missingSinceUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordSearchAttemptAsync(
        string movieId,
        string libraryId,
        string triggerKind,
        string outcome,
        DateTimeOffset now,
        DateTimeOffset? nextEligibleSearchUtc,
        string? lastSearchResult,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText =
                """
                INSERT INTO movie_search_history (
                    id, movie_id, library_id, trigger_kind, outcome, release_name, indexer_name, details_json, created_utc
                )
                VALUES (
                    @id, @movieId, @libraryId, @triggerKind, @outcome, NULL, NULL, NULL, @createdUtc
                );
                """;

            AddParameter(history, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(history, "@movieId", movieId);
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
                UPDATE movie_wanted_state
                SET
                    last_search_utc = @lastSearchUtc,
                    next_eligible_search_utc = @nextEligibleSearchUtc,
                    last_search_result = @lastSearchResult,
                    updated_utc = @updatedUtc
                WHERE movie_id = @movieId
                  AND library_id = @libraryId;
                """;

            AddParameter(update, "@movieId", movieId);
            AddParameter(update, "@libraryId", libraryId);
            AddParameter(update, "@lastSearchUtc", now.ToString("O"));
            AddParameter(update, "@nextEligibleSearchUtc", nextEligibleSearchUtc?.ToString("O"));
            AddParameter(update, "@lastSearchResult", lastSearchResult);
            AddParameter(update, "@updatedUtc", now.ToString("O"));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MovieImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken)
    {
        var cases = new List<MovieImportRecoveryCase>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
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
                FROM movie_import_recovery_cases
                ORDER BY detected_utc DESC
                LIMIT 12;
                """;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cases.Add(new MovieImportRecoveryCase(
                    Id: reader.GetString(0),
                    Title: reader.GetString(1),
                    FailureKind: reader.GetString(2),
                    Summary: reader.GetString(3),
                    RecommendedAction: reader.GetString(4),
                    DetectedUtc: ParseTimestamp(reader.GetString(5))));
            }
        }

        return new MovieImportRecoverySummary(
            OpenCount: cases.Count,
            QualityCount: cases.Count(item => item.FailureKind == "quality"),
            UnmatchedCount: cases.Count(item => item.FailureKind == "unmatched"),
            CorruptCount: cases.Count(item => item.FailureKind == "corrupt"),
            DownloadFailedCount: cases.Count(item => item.FailureKind == "downloadFailed"),
            ImportFailedCount: cases.Count(item => item.FailureKind == "importFailed"),
            RecentCases: cases);
    }

    public async Task<MovieImportRecoveryCase> AddImportRecoveryCaseAsync(
        CreateMovieImportRecoveryCaseRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var item = new MovieImportRecoveryCase(
            Id: Guid.CreateVersion7().ToString("N"),
            Title: request.Title!.Trim(),
            FailureKind: NormalizeFailureKind(request.FailureKind),
            Summary: request.Summary!.Trim(),
            RecommendedAction: string.IsNullOrWhiteSpace(request.RecommendedAction)
                ? "Review this import and decide whether Deluno should retry, rematch, or remove it."
                : request.RecommendedAction.Trim(),
            DetectedUtc: now);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO movie_import_recovery_cases (
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
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM movie_import_recovery_cases WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static MovieListItem ReadMovie(System.Data.Common.DbDataReader reader)
    {
        return new MovieListItem(
            Id: reader.GetString(0),
            Title: reader.GetString(1),
            ReleaseYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ImdbId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Monitored: reader.GetInt32(4) == 1,
            CreatedUtc: ParseTimestamp(reader.GetString(5)),
            UpdatedUtc: ParseTimestamp(reader.GetString(6)));
    }

    private static MovieWantedItem ReadWantedMovie(System.Data.Common.DbDataReader reader)
    {
        return new MovieWantedItem(
            MovieId: reader.GetString(0),
            Title: reader.GetString(1),
            ReleaseYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
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
