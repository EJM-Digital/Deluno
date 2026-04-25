using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Movies.Contracts;
using Deluno.Platform.Quality;

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
            MetadataProvider: NormalizeExternalId(request.MetadataProvider),
            MetadataProviderId: NormalizeExternalId(request.MetadataProviderId),
            OriginalTitle: NormalizeText(request.OriginalTitle),
            Overview: NormalizeText(request.Overview),
            PosterUrl: NormalizeText(request.PosterUrl),
            BackdropUrl: NormalizeText(request.BackdropUrl),
            Rating: request.Rating,
            Ratings: BuildRatings(request.Rating, request.MetadataJson),
            Genres: NormalizeText(request.Genres),
            ExternalUrl: NormalizeText(request.ExternalUrl),
            MetadataJson: NormalizeText(request.MetadataJson),
            MetadataUpdatedUtc: string.IsNullOrWhiteSpace(request.MetadataProviderId) ? null : now,
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
                @releaseYear,
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
            );
            """;

        AddParameter(command, "@id", movie.Id);
        AddParameter(command, "@title", movie.Title);
        AddParameter(command, "@releaseYear", movie.ReleaseYear);
        AddParameter(command, "@imdbId", movie.ImdbId);
        AddParameter(command, "@monitored", movie.Monitored ? 1 : 0);
        AddParameter(command, "@metadataProvider", movie.MetadataProvider);
        AddParameter(command, "@metadataProviderId", movie.MetadataProviderId);
        AddParameter(command, "@originalTitle", movie.OriginalTitle);
        AddParameter(command, "@overview", movie.Overview);
        AddParameter(command, "@posterUrl", movie.PosterUrl);
        AddParameter(command, "@backdropUrl", movie.BackdropUrl);
        AddParameter(command, "@rating", movie.Rating);
        AddParameter(command, "@genres", movie.Genres);
        AddParameter(command, "@externalUrl", movie.ExternalUrl);
        AddParameter(command, "@metadataJson", movie.MetadataJson);
        AddParameter(command, "@metadataUpdatedUtc", movie.MetadataUpdatedUtc?.ToString("O"));
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

    public async Task<int> UpdateMonitoredAsync(
        IReadOnlyList<string> movieIds,
        bool monitored,
        CancellationToken cancellationToken)
    {
        if (movieIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        var updated = 0;
        var now = timeProvider.GetUtcNow().ToString("O");
        foreach (var movieId in movieIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE movie_entries
                SET monitored = @monitored,
                    updated_utc = @updatedUtc
                WHERE id = @id;
                """;
            AddParameter(command, "@id", movieId);
            AddParameter(command, "@monitored", monitored ? 1 : 0);
            AddParameter(command, "@updatedUtc", now);
            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return updated;
    }

    public async Task<MovieListItem?> UpdateMetadataAsync(
        string id,
        string? metadataProvider,
        string? metadataProviderId,
        string? originalTitle,
        string? overview,
        string? posterUrl,
        string? backdropUrl,
        double? rating,
        string? genres,
        string? externalUrl,
        string? imdbId,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_entries
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
                metadata_updated_utc = @metadataUpdatedUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@imdbId", NormalizeExternalId(imdbId));
        AddParameter(command, "@metadataProvider", NormalizeExternalId(metadataProvider));
        AddParameter(command, "@metadataProviderId", NormalizeExternalId(metadataProviderId));
        AddParameter(command, "@originalTitle", NormalizeText(originalTitle));
        AddParameter(command, "@overview", NormalizeText(overview));
        AddParameter(command, "@posterUrl", NormalizeText(posterUrl));
        AddParameter(command, "@backdropUrl", NormalizeText(backdropUrl));
        AddParameter(command, "@rating", rating);
        AddParameter(command, "@genres", NormalizeText(genres));
        AddParameter(command, "@externalUrl", NormalizeText(externalUrl));
        AddParameter(command, "@metadataJson", NormalizeText(metadataJson));
        AddParameter(command, "@metadataUpdatedUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return null;
        }

        return await GetByIdAsync(id, cancellationToken);
    }

    public async Task<MovieWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken)
    {
        var items = new List<MovieWantedItem>();
        var totalWanted = 0;
        var missingCount = 0;
        var upgradeCount = 0;
        var waitingCount = 0;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
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
                FROM movie_wanted_state;
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
                m.id, m.title, m.release_year, m.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
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
            TotalWanted: totalWanted,
            MissingCount: missingCount,
            UpgradeCount: upgradeCount,
            WaitingCount: waitingCount,
            RecentItems: items);
    }

    public async Task<IReadOnlyList<MovieSearchHistoryItem>> ListSearchHistoryAsync(CancellationToken cancellationToken)
    {
        var items = new List<MovieSearchHistoryItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, movie_id, library_id, trigger_kind, outcome, release_name, indexer_name, details_json, created_utc
            FROM movie_search_history
            ORDER BY created_utc DESC
            LIMIT 20;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MovieSearchHistoryItem(
                Id: reader.GetString(0),
                MovieId: reader.GetString(1),
                LibraryId: reader.GetString(2),
                TriggerKind: reader.GetString(3),
                Outcome: reader.GetString(4),
                ReleaseName: reader.IsDBNull(5) ? null : reader.GetString(5),
                IndexerName: reader.IsDBNull(6) ? null : reader.GetString(6),
                DetailsJson: reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedUtc: ParseTimestamp(reader.GetString(8))));
        }

        return items;
    }

    public async Task<IReadOnlyList<MovieWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken)
    {
        var items = new List<MovieWantedItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            ignoreRetryWindow
                ? """
                  SELECT
                      m.id, m.title, m.release_year, m.imdb_id,
                      w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                      w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
                  FROM movie_wanted_state w
                  INNER JOIN movie_entries m ON m.id = w.movie_id
                  WHERE w.library_id = @libraryId
                    AND w.wanted_status IN ('missing', 'upgrade')
                  ORDER BY
                      CASE w.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                      COALESCE(w.last_search_utc, w.missing_since_utc, w.updated_utc) ASC,
                      m.title ASC
                  LIMIT @take;
                  """
                : """
                  SELECT
                      m.id, m.title, m.release_year, m.imdb_id,
                      w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
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
        bool hasFile,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
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
                current_quality, target_quality, missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            )
            VALUES (
                @movieId, @libraryId, @wantedStatus, @wantedReason, @hasFile, @qualityCutoffMet,
                @currentQuality, @targetQuality, @missingSinceUtc, NULL, NULL, NULL, @updatedUtc
            )
            ON CONFLICT(movie_id, library_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                has_file = excluded.has_file,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@wantedStatus", NormalizeWantedStatus(wantedStatus));
        AddParameter(command, "@wantedReason", wantedReason.Trim());
        AddParameter(command, "@hasFile", hasFile ? 1 : 0);
        AddParameter(command, "@currentQuality", currentQuality);
        AddParameter(command, "@targetQuality", targetQuality);
        AddParameter(command, "@qualityCutoffMet", qualityCutoffMet ? 1 : 0);
        AddParameter(command, "@missingSinceUtc", now.ToString("O"));
        AddParameter(command, "@updatedUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ImportExistingAsync(
        string libraryId,
        string title,
        int? releaseYear,
        string wantedStatus,
        string wantedReason,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        var normalizedTitle = title.Trim();
        var now = timeProvider.GetUtcNow();
        string? movieId = null;

        using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText =
                """
                SELECT id
                FROM movie_entries
                WHERE lower(title) = lower(@title)
                  AND ((release_year IS NULL AND @releaseYear IS NULL) OR release_year = @releaseYear)
                LIMIT 1;
                """;

            AddParameter(lookup, "@title", normalizedTitle);
            AddParameter(lookup, "@releaseYear", releaseYear);

            movieId = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        }

        var created = false;
        if (string.IsNullOrWhiteSpace(movieId))
        {
            movieId = Guid.CreateVersion7().ToString("N");
            created = true;

            using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO movie_entries (
                    id, title, release_year, imdb_id, monitored, created_utc, updated_utc
                )
                VALUES (
                    @id, @title, @releaseYear, NULL, 1, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(insert, "@id", movieId);
            AddParameter(insert, "@title", normalizedTitle);
            AddParameter(insert, "@releaseYear", releaseYear);
            AddParameter(insert, "@createdUtc", now.ToString("O"));
            AddParameter(insert, "@updatedUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        using var wanted = connection.CreateCommand();
        wanted.CommandText =
            """
            INSERT INTO movie_wanted_state (
                movie_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            )
            VALUES (
                @movieId, @libraryId, @wantedStatus, @wantedReason, 1, @qualityCutoffMet,
                @currentQuality, @targetQuality, NULL, NULL, NULL, 'Imported from your existing library.', @updatedUtc
            )
            ON CONFLICT(movie_id, library_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                has_file = 1,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met,
                last_search_result = excluded.last_search_result,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(wanted, "@movieId", movieId);
        AddParameter(wanted, "@libraryId", libraryId);
        AddParameter(wanted, "@wantedStatus", NormalizeWantedStatus(wantedStatus));
        AddParameter(wanted, "@wantedReason", wantedReason.Trim());
        AddParameter(wanted, "@currentQuality", currentQuality);
        AddParameter(wanted, "@targetQuality", targetQuality);
        AddParameter(wanted, "@qualityCutoffMet", qualityCutoffMet ? 1 : 0);
        AddParameter(wanted, "@updatedUtc", now.ToString("O"));
        await wanted.ExecuteNonQueryAsync(cancellationToken);

        return created;
    }

    public async Task RecordSearchAttemptAsync(
        string movieId,
        string libraryId,
        string triggerKind,
        string outcome,
        DateTimeOffset now,
        DateTimeOffset? nextEligibleSearchUtc,
        string? lastSearchResult,
        string? releaseName,
        string? indexerName,
        string? detailsJson,
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
                    @id, @movieId, @libraryId, @triggerKind, @outcome, @releaseName, @indexerName, @detailsJson, @createdUtc
                );
                """;

            AddParameter(history, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(history, "@movieId", movieId);
            AddParameter(history, "@libraryId", libraryId);
            AddParameter(history, "@triggerKind", triggerKind);
            AddParameter(history, "@outcome", outcome);
            AddParameter(history, "@releaseName", releaseName);
            AddParameter(history, "@indexerName", indexerName);
            AddParameter(history, "@detailsJson", detailsJson);
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

    public async Task<int> ReevaluateLibraryWantedStateAsync(
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken)
    {
        var items = new List<(string MovieId, bool HasFile, string? CurrentQuality)>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT movie_id, has_file, current_quality
                FROM movie_wanted_state
                WHERE library_id = @libraryId;
                """;
            AddParameter(command, "@libraryId", libraryId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add((
                    reader.GetString(0),
                    reader.GetInt64(1) == 1,
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var updated = 0;
        foreach (var item in items)
        {
            var decision = LibraryQualityDecider.Decide(
                mediaLabel: "movie",
                hasFile: item.HasFile,
                currentQuality: item.CurrentQuality,
                cutoffQuality: cutoffQuality,
                upgradeUntilCutoff: upgradeUntilCutoff,
                upgradeUnknownItems: upgradeUnknownItems);

            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE movie_wanted_state
                SET
                    wanted_status = @wantedStatus,
                    wanted_reason = @wantedReason,
                    target_quality = @targetQuality,
                    quality_cutoff_met = @qualityCutoffMet,
                    updated_utc = @updatedUtc
                WHERE movie_id = @movieId
                  AND library_id = @libraryId;
                """;
            AddParameter(update, "@movieId", item.MovieId);
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
                    details_json,
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
                    DetailsJson: reader.IsDBNull(5) ? null : reader.GetString(5),
                    DetectedUtc: ParseTimestamp(reader.GetString(6))));
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
            DetailsJson: string.IsNullOrWhiteSpace(request.DetailsJson) ? null : request.DetailsJson.Trim(),
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
                details_json,
                detected_utc
            )
            VALUES (
                @id,
                @title,
                @failureKind,
                @summary,
                @recommendedAction,
                @detailsJson,
                @detectedUtc
            );
            """;

        AddParameter(command, "@id", item.Id);
        AddParameter(command, "@title", item.Title);
        AddParameter(command, "@failureKind", item.FailureKind);
        AddParameter(command, "@summary", item.Summary);
        AddParameter(command, "@recommendedAction", item.RecommendedAction);
        AddParameter(command, "@detailsJson", item.DetailsJson);
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
            MetadataProvider: reader.IsDBNull(5) ? null : reader.GetString(5),
            MetadataProviderId: reader.IsDBNull(6) ? null : reader.GetString(6),
            OriginalTitle: reader.IsDBNull(7) ? null : reader.GetString(7),
            Overview: reader.IsDBNull(8) ? null : reader.GetString(8),
            PosterUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
            BackdropUrl: reader.IsDBNull(10) ? null : reader.GetString(10),
            Rating: reader.IsDBNull(11) ? null : reader.GetDouble(11),
            Ratings: BuildRatings(reader.IsDBNull(11) ? null : reader.GetDouble(11), reader.IsDBNull(14) ? null : reader.GetString(14)),
            Genres: reader.IsDBNull(12) ? null : reader.GetString(12),
            ExternalUrl: reader.IsDBNull(13) ? null : reader.GetString(13),
            MetadataJson: reader.IsDBNull(14) ? null : reader.GetString(14),
            MetadataUpdatedUtc: reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)),
            CreatedUtc: ParseTimestamp(reader.GetString(16)),
            UpdatedUtc: ParseTimestamp(reader.GetString(17)));
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
            CurrentQuality: reader.IsDBNull(8) ? null : reader.GetString(8),
            TargetQuality: reader.IsDBNull(9) ? null : reader.GetString(9),
            QualityCutoffMet: reader.GetInt64(10) == 1,
            MissingSinceUtc: reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11)),
            LastSearchUtc: reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12)),
            NextEligibleSearchUtc: reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)),
            LastSearchResult: reader.IsDBNull(14) ? null : reader.GetString(14),
            UpdatedUtc: ParseTimestamp(reader.GetString(15)));
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

    private static string? NormalizeText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static IReadOnlyList<MetadataRatingItem> BuildRatings(double? fallbackRating, string? metadataJson)
    {
        var fromMetadata = ReadRatings(metadataJson);
        if (fromMetadata.Count > 0)
        {
            return fromMetadata;
        }

        return fallbackRating is null
            ? []
            :
            [
                new MetadataRatingItem(
                    Source: "tmdb",
                    Label: "TMDb",
                    Score: Math.Round(fallbackRating.Value, 1),
                    MaxScore: 10,
                    VoteCount: null,
                    Url: null,
                    Kind: "community")
            ];
    }

    private static IReadOnlyList<MetadataRatingItem> ReadRatings(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (!TryGetProperty(document.RootElement, "ratings", out var ratingsElement) ||
                ratingsElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ratings = new List<MetadataRatingItem>();
            foreach (var item in ratingsElement.EnumerateArray())
            {
                var source = ReadString(item, "source");
                var label = ReadString(item, "label") ?? source?.ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                ratings.Add(new MetadataRatingItem(
                    Source: source,
                    Label: label,
                    Score: ReadDouble(item, "score"),
                    MaxScore: ReadDouble(item, "maxScore") ?? ReadDouble(item, "max_score"),
                    VoteCount: ReadInt(item, "voteCount") ?? ReadInt(item, "vote_count"),
                    Url: ReadString(item, "url"),
                    Kind: ReadString(item, "kind")));
            }

            return ratings;
        }
        catch
        {
            return [];
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement property)
    {
        foreach (var item in element.EnumerateObject())
        {
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = item.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String
            ? NormalizeText(property.GetString())
            : null;

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
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
