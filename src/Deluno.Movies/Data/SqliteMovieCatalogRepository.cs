using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Quality;
using Microsoft.Data.Sqlite;

namespace Deluno.Movies.Data;

public sealed class SqliteMovieCatalogRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IMediaStateRepository? sharedMediaStateRepository = null)
    : IMovieCatalogRepository
{
    private readonly IMediaStateRepository? sharedMediaStateRepository = sharedMediaStateRepository;

    public async Task<MovieListItem> AddAsync(CreateMovieRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var movie = new MovieListItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Title: request.Title!.Trim(),
            ReleaseYear: request.ReleaseYear,
            ImdbId: NormalizeExternalId(request.ImdbId),
            Monitored: request.Monitored,
            HasFile: false,
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

        var existing = await FindExistingMovieAsync(connection, movie, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

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

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            existing = await FindExistingMovieAsync(connection, movie, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }

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
                m.id,
                m.title,
                m.release_year,
                m.imdb_id,
                m.monitored,
                COALESCE(MAX(w.has_file), 0) AS has_file,
                m.metadata_provider,
                m.metadata_provider_id,
                m.original_title,
                m.overview,
                m.poster_url,
                m.backdrop_url,
                m.rating,
                m.genres,
                m.external_url,
                m.metadata_json,
                m.metadata_updated_utc,
                m.created_utc,
                m.updated_utc,
                m.in_cinemas_date,
                m.digital_release_date,
                m.physical_release_date,
                m.minimum_availability
            FROM movie_entries m
            LEFT JOIN movie_wanted_state w ON w.movie_id = m.id
            WHERE m.id = @id
            GROUP BY m.id
            LIMIT 1;
            """;

        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadMovie(reader)
            : null;
    }

    private static async Task<MovieListItem?> FindExistingMovieAsync(
        System.Data.Common.DbConnection connection,
        MovieListItem movie,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                m.id,
                m.title,
                m.release_year,
                m.imdb_id,
                m.monitored,
                COALESCE(MAX(w.has_file), 0) AS has_file,
                m.metadata_provider,
                m.metadata_provider_id,
                m.original_title,
                m.overview,
                m.poster_url,
                m.backdrop_url,
                m.rating,
                m.genres,
                m.external_url,
                m.metadata_json,
                m.metadata_updated_utc,
                m.created_utc,
                m.updated_utc,
                m.in_cinemas_date,
                m.digital_release_date,
                m.physical_release_date,
                m.minimum_availability
            FROM movie_entries m
            LEFT JOIN movie_wanted_state w ON w.movie_id = m.id
            WHERE
                (@imdbId IS NOT NULL AND m.imdb_id = @imdbId)
                OR (
                    @metadataProvider IS NOT NULL
                    AND @metadataProviderId IS NOT NULL
                    AND m.metadata_provider = @metadataProvider
                    AND m.metadata_provider_id = @metadataProviderId
                )
                OR (
                    lower(m.title) = lower(@title)
                    AND COALESCE(m.release_year, -1) = COALESCE(@releaseYear, -1)
                )
            GROUP BY m.id
            ORDER BY m.created_utc ASC
            LIMIT 1;
            """;
        AddParameter(command, "@imdbId", movie.ImdbId);
        AddParameter(command, "@metadataProvider", movie.MetadataProvider);
        AddParameter(command, "@metadataProviderId", movie.MetadataProviderId);
        AddParameter(command, "@title", movie.Title);
        AddParameter(command, "@releaseYear", movie.ReleaseYear);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMovie(reader) : null;
    }

    public async Task RecordMetadataAttemptAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_entries
            SET metadata_attempted_utc = @attemptedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@attemptedUtc", timeProvider.GetUtcNow().ToString("O"));
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// What counts as stale, in one place so the list and the count cannot
    /// disagree about it.
    /// </summary>
    /// <remarks>
    /// The last clause is what stops a forced refresh becoming a hot loop. An
    /// entry the provider cannot match never gets a
    /// <c>metadata_updated_utc</c>, so without it a refresh request would
    /// re-select that entry on every pass, forever. Once an attempt has been
    /// made after the request, only the ordinary cooldown applies.
    ///
    /// The comparisons are strict for the same reason. A refresh that lands in
    /// the same instant as the request is treated as having satisfied it, which
    /// at worst skips one forced refresh; the alternative reading would leave a
    /// successfully refreshed entry permanently requested.
    /// </remarks>
    private const string StaleMetadataPredicate =
        """
        (
            metadata_provider_id IS NULL
         OR TRIM(metadata_provider_id) = ''
         OR metadata_updated_utc IS NULL
         OR metadata_updated_utc < @staleBefore
         OR (
                metadata_refresh_requested_utc IS NOT NULL
            AND (metadata_updated_utc IS NULL OR metadata_refresh_requested_utc > metadata_updated_utc)
            )
        )
        AND (
            metadata_attempted_utc IS NULL
         OR metadata_attempted_utc < @retryAttemptsBefore
         OR (
                metadata_refresh_requested_utc IS NOT NULL
            AND metadata_attempted_utc < metadata_refresh_requested_utc
            )
        )
        """;

    public async Task<int> CountStaleMetadataCandidatesAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset retryAttemptsBefore,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM movie_entries
            WHERE {StaleMetadataPredicate};
            """;
        AddParameter(command, "@staleBefore", staleBefore.ToString("O"));
        AddParameter(command, "@retryAttemptsBefore", retryAttemptsBefore.ToString("O"));

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<int> RequestMetadataRefreshForAllAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_entries
            SET metadata_refresh_requested_utc = @now
            WHERE metadata_refresh_requested_utc IS NULL
               OR metadata_refresh_requested_utc < @now;
            """;
        AddParameter(command, "@now", now.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Deluno.Jobs.Contracts.MetadataRefreshCandidate>> ListStaleMetadataCandidatesAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset retryAttemptsBefore,
        int take,
        CancellationToken cancellationToken)
    {
        var candidates = new List<Deluno.Jobs.Contracts.MetadataRefreshCandidate>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        // Filter, order and limit all happen here rather than over a
        // fully-materialised catalogue. Rows never metadata-matched sort first
        // (metadata_updated_utc IS NULL), then oldest-refreshed.
        command.CommandText =
            $"""
            SELECT id, title, release_year
            FROM movie_entries
            WHERE {StaleMetadataPredicate}
            ORDER BY
                CASE WHEN metadata_refresh_requested_utc IS NOT NULL THEN 0 ELSE 1 END,
                CASE WHEN metadata_updated_utc IS NULL THEN 0 ELSE 1 END,
                metadata_updated_utc ASC,
                title ASC
            LIMIT @take;
            """;

        AddParameter(command, "@staleBefore", staleBefore.ToString("O"));
        AddParameter(command, "@retryAttemptsBefore", retryAttemptsBefore.ToString("O"));
        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new Deluno.Jobs.Contracts.MetadataRefreshCandidate(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }

        return candidates;
    }

    /// <summary>
    /// The catalogue list. Returns a deliberately lighter row than
    /// <see cref="GetByIdAsync"/>: <c>MetadataJson</c> is always null here.
    /// </summary>
    /// <remarks>
    /// Measured at 20,000 movies, <c>metadata_json</c> was 38 MB of a 50 MB
    /// response and <c>overview</c> a further 11 MB, against 0.4 MB for
    /// everything the list actually renders. The blob is a serialised provider
    /// match, so every field in it except <c>Cast</c> already exists as its own
    /// column, and <c>Cast</c> is only read on the detail page — which fetches
    /// through <see cref="GetByIdAsync"/> and still receives it.
    ///
    /// Callers needing the blob must fetch the single entity rather than
    /// reading it off a list row.
    /// </remarks>
    public async Task<string?> FindExistingIdAsync(
        string title,
        int? releaseYear,
        string? imdbId,
        string? metadataProvider,
        string? metadataProviderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();

        // The same three rules AddAsync matches on, and each has an index:
        // ix_movie_entries_imdb_id, ix_movie_entries_metadata_provider_id and
        // ix_movie_entries_title_year. No join and no GROUP BY -- those are in
        // AddAsync's version only because it returns the whole row.
        command.CommandText =
            """
            SELECT id
            FROM movie_entries
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
                    AND COALESCE(release_year, -1) = COALESCE(@releaseYear, -1)
                )
            ORDER BY created_utc ASC
            LIMIT 1;
            """;
        AddParameter(command, "@imdbId", NormalizeExternalId(imdbId));
        AddParameter(command, "@metadataProvider", NormalizeExternalId(metadataProvider));
        AddParameter(command, "@metadataProviderId", NormalizeExternalId(metadataProviderId));
        AddParameter(command, "@title", title.Trim());
        AddParameter(command, "@releaseYear", releaseYear);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    private const string CatalogueHasFile = "EXISTS(SELECT 1 FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.has_file = 1)";
    private const string CatalogueUpgrade = "EXISTS(SELECT 1 FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.has_file = 1 AND w.quality_cutoff_met = 0)";

    /// <summary>
    /// The projection a catalogue page returns. Matches <see cref="ReadMovie"/>
    /// ordinal for ordinal, then adds the two fields the list actually displays
    /// and previously read out of an empty metadata blob — the file size and the
    /// current quality, which live in the wanted state, not in provider
    /// metadata.
    /// </summary>
    private const string CataloguePageColumns =
        """
            m.id,
            m.title,
            m.release_year,
            m.imdb_id,
            m.monitored,
            EXISTS(SELECT 1 FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.has_file = 1) AS has_file,
            m.metadata_provider,
            m.metadata_provider_id,
            m.original_title,
            m.overview,
            m.poster_url,
            m.backdrop_url,
            m.rating,
            m.genres,
            m.external_url,
            NULL AS metadata_json,
            m.metadata_updated_utc,
            m.created_utc,
            m.updated_utc,
            m.in_cinemas_date,
            m.digital_release_date,
            m.physical_release_date,
            m.minimum_availability,
            (SELECT MAX(w.file_size_bytes) FROM movie_wanted_state w WHERE w.movie_id = m.id) AS file_size_bytes,
            (SELECT w.current_quality FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.current_quality IS NOT NULL LIMIT 1) AS current_quality,
            (SELECT w.file_path FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.file_path IS NOT NULL LIMIT 1) AS file_path,
            (SELECT w.video_codec FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.video_codec IS NOT NULL LIMIT 1) AS video_codec,
            (SELECT w.audio_codec FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.audio_codec IS NOT NULL LIMIT 1) AS audio_codec,
            (SELECT w.audio_channels FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.audio_channels IS NOT NULL LIMIT 1) AS audio_channels,
            (SELECT w.release_group FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.release_group IS NOT NULL LIMIT 1) AS release_group,
            m.runtime_minutes,
            m.popularity,
            m.vote_count
        """;

    /// <summary>
    /// One page of the catalogue — searched, filtered, sorted and counted in SQL.
    ///
    /// This replaces "hand the caller every row and let the browser work it
    /// out", which is fine at two hundred titles and impossible at twenty
    /// thousand. The page itself is a seek, so page four hundred costs what page
    /// one costs; the counts are a separate pass, done once per filter rather
    /// than on every page of it.
    /// </summary>
    public async Task<CataloguePage<MovieListItem>> ListPageAsync(
        CatalogueQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = new PageRequest(query.PageSize, query.PageToken).BoundedPageSize;
        var sort = CatalogueSortFields.Normalize(query.Sort);
        var status = CatalogueStatusFilters.Normalize(query.Status);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var token = CataloguePageToken.Decode(query.PageToken);

        var sortExpression = CatalogueKeyset.SortExpression(sort, "m", "release_year");
        var where = CatalogueKeyset.CombineFilters(
            search is null ? string.Empty : CatalogueKeyset.SearchFilter("m"),
            CatalogueKeyset.StatusFilter(status, "m", CatalogueHasFile, CatalogueUpgrade),
            token is null ? string.Empty : CatalogueKeyset.SeekPredicate(sortExpression, "m", query.Descending));

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        var items = new List<MovieListItem>(pageSize + 1);
        var sortValues = new List<string?>(pageSize + 1);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT
                {CataloguePageColumns},
                    {sortExpression} AS sort_value
                FROM movie_entries m
                WHERE {where}
                ORDER BY {CatalogueKeyset.OrderBy(sortExpression, "m", query.Descending)}
                LIMIT @fetchCount;
                """;

            AddParameter(command, "@fetchCount", pageSize + 1);
            CatalogueKeyset.BindSearch(command, search);
            if (token is not null)
            {
                CatalogueKeyset.BindSeek(command, token, sort);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var fileSizeBytes = reader.IsDBNull(23) ? (long?)null : reader.GetInt64(23);
                var runtimeMinutes = reader.IsDBNull(30) ? (int?)null : reader.GetInt32(30);

                items.Add(ReadMovie(reader) with
                {
                    FileSizeBytes = fileSizeBytes,
                    CurrentQuality = reader.IsDBNull(24) ? null : reader.GetString(24),
                    FilePath = reader.IsDBNull(25) ? null : reader.GetString(25),
                    VideoCodec = reader.IsDBNull(26) ? null : reader.GetString(26),
                    AudioCodec = reader.IsDBNull(27) ? null : reader.GetString(27),
                    AudioChannels = reader.IsDBNull(28) ? null : reader.GetString(28),
                    ReleaseGroup = reader.IsDBNull(29) ? null : reader.GetString(29),
                    RuntimeMinutes = runtimeMinutes,
                    Popularity = reader.IsDBNull(31) ? null : reader.GetDouble(31),
                    VoteCount = reader.IsDBNull(32) ? null : reader.GetInt32(32),
                    // Derived here rather than stored, so it cannot go stale
                    // against either the file or the runtime it comes from.
                    ApproximateBitrateMbps = MediaFileFacts.ApproximateBitrateMbps(fileSizeBytes, runtimeMinutes)
                });

                sortValues.Add(CatalogueKeyset.ReadSortValue(reader, 33));
            }
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(pageSize);
        }

        var nextPageToken = hasMore
            ? new CataloguePageToken(sortValues[pageSize - 1], items[^1].Id).Encode()
            : null;

        // Counting scans, so it happens once per filter rather than on every
        // page of it. A continuation page keeps the numbers the caller has.
        if (token is not null)
        {
            return new CataloguePage<MovieListItem>(items, nextPageToken, hasMore, null, null);
        }

        var facets = await CountCatalogueFacetsAsync(connection, search, cancellationToken);

        return new CataloguePage<MovieListItem>(
            items,
            nextPageToken,
            hasMore,
            SelectFacetTotal(facets, status),
            facets);
    }

    /// <summary>
    /// Every quick-filter count in one pass over the searched set, so the five
    /// numbers above the list cost one scan rather than five queries — or, as
    /// before, a download of the whole catalogue and a count in the browser.
    /// </summary>
    private async Task<CatalogueFacets> CountCatalogueFacetsAsync(
        System.Data.Common.DbConnection connection,
        string? search,
        CancellationToken cancellationToken)
    {
        var where = CatalogueKeyset.CombineFilters(
            search is null ? string.Empty : CatalogueKeyset.SearchFilter("m"));

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                COUNT(*),
                SUM(CASE WHEN m.monitored = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN m.monitored = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN {CatalogueHasFile} THEN 1 ELSE 0 END),
                SUM(CASE WHEN {CatalogueHasFile} THEN 0 ELSE 1 END),
                SUM(CASE WHEN {CatalogueUpgrade} THEN 1 ELSE 0 END)
            FROM movie_entries m
            WHERE {where};
            """;
        CatalogueKeyset.BindSearch(command, search);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CatalogueFacets(0, 0, 0, 0, 0, 0);
        }

        return new CatalogueFacets(
            All: reader.GetInt32(0),
            Monitored: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            Unmonitored: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            Downloaded: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            Missing: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Upgrades: reader.IsDBNull(5) ? 0 : reader.GetInt32(5));
    }

    private static int SelectFacetTotal(CatalogueFacets facets, string status)
        => status switch
        {
            CatalogueStatusFilters.Monitored => facets.Monitored,
            CatalogueStatusFilters.Unmonitored => facets.Unmonitored,
            CatalogueStatusFilters.Downloaded => facets.Downloaded,
            CatalogueStatusFilters.Missing => facets.Missing,
            CatalogueStatusFilters.Upgrades => facets.Upgrades,
            _ => facets.All
        };

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
                m.id,
                m.title,
                m.release_year,
                m.imdb_id,
                m.monitored,
                EXISTS(
                    SELECT 1 FROM movie_wanted_state w
                    WHERE w.movie_id = m.id AND w.has_file = 1
                ) AS has_file,
                m.metadata_provider,
                m.metadata_provider_id,
                m.original_title,
                m.overview,
                m.poster_url,
                m.backdrop_url,
                m.rating,
                m.genres,
                m.external_url,
                -- Deliberately not shipped in the list. It is ~76% of the list
                -- payload (38 MB of 50 MB at 20k movies) and the list needs
                -- nothing from it: every field it carries -- overview, poster,
                -- ratings, genres, title -- is already its own column, and the
                -- only blob-only field, Cast, is read on the detail page via
                -- GetByIdAsync, which still selects it. See ListAsync's remarks.
                NULL AS metadata_json,
                m.metadata_updated_utc,
                m.created_utc,
                m.updated_utc,
                m.in_cinemas_date,
                m.digital_release_date,
                m.physical_release_date,
                m.minimum_availability
            FROM movie_entries m
            ORDER BY m.created_utc DESC, m.title ASC;
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
        CancellationToken cancellationToken,
        int? runtimeMinutes = null,
        double? popularity = null,
        int? voteCount = null)
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
                -- COALESCE: a provider that does not answer for one of these
                -- must not blank a value an earlier refresh did answer for.
                runtime_minutes = COALESCE(@runtimeMinutes, runtime_minutes),
                popularity = COALESCE(@popularity, popularity),
                vote_count = COALESCE(@voteCount, vote_count),
                metadata_updated_utc = @metadataUpdatedUtc,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;

        AddParameter(command, "@id", id);
        AddParameter(command, "@runtimeMinutes", runtimeMinutes);
        AddParameter(command, "@popularity", popularity);
        AddParameter(command, "@voteCount", voteCount);
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
        if (sharedMediaStateRepository is not null)
        {
            var sharedSummary = await sharedMediaStateRepository.GetWantedSummaryAsync(
                MediaKind.Movie,
                cancellationToken);
            return new MovieWantedSummary(
                sharedSummary.TotalWanted,
                sharedSummary.MissingCount,
                sharedSummary.UpgradeCount,
                sharedSummary.WaitingCount,
                sharedSummary.RecentItems.Select(MapWanted).ToArray());
        }

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
                w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc,
                w.prevent_lower_quality_replacements, w.quality_delta_last_decision
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
        if (sharedMediaStateRepository is not null)
        {
            var sharedItems = await sharedMediaStateRepository.ListSearchHistoryAsync(
                MediaKind.Movie,
                cancellationToken);
            return sharedItems.Select(MapSearchHistory).ToArray();
        }

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
        if (sharedMediaStateRepository is not null)
        {
            var sharedItems = await sharedMediaStateRepository.ListEligibleWantedAsync(
                MediaKind.Movie,
                libraryId,
                take,
                now,
                ignoreRetryWindow,
                cancellationToken);
            return sharedItems.Select(MapWanted).ToArray();
        }

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
                      w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc,
                      w.prevent_lower_quality_replacements, w.quality_delta_last_decision
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
                      w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc,
                      w.prevent_lower_quality_replacements, w.quality_delta_last_decision
                  FROM movie_wanted_state w
                  INNER JOIN movie_entries m ON m.id = w.movie_id
                  WHERE w.library_id = @libraryId
                    AND w.wanted_status IN ('missing', 'upgrade')
                    AND m.monitored = 1
                    AND (w.next_eligible_search_utc IS NULL OR w.next_eligible_search_utc <= @now)
                    -- Nothing to find before a film is obtainable, so do not spend
                    -- a search cycle on one. 'announced' opts out of the wait.
                    AND (
                        m.minimum_availability = 'announced'
                        OR (m.minimum_availability = 'inCinemas' AND (
                            m.in_cinemas_date IS NULL AND m.digital_release_date IS NULL AND m.physical_release_date IS NULL
                            OR COALESCE(m.in_cinemas_date, m.digital_release_date, m.physical_release_date) <= @today))
                        OR (m.minimum_availability NOT IN ('announced', 'inCinemas') AND (
                            m.digital_release_date IS NULL AND m.physical_release_date IS NULL
                            OR MIN(COALESCE(m.digital_release_date, m.physical_release_date), COALESCE(m.physical_release_date, m.digital_release_date)) <= @today))
                    )
                  ORDER BY
                      CASE w.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                      COALESCE(w.last_search_utc, w.missing_since_utc, w.updated_utc) ASC,
                      m.title ASC
                  LIMIT @take;
                  """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@today", DateOnly.FromDateTime(now.UtcDateTime).ToString("yyyy-MM-dd"));
        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWantedMovie(reader));
        }

        return items;
    }

    public async Task<int> CountRetryDelayedWantedAsync(
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.CountRetryDelayedWantedAsync(
                MediaKind.Movie,
                libraryId,
                now,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM movie_wanted_state w
            INNER JOIN movie_entries m ON m.id = w.movie_id
            WHERE w.library_id = @libraryId
              AND w.wanted_status IN ('missing', 'upgrade')
              AND m.monitored = 1
              AND w.next_eligible_search_utc IS NOT NULL
              AND w.next_eligible_search_utc > @now;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
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
        if (sharedMediaStateRepository is not null)
        {
            await sharedMediaStateRepository.EnsureWantedStateAsync(
                MediaKind.Movie,
                movieId,
                libraryId,
                wantedStatus,
                wantedReason,
                hasFile,
                currentQuality,
                targetQuality,
                qualityCutoffMet,
                cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO movie_wanted_state (
                movie_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc,
                prevent_lower_quality_replacements, quality_delta_last_decision
            )
            VALUES (
                @movieId, @libraryId, @wantedStatus, @wantedReason, @hasFile, @qualityCutoffMet,
                @currentQuality, @targetQuality, @missingSinceUtc, NULL, NULL, NULL, @updatedUtc,
                1, 0
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
        bool unmonitorWhenCutoffMet,
        string? filePath,
        long? fileSizeBytes,
        CancellationToken cancellationToken)
    {
        var created = await ImportExistingBatchAsync(
            libraryId,
            [
                new ExistingMovieImportRequest(
                    Title: title,
                    ReleaseYear: releaseYear,
                    WantedStatus: wantedStatus,
                    WantedReason: wantedReason,
                    CurrentQuality: currentQuality,
                    TargetQuality: targetQuality,
                    QualityCutoffMet: qualityCutoffMet,
                    UnmonitorWhenCutoffMet: unmonitorWhenCutoffMet,
                    FilePath: filePath,
                    FileSizeBytes: fileSizeBytes)
            ],
            cancellationToken);

        return created > 0;
    }

    /// <summary>
    /// Imports a slice of already-on-disk titles inside one transaction, and
    /// returns how many were newly created.
    ///
    /// A transaction per title means a disk flush per title, which is most of
    /// why importing 20,000 movies took hours rather than the seconds of real
    /// work involved. The batch size is the caller's slice, so this stays
    /// bounded however large the library is.
    /// </summary>
    public async Task<int> ImportExistingBatchAsync(
        string libraryId,
        IReadOnlyList<ExistingMovieImportRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var created = 0;

        foreach (var request in requests)
        {
            if (await ImportExistingCoreAsync(connection, transaction, libraryId, request, now, cancellationToken))
            {
                created++;
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return created;
    }

    private static async Task<bool> ImportExistingCoreAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string libraryId,
        ExistingMovieImportRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = request.Title.Trim();
        var normalizedFilePath = NormalizeText(request.FilePath);
        // What the file name says about the file. Read here rather than at each
        // call site so every path that records a file — an existing-library
        // import, a completed download — gets it, and gets it the same way.
        var fileFacts = MediaFileNameFacts.Parse(request.FilePath);
        string? movieId;

        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText =
                """
                SELECT id
                FROM movie_entries
                WHERE lower(title) = lower(@title)
                  AND ((release_year IS NULL AND @releaseYear IS NULL) OR release_year = @releaseYear)
                LIMIT 1;
                """;

            AddParameter(lookup, "@title", normalizedTitle);
            AddParameter(lookup, "@releaseYear", request.ReleaseYear);

            movieId = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        }

        var created = false;
        if (string.IsNullOrWhiteSpace(movieId))
        {
            movieId = Guid.CreateVersion7().ToString("N");
            created = true;

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO movie_entries (
                    id, title, release_year, imdb_id, monitored, created_utc, updated_utc
                )
                VALUES (
                    @id, @title, @releaseYear, NULL, @monitored, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(insert, "@id", movieId);
            AddParameter(insert, "@title", normalizedTitle);
            AddParameter(insert, "@releaseYear", request.ReleaseYear);
            AddParameter(insert, "@monitored", request.UnmonitorWhenCutoffMet && request.QualityCutoffMet ? 0 : 1);
            AddParameter(insert, "@createdUtc", now.ToString("O"));
            AddParameter(insert, "@updatedUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (request.UnmonitorWhenCutoffMet && request.QualityCutoffMet)
        {
            using var unmonitor = connection.CreateCommand();
            unmonitor.Transaction = transaction;
            unmonitor.CommandText =
                """
                UPDATE movie_entries
                SET monitored = 0,
                    updated_utc = @updatedUtc
                WHERE id = @movieId;
                """;
            AddParameter(unmonitor, "@movieId", movieId);
            AddParameter(unmonitor, "@updatedUtc", now.ToString("O"));
            await unmonitor.ExecuteNonQueryAsync(cancellationToken);
        }

        using var wanted = connection.CreateCommand();
        wanted.Transaction = transaction;
        wanted.CommandText =
            """
            INSERT INTO movie_wanted_state (
                movie_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, file_path, file_size_bytes, imported_utc, last_verified_utc,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc,
                prevent_lower_quality_replacements, quality_delta_last_decision,
                video_codec, audio_codec, audio_channels, release_group
            )
            VALUES (
                @movieId, @libraryId, @wantedStatus, @wantedReason, 1, @qualityCutoffMet,
                @currentQuality, @targetQuality, @filePath, @fileSizeBytes, @importedUtc, @lastVerifiedUtc,
                NULL, NULL, NULL, 'Imported from your existing library.', @updatedUtc,
                1, 0,
                @videoCodec, @audioCodec, @audioChannels, @releaseGroup
            )
            ON CONFLICT(movie_id, library_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                has_file = 1,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met,
                file_path = excluded.file_path,
                file_size_bytes = excluded.file_size_bytes,
                imported_utc = COALESCE(movie_wanted_state.imported_utc, excluded.imported_utc),
                last_verified_utc = excluded.last_verified_utc,
                missing_detected_utc = NULL,
                last_search_result = excluded.last_search_result,
                -- The file replaced the one that was there, so its facts
                -- replace the old ones outright rather than merging.
                video_codec = excluded.video_codec,
                audio_codec = excluded.audio_codec,
                audio_channels = excluded.audio_channels,
                release_group = excluded.release_group,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(wanted, "@movieId", movieId);
        AddParameter(wanted, "@videoCodec", fileFacts.VideoCodec);
        AddParameter(wanted, "@audioCodec", fileFacts.AudioCodec);
        AddParameter(wanted, "@audioChannels", fileFacts.AudioChannels);
        AddParameter(wanted, "@releaseGroup", fileFacts.ReleaseGroup);
        AddParameter(wanted, "@libraryId", libraryId);
        AddParameter(wanted, "@wantedStatus", NormalizeWantedStatus(request.WantedStatus));
        AddParameter(wanted, "@wantedReason", request.WantedReason.Trim());
        AddParameter(wanted, "@currentQuality", request.CurrentQuality);
        AddParameter(wanted, "@targetQuality", request.TargetQuality);
        AddParameter(wanted, "@qualityCutoffMet", request.QualityCutoffMet ? 1 : 0);
        AddParameter(wanted, "@filePath", normalizedFilePath);
        AddParameter(wanted, "@fileSizeBytes", request.FileSizeBytes);
        AddParameter(wanted, "@importedUtc", normalizedFilePath is null ? null : now.ToString("O"));
        AddParameter(wanted, "@lastVerifiedUtc", normalizedFilePath is null ? null : now.ToString("O"));
        AddParameter(wanted, "@updatedUtc", now.ToString("O"));
        await wanted.ExecuteNonQueryAsync(cancellationToken);

        return created;
    }

    public async Task<IReadOnlyList<MovieTrackedFileItem>> ListTrackedFilesAsync(
        string libraryId,
        CancellationToken cancellationToken)
    {
        var items = new List<MovieTrackedFileItem>();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                w.movie_id,
                w.library_id,
                m.title,
                m.release_year,
                w.file_path,
                w.file_size_bytes,
                w.imported_utc,
                w.last_verified_utc
            FROM movie_wanted_state w
            INNER JOIN movie_entries m ON m.id = w.movie_id
            WHERE w.library_id = @libraryId
              AND w.has_file = 1
              AND w.file_path IS NOT NULL
            ORDER BY m.title COLLATE NOCASE;
            """;
        AddParameter(command, "@libraryId", libraryId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new MovieTrackedFileItem(
                MovieId: reader.GetString(0),
                LibraryId: reader.GetString(1),
                Title: reader.GetString(2),
                ReleaseYear: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                FilePath: reader.GetString(4),
                FileSizeBytes: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                ImportedUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                LastVerifiedUtc: reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7))));
        }

        return items;
    }

    public async Task<bool> MarkTrackedFileMissingAsync(
        string movieId,
        string libraryId,
        string filePath,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_wanted_state
            SET has_file = 0,
                wanted_status = 'missing',
                wanted_reason = 'Reconciliation detected that the tracked library file is missing from disk.',
                missing_since_utc = COALESCE(missing_since_utc, @now),
                missing_detected_utc = @now,
                last_verified_utc = @now,
                updated_utc = @now
            WHERE movie_id = @movieId
              AND library_id = @libraryId
              AND file_path = @filePath;
            """;
        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@filePath", filePath);
        AddParameter(command, "@now", now.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
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

    public async Task<bool> DeferWantedSearchAsync(
        string movieId,
        string libraryId,
        DateTimeOffset deferredUntilUtc,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.DeferWantedSearchAsync(
                MediaKind.Movie,
                movieId,
                libraryId,
                deferredUntilUtc,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_wanted_state
            SET next_eligible_search_utc = @deferredUntilUtc,
                last_search_result = 'Deferred by user.',
                updated_utc = @updatedUtc
            WHERE movie_id = @movieId
              AND library_id = @libraryId
              AND wanted_status IN ('missing', 'upgrade');
            """;
        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@deferredUntilUtc", deferredUntilUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> SkipNextWantedSearchAsync(
        string movieId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.SkipNextWantedSearchAsync(
                MediaKind.Movie,
                movieId,
                libraryId,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_wanted_state
            SET skip_next_automation_search = 1,
                last_search_result = 'Will skip the next scheduled search by user request.',
                updated_utc = @updatedUtc
            WHERE movie_id = @movieId
              AND library_id = @libraryId
              AND wanted_status IN ('missing', 'upgrade');
            """;
        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ConsumeSkipNextWantedSearchAsync(
        string movieId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.ConsumeSkipNextWantedSearchAsync(
                MediaKind.Movie,
                movieId,
                libraryId,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Movies, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_wanted_state
            SET skip_next_automation_search = 0,
                updated_utc = @updatedUtc
            WHERE movie_id = @movieId
              AND library_id = @libraryId
              AND skip_next_automation_search = 1;
            """;
        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> ReevaluateLibraryWantedStateAsync(
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.ReevaluateLibraryWantedStateAsync(
                MediaKind.Movie,
                libraryId,
                cutoffQuality,
                upgradeUntilCutoff,
                upgradeUnknownItems,
                cancellationToken);
        }

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
            var decision = MediaDecisionRules.DecideWantedState(new MediaWantedDecisionInput(
                MediaType: "movies",
                HasFile: item.HasFile,
                CurrentQuality: item.CurrentQuality,
                CutoffQuality: cutoffQuality,
                UpgradeUntilCutoff: upgradeUntilCutoff,
                UpgradeUnknownItems: upgradeUnknownItems));

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
        var openCases = new List<MovieImportRecoveryCase>();
        int openCount = 0;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM movie_import_recovery_cases WHERE status = 'open';";
            openCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken));
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
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
                FROM movie_import_recovery_cases
                WHERE status = 'open'
                ORDER BY detected_utc DESC
                LIMIT 12;
                """;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                openCases.Add(ReadImportRecoveryCase(reader));
            }
        }

        return new MovieImportRecoverySummary(
            OpenCount: openCount,
            QualityCount: openCases.Count(item => item.FailureKind == "quality"),
            UnmatchedCount: openCases.Count(item => item.FailureKind == "unmatched"),
            CorruptCount: openCases.Count(item => item.FailureKind == "corrupt"),
            DownloadFailedCount: openCases.Count(item => item.FailureKind == "downloadFailed"),
            ImportFailedCount: openCases.Count(item => item.FailureKind == "importFailed"),
            RecentCases: openCases);
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
            Status: "open",
            Summary: request.Summary!.Trim(),
            RecommendedAction: string.IsNullOrWhiteSpace(request.RecommendedAction)
                ? "Review this import and decide whether Deluno should retry, rematch, or remove it."
                : request.RecommendedAction.Trim(),
            DetailsJson: string.IsNullOrWhiteSpace(request.DetailsJson) ? null : request.DetailsJson.Trim(),
            DetectedUtc: now,
            ResolvedUtc: null);

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
                status,
                summary,
                recommended_action,
                details_json,
                detected_utc
            )
            VALUES (
                @id,
                @title,
                @failureKind,
                'open',
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

        await AddImportRecoveryEventAsync(item.Id, "case_opened", "Import recovery case created.", null, cancellationToken);

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

    public async Task<MovieImportRecoveryCase?> ResolveImportRecoveryCaseAsync(
        string id,
        string status,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE movie_import_recovery_cases
                SET status = @status, resolved_utc = @resolvedUtc
                WHERE id = @id AND status = 'open';
                """;
            AddParameter(update, "@id", id);
            AddParameter(update, "@status", status);
            AddParameter(update, "@resolvedUtc", now.ToString("O"));
            var rows = await update.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 0)
            {
                return null;
            }
        }

        using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT id, title, failure_kind, status, summary, recommended_action, details_json, detected_utc, resolved_utc
            FROM movie_import_recovery_cases
            WHERE id = @id;
            """;
        AddParameter(select, "@id", id);
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadImportRecoveryCase(reader);
        }

        return null;
    }

    public async Task AddImportRecoveryEventAsync(
        string caseId,
        string eventKind,
        string message,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO movie_import_recovery_events (id, case_id, event_kind, message, metadata_json, created_utc)
            VALUES (@id, @caseId, @eventKind, @message, @metadataJson, @createdUtc);
            """;
        AddParameter(command, "@id", Guid.CreateVersion7().ToString("N"));
        AddParameter(command, "@caseId", caseId);
        AddParameter(command, "@eventKind", eventKind);
        AddParameter(command, "@message", message);
        AddParameter(command, "@metadataJson", metadataJson);
        AddParameter(command, "@createdUtc", timeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CleanupImportRecoveryCasesAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM movie_import_recovery_cases
            WHERE status IN ('resolved', 'dismissed')
              AND resolved_utc < @olderThan;
            """;
        AddParameter(command, "@olderThan", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MovieImportRecoveryCase ReadImportRecoveryCase(System.Data.Common.DbDataReader reader) =>
        new MovieImportRecoveryCase(
            Id: reader.GetString(0),
            Title: reader.GetString(1),
            FailureKind: reader.GetString(2),
            Status: reader.GetString(3),
            Summary: reader.GetString(4),
            RecommendedAction: reader.GetString(5),
            DetailsJson: reader.IsDBNull(6) ? null : reader.GetString(6),
            DetectedUtc: ParseTimestamp(reader.GetString(7)),
            ResolvedUtc: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)));

    public async Task<MovieWantedItem?> GetMovieWantedStateAsync(
        string movieId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                m.id, m.title, m.release_year, m.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc,
                w.prevent_lower_quality_replacements, w.quality_delta_last_decision
            FROM movie_wanted_state w
            INNER JOIN movie_entries m ON m.id = w.movie_id
            WHERE w.movie_id = @movieId
              AND w.library_id = @libraryId
            LIMIT 1;
            """;

        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWantedMovie(reader) : null;
    }

    public async Task<bool> UpdateMovieReplacementPolicyAsync(
        string movieId,
        string libraryId,
        bool preventLowerQualityReplacements,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_wanted_state
            SET prevent_lower_quality_replacements = @preventLowerQuality,
                updated_utc = @updatedUtc
            WHERE movie_id = @movieId
              AND library_id = @libraryId;
            """;

        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@preventLowerQuality", preventLowerQualityReplacements ? 1 : 0);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdateMovieQualityDeltaAsync(
        string movieId,
        string libraryId,
        int? qualityDelta,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_wanted_state
            SET quality_delta_last_decision = @qualityDelta,
                updated_utc = @updatedUtc
            WHERE movie_id = @movieId
              AND library_id = @libraryId;
            """;

        AddParameter(command, "@movieId", movieId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@qualityDelta", qualityDelta);
        AddParameter(command, "@updatedUtc", now.ToString("O"));

        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> DeleteAsync(string movieId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM movie_entries WHERE id = @id;";
        AddParameter(command, "@id", movieId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdateQualityProfileAsync(string movieId, string qualityProfileId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE movie_entries SET quality_profile_id = @qualityProfileId, updated_utc = @now WHERE id = @id;";
        AddParameter(command, "@id", movieId);
        AddParameter(command, "@qualityProfileId", qualityProfileId);
        AddParameter(command, "@now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdateReleaseDatesAsync(
        string movieId,
        DateOnly? inCinemas,
        DateOnly? digital,
        DateOnly? physical,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_entries
            SET in_cinemas_date = @inCinemas,
                digital_release_date = @digital,
                physical_release_date = @physical,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", movieId);
        AddParameter(command, "@inCinemas", inCinemas?.ToString("yyyy-MM-dd"));
        AddParameter(command, "@digital", digital?.ToString("yyyy-MM-dd"));
        AddParameter(command, "@physical", physical?.ToString("yyyy-MM-dd"));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdateMinimumAvailabilityAsync(
        string movieId,
        string minimumAvailability,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE movie_entries
            SET minimum_availability = @minimumAvailability,
                updated_utc = @updatedUtc
            WHERE id = @id;
            """;
        AddParameter(command, "@id", movieId);
        AddParameter(command, "@minimumAvailability", MovieAvailability.Normalize(minimumAvailability));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Films whose cinema, digital or physical release falls inside a window.
    /// Each date is its own row so the calendar can show a film twice when it
    /// reaches cinemas in one month and streaming in another.
    /// </summary>
    public async Task<IReadOnlyList<MovieCalendarItem>> ListCalendarMoviesAsync(
        DateOnly fromDate,
        DateOnly toDate,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, release_year, poster_url, monitored, kind, date,
                   EXISTS (SELECT 1 FROM movie_wanted_state w WHERE w.movie_id = m.id AND w.has_file = 1)
            FROM (
                SELECT id, title, release_year, poster_url, monitored, 'inCinemas' AS kind, in_cinemas_date AS date
                FROM movie_entries WHERE in_cinemas_date IS NOT NULL
                UNION ALL
                SELECT id, title, release_year, poster_url, monitored, 'digital', digital_release_date
                FROM movie_entries WHERE digital_release_date IS NOT NULL
                UNION ALL
                SELECT id, title, release_year, poster_url, monitored, 'physical', physical_release_date
                FROM movie_entries WHERE physical_release_date IS NOT NULL
            ) AS m
            WHERE date >= @fromDate AND date < @toDate
            ORDER BY date ASC, title ASC
            LIMIT @take;
            """;
        AddParameter(command, "@fromDate", fromDate.ToString("yyyy-MM-dd"));
        AddParameter(command, "@toDate", toDate.ToString("yyyy-MM-dd"));
        AddParameter(command, "@take", take);

        var items = new List<MovieCalendarItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!DateOnly.TryParse(reader.GetString(6), out var date))
            {
                continue;
            }

            items.Add(new MovieCalendarItem(
                MovieId: reader.GetString(0),
                Title: reader.GetString(1),
                ReleaseYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                PosterUrl: reader.IsDBNull(3) ? null : reader.GetString(3),
                Kind: reader.GetString(5),
                Date: date,
                HasFile: reader.GetInt64(7) == 1,
                Monitored: reader.GetInt32(4) == 1));
        }

        return items;
    }

    public async Task<MediaDailyMetrics> GetDailyMetricsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.GetDailyMetricsAsync(
                MediaKind.Movie,
                fromDate,
                toDate,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        var from = fromDate.ToString("yyyy-MM-dd");
        var toExclusive = toDate.AddDays(1).ToString("yyyy-MM-dd");

        var added = await ReadDailyAsync(
            connection,
            $"SELECT {DailyCounts.GroupBy("created_utc")} AS day, COUNT(*) FROM movie_entries WHERE created_utc >= @from AND created_utc < @to GROUP BY day;",
            from, toExclusive, cancellationToken);

        var before = await ReadScalarAsync(
            connection,
            "SELECT COUNT(*) FROM movie_entries WHERE created_utc < @from;",
            from, cancellationToken);

        var matched = await ReadDailyAsync(
            connection,
            $"SELECT {DailyCounts.GroupBy("created_utc")} AS day, COUNT(*) FROM movie_search_history WHERE outcome = 'matched' AND created_utc >= @from AND created_utc < @to GROUP BY day;",
            from, toExclusive, cancellationToken);

        var unmatched = await ReadDailyAsync(
            connection,
            $"SELECT {DailyCounts.GroupBy("created_utc")} AS day, COUNT(*) FROM movie_search_history WHERE outcome <> 'matched' AND created_utc >= @from AND created_utc < @to GROUP BY day;",
            from, toExclusive, cancellationToken);

        var importFailures = await ReadDailyAsync(
            connection,
            $"SELECT {DailyCounts.GroupBy("detected_utc")} AS day, COUNT(*) FROM movie_import_recovery_cases WHERE detected_utc >= @from AND detected_utc < @to GROUP BY day;",
            from, toExclusive, cancellationToken);

        return new MediaDailyMetrics(before, added, matched, unmatched, importFailures);
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadDailyAsync(
        System.Data.Common.DbConnection connection,
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
        System.Data.Common.DbConnection connection,
        string sql,
        string from,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@from", from);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static MovieListItem ReadMovie(System.Data.Common.DbDataReader reader)
    {
        return new MovieListItem(
            Id: reader.GetString(0),
            Title: reader.GetString(1),
            ReleaseYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ImdbId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Monitored: reader.GetInt32(4) == 1,
            HasFile: reader.GetInt32(5) == 1,
            MetadataProvider: reader.IsDBNull(6) ? null : reader.GetString(6),
            MetadataProviderId: reader.IsDBNull(7) ? null : reader.GetString(7),
            OriginalTitle: reader.IsDBNull(8) ? null : reader.GetString(8),
            Overview: reader.IsDBNull(9) ? null : reader.GetString(9),
            PosterUrl: reader.IsDBNull(10) ? null : reader.GetString(10),
            BackdropUrl: reader.IsDBNull(11) ? null : reader.GetString(11),
            Rating: reader.IsDBNull(12) ? null : reader.GetDouble(12),
            Ratings: BuildRatings(reader.IsDBNull(12) ? null : reader.GetDouble(12), reader.IsDBNull(15) ? null : reader.GetString(15)),
            Genres: reader.IsDBNull(13) ? null : reader.GetString(13),
            ExternalUrl: reader.IsDBNull(14) ? null : reader.GetString(14),
            MetadataJson: reader.IsDBNull(15) ? null : reader.GetString(15),
            MetadataUpdatedUtc: reader.IsDBNull(16) ? null : ParseTimestamp(reader.GetString(16)),
            CreatedUtc: ParseTimestamp(reader.GetString(17)),
            UpdatedUtc: ParseTimestamp(reader.GetString(18)),
            InCinemasDate: ReadDate(reader, 19),
            DigitalReleaseDate: ReadDate(reader, 20),
            PhysicalReleaseDate: ReadDate(reader, 21),
            MinimumAvailability: MovieAvailability.Normalize(reader.IsDBNull(22) ? null : reader.GetString(22)),
            IsAvailable: MovieAvailability.IsAvailable(
                reader.IsDBNull(22) ? null : reader.GetString(22),
                ReadDate(reader, 19),
                ReadDate(reader, 20),
                ReadDate(reader, 21),
                DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    private static DateOnly? ReadDate(System.Data.Common.DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || !DateOnly.TryParse(reader.GetString(ordinal), out var parsed) ? null : parsed;

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
            UpdatedUtc: ParseTimestamp(reader.GetString(15)),
            PreventLowerQualityReplacements: reader.GetInt64(16) == 1,
            LastQualityDeltaDecision: reader.IsDBNull(17) ? null : reader.GetInt32(17));
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

    public async Task<IReadOnlyList<CrossLibraryDuplicateItem>> FindCrossLibraryDuplicatesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                m.id, m.title, m.release_year, m.imdb_id,
                w.library_id, w.wanted_status, w.has_file, w.current_quality
            FROM movie_entries m
            JOIN movie_wanted_state w ON w.movie_id = m.id
            WHERE m.id IN (
                SELECT movie_id
                FROM movie_wanted_state
                GROUP BY movie_id
                HAVING COUNT(DISTINCT library_id) > 1
            )
            ORDER BY m.title ASC, m.id ASC, w.library_id ASC;
            """;

        var byMovieId = new Dictionary<string, (string Title, int? Year, string? ImdbId, List<DuplicateLibraryEntry> Entries)>();

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var movieId = reader.GetString(0);
            var libraryEntry = new DuplicateLibraryEntry(
                LibraryId: reader.GetString(4),
                LibraryName: reader.GetString(4),
                WantedStatus: reader.GetString(5),
                HasFile: reader.GetInt64(6) == 1,
                CurrentQuality: reader.IsDBNull(7) ? null : reader.GetString(7));

            if (!byMovieId.TryGetValue(movieId, out var existing))
            {
                byMovieId[movieId] = (
                    Title: reader.GetString(1),
                    Year: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    ImdbId: reader.IsDBNull(3) ? null : reader.GetString(3),
                    Entries: [libraryEntry]);
            }
            else
            {
                existing.Entries.Add(libraryEntry);
            }
        }

        return byMovieId.Select(kvp => new CrossLibraryDuplicateItem(
            MovieId: kvp.Key,
            Title: kvp.Value.Title,
            ReleaseYear: kvp.Value.Year,
            ImdbId: kvp.Value.ImdbId,
            Libraries: kvp.Value.Entries)).ToArray();
    }

    public async Task<int> ReassignLibraryAsync(
        IReadOnlyList<string> movieIds,
        string fromLibraryId,
        string toLibraryId,
        CancellationToken cancellationToken)
    {
        if (movieIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Movies,
            cancellationToken);

        var ids = string.Join(",", movieIds.Select((_, i) => $"@id{i}"));
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE movie_wanted_state
            SET library_id = @toLibraryId
            WHERE library_id = @fromLibraryId
              AND movie_id IN ({ids});
            """;

        AddParameter(command, "@fromLibraryId", fromLibraryId);
        AddParameter(command, "@toLibraryId", toLibraryId);
        for (var i = 0; i < movieIds.Count; i++)
        {
            AddParameter(command, $"@id{i}", movieIds[i]);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MovieWantedItem MapWanted(MediaWantedItem item)
        => new(
            MovieId: item.Id,
            Title: item.Title,
            ReleaseYear: item.Year,
            ImdbId: item.ImdbId,
            LibraryId: item.LibraryId,
            WantedStatus: item.WantedStatus,
            WantedReason: item.WantedReason,
            HasFile: item.HasFile,
            CurrentQuality: item.CurrentQuality,
            TargetQuality: item.TargetQuality,
            QualityCutoffMet: item.QualityCutoffMet,
            MissingSinceUtc: item.MissingSinceUtc,
            LastSearchUtc: item.LastSearchUtc,
            NextEligibleSearchUtc: item.NextEligibleSearchUtc,
            LastSearchResult: item.LastSearchResult,
            PreventLowerQualityReplacements: item.PreventLowerQualityReplacements,
            LastQualityDeltaDecision: item.LastQualityDeltaDecision,
            UpdatedUtc: item.UpdatedUtc);

    private static MovieSearchHistoryItem MapSearchHistory(MediaSearchHistoryItem item)
        => new(
            Id: item.Id,
            MovieId: item.MediaId,
            LibraryId: item.LibraryId,
            TriggerKind: item.TriggerKind,
            Outcome: item.Outcome,
            ReleaseName: item.ReleaseName,
            IndexerName: item.IndexerName,
            DetailsJson: item.DetailsJson,
            CreatedUtc: item.CreatedUtc);

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
