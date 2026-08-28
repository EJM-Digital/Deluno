using MetadataSearchResult = Deluno.Integrations.Metadata.MetadataSearchResult;
using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Migrations;
using Microsoft.Data.Sqlite;
using Deluno.Libraries.Data;

namespace Deluno.Series.Data;

public sealed class SqliteSeriesCatalogRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IMediaStateRepository? sharedMediaStateRepository = null,
    ILibrarySubtitlePreferences? librarySubtitlePreferences = null)
    : ISeriesCatalogRepository
{
    private readonly IMediaStateRepository? sharedMediaStateRepository = sharedMediaStateRepository;
    private readonly ILibrarySubtitlePreferences? librarySubtitlePreferences = librarySubtitlePreferences;

    /// <summary>
    /// The shared media state, injected or built.
    ///
    /// <para>The parameter is optional so a test can construct this repository
    /// with two arguments, and for a long time that optionality meant a whole
    /// second implementation of every write. Building one on demand keeps the
    /// convenience and removes the fork: there is one statement, and the tests
    /// exercise the same one production does.</para>
    /// </summary>
    private IMediaStateRepository SharedMediaState =>
        sharedMediaStateRepository ?? new SqliteMediaStateRepository(databaseConnectionFactory, timeProvider);

    public async Task<SeriesListItem> AddAsync(CreateSeriesRequest request, CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            var id = await sharedMediaStateRepository.AddAsync(
                MediaKind.Series,
                new MediaEntryCreate(
                    request.Title!,
                    request.StartYear,
                    request.ImdbId,
                    request.Monitored,
                    request.MetadataProvider,
                    request.MetadataProviderId,
                    request.OriginalTitle,
                    request.Overview,
                    request.PosterUrl,
                    request.BackdropUrl,
                    request.Rating,
                    request.Genres,
                    request.ExternalUrl,
                    request.MetadataJson),
                cancellationToken);
            return (await GetByIdAsync(id, cancellationToken))!;
        }

        var now = timeProvider.GetUtcNow();
        var series = new SeriesListItem(
            Id: Guid.CreateVersion7().ToString("N"),
            Title: request.Title!.Trim(),
            StartYear: request.StartYear,
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
            DelunoDatabaseNames.Series,
            cancellationToken);

        var existing = await FindExistingSeriesAsync(connection, series, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series_entries (
                id,
                title,
                start_year,
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
                @startYear,
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

        AddParameter(command, "@id", series.Id);
        AddParameter(command, "@title", series.Title);
        AddParameter(command, "@startYear", series.StartYear);
        AddParameter(command, "@imdbId", series.ImdbId);
        AddParameter(command, "@monitored", series.Monitored ? 1 : 0);
        AddParameter(command, "@metadataProvider", series.MetadataProvider);
        AddParameter(command, "@metadataProviderId", series.MetadataProviderId);
        AddParameter(command, "@originalTitle", series.OriginalTitle);
        AddParameter(command, "@overview", series.Overview);
        AddParameter(command, "@posterUrl", series.PosterUrl);
        AddParameter(command, "@backdropUrl", series.BackdropUrl);
        AddParameter(command, "@rating", series.Rating);
        AddParameter(command, "@genres", series.Genres);
        AddParameter(command, "@externalUrl", series.ExternalUrl);
        AddParameter(command, "@metadataJson", series.MetadataJson);
        AddParameter(command, "@metadataUpdatedUtc", series.MetadataUpdatedUtc?.ToString("O"));
        AddParameter(command, "@createdUtc", series.CreatedUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", series.UpdatedUtc.ToString("O"));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            existing = await FindExistingSeriesAsync(connection, series, cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }

        return series;
    }

    public async Task<SeriesListItem?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            var entry = await sharedMediaStateRepository.GetByIdAsync(
                MediaKind.Series,
                id,
                cancellationToken);
            return entry is null
                ? null
                : new SeriesListItem(
                    Id: entry.Id,
                    Title: entry.Title,
                    StartYear: entry.Year,
                    ImdbId: entry.ImdbId,
                    Monitored: entry.Monitored,
                    HasFile: entry.HasFile,
                    MetadataProvider: entry.MetadataProvider,
                    MetadataProviderId: entry.MetadataProviderId,
                    OriginalTitle: entry.OriginalTitle,
                    Overview: entry.Overview,
                    PosterUrl: entry.PosterUrl,
                    BackdropUrl: entry.BackdropUrl,
                    Rating: entry.Rating,
                    Ratings: BuildRatings(entry.Rating, entry.MetadataJson),
                    Genres: entry.Genres,
                    ExternalUrl: entry.ExternalUrl,
                    MetadataJson: entry.MetadataJson,
                    MetadataUpdatedUtc: entry.MetadataUpdatedUtc,
                    CreatedUtc: entry.CreatedUtc,
                    UpdatedUtc: entry.UpdatedUtc,
                    CurrentQuality: entry.CurrentQuality,
                    LibraryId: entry.LibraryId,
                    WantedStatus: entry.WantedStatus,
                    WantedReason: entry.WantedReason,
                    TargetQuality: entry.TargetQuality,
                    QualityCutoffMet: entry.QualityCutoffMet,
                    LastSearchUtc: entry.LastSearchUtc,
                    NextEligibleSearchUtc: entry.NextEligibleSearchUtc);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                s.id,
                s.title,
                s.start_year,
                s.imdb_id,
                s.monitored,
                {CatalogueWantedState.HasFileColumn},
                s.metadata_provider,
                s.metadata_provider_id,
                s.original_title,
                s.overview,
                s.poster_url,
                s.backdrop_url,
                s.rating,
                s.genres,
                s.external_url,
                s.metadata_json,
                s.metadata_updated_utc,
                s.created_utc,
                s.updated_utc,
                ws.current_quality,
            {CatalogueWantedState.PageColumns}
            FROM series_entries s
            {CatalogueWantedState.Join("s", "series_wanted_state", "series_id", scopedToLibrary: false)}
            WHERE s.id = @id
            LIMIT 1;
            """;

        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        // Ordinal 19 is the current quality; the search state follows it, in the
        // order CatalogueWantedState.PageColumns declares.
        var singleWanted = CatalogueWantedState.Read(reader, 20);

        return ReadSeries(reader) with
        {
            CurrentQuality = reader.IsDBNull(19) ? null : reader.GetString(19),
            LibraryId = singleWanted.LibraryId,
            WantedStatus = singleWanted.WantedStatus,
            WantedReason = singleWanted.WantedReason,
            TargetQuality = singleWanted.TargetQuality,
            QualityCutoffMet = singleWanted.QualityCutoffMet,
            LastSearchUtc = singleWanted.LastSearchUtc,
            NextEligibleSearchUtc = singleWanted.NextEligibleSearchUtc
        };
    }

    private static async Task<SeriesListItem?> FindExistingSeriesAsync(
        System.Data.Common.DbConnection connection,
        SeriesListItem series,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id,
                s.title,
                s.start_year,
                s.imdb_id,
                s.monitored,
                COALESCE(MAX(w.has_file), 0) AS has_file,
                s.metadata_provider,
                s.metadata_provider_id,
                s.original_title,
                s.overview,
                s.poster_url,
                s.backdrop_url,
                s.rating,
                s.genres,
                s.external_url,
                s.metadata_json,
                s.metadata_updated_utc,
                s.created_utc,
                s.updated_utc
            FROM series_entries s
            LEFT JOIN series_wanted_state w ON w.series_id = s.id
            WHERE
                (@imdbId IS NOT NULL AND s.imdb_id = @imdbId)
                OR (
                    @metadataProvider IS NOT NULL
                    AND @metadataProviderId IS NOT NULL
                    AND s.metadata_provider = @metadataProvider
                    AND s.metadata_provider_id = @metadataProviderId
                )
                OR (
                    lower(s.title) = lower(@title)
                    AND COALESCE(s.start_year, -1) = COALESCE(@startYear, -1)
                )
            GROUP BY s.id
            ORDER BY s.created_utc ASC
            LIMIT 1;
            """;
        AddParameter(command, "@imdbId", series.ImdbId);
        AddParameter(command, "@metadataProvider", series.MetadataProvider);
        AddParameter(command, "@metadataProviderId", series.MetadataProviderId);
        AddParameter(command, "@title", series.Title);
        AddParameter(command, "@startYear", series.StartYear);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSeries(reader) : null;
    }

    public async Task RecordMetadataAttemptAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE series_entries
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
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT COUNT(*)
            FROM series_entries
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
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE series_entries
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
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        // See the movie repository's equivalent: filter/order/limit in SQL so a
        // large backlog never materialises the whole catalogue.
        command.CommandText =
            $"""
            SELECT id, title, start_year
            FROM series_entries
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

    public async Task<string?> FindExistingIdAsync(
        string title,
        int? startYear,
        string? imdbId,
        string? metadataProvider,
        string? metadataProviderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();

        // The same three rules AddAsync matches on, and each has an index:
        // ix_series_entries_imdb_id, ix_series_entries_metadata_provider_id and
        // ix_series_entries_title_year. No join and no GROUP BY -- those are in
        // AddAsync's version only because it returns the whole row.
        command.CommandText =
            """
            SELECT id
            FROM series_entries
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
                    AND COALESCE(start_year, -1) = COALESCE(@startYear, -1)
                )
            ORDER BY created_utc ASC
            LIMIT 1;
            """;
        AddParameter(command, "@imdbId", NormalizeExternalId(imdbId));
        AddParameter(command, "@metadataProvider", NormalizeExternalId(metadataProvider));
        AddParameter(command, "@metadataProviderId", NormalizeExternalId(metadataProviderId));
        AddParameter(command, "@title", title.Trim());
        AddParameter(command, "@startYear", startYear);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>
    private static string CatalogueStateScope(string? libraryId)
        => string.IsNullOrWhiteSpace(libraryId) ? string.Empty : " AND w.library_id = @libraryId";

    private static string CatalogueLibraryFilter(string? libraryId)
        => string.IsNullOrWhiteSpace(libraryId)
            ? string.Empty
            : "EXISTS(SELECT 1 FROM series_wanted_state w WHERE w.series_id = s.id AND w.library_id = @libraryId)";

    /// <summary>
    /// "Deluno has this show", for the counts above the shelf.
    ///
    /// <para><b>Read from the rung, not from <c>has_file</c>.</b> That column
    /// says one arbitrary episode file exists, which for a collection is not the
    /// same question at all — a show with three of twenty episodes has a file and
    /// is not "had". Rebuilding the ladder here out of <c>has_file</c> and
    /// <c>quality_cutoff_met</c> was the ladder written a third time, and it is
    /// why the chips summed to seven for six shows.</para>
    /// </summary>
    private static string CatalogueHasFileFor(string? libraryId)
        => $"EXISTS(SELECT 1 FROM series_wanted_state w WHERE w.series_id = s.id{CatalogueStateScope(libraryId)} AND w.wanted_status IN ('{WantedStatuses.Covered}', '{WantedStatuses.Upgrade}'))";

    /// <summary>
    /// "This entry is in one particular wanted state." The counts the toolbar
    /// prints need Quality met and Upcoming, which are states rather than
    /// file-presence facts and so cannot be derived from <c>has_file</c>.
    /// </summary>
    private static string CatalogueWantedIs(string? libraryId, string wantedStatus)
        => $"EXISTS(SELECT 1 FROM series_wanted_state w WHERE w.series_id = s.id{CatalogueStateScope(libraryId)} AND w.wanted_status = '{wantedStatus}')";

    /// <summary>
    /// "Deluno has all of this show and some of it could be better." Also from
    /// the rung, for the same reason: <c>has_file = 1 AND quality_cutoff_met = 0</c>
    /// was true of a show holding one below-cutoff episode out of eighty-seven,
    /// so a barely-started show counted as Upgradable <i>and</i> as Missing.
    /// </summary>
    private static string CatalogueUpgradeFor(string? libraryId)
        => CatalogueWantedIs(libraryId, WantedStatuses.Upgrade);

    /// <summary>
    /// Where <see cref="CatalogueWantedState.PageColumns"/> begins in the page
    /// projection below. Named rather than counted at the call site, because
    /// every ordinal from there on moves together.
    /// </summary>
    /// <summary>
    /// The score columns the page carries, so a shelf can show a particular
    /// source's number without reading a metadata blob per row. Generated and
    /// counted, exactly as on the movie catalogue, so a fifth source moves
    /// <see cref="WantedStateOrdinal"/> with it.
    /// </summary>
    private static readonly string[] RatingColumns =
        [.. RatingSources.All.SelectMany(source => source.VotesColumn is null
            ? new[] { source.ScoreColumn }
            : [source.ScoreColumn, source.VotesColumn])];

    private const int RatingOrdinal = 29;

    private static readonly int WantedStateOrdinal = RatingOrdinal + RatingColumns.Length;

    /// <summary>
    /// The projection a catalogue page returns. Matches <see cref="ReadSeries"/>
    /// ordinal for ordinal, then the file's own facts — size, quality, codecs —
    /// which the list displays and once read out of an empty metadata blob, and
    /// finally the search state, which the grid used to fetch separately from a
    /// summary capped at 25 titles.
    ///
    /// All of it comes from the single wanted-state row
    /// <see cref="CatalogueWantedState.Join"/> binds, so the fields describe one
    /// library's copy rather than a different one each.
    /// </summary>
    private static string CataloguePageColumns =>
        $"""
            s.id,
            s.title,
            s.start_year,
            s.imdb_id,
            s.monitored,
            {CatalogueWantedState.HasFileColumn},
            s.metadata_provider,
            s.metadata_provider_id,
            s.original_title,
            s.overview,
            s.poster_url,
            s.backdrop_url,
            s.rating,
            s.genres,
            s.external_url,
            NULL AS metadata_json,
            s.metadata_updated_utc,
            s.created_utc,
            s.updated_utc,
            ws.file_size_bytes,
            ws.current_quality,
            ws.file_path,
            ws.video_codec,
            ws.audio_codec,
            ws.audio_channels,
            ws.release_group,
            s.runtime_minutes,
            s.popularity,
            s.vote_count,
            {string.Join(", ", RatingColumns.Select(column => "s." + column))},
        {CatalogueWantedState.PageColumns}
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


    /// <summary>
    /// The quality ladder, pushed into this catalogue's own database so a shelf
    /// can be ordered by it, and the cached ranks recomputed so the new order is
    /// true at once rather than the next time each file changes.
    /// </summary>
    /// <summary>
    /// Push the ladder into this catalogue's own database.
    ///
    /// <para>SQL cannot ask C# what a tier is worth or how big a file at that
    /// tier should be, and both questions have to be answered beside the file —
    /// an ORDER BY needs a number on an indexed column, and #309's conformance
    /// verdict needs the bounds next to the size. So the model is pushed here
    /// whenever it is saved.</para>
    ///
    /// <para><b>And what is cached is recomputed.</b> Re-ranking a tier or
    /// widening its size rule changes the answer for titles nobody has touched,
    /// which no trigger will ever see — a trigger fires on a write, and editing
    /// a rule is not a write to any of these rows. Without the recompute the
    /// shelf would be right about the ladder it had when each file last
    /// changed.</para>
    /// </summary>
    public async Task SyncQualityRanksAsync(
        IReadOnlyList<QualityTierDefinition> tiers,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM quality_ranks;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tier in tiers)
        {
            var (floor, ceiling) = QualityTierBytes.ForEpisode(tier);

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR REPLACE INTO quality_ranks (name, rank, floor_bytes, ceiling_bytes) VALUES (@name, @rank, @floor, @ceiling);";
            AddParameter(insert, "@name", tier.Name);
            AddParameter(insert, "@rank", tier.Rank);
            AddParameter(insert, "@floor", floor);
            AddParameter(insert, "@ceiling", ceiling);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var recompute = connection.CreateCommand())
        {
            recompute.Transaction = transaction;
            // The same pick the trigger and CatalogueWantedState.Join use.
            recompute.CommandText = $"""
                UPDATE series_entries SET primary_quality_rank = (
                    SELECT (SELECT r.rank FROM quality_ranks r WHERE r.name = pick.current_quality)
                    FROM series_wanted_state pick
                    WHERE pick.series_id = series_entries.id
                    ORDER BY pick.has_file DESC, pick.quality_cutoff_met ASC, pick.library_id ASC
                    LIMIT 1
                );

                UPDATE series_entries SET size_conformance = {CatalogueConformanceMigrationSql.Verdict("series_entries", "series_wanted_state", "series_id", "series_entries.id")};
                """;
            await recompute.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListGenresAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT genres
            FROM series_entries
            WHERE genres IS NOT NULL AND genres <> '';
            """;

        var genres = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            foreach (var genre in reader.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                genres.Add(genre);
            }
        }

        return [.. genres];
    }

    /// <summary>
    /// Brings up to date the handful of shows whose progress figures have gone
    /// stale since anybody last looked, and does nothing at all for the rest.
    ///
    /// <para><b>Why this exists on a read path.</b> Every other cached fact in
    /// the catalogue changes when something is written, so a trigger keeps it
    /// true. How far through a show you are changes because <i>time passed</i> —
    /// nothing is written when Thursday's episode airs — so there is no write to
    /// hang a trigger on and a stored number would be wrong by the following
    /// morning.</para>
    ///
    /// <para><b>Why it costs nothing.</b> <c>next_air_date_utc</c> is stored
    /// beside the counts and is exactly the moment they stop being true. A
    /// finished show has none and is never looked at again; a running show
    /// between episodes has one in the future and is skipped. What is left is
    /// the shows that aired something since the last look, found through a
    /// partial index on that column, which on any real library is nothing or
    /// close to it.</para>
    ///
    /// <para><b>And why only when it is asked for.</b> #322 rule 3: a page
    /// asking for nothing runs exactly the query it ran before any of this
    /// existed. The counts a page <i>displays</i> are already worked out exactly,
    /// per page, from the episodes themselves — these columns exist so a library
    /// can be filtered and ordered by them, so they only have to be true when
    /// somebody filters or orders by them.</para>
    /// </summary>
    private async Task RefreshExpiredProgressAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = V0020SeriesProgressFacts.RecomputeSql;

        // Formatted as the column is written. A DateTimeOffset renders its
        // offset as +00:00 and a DateTime renders it as Z, and SQLite compares
        // these as text.
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CataloguePage<SeriesListItem>> ListPageAsync(
        CatalogueQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = new PageRequest(query.PageSize, query.PageToken).BoundedPageSize;
        var sort = CatalogueSortFields.Normalize(query.Sort, MediaKind.Series);
        var status = CatalogueStatusFilters.Normalize(query.Status);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var libraryId = string.IsNullOrWhiteSpace(query.LibraryId) ? null : query.LibraryId.Trim();
        var token = CataloguePageToken.Decode(query.PageToken);

        var sortExpression = CatalogueKeyset.SortExpression(sort, "s", "start_year");
        var where = CatalogueKeyset.CombineFilters(
            search is null ? string.Empty : CatalogueKeyset.SearchFilter("s"),
            CatalogueLibraryFilter(libraryId),
            CatalogueKeyset.MonitoredFilter(query.Monitored, "s"),
            CatalogueKeyset.StatusFilter(
                status,
                "s",
                CatalogueHasFileFor(libraryId),
                CatalogueUpgradeFor(libraryId),
                CatalogueWantedIs(libraryId, WantedStatuses.Covered),
                CatalogueWantedIs(libraryId, WantedStatuses.Upcoming),
                CatalogueWantedIs(libraryId, WantedStatuses.Downloading)),
            // Quality and size read `ws` — the one wanted-state row this page
            // speaks for — rather than an EXISTS over all of them, so what the
            // filter selects and what the row displays cannot disagree.
            CatalogueKeyset.CustomFilters(query.Filters, MediaKind.Series, "s", "start_year"),
            token is null ? string.Empty : CatalogueKeyset.SeekPredicate(sortExpression, "s", query.Descending));

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        // Only when the shelf is actually being narrowed or reordered. #322
        // rule 3: a page asking for nothing runs exactly the query it ran before
        // any of this existed, and an unfiltered page never reads these columns.
        if (!(query.Filters?.IsEmpty ?? true))
        {
            await RefreshExpiredProgressAsync(connection, cancellationToken);
        }

        var items = new List<SeriesListItem>(pageSize + 1);
        var sortValues = new List<string?>(pageSize + 1);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT
                {CataloguePageColumns},
                    {sortExpression} AS sort_value
                FROM series_entries s
                {CatalogueWantedState.Join("s", "series_wanted_state", "series_id", libraryId is not null)}
                WHERE {where}
                ORDER BY {CatalogueKeyset.OrderBy(sortExpression, "s", query.Descending)}
                LIMIT @fetchCount;
                """;

            AddParameter(command, "@fetchCount", pageSize + 1);
            CatalogueKeyset.BindSearch(command, search);
            CatalogueKeyset.BindCustomFilters(command, query.Filters, MediaKind.Series, timeProvider.GetUtcNow());
            AddParameter(command, "@libraryId", libraryId);
            if (token is not null)
            {
                CatalogueKeyset.BindSeek(command, token, sort);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var fileSizeBytes = reader.IsDBNull(19) ? (long?)null : reader.GetInt64(19);
                var runtimeMinutes = reader.IsDBNull(26) ? (int?)null : reader.GetInt32(26);

                var wanted = CatalogueWantedState.Read(reader, WantedStateOrdinal);

                items.Add(ReadSeries(reader) with
                {
                    // From the columns, not the blob — the page projects
                    // NULL AS metadata_json on purpose. See the movie
                    // catalogue for the failure this fixes.
                    Ratings = ReadRatingColumns(reader),
                    FileSizeBytes = fileSizeBytes,
                    CurrentQuality = reader.IsDBNull(20) ? null : reader.GetString(20),
                    FilePath = reader.IsDBNull(21) ? null : reader.GetString(21),
                    VideoCodec = reader.IsDBNull(22) ? null : reader.GetString(22),
                    AudioCodec = reader.IsDBNull(23) ? null : reader.GetString(23),
                    AudioChannels = reader.IsDBNull(24) ? null : reader.GetString(24),
                    ReleaseGroup = reader.IsDBNull(25) ? null : reader.GetString(25),
                    RuntimeMinutes = runtimeMinutes,
                    Popularity = reader.IsDBNull(27) ? null : reader.GetDouble(27),
                    VoteCount = reader.IsDBNull(28) ? null : reader.GetInt32(28),
                    // Derived here rather than stored, so it cannot go stale
                    // against either the file or the runtime it comes from.
                    ApproximateBitrateMbps = MediaFileFacts.ApproximateBitrateMbps(fileSizeBytes, runtimeMinutes),
                    LibraryId = wanted.LibraryId,
                    WantedStatus = wanted.WantedStatus,
                    WantedReason = wanted.WantedReason,
                    TargetQuality = wanted.TargetQuality,
                    QualityCutoffMet = wanted.QualityCutoffMet,
                    LastSearchUtc = wanted.LastSearchUtc,
                    NextEligibleSearchUtc = wanted.NextEligibleSearchUtc
                });

                sortValues.Add(CatalogueKeyset.ReadSortValue(reader, WantedStateOrdinal + CatalogueWantedState.PageColumnCount));
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

        var withEpisodes = await AttachEpisodeProgressAsync(connection, items, cancellationToken);
        var withSubtitles = await AttachSubtitleCountsAsync(connection, withEpisodes, cancellationToken);

        // Counting scans, so it happens once per filter rather than on every
        // page of it. A continuation page keeps the numbers the caller has.
        if (token is not null)
        {
            return new CataloguePage<SeriesListItem>(withSubtitles, nextPageToken, hasMore, null, null);
        }

        var facets = await CountCatalogueFacetsAsync(connection, search, libraryId, status, query.Monitored, query.Filters, cancellationToken);

        return new CataloguePage<SeriesListItem>(
            withSubtitles,
            nextPageToken,
            hasMore,
            facets.TotalFor(status),
            facets);
    }

    /// <summary>
    /// How far through its aired episodes each show on the page is.
    ///
    /// One grouped pass over the page's shows, rather than five aggregates per
    /// row inside the page query: the work is bounded by the page, not by the
    /// catalogue, so page four hundred of twenty thousand shows costs what page
    /// one costs — which is the whole point of the seek above it.
    ///
    /// Aired means aired: an episode with no air date, or one still to come, is
    /// not something the library is missing, and counting it as such would leave
    /// every ongoing show permanently short of itself.
    /// </summary>
    /// <summary>
    /// Names the rule that decided a show's rung, so a stored row says which
    /// version of the thinking produced it.
    /// </summary>
    private const string SeriesRungPolicyVersion = "series-rung/episodes-v1";

    /// <summary>
    /// Why a show is on the rung it is on, in the words Activity and the detail
    /// page print. Counts, because "missing" on a show is only useful with the
    /// number beside it.
    /// </summary>
    private static string SeriesRungReason(string status, int aired, int airedWithFile) => status switch
    {
        WantedStatuses.Upcoming => "Nothing has aired yet.",
        WantedStatuses.Covered => $"All {aired} aired episode{(aired == 1 ? "" : "s")} are here at the quality you asked for.",
        WantedStatuses.Upgrade => $"All {aired} aired episode{(aired == 1 ? "" : "s")} are here, and some could be better.",
        _ => aired == 0
            ? "Deluno has not catalogued this show's episodes yet."
            : $"{airedWithFile} of {aired} aired episodes are here."
    };

    private async Task<IReadOnlyList<SeriesListItem>> AttachEpisodeProgressAsync(
        System.Data.Common.DbConnection connection,
        List<SeriesListItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        var now = timeProvider.GetUtcNow().ToString("O");
        var idParameters = string.Join(", ", items.Select((_, index) => $"@id{index}"));

        var progress = new Dictionary<string, (int Total, int Aired, int AiredWithFile, int AiredUpgradable, string? NextAir)>(items.Count);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT
                    e.series_id,
                    COUNT(*),
                    SUM(CASE WHEN e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now THEN 1 ELSE 0 END),
                    SUM(CASE WHEN e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now AND e.has_file = 1 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now AND e.has_file = 1 AND e.quality_cutoff_met = 0 THEN 1 ELSE 0 END),
                    MIN(CASE WHEN e.air_date_utc > @now THEN e.air_date_utc END)
                FROM episode_entries e
                WHERE e.series_id IN ({idParameters})
                GROUP BY e.series_id;
                """;

            AddParameter(command, "@now", now);
            for (var index = 0; index < items.Count; index++)
            {
                AddParameter(command, $"@id{index}", items[index].Id);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                progress[reader.GetString(0)] = (
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }

        var attached = new List<SeriesListItem>(items.Count);
        foreach (var item in items)
        {
            if (!progress.TryGetValue(item.Id, out var counts))
            {
                // A show whose episodes Deluno has not catalogued yet. Zero
                // episodes is the truth; it is not zero of some known total.
                attached.Add(item);
                continue;
            }

            attached.Add(item with
            {
                EpisodeCount = counts.Total,
                AiredEpisodeCount = counts.Aired,
                AiredWithFileCount = counts.AiredWithFile,
                AiredUpgradableCount = counts.AiredUpgradable,
                NextAirDateUtc = counts.NextAir is null ? null : ParseTimestamp(counts.NextAir),
                // The rung, decided from the episodes rather than from the
                // title-level row — which described one arbitrary file and made
                // a show with three of twenty episodes read "Quality met".
                //
                // Computed here because the numbers are already in hand, so it
                // costs nothing, and served rather than recomputed in the
                // browser: the browser used to derive its own answer from these
                // same fields, which is how the shelf and the chips came to
                // disagree about the same show.
                WantedStatus = SeriesRung.From(
                    counts.Aired,
                    counts.AiredWithFile,
                    counts.AiredUpgradable,
                    counts.NextAir is not null)
            });
        }

        return attached;
    }

    /// <summary>
    /// How many subtitle languages each show on the page was asked for, per
    /// episode, and how many it holds across the episodes it has.
    ///
    /// DESIGN-002 asked for this to ride the episode progress pass above.
    /// It cannot, and the reason is worth writing down: held is not one number
    /// per show. It is a number per language, because it only counts languages
    /// the show's own library asked for, and a page can hold two libraries that
    /// asked for different ones. Folding that into the progress query means
    /// either joining the subtitle rows in — which fans every existing count
    /// out and makes all five of them depend on a DISTINCT — or writing one
    /// library's language list into a query that serves both.
    ///
    /// So it is its own grouped pass over the page's shows, keyed and indexed
    /// the same way, and bounded by the page rather than the catalogue. It runs
    /// once per library present on the page, which on a library-filtered page
    /// is always one, and on a shelf nobody has asked for subtitles is none.
    /// </summary>
    private async Task<IReadOnlyList<SeriesListItem>> AttachSubtitleCountsAsync(
        System.Data.Common.DbConnection connection,
        IReadOnlyList<SeriesListItem> items,
        CancellationToken cancellationToken)
    {
        if (librarySubtitlePreferences is null || items.Count == 0)
        {
            return items;
        }

        var preferences = await librarySubtitlePreferences.GetSubtitlePreferencesAsync(cancellationToken);
        var counts = await CatalogueSubtitleRollup.ForPageAsync(
            connection,
            MediaKind.Series,
            items.Select(item => (item.Id, item.LibraryId)).ToArray(),
            preferences,
            timeProvider.GetUtcNow(),
            cancellationToken);

        if (counts.Count == 0)
        {
            return items;
        }

        return items
            .Select(item => counts.TryGetValue(item.Id, out var count)
                ? item with
                {
                    SubtitleLanguagesWanted = count.WantedPerFile,
                    SubtitleLanguagesHeld = count.Held,
                    SubtitleLanguagesSettled = count.Settled
                }
                : item)
            .ToArray();
    }

    /// <summary>
    /// Every quick-filter count in one pass over the searched set, so the five
    /// numbers above the list cost one scan rather than five queries — or, as
    /// before, a download of the whole catalogue and a count in the browser.
    /// </summary>
    private async Task<CatalogueFacets> CountCatalogueFacetsAsync(
        System.Data.Common.DbConnection connection,
        string? search,
        string? libraryId,
        string status,
        bool? monitored,
        CatalogueFilters? filters,
        CancellationToken cancellationToken)
    {
        // The counts above the shelf count the same rows the shelf shows, so
        // the custom filters apply here too — and the join they need is added
        // only when one of them is actually asking for it, so an unfiltered
        // page runs exactly the query it ran before this existed.
        // Asked precisely, not "are there any filters at all": narrowing by
        // year or genre reads the entries table and needs no join, so it still
        // costs the counts nothing.
        var wantedJoin = filters?.NeedsWantedState(MediaKind.Series) == true
            ? CatalogueWantedState.Join("s", "series_wanted_state", "series_id", libraryId is not null)
            : string.Empty;

        var where = CatalogueKeyset.CombineFilters(
            search is null ? string.Empty : CatalogueKeyset.SearchFilter("s"),
            CatalogueLibraryFilter(libraryId),
            CatalogueKeyset.CustomFilters(filters, MediaKind.Series, "s", "start_year"));

        // The two axes cross here.
        //
        // Every *state* count is taken within the monitoring scope you have
        // chosen, so "Missing 3" under Unmonitored means three unmonitored
        // titles are missing. The two *monitoring* counts are taken within the
        // status scope, so the monitoring control can say how many of the
        // Missing titles fall each side. One pass either way — the base WHERE
        // is the search and the library, and each axis is a CASE arm.
        var monitoredArm = CatalogueKeyset.Always(CatalogueKeyset.MonitoredFilter(monitored, "s"));
        var statusArm = CatalogueKeyset.Always(CatalogueKeyset.StatusFilter(
            status,
            "s",
            CatalogueHasFileFor(libraryId),
            CatalogueUpgradeFor(libraryId),
            CatalogueWantedIs(libraryId, WantedStatuses.Covered),
            CatalogueWantedIs(libraryId, WantedStatuses.Upcoming),
                CatalogueWantedIs(libraryId, WantedStatuses.Downloading),
                CatalogueWantedIs(libraryId, WantedStatuses.Airing)));

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                SUM(CASE WHEN {monitoredArm} THEN 1 ELSE 0 END),
                SUM(CASE WHEN {statusArm} AND s.monitored = 1 THEN 1 ELSE 0 END),
                SUM(CASE WHEN {statusArm} AND s.monitored = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN {monitoredArm} AND {CatalogueHasFileFor(libraryId)} THEN 1 ELSE 0 END),
                SUM(CASE WHEN {monitoredArm} AND NOT ({CatalogueHasFileFor(libraryId)} OR {CatalogueWantedIs(libraryId, WantedStatuses.Upcoming)}) THEN 1 ELSE 0 END),
                SUM(CASE WHEN {monitoredArm} AND {CatalogueUpgradeFor(libraryId)} THEN 1 ELSE 0 END),
                SUM(CASE WHEN {monitoredArm} AND {CatalogueWantedIs(libraryId, WantedStatuses.Covered)} THEN 1 ELSE 0 END),
                SUM(CASE WHEN {monitoredArm} AND {CatalogueWantedIs(libraryId, WantedStatuses.Upcoming)} THEN 1 ELSE 0 END),
                SUM(CASE WHEN {monitoredArm} AND {CatalogueWantedIs(libraryId, WantedStatuses.Downloading)} THEN 1 ELSE 0 END),
                SUM(CASE WHEN {monitoredArm} AND {CatalogueWantedIs(libraryId, WantedStatuses.Airing)} THEN 1 ELSE 0 END)
            FROM series_entries s
            {wantedJoin}
            WHERE {where};
            """;
        CatalogueKeyset.BindSearch(command, search);
        CatalogueKeyset.BindCustomFilters(command, filters, MediaKind.Series, timeProvider.GetUtcNow());
        AddParameter(command, "@libraryId", libraryId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CatalogueFacets(0, 0, 0, 0, 0, 0, 0, 0);
        }

        return new CatalogueFacets(
            All: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            Monitored: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            Unmonitored: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            Downloaded: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            Missing: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Upgrades: reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            Covered: reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            Upcoming: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
            Downloading: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            Airing: reader.IsDBNull(9) ? 0 : reader.GetInt32(9));
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
                s.id,
                s.title,
                s.start_year,
                s.imdb_id,
                s.monitored,
                EXISTS(
                    SELECT 1 FROM series_wanted_state w
                    WHERE w.series_id = s.id AND w.has_file = 1
                ) AS has_file,
                s.metadata_provider,
                s.metadata_provider_id,
                s.original_title,
                s.overview,
                s.poster_url,
                s.backdrop_url,
                s.rating,
                s.genres,
                s.external_url,
                -- Not shipped in the list; see the movie repository's ListAsync
                -- for the reasoning. Detail (GetByIdAsync) still selects it.
                NULL AS metadata_json,
                s.metadata_updated_utc,
                s.created_utc,
                s.updated_utc
            FROM series_entries s
            ORDER BY s.created_utc DESC, s.title ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSeries(reader));
        }

        return items;
    }

    public async Task<int> UpdateMonitoredAsync(
        IReadOnlyList<string> seriesIds,
        bool monitored,
        CancellationToken cancellationToken)
    {
        if (seriesIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var updated = 0;
        var now = timeProvider.GetUtcNow().ToString("O");
        foreach (var seriesId in seriesIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE series_entries
                SET monitored = @monitored,
                    updated_utc = @updatedUtc
                WHERE id = @id;
                """;
            AddParameter(command, "@id", seriesId);
            AddParameter(command, "@monitored", monitored ? 1 : 0);
            AddParameter(command, "@updatedUtc", now);
            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return updated;
    }

    /// <summary>
    /// The one way metadata is written to this catalogue.
    ///
    /// <para>There used to be two: a shared statement when a
    /// <c>IMediaStateRepository</c> was injected, and a private copy of the same
    /// UPDATE for when one was not. Production always injected one, so the copy
    /// ran only under test — which is how four separate attempts to persist
    /// <c>status</c> each passed their tests and wrote nothing. A second copy of
    /// a write is not a fallback, it is a place for the two to disagree.</para>
    /// </summary>
    public Task<SeriesListItem?> UpdateMetadataAsync(
        string id,
        MetadataSearchResult metadata,
        CancellationToken cancellationToken)
        => UpdateMetadataAsync(CatalogueMetadata.ToUpdate(id, metadata, metadata.Network), cancellationToken);

    public async Task<SeriesListItem?> UpdateMetadataAsync(MediaMetadataUpdate update, CancellationToken cancellationToken)
    {
        var updated = await SharedMediaState.UpdateMetadataAsync(MediaKind.Series, update, cancellationToken);
        return updated ? await GetByIdAsync(update.Id, cancellationToken) : null;
    }

    public async Task<int> UpdateEpisodeMonitoredAsync(
        IReadOnlyList<string> episodeIds,
        bool monitored,
        CancellationToken cancellationToken)
    {
        if (episodeIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var updated = 0;
        var now = timeProvider.GetUtcNow().ToString("O");
        foreach (var episodeId in episodeIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE episode_entries
                SET monitored = @monitored,
                    updated_utc = @updatedUtc
                WHERE id = @id;
                """;
            AddParameter(command, "@id", episodeId);
            AddParameter(command, "@monitored", monitored ? 1 : 0);
            AddParameter(command, "@updatedUtc", now);
            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return updated;
    }

    public async Task<IReadOnlyList<string>> ListParentSeriesIdsAsync(
        IReadOnlyList<string> episodeIds,
        CancellationToken cancellationToken)
    {
        var distinctEpisodeIds = episodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctEpisodeIds.Length == 0)
        {
            return Array.Empty<string>();
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var seriesIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // SQLite has a finite parameter limit. The operation is bounded by the
        // caller's selected episode ids, never by the size of the catalogue.
        foreach (var episodeIdBatch in distinctEpisodeIds.Chunk(500))
        {
            using var command = connection.CreateCommand();
            var parameterNames = new List<string>(episodeIdBatch.Length);
            for (var index = 0; index < episodeIdBatch.Length; index++)
            {
                var parameterName = $"@episodeId{index}";
                parameterNames.Add(parameterName);
                AddParameter(command, parameterName, episodeIdBatch[index]);
            }

            command.CommandText = $"""
                SELECT DISTINCT series_id
                FROM episode_entries
                WHERE id IN ({string.Join(", ", parameterNames)});
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                seriesIds.Add(reader.GetString(0));
            }
        }

        return seriesIds.ToArray();
    }

    public async Task<SeriesWantedSummary> GetWantedSummaryAsync(CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            var sharedSummary = await sharedMediaStateRepository.GetWantedSummaryAsync(
                MediaKind.Series,
                cancellationToken);
            return new SeriesWantedSummary(
                sharedSummary.TotalWanted,
                sharedSummary.MissingCount,
                sharedSummary.UpgradeCount,
                sharedSummary.CoveredCount,
                sharedSummary.UpcomingCount,
                sharedSummary.RecentItems.Select(MapWanted).ToArray());
        }

        var items = new List<SeriesWantedItem>();
        var totalWanted = 0;
        var missingCount = 0;
        var upgradeCount = 0;
        var coveredCount = 0;
        var upcomingCount = 0;

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
                    SUM(CASE WHEN wanted_status = 'covered' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN wanted_status = 'upcoming' THEN 1 ELSE 0 END)
                FROM series_wanted_state;
                """;

            using var totalsReader = await totals.ExecuteReaderAsync(cancellationToken);
            if (await totalsReader.ReadAsync(cancellationToken))
            {
                totalWanted = totalsReader.IsDBNull(0) ? 0 : totalsReader.GetInt32(0);
                missingCount = totalsReader.IsDBNull(1) ? 0 : totalsReader.GetInt32(1);
                upgradeCount = totalsReader.IsDBNull(2) ? 0 : totalsReader.GetInt32(2);
                coveredCount = totalsReader.IsDBNull(3) ? 0 : totalsReader.GetInt32(3);
                upcomingCount = totalsReader.IsDBNull(4) ? 0 : totalsReader.GetInt32(4);
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id, s.title, s.start_year, s.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                w.prevent_lower_quality_replacements, w.quality_delta_last_decision,
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
            CoveredCount: coveredCount,
            UpcomingCount: upcomingCount,
            RecentItems: items);
    }

    public async Task<SeriesInventorySummary> GetInventorySummaryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM series_entries),
                (SELECT COUNT(*) FROM season_entries),
                (SELECT COUNT(*) FROM episode_entries),
                (SELECT COUNT(*) FROM episode_entries WHERE has_file = 1);
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new SeriesInventorySummary(
            SeriesCount: reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            SeasonCount: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            EpisodeCount: reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            ImportedEpisodeCount: reader.IsDBNull(3) ? 0 : reader.GetInt32(3));
    }

    public async Task<SeriesInventoryDetail?> GetInventoryDetailAsync(string seriesId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        string? title = null;
        int? startYear = null;
        using (var seriesCommand = connection.CreateCommand())
        {
            seriesCommand.CommandText =
                """
                SELECT title, start_year
                FROM series_entries
                WHERE id = @seriesId
                LIMIT 1;
                """;
            AddParameter(seriesCommand, "@seriesId", seriesId);

            using var seriesReader = await seriesCommand.ExecuteReaderAsync(cancellationToken);
            if (!await seriesReader.ReadAsync(cancellationToken))
            {
                return null;
            }

            title = seriesReader.GetString(0);
            startYear = seriesReader.IsDBNull(1) ? null : seriesReader.GetInt32(1);
        }

        var episodes = new List<SeriesEpisodeInventoryItem>();
        using (var episodeCommand = connection.CreateCommand())
        {
            episodeCommand.CommandText =
                """
                SELECT
                    e.id,
                    e.season_number,
                    e.episode_number,
                    e.title,
                    e.air_date_utc,
                    e.overview,
                    e.monitored,
                    e.has_file,
                    COALESCE(w.wanted_status, CASE WHEN e.has_file = 1 THEN 'covered' ELSE 'missing' END),
                    COALESCE(w.wanted_reason, CASE WHEN e.has_file = 1 THEN 'Episode already has an imported file.' ELSE 'Episode is missing from the library.' END),
                    COALESCE(w.quality_cutoff_met, e.quality_cutoff_met),
                    w.current_quality,
                    w.target_quality,
                    COALESCE(w.prevent_lower_quality_replacements, 1),
                    w.quality_delta_last_decision,
                    w.last_search_utc,
                    w.next_eligible_search_utc,
                    e.updated_utc
                FROM episode_entries e
                LEFT JOIN episode_wanted_state w ON w.episode_id = e.id
                WHERE e.series_id = @seriesId
                ORDER BY season_number ASC, episode_number ASC;
                """;
            AddParameter(episodeCommand, "@seriesId", seriesId);

            using var episodeReader = await episodeCommand.ExecuteReaderAsync(cancellationToken);
            while (await episodeReader.ReadAsync(cancellationToken))
            {
                episodes.Add(new SeriesEpisodeInventoryItem(
                    EpisodeId: episodeReader.GetString(0),
                    SeasonNumber: episodeReader.GetInt32(1),
                    EpisodeNumber: episodeReader.GetInt32(2),
                    Title: episodeReader.IsDBNull(3) ? null : episodeReader.GetString(3),
                    AirDateUtc: episodeReader.IsDBNull(4) ? null : ParseTimestamp(episodeReader.GetString(4)),
                    Overview: episodeReader.IsDBNull(5) ? null : episodeReader.GetString(5),
                    Monitored: episodeReader.GetInt64(6) == 1,
                    HasFile: episodeReader.GetInt64(7) == 1,
                    WantedStatus: episodeReader.GetString(8),
                    WantedReason: episodeReader.GetString(9),
                    QualityCutoffMet: episodeReader.GetInt64(10) == 1,
                    CurrentQuality: episodeReader.IsDBNull(11) ? null : episodeReader.GetString(11),
                    TargetQuality: episodeReader.IsDBNull(12) ? null : episodeReader.GetString(12),
                    PreventLowerQualityReplacements: episodeReader.GetInt64(13) == 1,
                    LastQualityDeltaDecision: episodeReader.IsDBNull(14) ? null : episodeReader.GetInt32(14),
                    LastSearchUtc: episodeReader.IsDBNull(15) ? null : ParseTimestamp(episodeReader.GetString(15)),
                    NextEligibleSearchUtc: episodeReader.IsDBNull(16) ? null : ParseTimestamp(episodeReader.GetString(16)),
                    UpdatedUtc: ParseTimestamp(episodeReader.GetString(17))));
            }
        }

        return new SeriesInventoryDetail(
            SeriesId: seriesId,
            Title: title!,
            StartYear: startYear,
            SeasonCount: episodes.Select(item => item.SeasonNumber).Distinct().Count(),
            EpisodeCount: episodes.Count,
            ImportedEpisodeCount: episodes.Count(item => item.HasFile),
            Episodes: episodes);
    }

    public async Task<IReadOnlyList<SeriesUpcomingEpisodeItem>> ListUpcomingEpisodesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var items = new List<SeriesUpcomingEpisodeItem>();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id,
                s.title,
                s.start_year,
                s.poster_url,
                e.id,
                e.season_number,
                e.episode_number,
                e.title,
                e.air_date_utc
            FROM episode_entries e
            INNER JOIN series_entries s ON s.id = e.series_id
            WHERE e.air_date_utc IS NOT NULL
              AND e.monitored = 1
              AND e.air_date_utc >= @fromUtc
              AND e.air_date_utc <= @toUtc
            ORDER BY e.air_date_utc ASC, s.title COLLATE NOCASE ASC, e.season_number ASC, e.episode_number ASC
            LIMIT @take;
            """;
        AddParameter(command, "@fromUtc", fromUtc.ToString("O"));
        AddParameter(command, "@toUtc", toUtc.ToString("O"));
        AddParameter(command, "@take", take);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SeriesUpcomingEpisodeItem(
                SeriesId: reader.GetString(0),
                Title: reader.GetString(1),
                StartYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                PosterUrl: reader.IsDBNull(3) ? null : reader.GetString(3),
                EpisodeId: reader.GetString(4),
                SeasonNumber: reader.GetInt32(5),
                EpisodeNumber: reader.GetInt32(6),
                EpisodeTitle: reader.IsDBNull(7) ? null : reader.GetString(7),
                AirDateUtc: ParseTimestamp(reader.GetString(8))));
        }

        return items;
    }

    public async Task<IReadOnlyList<SeriesSearchHistoryItem>> ListSearchHistoryAsync(CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            var sharedItems = await sharedMediaStateRepository.ListSearchHistoryAsync(
                MediaKind.Series,
                cancellationToken);
            return sharedItems.Select(MapSearchHistory).ToArray();
        }

        var items = new List<SeriesSearchHistoryItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COALESCE(h.id, ''),
                COALESCE(h.series_id, ''),
                h.episode_id,
                e.season_number,
                e.episode_number,
                COALESCE(h.library_id, ''),
                COALESCE(h.trigger_kind, 'manual'),
                COALESCE(h.outcome, 'unknown'),
                h.release_name,
                h.indexer_name,
                h.details_json,
                COALESCE(h.created_utc, '')
            FROM series_search_history h
            LEFT JOIN episode_entries e ON e.id = h.episode_id
            ORDER BY h.created_utc DESC
            LIMIT 20;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SeriesSearchHistoryItem(
                Id: reader.IsDBNull(0) || string.IsNullOrWhiteSpace(reader.GetString(0))
                    ? Guid.CreateVersion7().ToString("N")
                    : reader.GetString(0),
                SeriesId: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                EpisodeId: reader.IsDBNull(2) ? null : reader.GetString(2),
                SeasonNumber: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                EpisodeNumber: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                LibraryId: reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                TriggerKind: reader.IsDBNull(6) ? "manual" : reader.GetString(6),
                Outcome: reader.IsDBNull(7) ? "unknown" : reader.GetString(7),
                ReleaseName: reader.IsDBNull(8) ? null : reader.GetString(8),
                IndexerName: reader.IsDBNull(9) ? null : reader.GetString(9),
                DetailsJson: reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedUtc: reader.IsDBNull(11) || string.IsNullOrWhiteSpace(reader.GetString(11))
                    ? DateTimeOffset.UnixEpoch
                    : ParseTimestamp(reader.GetString(11))));
        }

        return items;
    }

    public async Task<IReadOnlyList<SeriesWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken,
        string? wantedStatus = null)
    {
        if (sharedMediaStateRepository is not null)
        {
            var sharedItems = await sharedMediaStateRepository.ListEligibleWantedAsync(
                MediaKind.Series,
                libraryId,
                take,
                now,
                ignoreRetryWindow,
                cancellationToken,
                wantedStatus);
            return sharedItems.Select(MapWanted).ToArray();
        }

        var items = new List<SeriesWantedItem>();
        var statusFilter = string.IsNullOrWhiteSpace(wantedStatus)
            ? "w.wanted_status IN ('missing', 'upgrade')"
            : "w.wanted_status = @wantedStatus";

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            ignoreRetryWindow
                ? $"""
                  SELECT
                      s.id, s.title, s.start_year, s.imdb_id,
                      w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                      w.prevent_lower_quality_replacements, w.quality_delta_last_decision,
                      w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
                  FROM series_wanted_state w
                  INNER JOIN series_entries s ON s.id = w.series_id
                  WHERE w.library_id = @libraryId
                    AND {statusFilter}
                  ORDER BY
                      CASE w.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                      COALESCE(w.last_search_utc, w.missing_since_utc, w.updated_utc) ASC,
                      s.title ASC
                  LIMIT @take;
                  """
                : $"""
                  SELECT
                      s.id, s.title, s.start_year, s.imdb_id,
                      w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                      w.prevent_lower_quality_replacements, w.quality_delta_last_decision,
                      w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
                  FROM series_wanted_state w
                  INNER JOIN series_entries s ON s.id = w.series_id
                  WHERE w.library_id = @libraryId
                    AND {statusFilter}
                    AND s.monitored = 1
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
        if (!string.IsNullOrWhiteSpace(wantedStatus))
        {
            AddParameter(command, "@wantedStatus", WantedStatuses.Normalize(wantedStatus));
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadWantedSeries(reader));
        }

        return items;
    }

    public async Task<int> CountRetryDelayedWantedAsync(
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? wantedStatus = null)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.CountRetryDelayedWantedAsync(
                MediaKind.Series,
                libraryId,
                now,
                cancellationToken,
                wantedStatus);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM series_wanted_state w
            INNER JOIN series_entries s ON s.id = w.series_id
            WHERE w.library_id = @libraryId
              AND (@wantedStatus IS NULL OR w.wanted_status = @wantedStatus)
              AND s.monitored = 1
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
        string seriesId,
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
                MediaKind.Series,
                seriesId,
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
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series_wanted_state (
                series_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            )
            VALUES (
                @seriesId, @libraryId, @wantedStatus, @wantedReason, @hasFile, @qualityCutoffMet,
                @currentQuality, @targetQuality, @missingSinceUtc, NULL, NULL, NULL, @updatedUtc
            )
            ON CONFLICT(series_id, library_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                has_file = excluded.has_file,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@wantedStatus", WantedStatuses.Normalize(wantedStatus));
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
        int? startYear,
        string wantedStatus,
        string wantedReason,
        string? currentQuality,
        string? targetQuality,
        bool qualityCutoffMet,
        bool unmonitorWhenCutoffMet,
        string? filePath,
        long? fileSizeBytes,
        IReadOnlyList<ImportedEpisodeItem>? episodes,
        CancellationToken cancellationToken)
    {
        var created = await ImportExistingBatchAsync(
            libraryId,
            [
                new ExistingSeriesImportRequest(
                    Title: title,
                    StartYear: startYear,
                    WantedStatus: wantedStatus,
                    WantedReason: wantedReason,
                    CurrentQuality: currentQuality,
                    TargetQuality: targetQuality,
                    QualityCutoffMet: qualityCutoffMet,
                    UnmonitorWhenCutoffMet: unmonitorWhenCutoffMet,
                    FilePath: filePath,
                    FileSizeBytes: fileSizeBytes,
                    Episodes: episodes)
            ],
            cancellationToken);

        return created > 0;
    }

    /// <summary>
    /// Imports a slice of already-on-disk shows inside one transaction, and
    /// returns how many were newly created.
    ///
    /// A transaction per show means a disk flush per show, and a show is not
    /// one row - it is a row per season and per episode as well. Batching is
    /// what keeps a library of a few thousand shows from costing hundreds of
    /// thousands of separate commits. The batch size is the caller's slice, so
    /// this stays bounded however large the library is.
    /// </summary>
    public async Task<int> ImportExistingBatchAsync(
        string libraryId,
        IReadOnlyList<ExistingSeriesImportRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
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

    private async Task<bool> ImportExistingCoreAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string libraryId,
        ExistingSeriesImportRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (title, startYear, wantedStatus, wantedReason, currentQuality, targetQuality,
            qualityCutoffMet, unmonitorWhenCutoffMet, filePath, fileSizeBytes, episodes) = request;

        var normalizedTitle = title.Trim();
        var normalizedFilePath = NormalizeText(filePath);
        // What the file name says about the file. Read here rather than at each
        // call site so every path that records a file — an existing-library
        // import, a completed download — gets it, and gets it the same way.
        var fileFacts = MediaFileNameFacts.Parse(filePath);
        string? seriesId = null;
        var created = false;

        if (sharedMediaStateRepository is null)
        {
            using (var lookup = connection.CreateCommand())
            {
                lookup.Transaction = transaction;
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

            if (string.IsNullOrWhiteSpace(seriesId))
            {
                seriesId = Guid.CreateVersion7().ToString("N");
                created = true;

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO series_entries (
                        id, title, start_year, imdb_id, monitored, created_utc, updated_utc
                    )
                    VALUES (
                        @id, @title, @startYear, NULL, @monitored, @createdUtc, @updatedUtc
                    );
                    """;

                AddParameter(insert, "@id", seriesId);
                AddParameter(insert, "@title", normalizedTitle);
                AddParameter(insert, "@startYear", startYear);
                // Reaching the cutoff stops upgrade searches; it must not stop
                // monitoring. Missing media and future episodes remain visible.
                AddParameter(insert, "@monitored", 1);
                AddParameter(insert, "@createdUtc", now.ToString("O"));
                AddParameter(insert, "@updatedUtc", now.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        else
        {
            var result = await sharedMediaStateRepository.ImportExistingAsync(
                MediaKind.Series,
                libraryId,
                new MediaExistingImportRequest(
                    title,
                    startYear,
                    wantedStatus,
                    wantedReason,
                    currentQuality,
                    targetQuality,
                    qualityCutoffMet,
                    unmonitorWhenCutoffMet,
                    filePath,
                    fileSizeBytes),
                connection,
                transaction,
                cancellationToken);
            seriesId = result.Id;
            created = result.Created;
        }

        if (sharedMediaStateRepository is null)
        {
            using var wanted = connection.CreateCommand();
            wanted.Transaction = transaction;
            wanted.CommandText =
            """
            INSERT INTO series_wanted_state (
                series_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, file_path, file_size_bytes, imported_utc, last_verified_utc,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc,
                video_codec, audio_codec, audio_channels, release_group
            )
            VALUES (
                @seriesId, @libraryId, @wantedStatus, @wantedReason, 1, @qualityCutoffMet,
                @currentQuality, @targetQuality, @filePath, @fileSizeBytes, @importedUtc, @lastVerifiedUtc,
                NULL, NULL, NULL, 'Imported from your existing library.', @updatedUtc,
                @videoCodec, @audioCodec, @audioChannels, @releaseGroup
            )
            ON CONFLICT(series_id, library_id) DO UPDATE SET
                wanted_status = excluded.wanted_status,
                wanted_reason = excluded.wanted_reason,
                has_file = 1,
                current_quality = excluded.current_quality,
                target_quality = excluded.target_quality,
                quality_cutoff_met = excluded.quality_cutoff_met,
                file_path = excluded.file_path,
                file_size_bytes = excluded.file_size_bytes,
                imported_utc = COALESCE(series_wanted_state.imported_utc, excluded.imported_utc),
                last_verified_utc = excluded.last_verified_utc,
                missing_detected_utc = NULL,
                -- The file is here, so it is not on its way any more. Cleared
                -- with the status rather than left behind, because a timestamp
                -- that outlives the state it describes is a column that lies.
                downloading_since_utc = NULL,
                last_search_result = excluded.last_search_result,
                -- The file replaced the one that was there, so its facts
                -- replace the old ones outright rather than merging.
                video_codec = excluded.video_codec,
                audio_codec = excluded.audio_codec,
                audio_channels = excluded.audio_channels,
                release_group = excluded.release_group,
                updated_utc = excluded.updated_utc;
            """;

            AddParameter(wanted, "@seriesId", seriesId);
            AddParameter(wanted, "@videoCodec", fileFacts.VideoCodec);
            AddParameter(wanted, "@audioCodec", fileFacts.AudioCodec);
            AddParameter(wanted, "@audioChannels", fileFacts.AudioChannels);
            AddParameter(wanted, "@releaseGroup", fileFacts.ReleaseGroup);
            AddParameter(wanted, "@libraryId", libraryId);
            AddParameter(wanted, "@wantedStatus", WantedStatuses.Normalize(wantedStatus));
            AddParameter(wanted, "@wantedReason", wantedReason.Trim());
            AddParameter(wanted, "@currentQuality", currentQuality);
            AddParameter(wanted, "@targetQuality", targetQuality);
            AddParameter(wanted, "@qualityCutoffMet", qualityCutoffMet ? 1 : 0);
            AddParameter(wanted, "@filePath", normalizedFilePath);
            AddParameter(wanted, "@fileSizeBytes", fileSizeBytes);
            AddParameter(wanted, "@importedUtc", normalizedFilePath is null ? null : now.ToString("O"));
            AddParameter(wanted, "@lastVerifiedUtc", normalizedFilePath is null ? null : now.ToString("O"));
            AddParameter(wanted, "@updatedUtc", now.ToString("O"));
            await wanted.ExecuteNonQueryAsync(cancellationToken);
        }

        if (episodes is { Count: > 0 })
        {
            foreach (var seasonNumber in episodes
                         .Select(item => item.SeasonNumber)
                         .Distinct()
                         .OrderBy(item => item))
            {
                string? seasonId = null;

                using (var findSeason = connection.CreateCommand())
                {
                    findSeason.Transaction = transaction;
                    findSeason.CommandText =
                        """
                        SELECT id
                        FROM season_entries
                        WHERE series_id = @seriesId
                          AND season_number = @seasonNumber
                        LIMIT 1;
                        """;
                    AddParameter(findSeason, "@seriesId", seriesId);
                    AddParameter(findSeason, "@seasonNumber", seasonNumber);
                    seasonId = await findSeason.ExecuteScalarAsync(cancellationToken) as string;
                }

                if (string.IsNullOrWhiteSpace(seasonId))
                {
                    seasonId = Guid.CreateVersion7().ToString("N");

                    using var insertSeason = connection.CreateCommand();
                    insertSeason.Transaction = transaction;
                    insertSeason.CommandText =
                        """
                        INSERT INTO season_entries (
                            id, series_id, season_number, monitored, created_utc, updated_utc
                        )
                        VALUES (
                            @id, @seriesId, @seasonNumber, 1, @createdUtc, @updatedUtc
                        );
                        """;
                    AddParameter(insertSeason, "@id", seasonId);
                    AddParameter(insertSeason, "@seriesId", seriesId);
                    AddParameter(insertSeason, "@seasonNumber", seasonNumber);
                    AddParameter(insertSeason, "@createdUtc", now.ToString("O"));
                    AddParameter(insertSeason, "@updatedUtc", now.ToString("O"));
                    await insertSeason.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var episode in episodes.Where(item => item.SeasonNumber == seasonNumber))
                {
                    string? episodeId = null;

                    using (var findEpisode = connection.CreateCommand())
                    {
                        findEpisode.Transaction = transaction;
                        findEpisode.CommandText =
                            """
                            SELECT id
                            FROM episode_entries
                            WHERE series_id = @seriesId
                              AND season_number = @seasonNumber
                              AND episode_number = @episodeNumber
                            LIMIT 1;
                            """;
                        AddParameter(findEpisode, "@seriesId", seriesId);
                        AddParameter(findEpisode, "@seasonNumber", seasonNumber);
                        AddParameter(findEpisode, "@episodeNumber", episode.EpisodeNumber);
                        episodeId = await findEpisode.ExecuteScalarAsync(cancellationToken) as string;
                    }

                    if (string.IsNullOrWhiteSpace(episodeId))
                    {
                        episodeId = Guid.CreateVersion7().ToString("N");

                        using var insertEpisode = connection.CreateCommand();
                        insertEpisode.Transaction = transaction;
                        insertEpisode.CommandText =
                            """
                            INSERT INTO episode_entries (
                                id, series_id, season_id, season_number, episode_number, title, air_date_utc,
                                monitored, has_file, quality_cutoff_met, file_path, file_size_bytes, imported_utc, last_verified_utc,
                                created_utc, updated_utc
                            )
                            VALUES (
                                @id, @seriesId, @seasonId, @seasonNumber, @episodeNumber, NULL, NULL,
                                1, @hasFile, 0, @filePath, @fileSizeBytes, @importedUtc, @lastVerifiedUtc,
                                @createdUtc, @updatedUtc
                            );
                            """;
                        AddParameter(insertEpisode, "@id", episodeId);
                        AddParameter(insertEpisode, "@seriesId", seriesId);
                        AddParameter(insertEpisode, "@seasonId", seasonId);
                        AddParameter(insertEpisode, "@seasonNumber", seasonNumber);
                        AddParameter(insertEpisode, "@episodeNumber", episode.EpisodeNumber);
                        AddParameter(insertEpisode, "@hasFile", episode.HasFile ? 1 : 0);
                        AddParameter(insertEpisode, "@filePath", NormalizeText(episode.FilePath));
                        AddParameter(insertEpisode, "@fileSizeBytes", episode.FileSizeBytes);
                        AddParameter(insertEpisode, "@importedUtc", NormalizeText(episode.FilePath) is null ? null : now.ToString("O"));
                        AddParameter(insertEpisode, "@lastVerifiedUtc", NormalizeText(episode.FilePath) is null ? null : now.ToString("O"));
                        AddParameter(insertEpisode, "@createdUtc", now.ToString("O"));
                        AddParameter(insertEpisode, "@updatedUtc", now.ToString("O"));
                        await insertEpisode.ExecuteNonQueryAsync(cancellationToken);
                    }
                    else
                    {
                        using var updateEpisode = connection.CreateCommand();
                        updateEpisode.Transaction = transaction;
                        updateEpisode.CommandText =
                            """
                            UPDATE episode_entries
                            SET
                                season_id = @seasonId,
                                has_file = @hasFile,
                                file_path = @filePath,
                                file_size_bytes = @fileSizeBytes,
                                imported_utc = COALESCE(imported_utc, @importedUtc),
                                last_verified_utc = @lastVerifiedUtc,
                                missing_detected_utc = NULL,
                                updated_utc = @updatedUtc
                            WHERE id = @id;
                            """;
                        AddParameter(updateEpisode, "@id", episodeId);
                        AddParameter(updateEpisode, "@seasonId", seasonId);
                        AddParameter(updateEpisode, "@hasFile", episode.HasFile ? 1 : 0);
                        AddParameter(updateEpisode, "@filePath", NormalizeText(episode.FilePath));
                        AddParameter(updateEpisode, "@fileSizeBytes", episode.FileSizeBytes);
                        AddParameter(updateEpisode, "@importedUtc", NormalizeText(episode.FilePath) is null ? null : now.ToString("O"));
                        AddParameter(updateEpisode, "@lastVerifiedUtc", NormalizeText(episode.FilePath) is null ? null : now.ToString("O"));
                        AddParameter(updateEpisode, "@updatedUtc", now.ToString("O"));
                        await updateEpisode.ExecuteNonQueryAsync(cancellationToken);
                    }

                    using var upsertEpisodeWanted = connection.CreateCommand();
                    upsertEpisodeWanted.Transaction = transaction;
                    upsertEpisodeWanted.CommandText =
                        """
                        INSERT INTO episode_wanted_state (
                            episode_id, series_id, library_id, wanted_status, wanted_reason,
                            last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
                        )
                        VALUES (
                            @episodeId, @seriesId, @libraryId, @wantedStatus, @wantedReason,
                            NULL, NULL, @lastSearchResult, @updatedUtc
                        )
                        ON CONFLICT(episode_id) DO UPDATE SET
                            library_id = excluded.library_id,
                            wanted_status = excluded.wanted_status,
                            wanted_reason = excluded.wanted_reason,
                            last_search_result = excluded.last_search_result,
                            updated_utc = excluded.updated_utc;
                        """;
                    AddParameter(upsertEpisodeWanted, "@episodeId", episodeId);
                    AddParameter(upsertEpisodeWanted, "@seriesId", seriesId);
                    AddParameter(upsertEpisodeWanted, "@libraryId", libraryId);
                    AddParameter(
                        upsertEpisodeWanted,
                        "@wantedStatus",
                        episode.HasFile ? "covered" : "missing");
                    AddParameter(
                        upsertEpisodeWanted,
                        "@wantedReason",
                        episode.HasFile
                            ? "Episode imported from your existing library."
                            : "Episode is missing from the library.");
                    AddParameter(
                        upsertEpisodeWanted,
                        "@lastSearchResult",
                        episode.HasFile ? "Imported from your existing library." : null);
                    AddParameter(upsertEpisodeWanted, "@updatedUtc", now.ToString("O"));
                    await upsertEpisodeWanted.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            // The episodes just changed, so the show's rung did too. Written
            // here, inside the same transaction, because this is the moment the
            // facts move — leaving it to a later recalculation is what let the
            // chips above the shelf disagree with the posters underneath them.
            await RefreshSeriesRungAsync(connection, transaction, seriesId!, libraryId, now, cancellationToken);
        }

        return created;
    }

    /// <summary>
    /// Re-decides one show's rung from its episodes and stores it.
    ///
    /// <para><b>The stored column is what the chips and the status filter read</b>
    /// — they are SQL over <c>series_wanted_state</c> and cannot call a C#
    /// rule. The catalogue page computes the same answer from the rollup it
    /// already has, so both sides come from <see cref="SeriesRung"/> and the only
    /// way they can disagree is if this stops being called when episodes
    /// change.</para>
    /// </summary>
    private async Task RefreshSeriesRungAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var aired = 0;
        var airedWithFile = 0;
        var airedUpgradable = 0;
        var hasFutureAirDate = false;

        using (var rollup = connection.CreateCommand())
        {
            rollup.Transaction = transaction;
            rollup.CommandText =
                """
                SELECT
                    COALESCE(SUM(CASE WHEN air_date_utc IS NOT NULL AND air_date_utc <= @now THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN air_date_utc IS NOT NULL AND air_date_utc <= @now AND has_file = 1 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN air_date_utc IS NOT NULL AND air_date_utc <= @now AND has_file = 1 AND quality_cutoff_met = 0 THEN 1 ELSE 0 END), 0),
                    COALESCE(MAX(CASE WHEN air_date_utc > @now THEN 1 ELSE 0 END), 0)
                FROM episode_entries
                WHERE series_id = @seriesId;
                """;
            AddParameter(rollup, "@seriesId", seriesId);
            AddParameter(rollup, "@now", now.ToString("O"));

            using var reader = await rollup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                aired = reader.GetInt32(0);
                airedWithFile = reader.GetInt32(1);
                airedUpgradable = reader.GetInt32(2);
                hasFutureAirDate = reader.GetInt32(3) == 1;
            }
        }

        var status = SeriesRung.From(aired, airedWithFile, airedUpgradable, hasFutureAirDate);

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE series_wanted_state
            SET wanted_status = @wantedStatus,
                wanted_reason = @wantedReason,
                quality_cutoff_met = @qualityCutoffMet,
                updated_utc = @updatedUtc
            WHERE series_id = @seriesId
              AND library_id = @libraryId;
            """;
        AddParameter(update, "@seriesId", seriesId);
        AddParameter(update, "@libraryId", libraryId);
        AddParameter(update, "@wantedStatus", status);
        AddParameter(update, "@wantedReason", SeriesRungReason(status, aired, airedWithFile));
        AddParameter(update, "@qualityCutoffMet", status == WantedStatuses.Covered ? 1 : 0);
        AddParameter(update, "@updatedUtc", now.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    public async IAsyncEnumerable<SeriesTrackedFileItem> StreamTrackedFilesAsync(
        string libraryId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        if (sharedMediaStateRepository is not null)
        {
            await foreach (var item in sharedMediaStateRepository.StreamTrackedFilesAsync(
                               MediaKind.Series,
                               libraryId,
                               cancellationToken))
            {
                yield return new SeriesTrackedFileItem(
                    SeriesId: item.MediaId,
                    EpisodeId: null,
                    LibraryId: item.LibraryId,
                    Title: item.Title,
                    StartYear: item.Year,
                    SeasonNumber: null,
                    EpisodeNumber: null,
                    FilePath: item.FilePath,
                    FileSizeBytes: item.FileSizeBytes,
                    ImportedUtc: item.ImportedUtc,
                    LastVerifiedUtc: item.LastVerifiedUtc);
            }
        }
        else
        {
            using var series = connection.CreateCommand();
            series.CommandText =
                """
                SELECT
                    w.series_id,
                    w.library_id,
                    s.title,
                    s.start_year,
                    w.file_path,
                    w.file_size_bytes,
                    w.imported_utc,
                    w.last_verified_utc
                FROM series_wanted_state w
                INNER JOIN series_entries s ON s.id = w.series_id
                WHERE w.library_id = @libraryId
                  AND w.has_file = 1
                  AND w.file_path IS NOT NULL
                ORDER BY s.title COLLATE NOCASE, s.id;
                """;
            AddParameter(series, "@libraryId", libraryId);

            using var reader = await series.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                yield return new SeriesTrackedFileItem(
                    SeriesId: reader.GetString(0),
                    EpisodeId: null,
                    LibraryId: reader.GetString(1),
                    Title: reader.GetString(2),
                    StartYear: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    SeasonNumber: null,
                    EpisodeNumber: null,
                    FilePath: reader.GetString(4),
                    FileSizeBytes: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    ImportedUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                    LastVerifiedUtc: reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7)));
            }
        }

        using (var episodes = connection.CreateCommand())
        {
            episodes.CommandText =
                """
                SELECT
                    e.series_id,
                    e.id,
                    @libraryId,
                    s.title,
                    s.start_year,
                    e.season_number,
                    e.episode_number,
                    e.file_path,
                    e.file_size_bytes,
                    e.imported_utc,
                    e.last_verified_utc
                FROM episode_entries e
                INNER JOIN series_entries s ON s.id = e.series_id
                INNER JOIN episode_wanted_state w ON w.episode_id = e.id
                WHERE w.library_id = @libraryId
                  AND e.has_file = 1
                  AND e.file_path IS NOT NULL
                ORDER BY s.title COLLATE NOCASE, e.season_number, e.episode_number;
                """;
            AddParameter(episodes, "@libraryId", libraryId);

            using var reader = await episodes.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                yield return new SeriesTrackedFileItem(
                    SeriesId: reader.GetString(0),
                    EpisodeId: reader.GetString(1),
                    LibraryId: reader.GetString(2),
                    Title: reader.GetString(3),
                    StartYear: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    SeasonNumber: reader.GetInt32(5),
                    EpisodeNumber: reader.GetInt32(6),
                    FilePath: reader.GetString(7),
                    FileSizeBytes: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    ImportedUtc: reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
                    LastVerifiedUtc: reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)));
            }
        }
    }

    public async Task<bool> MarkTrackedFileMissingAsync(
        string seriesId,
        string? episodeId,
        string libraryId,
        string filePath,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(episodeId))
        {
            using var episode = connection.CreateCommand();
            episode.CommandText =
                """
                UPDATE episode_entries
                SET has_file = 0,
                    missing_detected_utc = @now,
                    last_verified_utc = @now,
                    updated_utc = @now
                WHERE id = @episodeId
                  AND series_id = @seriesId
                  AND file_path = @filePath;
                """;
            AddParameter(episode, "@episodeId", episodeId);
            AddParameter(episode, "@seriesId", seriesId);
            AddParameter(episode, "@filePath", filePath);
            AddParameter(episode, "@now", now.ToString("O"));
            var updatedEpisode = await episode.ExecuteNonQueryAsync(cancellationToken);

            using var wanted = connection.CreateCommand();
            wanted.CommandText =
                """
                UPDATE episode_wanted_state
                SET wanted_status = 'missing',
                    wanted_reason = 'Reconciliation detected that the tracked episode file is missing from disk.',
                    updated_utc = @now
                WHERE episode_id = @episodeId
                  AND library_id = @libraryId;
                """;
            AddParameter(wanted, "@episodeId", episodeId);
            AddParameter(wanted, "@libraryId", libraryId);
            AddParameter(wanted, "@now", now.ToString("O"));
            await wanted.ExecuteNonQueryAsync(cancellationToken);
            return updatedEpisode > 0;
        }

        using var series = connection.CreateCommand();
        series.CommandText =
            """
            UPDATE series_wanted_state
            SET has_file = 0,
                wanted_status = 'missing',
                wanted_reason = 'Reconciliation detected that the tracked series file is missing from disk.',
                missing_since_utc = COALESCE(missing_since_utc, @now),
                missing_detected_utc = @now,
                last_verified_utc = @now,
                updated_utc = @now
            WHERE series_id = @seriesId
              AND library_id = @libraryId
              AND file_path = @filePath;
            """;
        AddParameter(series, "@seriesId", seriesId);
        AddParameter(series, "@libraryId", libraryId);
        AddParameter(series, "@filePath", filePath);
        AddParameter(series, "@now", now.ToString("O"));
        return await series.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task RecordSearchAttemptAsync(
        string seriesId,
        string? episodeId,
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
                    @id, @seriesId, @episodeId, @libraryId, @triggerKind, @outcome, @releaseName, @indexerName, @detailsJson, @createdUtc
                );
                """;

            AddParameter(history, "@id", Guid.CreateVersion7().ToString("N"));
            AddParameter(history, "@seriesId", seriesId);
            AddParameter(history, "@episodeId", episodeId);
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

        if (!string.IsNullOrWhiteSpace(episodeId))
        {
            using var updateEpisode = connection.CreateCommand();
            updateEpisode.Transaction = transaction;
            updateEpisode.CommandText =
                """
                UPDATE episode_wanted_state
                SET
                    last_search_utc = @lastSearchUtc,
                    next_eligible_search_utc = @nextEligibleSearchUtc,
                    last_search_result = @lastSearchResult,
                    updated_utc = @updatedUtc
                WHERE episode_id = @episodeId;
                """;

            AddParameter(updateEpisode, "@episodeId", episodeId);
            AddParameter(updateEpisode, "@lastSearchUtc", now.ToString("O"));
            AddParameter(updateEpisode, "@nextEligibleSearchUtc", nextEligibleSearchUtc?.ToString("O"));
            AddParameter(updateEpisode, "@lastSearchResult", lastSearchResult);
            AddParameter(updateEpisode, "@updatedUtc", now.ToString("O"));
            await updateEpisode.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> DeferWantedSearchAsync(
        string seriesId,
        string libraryId,
        DateTimeOffset deferredUntilUtc,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.DeferWantedSearchAsync(
                MediaKind.Series,
                seriesId,
                libraryId,
                deferredUntilUtc,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Series, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE series_wanted_state
            SET next_eligible_search_utc = @deferredUntilUtc,
                last_search_result = 'Deferred by user.',
                updated_utc = @updatedUtc
            WHERE series_id = @seriesId
              AND library_id = @libraryId
              AND wanted_status IN ('missing', 'upgrade');
            """;
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@deferredUntilUtc", deferredUntilUtc.ToString("O"));
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> SkipNextWantedSearchAsync(
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.SkipNextWantedSearchAsync(
                MediaKind.Series,
                seriesId,
                libraryId,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Series, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE series_wanted_state
            SET skip_next_automation_search = 1,
                last_search_result = 'Will skip the next scheduled search by user request.',
                updated_utc = @updatedUtc
            WHERE series_id = @seriesId
              AND library_id = @libraryId
              AND wanted_status IN ('missing', 'upgrade');
            """;
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@updatedUtc", timeProvider.GetUtcNow().ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ConsumeSkipNextWantedSearchAsync(
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            return await sharedMediaStateRepository.ConsumeSkipNextWantedSearchAsync(
                MediaKind.Series,
                seriesId,
                libraryId,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Series, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE series_wanted_state
            SET skip_next_automation_search = 0,
                updated_utc = @updatedUtc
            WHERE series_id = @seriesId
              AND library_id = @libraryId
              AND skip_next_automation_search = 1;
            """;
        AddParameter(command, "@seriesId", seriesId);
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
                MediaKind.Series,
                libraryId,
                cutoffQuality,
                upgradeUntilCutoff,
                upgradeUnknownItems,
                cancellationToken);
        }

        // The show, and what its *episodes* say about it.
        //
        // This used to read has_file and current_quality off the series row and
        // ask MediaDecisionRules the film question — "is this file the quality
        // you asked for". A show has no such file: the row describes whichever
        // one the import happened to see first. That is why a three-of-twenty
        // show was stored as "Quality met", and why the chips disagreed with
        // every poster on the shelf.
        var items = new List<(string SeriesId, int Aired, int AiredWithFile, int AiredUpgradable, bool HasFutureAirDate)>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var nowText = timeProvider.GetUtcNow().ToString("O");

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    w.series_id,
                    COALESCE(SUM(CASE WHEN e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now AND e.has_file = 1 THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now AND e.has_file = 1 AND e.quality_cutoff_met = 0 THEN 1 ELSE 0 END), 0),
                    COALESCE(MAX(CASE WHEN e.air_date_utc > @now THEN 1 ELSE 0 END), 0)
                FROM series_wanted_state w
                LEFT JOIN episode_entries e ON e.series_id = w.series_id
                WHERE w.library_id = @libraryId
                GROUP BY w.series_id;
                """;
            AddParameter(command, "@libraryId", libraryId);
            AddParameter(command, "@now", nowText);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add((
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4) == 1));
            }
        }

        var updated = 0;
        foreach (var item in items)
        {
            var status = SeriesRung.From(item.Aired, item.AiredWithFile, item.AiredUpgradable, item.HasFutureAirDate);
            var decision = new LibraryQualityDecision(
                WantedStatus: status,
                WantedReason: SeriesRungReason(status, item.Aired, item.AiredWithFile),
                QualityCutoffMet: status == WantedStatuses.Covered,
                // A show has no single file, so it has no single current
                // quality. Leaving this null is the honest answer and stops the
                // detail page printing one arbitrary episode's tier as if it
                // described the whole show.
                CurrentQuality: null,
                TargetQuality: cutoffQuality,
                PolicyVersion: SeriesRungPolicyVersion);

            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE series_wanted_state
                SET
                    wanted_status = @wantedStatus,
                    wanted_reason = @wantedReason,
                    target_quality = @targetQuality,
                    quality_cutoff_met = @qualityCutoffMet,
                    updated_utc = @updatedUtc
                WHERE series_id = @seriesId
                  AND library_id = @libraryId;
                """;
            AddParameter(update, "@seriesId", item.SeriesId);
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

    public async Task<SeriesImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken)
    {
        if (sharedMediaStateRepository is not null)
        {
            var sharedSummary = await sharedMediaStateRepository.GetImportRecoverySummaryAsync(
                MediaKind.Series,
                cancellationToken);
            return new SeriesImportRecoverySummary(
                sharedSummary.OpenCount,
                sharedSummary.QualityCount,
                sharedSummary.UnmatchedCount,
                sharedSummary.CorruptCount,
                sharedSummary.DownloadFailedCount,
                sharedSummary.ImportFailedCount,
                sharedSummary.RecentCases.Select(MapImportRecoveryCase).ToArray());
        }

        var openCases = new List<SeriesImportRecoveryCase>();
        int openCount = 0;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = "SELECT COUNT(*) FROM series_import_recovery_cases WHERE status = 'open';";
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
                FROM series_import_recovery_cases
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

        return new SeriesImportRecoverySummary(
            OpenCount: openCount,
            QualityCount: openCases.Count(item => item.FailureKind == "quality"),
            UnmatchedCount: openCases.Count(item => item.FailureKind == "unmatched"),
            CorruptCount: openCases.Count(item => item.FailureKind == "corrupt"),
            DownloadFailedCount: openCases.Count(item => item.FailureKind == "downloadFailed"),
            ImportFailedCount: openCases.Count(item => item.FailureKind == "importFailed"),
            RecentCases: openCases);
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
            Status: "open",
            Summary: request.Summary!.Trim(),
            RecommendedAction: string.IsNullOrWhiteSpace(request.RecommendedAction)
                ? "Review this import and decide whether Deluno should retry, rematch, or remove it."
                : request.RecommendedAction.Trim(),
            DetailsJson: string.IsNullOrWhiteSpace(request.DetailsJson) ? null : request.DetailsJson.Trim(),
            DetectedUtc: now,
            ResolvedUtc: null);

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
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM series_import_recovery_cases WHERE id = @id;";
        AddParameter(command, "@id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<SeriesImportRecoveryCase?> ResolveImportRecoveryCaseAsync(
        string id,
        string status,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using (var update = connection.CreateCommand())
        {
            update.CommandText =
                """
                UPDATE series_import_recovery_cases
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
            FROM series_import_recovery_cases
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
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series_import_recovery_events (id, case_id, event_kind, message, metadata_json, created_utc)
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
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM series_import_recovery_cases
            WHERE status IN ('resolved', 'dismissed')
              AND resolved_utc < @olderThan;
            """;
        AddParameter(command, "@olderThan", olderThan.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SeriesImportRecoveryCase ReadImportRecoveryCase(System.Data.Common.DbDataReader reader) =>
        new SeriesImportRecoveryCase(
            Id: reader.GetString(0),
            Title: reader.GetString(1),
            FailureKind: reader.GetString(2),
            Status: reader.GetString(3),
            Summary: reader.GetString(4),
            RecommendedAction: reader.GetString(5),
            DetailsJson: reader.IsDBNull(6) ? null : reader.GetString(6),
            DetectedUtc: ParseTimestamp(reader.GetString(7)),
            ResolvedUtc: reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)));

    public async Task<bool> DeleteAsync(string seriesId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM series_entries WHERE id = @id;";
        AddParameter(command, "@id", seriesId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdateQualityProfileAsync(string seriesId, string qualityProfileId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE series_entries SET quality_profile_id = @qualityProfileId, updated_utc = @now WHERE id = @id;";
        AddParameter(command, "@id", seriesId);
        AddParameter(command, "@qualityProfileId", qualityProfileId);
        AddParameter(command, "@now", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> ReassignLibraryAsync(
        IReadOnlyList<string> seriesIds,
        string fromLibraryId,
        string toLibraryId,
        CancellationToken cancellationToken)
    {
        if (seriesIds.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var ids = string.Join(",", seriesIds.Select((_, i) => $"@id{i}"));
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            UPDATE series_wanted_state
            SET library_id = @toLibraryId
            WHERE library_id = @fromLibraryId
              AND series_id IN ({ids});
            """;

        AddParameter(command, "@fromLibraryId", fromLibraryId);
        AddParameter(command, "@toLibraryId", toLibraryId);
        for (var i = 0; i < seriesIds.Count; i++)
        {
            AddParameter(command, $"@id{i}", seriesIds[i]);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EpisodeSearchEligibilityItem>> ListEligibleWantedEpisodesAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken,
        string? wantedStatus = null)
    {
        var items = new List<EpisodeSearchEligibilityItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                e.id, e.series_id, e.season_number, e.episode_number, e.title,
                ews.last_search_utc, ews.next_eligible_search_utc
            FROM episode_wanted_state ews
            INNER JOIN episode_entries e ON e.id = ews.episode_id
            WHERE ews.library_id = @libraryId
              -- 'missing', not 'wanted'. This read a word nothing has ever
              -- written, so the query matched no row in production and its test
              -- had to seed the value by hand to pass (#300, #303).
              AND ews.wanted_status IN ('missing', 'upgrade')
              AND (@wantedStatus IS NULL OR ews.wanted_status = @wantedStatus)
              AND (@ignoreRetryWindow = 1 OR ews.next_eligible_search_utc IS NULL OR ews.next_eligible_search_utc <= @now)
              AND e.monitored = 1
            ORDER BY
                CASE ews.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                COALESCE(ews.last_search_utc, ews.updated_utc) ASC,
                e.season_number ASC,
                e.episode_number ASC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@take", take);
        AddParameter(command, "@ignoreRetryWindow", ignoreRetryWindow ? 1 : 0);
        AddParameter(command, "@wantedStatus", wantedStatus);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new EpisodeSearchEligibilityItem(
                EpisodeId: reader.GetString(0),
                SeriesId: reader.GetString(1),
                SeasonNumber: reader.GetInt32(2),
                EpisodeNumber: reader.GetInt32(3),
                Title: reader.IsDBNull(4) ? null : reader.GetString(4),
                LastSearchUtc: reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
                NextEligibleSearchUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6))));
        }

        return items;
    }

    public async Task<string?> GetEpisodeTargetQualityAsync(
        string episodeId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sws.target_quality
            FROM episode_wanted_state ews
            INNER JOIN series_wanted_state sws ON sws.series_id = ews.series_id AND sws.library_id = ews.library_id
            WHERE ews.episode_id = @episodeId
              AND ews.library_id = @libraryId
            LIMIT 1;
            """;

        AddParameter(command, "@episodeId", episodeId);
        AddParameter(command, "@libraryId", libraryId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : value.ToString();
    }

    public async Task<string?> GetEpisodeCurrentQualityAsync(
        string episodeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sws.current_quality
            FROM episode_wanted_state ews
            INNER JOIN series_wanted_state sws ON sws.series_id = ews.series_id AND sws.library_id = ews.library_id
            WHERE ews.episode_id = @episodeId
            LIMIT 1;
            """;

        AddParameter(command, "@episodeId", episodeId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : value.ToString();
    }

    /// <summary>The scores a page row carries, read straight from their columns.</summary>
    private static IReadOnlyList<MetadataRatingItem> ReadRatingColumns(System.Data.Common.DbDataReader reader)
    {
        var ratings = new List<MetadataRatingItem>();
        var ordinal = RatingOrdinal;

        foreach (var source in RatingSources.All)
        {
            var score = reader.IsDBNull(ordinal) ? (double?)null : reader.GetDouble(ordinal);
            ordinal++;

            int? votes = null;
            if (source.VotesColumn is not null)
            {
                votes = reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
                ordinal++;
            }

            if (score is not null)
            {
                ratings.Add(new MetadataRatingItem(
                    source.Source,
                    source.Label,
                    score,
                    source.MaxScore,
                    votes,
                    Url: null,
                    Kind: source.MaxScore == 100 ? "critic" : "community"));
            }
        }

        return ratings;
    }

    private static SeriesListItem ReadSeries(System.Data.Common.DbDataReader reader)
    {
        return new SeriesListItem(
            Id: reader.GetString(0),
            Title: reader.GetString(1),
            StartYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
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
            UpdatedUtc: ParseTimestamp(reader.GetString(18)));
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
            CurrentQuality: reader.IsDBNull(8) ? null : reader.GetString(8),
            TargetQuality: reader.IsDBNull(9) ? null : reader.GetString(9),
            QualityCutoffMet: reader.GetInt64(10) == 1,
            PreventLowerQualityReplacements: reader.GetInt64(11) == 1,
            LastQualityDeltaDecision: reader.IsDBNull(12) ? null : reader.GetInt32(12),
            MissingSinceUtc: reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)),
            LastSearchUtc: reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)),
            NextEligibleSearchUtc: reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)),
            LastSearchResult: reader.IsDBNull(16) ? null : reader.GetString(16),
            UpdatedUtc: ParseTimestamp(reader.GetString(17)));
    }

    /// <summary>
    /// Apply the provider's catalogue to this series.
    ///
    /// The rule is that the provider owns what exists and the disk owns what you
    /// have. So this writes title, overview and air date, inserts episodes that
    /// were never downloaded, and never touches has_file, quality_cutoff_met,
    /// file_path, imported_utc or monitored — a user who unmonitored an episode
    /// keeps that decision through every future refresh.
    /// </summary>
    public async Task<SeriesCatalogueSyncResult> SyncEpisodeCatalogueAsync(
        string seriesId,
        IReadOnlyList<CatalogueEpisodeItem> episodes,
        string source,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesId) || episodes.Count == 0)
        {
            return SeriesCatalogueSyncResult.None;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var now = timeProvider.GetUtcNow();
        var stamp = now.ToString("O");
        var added = 0;
        var updated = 0;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var seasonNumber in episodes.Select(item => item.SeasonNumber).Distinct().OrderBy(item => item))
        {
            var seasonId = await EnsureSeasonAsync(connection, transaction, seriesId, seasonNumber, stamp, cancellationToken);

            foreach (var episode in episodes.Where(item => item.SeasonNumber == seasonNumber).OrderBy(item => item.EpisodeNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText =
                    """
                    INSERT INTO episode_entries (
                        id, series_id, season_id, season_number, episode_number, title, overview, air_date_utc,
                        monitored, has_file, quality_cutoff_met, catalogue_source, catalogue_synced_utc,
                        created_utc, updated_utc
                    )
                    VALUES (
                        @id, @seriesId, @seasonId, @seasonNumber, @episodeNumber, @title, @overview, @airDateUtc,
                        @monitored, 0, 0, @source, @syncedUtc,
                        @createdUtc, @updatedUtc
                    )
                    ON CONFLICT(series_id, season_number, episode_number) DO UPDATE SET
                        season_id = COALESCE(episode_entries.season_id, excluded.season_id),
                        title = excluded.title,
                        overview = excluded.overview,
                        air_date_utc = excluded.air_date_utc,
                        catalogue_source = excluded.catalogue_source,
                        catalogue_synced_utc = excluded.catalogue_synced_utc,
                        updated_utc = excluded.updated_utc;
                    """;
                AddParameter(upsert, "@id", Guid.CreateVersion7().ToString("N"));
                AddParameter(upsert, "@seriesId", seriesId);
                AddParameter(upsert, "@seasonId", seasonId);
                AddParameter(upsert, "@seasonNumber", episode.SeasonNumber);
                AddParameter(upsert, "@episodeNumber", episode.EpisodeNumber);
                // Season 0 is specials: extras, recaps and behind-the-scenes
                // pieces most people do not want hunted. Sonarr leaves them
                // unmonitored for this reason; monitoring them made a new show
                // arrive with nine "missing" specials to chase (#243). Only the
                // default differs — the per-episode switch still governs.
                AddParameter(upsert, "@monitored", episode.SeasonNumber == 0 ? 0 : 1);
                AddParameter(upsert, "@title", NormalizeText(episode.Title));
                AddParameter(upsert, "@overview", NormalizeText(episode.Overview));
                AddParameter(upsert, "@airDateUtc", episode.AirDateUtc?.ToString("O"));
                AddParameter(upsert, "@source", source);
                AddParameter(upsert, "@syncedUtc", stamp);
                AddParameter(upsert, "@createdUtc", stamp);
                AddParameter(upsert, "@updatedUtc", stamp);

                // SQLite reports 1 for both an insert and an ON CONFLICT update, so
                // ask which happened rather than inferring it from the row count.
                var existedBefore = await EpisodeExistsAsync(connection, transaction, seriesId, episode, cancellationToken);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
                if (existedBefore)
                {
                    updated++;
                }
                else
                {
                    added++;
                }
            }
        }

        // Episodes nobody has a file for still need a wanted state, or the search
        // engine has nothing to act on — this is what makes a missing episode
        // findable rather than merely visible.
        using (var backfill = connection.CreateCommand())
        {
            backfill.Transaction = transaction;
            backfill.CommandText =
                """
                INSERT INTO episode_wanted_state (
                    episode_id, series_id, library_id, wanted_status, wanted_reason, updated_utc
                )
                SELECT
                    episode.id,
                    episode.series_id,
                    series_state.library_id,
                    CASE WHEN episode.has_file = 1 THEN 'covered' ELSE 'missing' END,
                    CASE
                        WHEN episode.has_file = 1 THEN 'Imported from your existing library.'
                        WHEN episode.air_date_utc IS NULL THEN 'Not announced yet.'
                        WHEN episode.air_date_utc > @now THEN 'Has not aired yet.'
                        ELSE 'Deluno is looking for this episode.'
                    END,
                    @updatedUtc
                FROM episode_entries AS episode
                JOIN series_wanted_state AS series_state ON series_state.series_id = episode.series_id
                WHERE episode.series_id = @seriesId
                  AND NOT EXISTS (
                      SELECT 1 FROM episode_wanted_state AS existing WHERE existing.episode_id = episode.id
                  );
                """;
            AddParameter(backfill, "@seriesId", seriesId);
            AddParameter(backfill, "@now", stamp);
            AddParameter(backfill, "@updatedUtc", stamp);
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new SeriesCatalogueSyncResult(
            episodes.Select(item => item.SeasonNumber).Distinct().Count(),
            episodes.Count,
            added,
            updated);
    }

    /// <summary>
    /// Episodes airing inside a window, for the calendar.
    ///
    /// A full-catalogue series can hold hundreds of episodes, so the window is
    /// applied in SQL. The page used to pull every episode of every show and take
    /// the first 48 by date, which after a catalogue sync means 1989.
    /// </summary>
    public async Task<IReadOnlyList<SeriesCalendarEpisodeItem>> ListCalendarEpisodesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                e.id, e.series_id, s.title, s.poster_url,
                e.season_number, e.episode_number, e.title, e.air_date_utc,
                e.has_file, e.monitored,
                COALESCE(w.wanted_status, CASE WHEN e.has_file = 1 THEN 'covered' ELSE 'missing' END)
            FROM episode_entries e
            INNER JOIN series_entries s ON s.id = e.series_id
            LEFT JOIN episode_wanted_state w ON w.episode_id = e.id
            WHERE e.air_date_utc IS NOT NULL
              AND e.air_date_utc >= @fromUtc
              AND e.air_date_utc < @toUtc
            ORDER BY e.air_date_utc ASC, s.title ASC, e.season_number ASC, e.episode_number ASC
            LIMIT @take;
            """;
        AddParameter(command, "@fromUtc", fromUtc.ToString("O"));
        AddParameter(command, "@toUtc", toUtc.ToString("O"));
        AddParameter(command, "@take", take);

        var items = new List<SeriesCalendarEpisodeItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SeriesCalendarEpisodeItem(
                EpisodeId: reader.GetString(0),
                SeriesId: reader.GetString(1),
                SeriesTitle: reader.GetString(2),
                PosterUrl: reader.IsDBNull(3) ? null : reader.GetString(3),
                SeasonNumber: reader.GetInt32(4),
                EpisodeNumber: reader.GetInt32(5),
                Title: reader.IsDBNull(6) ? null : reader.GetString(6),
                AirDateUtc: ParseTimestamp(reader.GetString(7)),
                HasFile: reader.GetInt64(8) == 1,
                Monitored: reader.GetInt64(9) == 1,
                WantedStatus: reader.GetString(10)));
        }

        return items;
    }

    /// <summary>Episodes still wanted across every series, newest air date first.</summary>
    public async Task<IReadOnlyList<WantedEpisodeItem>> ListWantedEpisodesAsync(
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                e.id, e.series_id, s.title, e.season_number, e.episode_number, e.title,
                e.air_date_utc, e.monitored,
                COALESCE(w.wanted_status, 'missing'),
                COALESCE(w.wanted_reason, 'Episode is missing from the library.'),
                w.last_search_utc, w.next_eligible_search_utc
            FROM episode_entries e
            INNER JOIN series_entries s ON s.id = e.series_id
            LEFT JOIN episode_wanted_state w ON w.episode_id = e.id
            WHERE e.has_file = 0
              AND COALESCE(w.wanted_status, 'missing') IN ('missing', 'upgrade')
            ORDER BY
                CASE WHEN e.air_date_utc IS NULL THEN 1 ELSE 0 END,
                e.air_date_utc DESC,
                s.title ASC,
                e.season_number ASC,
                e.episode_number ASC
            LIMIT @take;
            """;
        AddParameter(command, "@take", take);

        var items = new List<WantedEpisodeItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new WantedEpisodeItem(
                EpisodeId: reader.GetString(0),
                SeriesId: reader.GetString(1),
                SeriesTitle: reader.GetString(2),
                SeasonNumber: reader.GetInt32(3),
                EpisodeNumber: reader.GetInt32(4),
                Title: reader.IsDBNull(5) ? null : reader.GetString(5),
                AirDateUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                Monitored: reader.GetInt64(7) == 1,
                WantedStatus: reader.GetString(8),
                WantedReason: reader.GetString(9),
                LastSearchUtc: reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)),
                NextEligibleSearchUtc: reader.IsDBNull(11) ? null : ParseTimestamp(reader.GetString(11))));
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
                MediaKind.Series,
                fromDate,
                toDate,
                cancellationToken);
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var from = fromDate.ToString("yyyy-MM-dd");
        var toExclusive = toDate.AddDays(1).ToString("yyyy-MM-dd");

        var added = await ReadDailyAsync(
            connection,
            $"SELECT {DailyCounts.GroupBy("created_utc")} AS day, COUNT(*) FROM series_entries WHERE created_utc >= @from AND created_utc < @to GROUP BY day;",
            from, toExclusive, cancellationToken);

        var before = await ReadScalarAsync(
            connection,
            "SELECT COUNT(*) FROM series_entries WHERE created_utc < @from;",
            from, cancellationToken);

        var matched = await ReadDailyAsync(
            connection,
            $"SELECT {DailyCounts.GroupBy("created_utc")} AS day, COUNT(*) FROM series_search_history WHERE outcome = 'matched' AND created_utc >= @from AND created_utc < @to GROUP BY day;",
            from, toExclusive, cancellationToken);

        var unmatched = await ReadDailyAsync(
            connection,
            $"SELECT {DailyCounts.GroupBy("created_utc")} AS day, COUNT(*) FROM series_search_history WHERE outcome <> 'matched' AND created_utc >= @from AND created_utc < @to GROUP BY day;",
            from, toExclusive, cancellationToken);

        var importFailures = await ReadDailyAsync(
            connection,
            $"SELECT {DailyCounts.GroupBy("detected_utc")} AS day, COUNT(*) FROM series_import_recovery_cases WHERE detected_utc >= @from AND detected_utc < @to GROUP BY day;",
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

    private static async Task<bool> EpisodeExistsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        CatalogueEpisodeItem episode,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1 FROM episode_entries
            WHERE series_id = @seriesId AND season_number = @seasonNumber AND episode_number = @episodeNumber
            LIMIT 1;
            """;
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@seasonNumber", episode.SeasonNumber);
        AddParameter(command, "@episodeNumber", episode.EpisodeNumber);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<string> EnsureSeasonAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        int seasonNumber,
        string stamp,
        CancellationToken cancellationToken)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText =
                """
                SELECT id FROM season_entries
                WHERE series_id = @seriesId AND season_number = @seasonNumber
                LIMIT 1;
                """;
            AddParameter(find, "@seriesId", seriesId);
            AddParameter(find, "@seasonNumber", seasonNumber);
            if (await find.ExecuteScalarAsync(cancellationToken) is string existing && !string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }

        var seasonId = Guid.CreateVersion7().ToString("N");
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO season_entries (id, series_id, season_number, monitored, created_utc, updated_utc)
            VALUES (@id, @seriesId, @seasonNumber, 1, @createdUtc, @updatedUtc);
            """;
        AddParameter(insert, "@id", seasonId);
        AddParameter(insert, "@seriesId", seriesId);
        AddParameter(insert, "@seasonNumber", seasonNumber);
        AddParameter(insert, "@createdUtc", stamp);
        AddParameter(insert, "@updatedUtc", stamp);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return seasonId;
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

    public async Task<SeriesWantedItem?> GetSeriesWantedStateAsync(
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                s.id, s.title, s.start_year, s.imdb_id,
                w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                w.prevent_lower_quality_replacements, w.quality_delta_last_decision,
                w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
            FROM series_wanted_state w
            INNER JOIN series_entries s ON s.id = w.series_id
            WHERE w.series_id = @seriesId AND w.library_id = @libraryId
            LIMIT 1;
            """;
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", libraryId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadWantedSeries(reader);
    }

    public async Task<bool> UpdateSeriesReplacementPolicyAsync(
        string seriesId,
        string libraryId,
        bool preventLowerQualityReplacements,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE series_wanted_state
            SET prevent_lower_quality_replacements = @value,
                updated_utc = @updatedUtc
            WHERE series_id = @seriesId AND library_id = @libraryId;
            """;
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@value", preventLowerQualityReplacements ? 1 : 0);
        AddParameter(command, "@updatedUtc", DateTimeOffset.UtcNow.ToString("O"));

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<IReadOnlyList<string>> ListEpisodesNeedingRecoveryAsync(
        string libraryId,
        int perSeriesLimit,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();

        // ROW_NUMBER is what keeps the per-series cap without reading every
        // series: the window is evaluated over the rows that already passed the
        // filter, not over the catalogue.
        command.CommandText =
            """
            SELECT id
            FROM (
                SELECT
                    e.id AS id,
                    e.updated_utc AS updated_utc,
                    ROW_NUMBER() OVER (
                        PARTITION BY e.series_id
                        ORDER BY e.updated_utc ASC, e.id ASC
                    ) AS rank_in_series
                FROM episode_entries e
                INNER JOIN episode_wanted_state w
                    ON w.episode_id = e.id
                   AND w.library_id = @libraryId
                WHERE e.monitored = 1
                  AND e.has_file = 1
                  AND COALESCE(w.quality_cutoff_met, e.quality_cutoff_met) = 0
            )
            WHERE rank_in_series <= @perSeriesLimit
            ORDER BY updated_utc ASC, id ASC
            LIMIT @take;
            """;
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@perSeriesLimit", Math.Clamp(perSeriesLimit, 1, 100));
        AddParameter(command, "@take", Math.Clamp(take, 1, 500));

        var episodeIds = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            episodeIds.Add(reader.GetString(0));
        }

        return episodeIds;
    }

    public async Task<DateTimeOffset?> GetEpisodeUpdatedUtcAsync(string episodeId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT updated_utc FROM episode_entries WHERE id = @episodeId LIMIT 1;";
        AddParameter(command, "@episodeId", episodeId);

        return await command.ExecuteScalarAsync(cancellationToken) is string value
            ? ParseTimestamp(value)
            : null;
    }

    public async Task<IReadOnlyList<SeriesEpisodeInventoryItem>> ListMonitoredMissingEpisodesAsync(
        string seriesId,
        string libraryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                e.id,
                e.season_number,
                e.episode_number,
                e.title,
                e.air_date_utc,
                e.monitored,
                e.has_file,
                COALESCE(w.wanted_status, 'missing'),
                COALESCE(w.wanted_reason, 'Episode is missing from the library.'),
                COALESCE(w.quality_cutoff_met, e.quality_cutoff_met),
                w.current_quality,
                w.target_quality,
                COALESCE(w.prevent_lower_quality_replacements, 1),
                w.quality_delta_last_decision,
                w.last_search_utc,
                w.next_eligible_search_utc,
                e.updated_utc
            FROM episode_entries e
            LEFT JOIN episode_wanted_state w ON w.episode_id = e.id AND w.library_id = @libraryId
            WHERE e.series_id = @seriesId
              AND e.monitored = 1
              AND e.has_file = 0
            ORDER BY e.season_number ASC, e.episode_number ASC;
            """;
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", libraryId);

        var episodes = new List<SeriesEpisodeInventoryItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            episodes.Add(new SeriesEpisodeInventoryItem(
                EpisodeId: reader.GetString(0),
                SeasonNumber: reader.GetInt32(1),
                EpisodeNumber: reader.GetInt32(2),
                Title: reader.IsDBNull(3) ? null : reader.GetString(3),
                AirDateUtc: reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                Overview: null,
                Monitored: reader.GetInt64(5) == 1,
                HasFile: reader.GetInt64(6) == 1,
                WantedStatus: reader.GetString(7),
                WantedReason: reader.GetString(8),
                QualityCutoffMet: reader.GetInt64(9) == 1,
                CurrentQuality: reader.IsDBNull(10) ? null : reader.GetString(10),
                TargetQuality: reader.IsDBNull(11) ? null : reader.GetString(11),
                PreventLowerQualityReplacements: reader.GetInt64(12) == 1,
                LastQualityDeltaDecision: reader.IsDBNull(13) ? null : reader.GetInt32(13),
                LastSearchUtc: reader.IsDBNull(14) ? null : ParseTimestamp(reader.GetString(14)),
                NextEligibleSearchUtc: reader.IsDBNull(15) ? null : ParseTimestamp(reader.GetString(15)),
                UpdatedUtc: ParseTimestamp(reader.GetString(16))));
        }

        return episodes;
    }

    private static SeriesWantedItem MapWanted(MediaWantedItem item)
        => new(
            SeriesId: item.Id,
            Title: item.Title,
            StartYear: item.Year,
            ImdbId: item.ImdbId,
            LibraryId: item.LibraryId,
            WantedStatus: item.WantedStatus,
            WantedReason: item.WantedReason,
            HasFile: item.HasFile,
            CurrentQuality: item.CurrentQuality,
            TargetQuality: item.TargetQuality,
            QualityCutoffMet: item.QualityCutoffMet,
            PreventLowerQualityReplacements: item.PreventLowerQualityReplacements,
            LastQualityDeltaDecision: item.LastQualityDeltaDecision,
            MissingSinceUtc: item.MissingSinceUtc,
            LastSearchUtc: item.LastSearchUtc,
            NextEligibleSearchUtc: item.NextEligibleSearchUtc,
            LastSearchResult: item.LastSearchResult,
            UpdatedUtc: item.UpdatedUtc);

    private static SeriesSearchHistoryItem MapSearchHistory(MediaSearchHistoryItem item)
        => new(
            Id: item.Id,
            SeriesId: item.MediaId,
            EpisodeId: item.EpisodeId,
            SeasonNumber: item.SeasonNumber,
            EpisodeNumber: item.EpisodeNumber,
            LibraryId: item.LibraryId,
            TriggerKind: item.TriggerKind,
            Outcome: item.Outcome,
            ReleaseName: item.ReleaseName,
            IndexerName: item.IndexerName,
            DetailsJson: item.DetailsJson,
            CreatedUtc: item.CreatedUtc);

    private static SeriesImportRecoveryCase MapImportRecoveryCase(MediaImportRecoveryCase item)
        => new(
            Id: item.Id,
            Title: item.Title,
            FailureKind: item.FailureKind,
            Status: item.Status,
            Summary: item.Summary,
            RecommendedAction: item.RecommendedAction,
            DetailsJson: item.DetailsJson,
            DetectedUtc: item.DetectedUtc,
            ResolvedUtc: item.ResolvedUtc);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
