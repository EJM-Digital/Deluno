using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Platform.Quality;
using Deluno.Series.Contracts;
using Microsoft.Data.Sqlite;

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

    private static async Task<SeriesListItem?> FindExistingSeriesAsync(
        System.Data.Common.DbConnection connection,
        SeriesListItem series,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
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
        AddParameter(command, "@imdbId", series.ImdbId);
        AddParameter(command, "@metadataProvider", series.MetadataProvider);
        AddParameter(command, "@metadataProviderId", series.MetadataProviderId);
        AddParameter(command, "@title", series.Title);
        AddParameter(command, "@startYear", series.StartYear);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSeries(reader) : null;
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

    public async Task<SeriesListItem?> UpdateMetadataAsync(
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
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE series_entries
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
            WaitingCount: waitingCount,
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
                    Monitored: episodeReader.GetInt64(5) == 1,
                    HasFile: episodeReader.GetInt64(6) == 1,
                    WantedStatus: episodeReader.GetString(7),
                    WantedReason: episodeReader.GetString(8),
                    QualityCutoffMet: episodeReader.GetInt64(9) == 1,
                    CurrentQuality: episodeReader.IsDBNull(10) ? null : episodeReader.GetString(10),
                    TargetQuality: episodeReader.IsDBNull(11) ? null : episodeReader.GetString(11),
                    PreventLowerQualityReplacements: episodeReader.GetInt64(12) == 1,
                    LastQualityDeltaDecision: episodeReader.IsDBNull(13) ? null : episodeReader.GetInt32(13),
                    LastSearchUtc: episodeReader.IsDBNull(14) ? null : ParseTimestamp(episodeReader.GetString(14)),
                    NextEligibleSearchUtc: episodeReader.IsDBNull(15) ? null : ParseTimestamp(episodeReader.GetString(15)),
                    UpdatedUtc: ParseTimestamp(episodeReader.GetString(16))));
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
        CancellationToken cancellationToken)
    {
        var items = new List<SeriesWantedItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            ignoreRetryWindow
                ? """
                  SELECT
                      s.id, s.title, s.start_year, s.imdb_id,
                      w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                      w.prevent_lower_quality_replacements, w.quality_delta_last_decision,
                      w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc
                  FROM series_wanted_state w
                  INNER JOIN series_entries s ON s.id = w.series_id
                  WHERE w.library_id = @libraryId
                    AND w.wanted_status IN ('missing', 'upgrade')
                  ORDER BY
                      CASE w.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                      COALESCE(w.last_search_utc, w.missing_since_utc, w.updated_utc) ASC,
                      s.title ASC
                  LIMIT @take;
                  """
                : """
                  SELECT
                      s.id, s.title, s.start_year, s.imdb_id,
                      w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                      w.prevent_lower_quality_replacements, w.quality_delta_last_decision,
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

    public async Task<int> CountRetryDelayedWantedAsync(
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM series_wanted_state
            WHERE library_id = @libraryId
              AND wanted_status IN ('missing', 'upgrade')
              AND next_eligible_search_utc IS NOT NULL
              AND next_eligible_search_utc > @now;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
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
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var normalizedTitle = title.Trim();
        var normalizedFilePath = NormalizeText(filePath);
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
                    @id, @title, @startYear, NULL, @monitored, @createdUtc, @updatedUtc
                );
                """;

            AddParameter(insert, "@id", seriesId);
            AddParameter(insert, "@title", normalizedTitle);
            AddParameter(insert, "@startYear", startYear);
            AddParameter(insert, "@monitored", unmonitorWhenCutoffMet && qualityCutoffMet ? 0 : 1);
            AddParameter(insert, "@createdUtc", now.ToString("O"));
            AddParameter(insert, "@updatedUtc", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (unmonitorWhenCutoffMet && qualityCutoffMet)
        {
            using var unmonitor = connection.CreateCommand();
            unmonitor.CommandText =
                """
                UPDATE series_entries
                SET monitored = 0,
                    updated_utc = @updatedUtc
                WHERE id = @seriesId;
                """;
            AddParameter(unmonitor, "@seriesId", seriesId);
            AddParameter(unmonitor, "@updatedUtc", now.ToString("O"));
            await unmonitor.ExecuteNonQueryAsync(cancellationToken);
        }

        using var wanted = connection.CreateCommand();
        wanted.CommandText =
            """
            INSERT INTO series_wanted_state (
                series_id, library_id, wanted_status, wanted_reason, has_file, quality_cutoff_met,
                current_quality, target_quality, file_path, file_size_bytes, imported_utc, last_verified_utc,
                missing_since_utc, last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
            )
            VALUES (
                @seriesId, @libraryId, @wantedStatus, @wantedReason, 1, @qualityCutoffMet,
                @currentQuality, @targetQuality, @filePath, @fileSizeBytes, @importedUtc, @lastVerifiedUtc,
                NULL, NULL, NULL, 'Imported from your existing library.', @updatedUtc
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
                last_search_result = excluded.last_search_result,
                updated_utc = excluded.updated_utc;
            """;

        AddParameter(wanted, "@seriesId", seriesId);
        AddParameter(wanted, "@libraryId", libraryId);
        AddParameter(wanted, "@wantedStatus", NormalizeWantedStatus(wantedStatus));
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
        }

        return created;
    }

    public async Task<IReadOnlyList<SeriesTrackedFileItem>> ListTrackedFilesAsync(
        string libraryId,
        CancellationToken cancellationToken)
    {
        var items = new List<SeriesTrackedFileItem>();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using (var series = connection.CreateCommand())
        {
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
                ORDER BY s.title COLLATE NOCASE;
                """;
            AddParameter(series, "@libraryId", libraryId);

            using var reader = await series.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new SeriesTrackedFileItem(
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
                    LastVerifiedUtc: reader.IsDBNull(7) ? null : ParseTimestamp(reader.GetString(7))));
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
                items.Add(new SeriesTrackedFileItem(
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
                    LastVerifiedUtc: reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10))));
            }
        }

        return items;
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

    public async Task<int> ReevaluateLibraryWantedStateAsync(
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken)
    {
        var items = new List<(string SeriesId, bool HasFile, string? CurrentQuality)>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT series_id, has_file, current_quality
                FROM series_wanted_state
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
                MediaType: "tv",
                HasFile: item.HasFile,
                CurrentQuality: item.CurrentQuality,
                CutoffQuality: cutoffQuality,
                UpgradeUntilCutoff: upgradeUntilCutoff,
                UpgradeUnknownItems: upgradeUnknownItems));

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
        command.CommandText = "DELETE FROM series WHERE id = @id;";
        AddParameter(command, "@id", seriesId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> UpdateQualityProfileAsync(string seriesId, string qualityProfileId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE series SET quality_profile_id = @qualityProfileId, updated_utc = @now WHERE id = @id;";
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
        CancellationToken cancellationToken)
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
              AND ews.wanted_status IN ('wanted', 'upgrade')
              AND (ews.next_eligible_search_utc IS NULL OR ews.next_eligible_search_utc <= @now)
              AND e.monitored = 1
            ORDER BY
                CASE ews.wanted_status WHEN 'wanted' THEN 0 ELSE 1 END,
                COALESCE(ews.last_search_utc, ews.updated_utc) ASC,
                e.season_number ASC,
                e.episode_number ASC
            LIMIT @take;
            """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@take", take);

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

    private static SeriesListItem ReadSeries(System.Data.Common.DbDataReader reader)
    {
        return new SeriesListItem(
            Id: reader.GetString(0),
            Title: reader.GetString(1),
            StartYear: reader.IsDBNull(2) ? null : reader.GetInt32(2),
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

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
