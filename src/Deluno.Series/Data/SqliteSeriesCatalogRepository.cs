using MetadataSearchResult = Deluno.Integrations.Metadata.MetadataSearchResult;
using ArtworkCacheKeys = Deluno.Integrations.Metadata.ArtworkCacheKeys;
using MetadataProviderIssue = Deluno.Integrations.Metadata.MetadataProviderIssue;
using MetadataIdentityConflict = Deluno.Integrations.Metadata.MetadataIdentityConflict;
using Deluno.Contracts;
using Deluno.Intake.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Media;
using Deluno.Quality;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Contracts;
using Deluno.Series.Migrations;
using Microsoft.Data.Sqlite;
using Deluno.Libraries.Data;

namespace Deluno.Series.Data;

public sealed class SqliteSeriesCatalogRepository(
    IDelunoDatabaseConnectionFactory databaseConnectionFactory,
    TimeProvider timeProvider,
    IMediaStateRepository? sharedMediaStateRepository = null,
    ILibrarySubtitlePreferences? librarySubtitlePreferences = null,
    IRealtimeEventPublisher? realtimeEventPublisher = null)
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
        sharedMediaStateRepository ?? new SqliteMediaStateRepository(databaseConnectionFactory, timeProvider, realtimeEventPublisher);

    public async Task<SeriesListItem> AddAsync(CreateSeriesRequest request, CancellationToken cancellationToken)
    {
        var seriesType = SeriesTypes.Normalize(request.SeriesType);
        var numberingScheme = SeriesNumberingSchemes.Resolve(seriesType, request.NumberingScheme);
        var numberingSource = SeriesNumberingSources.Normalize(request.NumberingSource);

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

            // The shared media store owns the common row, while TV-specific
            // numbering remains in this catalogue. Do not reset an existing
            // series when a legacy caller omitted the new optional fields.
            if (seriesType != SeriesTypes.Standard ||
                numberingScheme != SeriesNumberingSchemes.Standard ||
                numberingSource != SeriesNumberingSources.Provider ||
                request.SeasonFolder is not null ||
                request.ListMonitorMode is not null)
            {
                await UpdateNumberingAsync(
                    id,
                    new UpdateSeriesNumberingRequest(
                        seriesType,
                        numberingScheme,
                        numberingSource,
                        SeasonFolder: request.SeasonFolder,
                        ListMonitorMode: request.ListMonitorMode),
                    cancellationToken);
            }

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
            UpdatedUtc: now,
            SeriesType: seriesType,
            NumberingScheme: numberingScheme,
            NumberingSource: numberingSource);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var existing = await FindExistingSeriesAsync(connection, series, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // **Missing excludes Downloading, and that is not obvious.**
        //
        // This arm is "no file, and not Upcoming" rather than
        // `wanted_status = 'missing'`, so that a title Deluno holds no wanted row
        // for still counts as Missing — which is what the card draws for one.
        // But a downloading title also has no file, so it was counted twice: once
        // under Downloading and once under Missing, and the chip row summed to
        // twelve across eleven titles.
        //
        // Invisible until the lab library had anything downloading in it. Every
        // other arm names its status outright; this one is the only one that
        // describes a state by what it is not, which is why it was the one that
        // could quietly overlap.
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
                updated_utc,
                series_type,
                numbering_scheme,
                numbering_source,
                numbering_updated_utc,
                season_folder,
                list_monitor_mode
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
                @updatedUtc,
                @seriesType,
                @numberingScheme,
                @numberingSource,
                @numberingUpdatedUtc,
                @seasonFolder,
                @listMonitorMode
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
        AddParameter(command, "@seriesType", series.SeriesType);
        AddParameter(command, "@numberingScheme", series.NumberingScheme);
        AddParameter(command, "@numberingSource", series.NumberingSource);
        AddParameter(command, "@numberingUpdatedUtc", series.NumberingSource == SeriesNumberingSources.Owner ? series.UpdatedUtc.ToString("O") : null);
        AddParameter(command, "@seasonFolder", request.SeasonFolder is bool seasonFolder ? (seasonFolder ? 1 : 0) : null);
        AddParameter(command, "@listMonitorMode", request.ListMonitorMode);

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
            var sharedNumbering = entry is null
                ? DefaultNumberingHeader
                : await GetNumberingHeaderAsync(id, cancellationToken);
            var monitorNewEpisodes = entry is null
                ? true
                : await GetMonitorNewEpisodesAsync(id, cancellationToken);
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
                    NextEligibleSearchUtc: entry.NextEligibleSearchUtc,
                    // The file's own facts, for the same reason as the movie
                    // shelf: a detail projection poorer than the list one is a
                    // page that knows less than the grid it was opened from.
                    FilePath: entry.FilePath,
                    FileSizeBytes: entry.FileSizeBytes,
                    VideoCodec: entry.VideoCodec,
                    AudioCodec: entry.AudioCodec,
                    AudioChannels: entry.AudioChannels,
                    ReleaseGroup: entry.ReleaseGroup,
                    RuntimeMinutes: entry.RuntimeMinutes,
                    SeriesType: sharedNumbering.SeriesType,
                    NumberingScheme: sharedNumbering.NumberingScheme,
                    NumberingSource: sharedNumbering.NumberingSource,
                    MonitorNewEpisodes: monitorNewEpisodes);
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
                -- The file facts and the metadata numbers the LIST projection
                -- returns. See DetailMatchesListProjectionTests.
                s.primary_file_path,
                s.primary_file_size_bytes,
                s.primary_video_codec,
                s.primary_audio_codec,
                s.primary_audio_channels,
                s.primary_release_group,
                s.runtime_minutes,
                s.popularity,
                s.vote_count,
            {CatalogueWantedState.PageColumns},
                s.series_type,
                s.numbering_scheme,
                s.numbering_source,
                s.numbering_updated_utc
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

        // Ordinal 19 is the current quality, 20..28 are the file facts and the
        // metadata numbers, and the search state follows.
        var singleWanted = CatalogueWantedState.Read(reader, 29);
        var numbering = ReadNumberingHeader(reader, 29 + CatalogueWantedState.PageColumnCount);
        var monitorNewEpisodesPreset = await GetMonitorNewEpisodesAsync(id, cancellationToken);

        return ReadSeries(reader) with
        {
            CurrentQuality = reader.IsDBNull(19) ? null : reader.GetString(19),
            LibraryId = singleWanted.LibraryId,
            WantedStatus = singleWanted.WantedStatus,
            WantedReason = singleWanted.WantedReason,
            TargetQuality = singleWanted.TargetQuality,
            QualityCutoffMet = singleWanted.QualityCutoffMet,
            LastSearchUtc = singleWanted.LastSearchUtc,
            NextEligibleSearchUtc = singleWanted.NextEligibleSearchUtc,
            FilePath = reader.IsDBNull(20) ? null : reader.GetString(20),
            FileSizeBytes = reader.IsDBNull(21) ? null : reader.GetInt64(21),
            VideoCodec = reader.IsDBNull(22) ? null : reader.GetString(22),
            AudioCodec = reader.IsDBNull(23) ? null : reader.GetString(23),
            AudioChannels = reader.IsDBNull(24) ? null : reader.GetString(24),
            ReleaseGroup = reader.IsDBNull(25) ? null : reader.GetString(25),
            RuntimeMinutes = reader.IsDBNull(26) ? null : (int)reader.GetInt64(26),
            Popularity = reader.IsDBNull(27) ? null : reader.GetDouble(27),
            VoteCount = reader.IsDBNull(28) ? null : (int)reader.GetInt64(28),
            // Derived, not stored — see the movie repository's note.
            ApproximateBitrateMbps = MediaFileFacts.ApproximateBitrateMbps(
                reader.IsDBNull(21) ? null : reader.GetInt64(21),
                reader.IsDBNull(26) ? null : (int)reader.GetInt64(26)),
            SeriesType = numbering.SeriesType,
            NumberingScheme = numbering.NumberingScheme,
            NumberingSource = numbering.NumberingSource,
            MonitorNewEpisodes = monitorNewEpisodesPreset
        };
    }

    public async Task<SeriesNumberingDetail?> GetNumberingAsync(
        string seriesId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        string seriesType;
        string numberingScheme;
        string numberingSource;
        DateTimeOffset? updatedUtc;
        IReadOnlyList<string> availableSchemes;
        IReadOnlyList<string> otherSchemeNames;
        bool? seasonFolder;
        string? listMonitorMode;

        using (var series = connection.CreateCommand())
        {
            series.CommandText =
                """
                SELECT
                    COALESCE(series_type, 'standard'),
                    COALESCE(numbering_scheme, 'standard'),
                    COALESCE(numbering_source, 'provider'),
                    numbering_updated_utc,
                    available_numbering_schemes,
                    other_numbering_scheme_names,
                    season_folder,
                    list_monitor_mode
                FROM series_entries
                WHERE id = @seriesId
                LIMIT 1;
                """;
            AddParameter(series, "@seriesId", seriesId);

            using var reader = await series.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            seriesType = SeriesTypes.Normalize(reader.IsDBNull(0) ? null : reader.GetString(0));
            numberingScheme = SeriesNumberingSchemes.Normalize(reader.IsDBNull(1) ? null : reader.GetString(1));
            numberingSource = SeriesNumberingSources.Normalize(reader.IsDBNull(2) ? null : reader.GetString(2));
            updatedUtc = reader.IsDBNull(3) ? null : ParseTimestamp(reader.GetString(3));
            availableSchemes = SplitCsv(reader.IsDBNull(4) ? null : reader.GetString(4));
            otherSchemeNames = SplitCsv(reader.IsDBNull(5) ? null : reader.GetString(5));
            seasonFolder = reader.IsDBNull(6) ? null : reader.GetInt64(6) == 1;
            listMonitorMode = reader.IsDBNull(7) ? null : reader.GetString(7);
        }

        availableSchemes = MergeAvailableSchemes(seriesType, numberingScheme, availableSchemes);

        var episodes = new List<SeriesEpisodeNumbering>();
        using (var episodeCommand = connection.CreateCommand())
        {
            episodeCommand.CommandText =
                """
                SELECT
                    id,
                    season_number,
                    episode_number,
                    absolute_number,
                    scene_season_number,
                    scene_episode_number,
                    airdate_key,
                    numbering_source,
                    title,
                    dvd_season_number,
                    dvd_episode_number
                FROM episode_entries
                WHERE series_id = @seriesId
                ORDER BY season_number ASC, episode_number ASC, id ASC;
                """;
            AddParameter(episodeCommand, "@seriesId", seriesId);

            using var reader = await episodeCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                episodes.Add(new SeriesEpisodeNumbering(
                    EpisodeId: reader.GetString(0),
                    SeasonNumber: reader.GetInt32(1),
                    EpisodeNumber: reader.GetInt32(2),
                    AbsoluteNumber: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    SceneSeasonNumber: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    SceneEpisodeNumber: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    AirDate: ParseAirDateKey(reader.IsDBNull(6) ? null : reader.GetString(6)),
                    NumberingSource: reader.IsDBNull(7) ? null : reader.GetString(7),
                    Title: reader.IsDBNull(8) ? null : reader.GetString(8),
                    DvdSeasonNumber: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    DvdEpisodeNumber: reader.IsDBNull(10) ? null : reader.GetInt32(10)));
            }
        }

        return new SeriesNumberingDetail(
            seriesId,
            seriesType,
            numberingScheme,
            numberingSource,
            updatedUtc,
            episodes,
            availableSchemes,
            otherSchemeNames,
            seasonFolder,
            listMonitorMode);
    }

    /// <summary>
    /// Standard is always viewable - it is just the provider's plain
    /// season/episode list - even before any episode-groups refresh has ever
    /// run for this title. Air date and scene are offered whenever the
    /// series type or the currently-chosen scheme already implies them, so
    /// switching away from one and back never loses it from the list.
    /// Anything else (DVD order, absolute) only appears once a TMDb
    /// episode-groups refresh actually found one.
    /// </summary>
    private static IReadOnlyList<string> MergeAvailableSchemes(
        string seriesType,
        string numberingScheme,
        IReadOnlyList<string> storedSchemes)
    {
        var merged = new List<string> { SeriesNumberingSchemes.Standard };
        merged.AddRange(storedSchemes);
        if (seriesType == SeriesTypes.Daily || numberingScheme == SeriesNumberingSchemes.AirDate)
        {
            merged.Add(SeriesNumberingSchemes.AirDate);
        }

        if (seriesType == SeriesTypes.Anime || numberingScheme == SeriesNumberingSchemes.Scene)
        {
            merged.Add(SeriesNumberingSchemes.Scene);
        }

        return merged.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<SeriesNumberingDetail?> UpdateNumberingAsync(
        string seriesId,
        UpdateSeriesNumberingRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string currentType;
        string currentScheme;
        string currentSource;
        bool? currentSeasonFolder;
        string? currentListMonitorMode;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText =
                """
                SELECT
                    COALESCE(series_type, 'standard'),
                    COALESCE(numbering_scheme, 'standard'),
                    COALESCE(numbering_source, 'provider'),
                    season_folder,
                    list_monitor_mode
                FROM series_entries
                WHERE id = @seriesId
                LIMIT 1;
                """;
            AddParameter(current, "@seriesId", seriesId);

            using var reader = await current.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            currentType = SeriesTypes.Normalize(reader.GetString(0));
            currentScheme = SeriesNumberingSchemes.Normalize(reader.GetString(1));
            currentSource = SeriesNumberingSources.Normalize(reader.GetString(2));
            currentSeasonFolder = reader.IsDBNull(3) ? null : reader.GetInt64(3) == 1;
            currentListMonitorMode = reader.IsDBNull(4) ? null : reader.GetString(4);
        }

        var seriesType = request.SeriesType is null ? currentType : SeriesTypes.Normalize(request.SeriesType);
        var numberingScheme = request.NumberingScheme is null
            ? currentScheme
            : SeriesNumberingSchemes.Normalize(request.NumberingScheme);
        var numberingSource = request.NumberingSource is null
            ? request.Mappings is not null ? SeriesNumberingSources.Owner : currentSource
            : SeriesNumberingSources.Normalize(request.NumberingSource);
        var seasonFolder = request.SeasonFolder ?? currentSeasonFolder;
        var listMonitorMode = request.ListMonitorMode ?? currentListMonitorMode;
        var now = timeProvider.GetUtcNow();

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE series_entries
                SET series_type = @seriesType,
                    numbering_scheme = @numberingScheme,
                    numbering_source = @numberingSource,
                    numbering_updated_utc = @numberingUpdatedUtc,
                    updated_utc = @updatedUtc,
                    season_folder = @seasonFolder,
                    list_monitor_mode = @listMonitorMode
                WHERE id = @seriesId;
                """;
            AddParameter(update, "@seriesId", seriesId);
            AddParameter(update, "@seriesType", seriesType);
            AddParameter(update, "@numberingScheme", numberingScheme);
            AddParameter(update, "@numberingSource", numberingSource);
            AddParameter(update, "@numberingUpdatedUtc", now.ToString("O"));
            AddParameter(update, "@updatedUtc", now.ToString("O"));
            AddParameter(update, "@seasonFolder", seasonFolder is bool sf ? (sf ? 1 : 0) : null);
            AddParameter(update, "@listMonitorMode", listMonitorMode);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (request.Mappings is not null)
        {
            var duplicateIds = request.Mappings
                .GroupBy(mapping => mapping.EpisodeId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateIds.Length > 0)
            {
                throw new ArgumentException("Each episode may appear only once in a numbering mapping.", nameof(request));
            }

            if (request.Mappings.Count == 0)
            {
                using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText =
                    """
                    UPDATE episode_entries
                    SET absolute_number = NULL,
                        scene_season_number = NULL,
                        scene_episode_number = NULL,
                        airdate_key = NULL,
                        numbering_source = @numberingSource
                    WHERE series_id = @seriesId;
                    """;
                AddParameter(clear, "@seriesId", seriesId);
                AddParameter(clear, "@numberingSource", numberingSource);
                await clear.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                foreach (var mapping in request.Mappings)
                {
                    ValidateMapping(mapping);

                    using var updateEpisode = connection.CreateCommand();
                    updateEpisode.Transaction = transaction;
                    updateEpisode.CommandText =
                        """
                        UPDATE episode_entries
                        SET absolute_number = @absoluteNumber,
                            scene_season_number = @sceneSeasonNumber,
                            scene_episode_number = @sceneEpisodeNumber,
                            airdate_key = @airDate,
                            numbering_source = @numberingSource
                        WHERE id = @episodeId
                          AND series_id = @seriesId;
                        """;
                    AddParameter(updateEpisode, "@seriesId", seriesId);
                    AddParameter(updateEpisode, "@episodeId", mapping.EpisodeId);
                    AddParameter(updateEpisode, "@absoluteNumber", mapping.AbsoluteNumber);
                    AddParameter(updateEpisode, "@sceneSeasonNumber", mapping.SceneSeasonNumber);
                    AddParameter(updateEpisode, "@sceneEpisodeNumber", mapping.SceneEpisodeNumber);
                    AddParameter(updateEpisode, "@airDate", mapping.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    AddParameter(updateEpisode, "@numberingSource", numberingSource);
                    if (await updateEpisode.ExecuteNonQueryAsync(cancellationToken) != 1)
                    {
                        throw new ArgumentException(
                            $"Episode '{mapping.EpisodeId}' does not belong to series '{seriesId}'.",
                            nameof(request));
                    }
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetNumberingAsync(seriesId, cancellationToken);
    }

    public async Task<int> ApplyAutomaticNumberingAsync(
        string seriesId,
        IReadOnlyList<SeriesEpisodeSceneMapping> mappings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesId) || mappings.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var updated = 0;
        foreach (var mapping in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE episode_entries
                SET absolute_number = @absoluteNumber,
                    scene_season_number = @sceneSeasonNumber,
                    scene_episode_number = @sceneEpisodeNumber,
                    numbering_source = @numberingSource
                WHERE series_id = @seriesId
                  AND season_number = @seasonNumber
                  AND episode_number = @episodeNumber
                  AND (numbering_source IS NOT 'owner');
                """;
            AddParameter(update, "@seriesId", seriesId);
            AddParameter(update, "@seasonNumber", mapping.SeasonNumber);
            AddParameter(update, "@episodeNumber", mapping.EpisodeNumber);
            AddParameter(update, "@absoluteNumber", mapping.AbsoluteNumber);
            AddParameter(update, "@sceneSeasonNumber", mapping.SceneSeasonNumber);
            AddParameter(update, "@sceneEpisodeNumber", mapping.SceneEpisodeNumber);
            AddParameter(update, "@numberingSource", SeriesNumberingSources.SceneMap);
            updated += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return updated;
    }

    public async Task<int> ApplyEpisodeGroupsAsync(
        string seriesId,
        IReadOnlyList<string> availableNumberingSchemes,
        IReadOnlyList<string> otherNumberingSchemeNames,
        IReadOnlyList<SeriesEpisodeGroupMapping> mappings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using (var header = connection.CreateCommand())
        {
            header.CommandText =
                """
                UPDATE series_entries
                SET available_numbering_schemes = @availableSchemes,
                    other_numbering_scheme_names = @otherSchemeNames
                WHERE id = @seriesId;
                """;
            AddParameter(header, "@seriesId", seriesId);
            AddParameter(header, "@availableSchemes", availableNumberingSchemes.Count == 0
                ? null
                : string.Join(",", availableNumberingSchemes));
            AddParameter(header, "@otherSchemeNames", otherNumberingSchemeNames.Count == 0
                ? null
                : string.Join(",", otherNumberingSchemeNames));
            await header.ExecuteNonQueryAsync(cancellationToken);
        }

        var updated = 0;
        foreach (var mapping in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var update = connection.CreateCommand();
            // Every column here is refreshed from TMDb's episode groups, a
            // different source than TheXEM's scene/absolute pass above, so a
            // mapping's own null (this show has no DVD-order group, say)
            // must leave that column untouched rather than blanking out what
            // the other source already found.
            update.CommandText =
                """
                UPDATE episode_entries
                SET absolute_number = CASE WHEN @absoluteNumber IS NOT NULL THEN @absoluteNumber ELSE absolute_number END,
                    dvd_season_number = @dvdSeasonNumber,
                    dvd_episode_number = @dvdEpisodeNumber,
                    numbering_source = CASE WHEN @absoluteNumber IS NOT NULL THEN @numberingSource ELSE numbering_source END
                WHERE series_id = @seriesId
                  AND season_number = @seasonNumber
                  AND episode_number = @episodeNumber
                  AND (numbering_source IS NOT 'owner');
                """;
            AddParameter(update, "@seriesId", seriesId);
            AddParameter(update, "@seasonNumber", mapping.SeasonNumber);
            AddParameter(update, "@episodeNumber", mapping.EpisodeNumber);
            AddParameter(update, "@absoluteNumber", mapping.AbsoluteNumber);
            AddParameter(update, "@dvdSeasonNumber", mapping.DvdSeasonNumber);
            AddParameter(update, "@dvdEpisodeNumber", mapping.DvdEpisodeNumber);
            AddParameter(update, "@numberingSource", SeriesNumberingSources.SceneMap);
            updated += await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return updated;
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
                s.updated_utc,
                s.series_type,
                s.numbering_scheme,
                s.numbering_source,
                s.numbering_updated_utc
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
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var numbering = ReadNumberingHeader(reader, 19);
        return ReadSeries(reader) with
        {
            SeriesType = numbering.SeriesType,
            NumberingScheme = numbering.NumberingScheme,
            NumberingSource = numbering.NumberingSource
        };
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

    public async Task<MetadataProviderIssue?> GetMetadataProviderIssueAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT kind, provider, provider_id, evidence_key, detected_utc, acknowledged_utc
            FROM series_metadata_provider_issue
            WHERE series_id = @id
            LIMIT 1;
            """;
        AddParameter(command, "@id", id);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MetadataProviderIssue(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(5)
                ? null
                : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task<bool> RecordMetadataProviderIssueAsync(
        string id,
        MetadataProviderIssue issue,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series_metadata_provider_issue (
                series_id, kind, provider, provider_id, evidence_key, detected_utc, acknowledged_utc
            ) VALUES (
                @id, @kind, @provider, @providerId, @evidenceKey, @detectedUtc, @acknowledgedUtc
            )
            ON CONFLICT(series_id) DO UPDATE SET
                kind = excluded.kind,
                provider = excluded.provider,
                provider_id = excluded.provider_id,
                evidence_key = excluded.evidence_key,
                detected_utc = excluded.detected_utc,
                acknowledged_utc = NULL
            WHERE series_metadata_provider_issue.evidence_key <> excluded.evidence_key
            RETURNING 1;
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@kind", issue.Kind);
        AddParameter(command, "@provider", issue.Provider);
        AddParameter(command, "@providerId", issue.ProviderId);
        AddParameter(command, "@evidenceKey", issue.EvidenceKey);
        AddParameter(command, "@detectedUtc", issue.DetectedUtc.ToString("O"));
        AddParameter(command, "@acknowledgedUtc", issue.AcknowledgedUtc?.ToString("O"));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<MetadataProviderIssue?> AcknowledgeMetadataProviderIssueAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE series_metadata_provider_issue
            SET acknowledged_utc = COALESCE(acknowledged_utc, @now)
            WHERE series_id = @id;
            """;
        AddParameter(command, "@now", timeProvider.GetUtcNow().ToString("O"));
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetMetadataProviderIssueAsync(id, cancellationToken);
    }

    public async Task ClearMetadataProviderIssueAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM series_metadata_provider_issue WHERE series_id = @id;";
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// What counts as stale, in one place so the list and the count cannot
    /// disagree about it.
    /// </summary>
    /// <remarks>
    /// A poster or backdrop that is still a remote address was never saved locally
    /// (#1089), so the entry is due again rather than waiting out the window.
    ///
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
         OR (poster_url IS NOT NULL AND TRIM(poster_url) <> '' AND poster_url NOT LIKE '/api/metadata/artwork/%')
         OR (backdrop_url IS NOT NULL AND TRIM(backdrop_url) <> '' AND backdrop_url NOT LIKE '/api/metadata/artwork/%')
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

    public async Task<MetadataIdentityConflict?> FindMetadataIdentityConflictAsync(
        string excludeId,
        string title,
        int? startYear,
        string? imdbId,
        string metadataProvider,
        string metadataProviderId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title,
                CASE
                    WHEN metadata_provider = @metadataProvider AND metadata_provider_id = @metadataProviderId THEN 'provider-id'
                    WHEN @imdbId IS NOT NULL AND imdb_id = @imdbId THEN 'imdb-id'
                    ELSE 'title-year'
                END AS reason
            FROM series_entries
            WHERE id <> @excludeId
              AND (
                    (metadata_provider = @metadataProvider AND metadata_provider_id = @metadataProviderId)
                 OR (@imdbId IS NOT NULL AND imdb_id = @imdbId)
                 OR (lower(title) = lower(@title) AND COALESCE(start_year, -1) = COALESCE(@startYear, -1))
              )
            ORDER BY
                CASE
                    WHEN metadata_provider = @metadataProvider AND metadata_provider_id = @metadataProviderId THEN 0
                    WHEN @imdbId IS NOT NULL AND imdb_id = @imdbId THEN 1
                    ELSE 2
                END,
                created_utc ASC
            LIMIT 1;
            """;
        AddParameter(command, "@excludeId", excludeId);
        AddParameter(command, "@title", title.Trim());
        AddParameter(command, "@startYear", startYear);
        AddParameter(command, "@imdbId", NormalizeExternalId(imdbId));
        AddParameter(command, "@metadataProvider", NormalizeExternalId(metadataProvider));
        AddParameter(command, "@metadataProviderId", NormalizeExternalId(metadataProviderId));
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MetadataIdentityConflict(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

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
    private static string CatalogueHasFileFor()
        => $"COALESCE(ws.wanted_status, '') IN ('{WantedStatuses.Covered}', '{WantedStatuses.Upgrade}')";

    /// <summary>
    /// "This entry is on one particular rung", read from the one picked
    /// wanted-state row the page shows (<see cref="CatalogueWantedState.Join"/>),
    /// so a show held in two libraries is on one rung, not two (#1048).
    /// </summary>
    /// <summary>
    /// A show whose episodes are catalogued but none is monitored: nothing in it is wanted, so it counts as Unmonitored on the
    /// pill, the filter and the card alike, even when the show itself is switched on (#1072). A show with no catalogued episodes yet
    /// is unknown, not unwanted.
    /// </summary>
    private const string NothingWantedInShow =
        "(EXISTS (SELECT 1 FROM episode_entries ne WHERE ne.series_id = s.id) "
        + "AND NOT EXISTS (SELECT 1 FROM episode_entries me WHERE me.series_id = s.id AND me.monitored = 1))";

    private static string CatalogueWantedIs(string wantedStatus) => CatalogueWantedState.Is(wantedStatus);

    /// <summary>
    /// "Deluno has all of this show and some of it could be better." Also from
    /// the rung, for the same reason: <c>has_file = 1 AND quality_cutoff_met = 0</c>
    /// was true of a show holding one below-cutoff episode out of eighty-seven,
    /// so a barely-started show counted as Upgradable <i>and</i> as Missing.
    /// </summary>
    private static string CatalogueUpgradeFor()
        => CatalogueWantedIs(WantedStatuses.Upgrade);

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
    private const int SeriesNumberingColumnCount = 4;
    private static int NumberingPageOrdinal => WantedStateOrdinal + CatalogueWantedState.PageColumnCount;

    private static string SeriesNumberingColumns =>
        "s.series_type, s.numbering_scheme, s.numbering_source, s.numbering_updated_utc";

    private static readonly (string SeriesType, string NumberingScheme, string NumberingSource) DefaultNumberingHeader =
        (SeriesTypes.Standard, SeriesNumberingSchemes.Standard, SeriesNumberingSources.Provider);

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
        {CatalogueWantedState.PageColumns},
        {SeriesNumberingColumns}
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
        var filterParts = new[]
        {
            search is null ? string.Empty : CatalogueKeyset.SearchFilter("s"),
            CatalogueLibraryFilter(libraryId),
            CatalogueKeyset.MonitoredFilter(query.Monitored, "s", NothingWantedInShow),
            CatalogueKeyset.StatusFilter(
                status,
                "s",
                CatalogueHasFileFor(),
                CatalogueUpgradeFor(),
                CatalogueWantedIs(WantedStatuses.Covered),
                CatalogueWantedIs(WantedStatuses.Upcoming),
                CatalogueWantedIs(WantedStatuses.Downloading),
                // Without this the Airing chip filtered nothing: the switch
                // fell through to "no predicate", which reads as "everything",
                // so picking Continuing showed the whole shelf while the facet
                // beside it — computed a few lines down with this same
                // expression already present — correctly counted zero (#934).
                CatalogueWantedIs(WantedStatuses.Airing),
                monitoredOnly: !query.IncludeUnmonitoredInStatus,
                notWantedHere: NothingWantedInShow),
            // Quality and size read `ws` — the one wanted-state row this page
            // speaks for — rather than an EXISTS over all of them, so what the
            // filter selects and what the row displays cannot disagree.
            CatalogueKeyset.CustomFilters(query.Filters, MediaKind.Series, "s", "start_year")
        };
        var where = CatalogueKeyset.CombineFilters([.. filterParts, token is null ? string.Empty : CatalogueKeyset.SeekPredicate(sortExpression, "s", query.Descending)]);
        var countWhere = CatalogueKeyset.CombineFilters(filterParts);

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
                var numbering = ReadNumberingHeader(reader, NumberingPageOrdinal);

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
                    NextEligibleSearchUtc = wanted.NextEligibleSearchUtc,
                    SeriesType = numbering.SeriesType,
                    NumberingScheme = numbering.NumberingScheme,
                    NumberingSource = numbering.NumberingSource
                });

                sortValues.Add(CatalogueKeyset.ReadSortValue(reader, NumberingPageOrdinal + SeriesNumberingColumnCount));
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

        if (!query.IncludeFacets)
        {
            // The caller wants the number of matches and not the chips (#1081).
            var matching = await CountMatchingAsync(connection, countWhere, search, libraryId, query.Filters, cancellationToken);
            return new CataloguePage<SeriesListItem>(withSubtitles, nextPageToken, hasMore, matching, null);
        }

        // The chips are independent of the status pill and the monitoring filter, so
        // one answer serves every click until something is written to the catalogue
        // or ten seconds pass (#1081). Only the plain shelf and a search are kept: a
        // custom filter is rarely asked twice.
        CatalogueFacets facets;
        if (query.Filters is null || query.Filters.IsEmpty)
        {
            var generation = databaseConnectionFactory.GetWriteGeneration(DelunoDatabaseNames.Series);
            facets = await _facetMemo.GetOrAddAsync(
                $"{libraryId}|{search}",
                generation,
                FacetMemoLifetime,
                () => CountCatalogueFacetsAsync(connection, search, libraryId, status, query.Monitored, query.Filters, CancellationToken.None),
                cancellationToken);
        }
        else
        {
            facets = await CountCatalogueFacetsAsync(connection, search, libraryId, status, query.Monitored, query.Filters, cancellationToken);
        }

        return new CataloguePage<SeriesListItem>(
            withSubtitles,
            nextPageToken,
            hasMore,
            facets.TotalFor(status, query.Monitored),
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

        var progress = new Dictionary<string, (int Total, int Aired, int AiredWithFile, int AiredUpgradable, string? NextAir, bool HasUndated, int Monitored, bool HasMonitoredFuture)>(items.Count);

        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                $"""
                SELECT
                    e.series_id,
                    COUNT(*),
                    SUM(CASE WHEN e.monitored = 1 AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now THEN 1 ELSE 0 END),
                    SUM(CASE WHEN e.monitored = 1 AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now AND e.has_file = 1 THEN 1 ELSE 0 END),
                    SUM(CASE WHEN e.monitored = 1 AND e.air_date_utc IS NOT NULL AND e.air_date_utc <= @now AND e.has_file = 1 AND {SeriesRungWriter.EpisodeIsUpgradable("e", "ews")} THEN 1 ELSE 0 END),
                    MIN(CASE WHEN e.air_date_utc > @now THEN e.air_date_utc END),
                    MAX(CASE WHEN e.monitored = 1 AND e.air_date_utc IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN e.monitored = 1 THEN 1 ELSE 0 END),
                    MAX(CASE WHEN e.monitored = 1 AND e.air_date_utc > @now THEN 1 ELSE 0 END)
                FROM episode_entries e
                LEFT JOIN episode_wanted_state ews ON ews.episode_id = e.id
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
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    !reader.IsDBNull(6) && reader.GetInt32(6) == 1,
                    reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    !reader.IsDBNull(8) && reader.GetInt32(8) == 1);
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
                Monitored = item.Monitored && (counts.Total == 0 || counts.Monitored > 0),
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
                    counts.HasMonitoredFuture,
                    counts.HasUndated,
                    nothingWanted: counts.Total > 0 && counts.Monitored == 0)
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

    private static readonly TimeSpan FacetMemoLifetime = TimeSpan.FromSeconds(10);

    private readonly Deluno.Infrastructure.Caching.GenerationMemo<CatalogueFacets> _facetMemo = new(timeProvider);

    /// <summary>How many titles match the page's own filters, and nothing else.</summary>
    private async Task<int> CountMatchingAsync(
        System.Data.Common.DbConnection connection,
        string countWhere,
        string? search,
        string? libraryId,
        CatalogueFilters? filters,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = countWhere == "1 = 1"
            ? "SELECT COUNT(*) FROM series_entries;"
            : $"""
              SELECT COUNT(*)
              FROM series_entries s
              {CatalogueWantedState.Join("s", "series_wanted_state", "series_id", libraryId is not null)}
              WHERE {countWhere};
              """;
        CatalogueKeyset.BindSearch(command, search);
        CatalogueKeyset.BindCustomFilters(command, filters, MediaKind.Series, timeProvider.GetUtcNow());
        AddParameter(command, "@libraryId", libraryId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
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
        // Always joined: every pill count reads the same picked row the page
        // shows, so a show is counted under exactly one rung (#1048).
        var wantedJoin = CatalogueWantedState.Join("s", "series_wanted_state", "series_id", libraryId is not null);

        var where = CatalogueKeyset.CombineFilters(
            search is null ? string.Empty : CatalogueKeyset.SearchFilter("s"),
            CatalogueLibraryFilter(libraryId),
            CatalogueKeyset.CustomFilters(filters, MediaKind.Series, "s", "start_year"));

        // Independent of the status pill and the monitoring filter (#1048), so
        // clicking a pill never moves another number.
        var selectList = CatalogueKeyset.FacetSelectList(
            "s",
            CatalogueHasFileFor(),
            CatalogueUpgradeFor(),
            CatalogueWantedIs(WantedStatuses.Covered),
            CatalogueWantedIs(WantedStatuses.Upcoming),
            CatalogueWantedIs(WantedStatuses.Downloading),
            CatalogueWantedIs(WantedStatuses.Airing),
            NothingWantedInShow);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                {selectList}
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
                s.updated_utc,
                s.series_type,
                s.numbering_scheme,
                s.numbering_source,
                s.numbering_updated_utc
            FROM series_entries s
            ORDER BY s.created_utc DESC, s.title ASC;
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var numbering = ReadNumberingHeader(reader, 19);
            items.Add(ReadSeries(reader) with
            {
                SeriesType = numbering.SeriesType,
                NumberingScheme = numbering.NumberingScheme,
                NumberingSource = numbering.NumberingSource
            });
        }

        return items;
    }

    public async Task<IReadOnlySet<string>> ListReferencedArtworkCacheKeysAsync(
        CancellationToken cancellationToken)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT poster_url
            FROM series_entries
            WHERE poster_url LIKE '/api/metadata/artwork/%'
            UNION ALL
            SELECT backdrop_url
            FROM series_entries
            WHERE backdrop_url LIKE '/api/metadata/artwork/%';
            """;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0) && ArtworkCacheKeys.TryGet(reader.GetString(0), out var key))
            {
                keys.Add(key);
            }
        }

        return keys;
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
        bool updated;
        try
        {
            updated = await SharedMediaState.UpdateMetadataAsync(MediaKind.Series, update, cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new Deluno.Integrations.Metadata.MetadataIdentityConflictException(
                "Another show already owns the proposed metadata identity.",
                exception);
        }
        if (!updated)
        {
            return null;
        }

        await ClearMetadataProviderIssueAsync(update.Id, cancellationToken);
        return await GetByIdAsync(update.Id, cancellationToken);
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
        var now = timeProvider.GetUtcNow();
        var stamp = now.ToString("O");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var episodeId in episodeIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE episode_entries
                SET monitored = @monitored,
                    updated_utc = @updatedUtc
                WHERE id = @id
                RETURNING series_id;
                """;
            AddParameter(command, "@id", episodeId);
            AddParameter(command, "@monitored", monitored ? 1 : 0);
            AddParameter(command, "@updatedUtc", stamp);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                updated++;
                touched.Add(reader.GetString(0));
            }
        }

        // An unmonitored episode is not wanted, so not missing: the show's stored rung moves with it (#1072).
        await RefreshSeriesRungsAsync(connection, transaction, touched, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
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
                w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc,
                NULL AS available_utc, w.file_path, w.file_size_bytes
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

    public async Task<SeriesInventoryDetail?> GetInventoryDetailAsync(string seriesId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        string? title = null;
        int? startYear = null;
        var seriesType = SeriesTypes.Standard;
        var numberingScheme = SeriesNumberingSchemes.Standard;
        var numberingSource = SeriesNumberingSources.Provider;
        DateTimeOffset? numberingUpdatedUtc = null;
        IReadOnlyList<string> availableSchemes = [];
        IReadOnlyList<string> otherSchemeNames = [];
        using (var seriesCommand = connection.CreateCommand())
        {
            seriesCommand.CommandText =
                """
                SELECT
                    title,
                    start_year,
                    series_type,
                    numbering_scheme,
                    numbering_source,
                    numbering_updated_utc,
                    available_numbering_schemes,
                    other_numbering_scheme_names
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
            seriesType = SeriesTypes.Normalize(seriesReader.IsDBNull(2) ? null : seriesReader.GetString(2));
            numberingScheme = SeriesNumberingSchemes.Normalize(seriesReader.IsDBNull(3) ? null : seriesReader.GetString(3));
            numberingSource = SeriesNumberingSources.Normalize(seriesReader.IsDBNull(4) ? null : seriesReader.GetString(4));
            numberingUpdatedUtc = seriesReader.IsDBNull(5) ? null : ParseTimestamp(seriesReader.GetString(5));
            availableSchemes = SplitCsv(seriesReader.IsDBNull(6) ? null : seriesReader.GetString(6));
            otherSchemeNames = SplitCsv(seriesReader.IsDBNull(7) ? null : seriesReader.GetString(7));
        }

        availableSchemes = MergeAvailableSchemes(seriesType, numberingScheme, availableSchemes);

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
                    e.updated_utc,
                    e.absolute_number,
                    e.scene_season_number,
                    e.scene_episode_number,
                    e.airdate_key,
                    e.numbering_source,
                    e.catalogue_source,
                    w.last_search_result
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
                    UpdatedUtc: ParseTimestamp(episodeReader.GetString(17)),
                    AbsoluteNumber: episodeReader.IsDBNull(18) ? null : episodeReader.GetInt32(18),
                    SceneSeasonNumber: episodeReader.IsDBNull(19) ? null : episodeReader.GetInt32(19),
                    SceneEpisodeNumber: episodeReader.IsDBNull(20) ? null : episodeReader.GetInt32(20),
                    AirDate: ParseAirDateKey(episodeReader.IsDBNull(21) ? null : episodeReader.GetString(21)),
                    NumberingSource: episodeReader.IsDBNull(22) ? null : episodeReader.GetString(22),
                    CatalogueSource: episodeReader.IsDBNull(23) ? null : episodeReader.GetString(23),
                    LastSearchResult: episodeReader.IsDBNull(24) ? null : episodeReader.GetString(24)));
            }
        }

        return new SeriesInventoryDetail(
            SeriesId: seriesId,
            Title: title!,
            StartYear: startYear,
            SeasonCount: episodes.Select(item => item.SeasonNumber).Distinct().Count(),
            EpisodeCount: episodes.Count,
            ImportedEpisodeCount: episodes.Count(item => item.HasFile),
            Episodes: episodes,
            Numbering: new SeriesNumberingDetail(
                seriesId,
                seriesType,
                numberingScheme,
                numberingSource,
                numberingUpdatedUtc,
                episodes.Select(item => new SeriesEpisodeNumbering(
                    item.EpisodeId,
                    item.SeasonNumber,
                    item.EpisodeNumber,
                    item.AbsoluteNumber,
                    item.SceneSeasonNumber,
                    item.SceneEpisodeNumber,
                    item.AirDate,
                    item.NumberingSource,
                    item.Title)).ToArray(),
                availableSchemes,
                otherSchemeNames));
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
                COALESCE(h.created_utc, ''),
                h.indexer_id
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
                    : ParseTimestamp(reader.GetString(11)),
                IndexerId: reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return items;
    }

    public async Task<IReadOnlyList<SeriesSearchHistoryItem>> ListRecentSearchHistoryAsync(int limit, CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        if (sharedMediaStateRepository is not null)
        {
            var sharedItems = await sharedMediaStateRepository.ListSearchHistoryAsync(
                MediaKind.Series,
                boundedLimit,
                cancellationToken);
            return sharedItems.Select(MapSearchHistory).ToArray();
        }

        var items = new List<SeriesSearchHistoryItem>();

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
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
                COALESCE(h.created_utc, ''),
                h.indexer_id
            FROM series_search_history h
            LEFT JOIN episode_entries e ON e.id = h.episode_id
            ORDER BY h.created_utc DESC
            LIMIT {boundedLimit};
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
                    : ParseTimestamp(reader.GetString(11)),
                IndexerId: reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return items;
    }

    public async Task<IReadOnlyList<SeriesWantedItem>> ListEligibleWantedAsync(
        string libraryId,
        int take,
        DateTimeOffset now,
        bool ignoreRetryWindow,
        CancellationToken cancellationToken,
        string? wantedStatus = null,
        CatalogueFilters? filters = null,
        bool requireMonitored = true,
        bool randomOrder = false)
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
                wantedStatus,
                filters,
                requireMonitored,
                randomOrder);
            return sharedItems.Select(MapWanted).ToArray();
        }

        var items = new List<SeriesWantedItem>();
        var statusFilter = string.IsNullOrWhiteSpace(wantedStatus)
            ? "w.wanted_status IN ('missing', 'upgrade')"
            : "w.wanted_status = @wantedStatus";
        var customFilterSql = CatalogueKeyset.CustomFilters(filters, MediaKind.Series, "s", "start_year");
        var customFilter = string.IsNullOrWhiteSpace(customFilterSql) ? string.Empty : $"AND {customFilterSql}";

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        // requireMonitored is independent of ignoreRetryWindow: a person's own
        // retry ignores both, but an automatic targeted search (add-triggered,
        // health-replacement) ignores only the retry window and still must
        // never reach an unmonitored series (#653).
        var monitoredFilter = requireMonitored ? "AND s.monitored = 1" : string.Empty;
        var retryFilter = ignoreRetryWindow
            ? string.Empty
            : "AND (w.next_eligible_search_utc IS NULL OR w.next_eligible_search_utc <= @now)";

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
              SELECT
                  s.id, s.title, s.start_year, s.imdb_id,
                  w.library_id, w.wanted_status, w.wanted_reason, w.has_file, w.current_quality, w.target_quality, w.quality_cutoff_met,
                  w.prevent_lower_quality_replacements, w.quality_delta_last_decision,
                  w.missing_since_utc, w.last_search_utc, w.next_eligible_search_utc, w.last_search_result, w.updated_utc,
                  NULL AS available_utc, w.file_path, w.file_size_bytes, s.runtime_minutes
              FROM series_wanted_state w
              INNER JOIN series_entries s ON s.id = w.series_id
              WHERE w.library_id = @libraryId
                AND {statusFilter}
                {customFilter}
                {monitoredFilter}
                {retryFilter}
              ORDER BY
                  CASE w.wanted_status WHEN 'missing' THEN 0 ELSE 1 END,
                  {(randomOrder ? "RANDOM()" : "COALESCE(w.last_search_utc, w.missing_since_utc, w.updated_utc) ASC, s.title ASC")}
              LIMIT @take;
              """;

        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@now", now.ToString("O"));
        AddParameter(command, "@take", take);
        if (!string.IsNullOrWhiteSpace(wantedStatus))
        {
            AddParameter(command, "@wantedStatus", WantedStatuses.Normalize(wantedStatus));
        }
        CatalogueKeyset.BindCustomFilters(command, filters, MediaKind.Series, now);

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
        string? filePath,
        long? fileSizeBytes,
        IReadOnlyList<ImportedEpisodeItem>? episodes,
        CancellationToken cancellationToken,
        PreferenceEvaluationSnapshot? preferenceEvaluation = null,
        IReadOnlyList<ImportedEpisodeNumberingItem>? alternateEpisodes = null,
        IReadOnlyList<ImportedSeasonPackItem>? seasonPacks = null,
        IReadOnlyList<PreferenceEvaluationSnapshot>? preferenceEvaluations = null,
        string? seriesId = null,
        bool upgradeUntilCutoff = false,
        bool upgradeUnknownItems = false,
        string episodeImportSource = "download",
        string? resultNote = null)
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
                    FilePath: filePath,
                    FileSizeBytes: fileSizeBytes,
                    Episodes: episodes,
                    PreferenceEvaluation: preferenceEvaluation,
                    AlternateEpisodes: alternateEpisodes,
                    SeasonPacks: seasonPacks,
                    PreferenceEvaluations: preferenceEvaluations,
                    SeriesId: seriesId,
                    UpgradeUntilCutoff: upgradeUntilCutoff,
                    UpgradeUnknownItems: upgradeUnknownItems,
                    EpisodeImportSource: episodeImportSource,
                    ResultNote: resultNote)
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
            qualityCutoffMet, filePath, fileSizeBytes, episodes,
            preferenceEvaluation, alternateEpisodes, seasonPacks, preferenceEvaluations, knownSeriesId,
            upgradeUntilCutoff, upgradeUnknownItems, episodeImportSource, _) = request;

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
            if (!string.IsNullOrWhiteSpace(knownSeriesId))
            {
                using var known = connection.CreateCommand();
                known.Transaction = transaction;
                known.CommandText = "SELECT id FROM series_entries WHERE id = @id LIMIT 1;";
                AddParameter(known, "@id", knownSeriesId.Trim());
                seriesId = await known.ExecuteScalarAsync(cancellationToken) as string;
            }

            if (string.IsNullOrWhiteSpace(seriesId))
            {
                using var lookup = connection.CreateCommand();
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
                    filePath,
                    fileSizeBytes,
                    preferenceEvaluation,
                    preferenceEvaluations,
                    knownSeriesId,
                    request.ResultNote),
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

        if (alternateEpisodes is { Count: > 0 })
        {
            var resolved = await ResolveAlternateEpisodesAsync(
                connection,
                transaction,
                seriesId!,
                alternateEpisodes,
                cancellationToken);
            if (resolved.Count > 0)
            {
                var merged = (episodes ?? [])
                    .GroupBy(item => (item.SeasonNumber, item.EpisodeNumber))
                    .ToDictionary(group => group.Key, group => group.First());
                foreach (var item in resolved)
                {
                    if (merged.TryGetValue((item.SeasonNumber, item.EpisodeNumber), out var existing))
                    {
                        merged[(item.SeasonNumber, item.EpisodeNumber)] = existing with
                        {
                            AbsoluteNumber = existing.AbsoluteNumber ?? item.AbsoluteNumber,
                            AirDate = existing.AirDate ?? item.AirDate,
                            SceneSeasonNumber = existing.SceneSeasonNumber ?? item.SceneSeasonNumber,
                            SceneEpisodeNumber = existing.SceneEpisodeNumber ?? item.SceneEpisodeNumber
                        };
                    }
                    else
                    {
                        merged[(item.SeasonNumber, item.EpisodeNumber)] = item;
                    }
                }

                episodes = merged.Values
                    .OrderBy(item => item.SeasonNumber)
                    .ThenBy(item => item.EpisodeNumber)
                    .ToArray();
            }
        }

        if (seasonPacks is { Count: > 0 })
        {
            var expanded = await ExpandSeasonPacksAsync(
                connection,
                transaction,
                seriesId!,
                seasonPacks,
                cancellationToken);
            if (expanded.Count > 0)
            {
                var merged = (episodes ?? [])
                    .GroupBy(item => (item.SeasonNumber, item.EpisodeNumber))
                    .ToDictionary(group => group.Key, group => group.First());

                foreach (var item in expanded)
                {
                    // A specifically numbered file is more precise than a
                    // season pack. Keep it when both describe the same
                    // episode, while allowing the pack to fill gaps.
                    if (!merged.ContainsKey((item.SeasonNumber, item.EpisodeNumber)))
                    {
                        merged[(item.SeasonNumber, item.EpisodeNumber)] = item;
                    }
                }

                episodes = merged.Values
                    .OrderBy(item => item.SeasonNumber)
                    .ThenBy(item => item.EpisodeNumber)
                    .ToArray();
            }
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
                                created_utc, updated_utc, absolute_number, scene_season_number,
                                scene_episode_number, airdate_key, numbering_source
                            )
                            VALUES (
                                @id, @seriesId, @seasonId, @seasonNumber, @episodeNumber, NULL, NULL,
                                1, @hasFile, 0, @filePath, @fileSizeBytes, @importedUtc, @lastVerifiedUtc,
                                @createdUtc, @updatedUtc, @absoluteNumber, @sceneSeasonNumber,
                                @sceneEpisodeNumber, @airdateKey, @numberingSource
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
                        AddParameter(insertEpisode, "@absoluteNumber", episode.AbsoluteNumber);
                        AddParameter(insertEpisode, "@sceneSeasonNumber", episode.SceneSeasonNumber);
                        AddParameter(insertEpisode, "@sceneEpisodeNumber", episode.SceneEpisodeNumber);
                        AddParameter(insertEpisode, "@airdateKey", episode.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        AddParameter(insertEpisode, "@numberingSource", episode.NumberingSource);
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
                                absolute_number = CASE
                                    WHEN numbering_source = 'owner' OR @absoluteNumber IS NULL THEN absolute_number
                                    ELSE @absoluteNumber END,
                                scene_season_number = CASE
                                    WHEN numbering_source = 'owner' OR @sceneSeasonNumber IS NULL THEN scene_season_number
                                    ELSE @sceneSeasonNumber END,
                                scene_episode_number = CASE
                                    WHEN numbering_source = 'owner' OR @sceneEpisodeNumber IS NULL THEN scene_episode_number
                                    ELSE @sceneEpisodeNumber END,
                                airdate_key = CASE
                                    WHEN numbering_source = 'owner' OR @airdateKey IS NULL THEN airdate_key
                                    ELSE @airdateKey END,
                                numbering_source = CASE
                                    WHEN numbering_source = 'owner' OR @numberingSource IS NULL THEN numbering_source
                                    ELSE @numberingSource END,
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
                        AddParameter(updateEpisode, "@absoluteNumber", episode.AbsoluteNumber);
                        AddParameter(updateEpisode, "@sceneSeasonNumber", episode.SceneSeasonNumber);
                        AddParameter(updateEpisode, "@sceneEpisodeNumber", episode.SceneEpisodeNumber);
                        AddParameter(updateEpisode, "@airdateKey", episode.AirDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                        AddParameter(updateEpisode, "@numberingSource", episode.NumberingSource);
                        await updateEpisode.ExecuteNonQueryAsync(cancellationToken);
                    }

                    using var upsertEpisodeWanted = connection.CreateCommand();
                    upsertEpisodeWanted.Transaction = transaction;
                    upsertEpisodeWanted.CommandText =
                        """
                        INSERT INTO episode_wanted_state (
                            episode_id, series_id, library_id, wanted_status, wanted_reason,
                            current_quality, target_quality, quality_cutoff_met,
                            last_search_utc, next_eligible_search_utc, last_search_result, updated_utc
                        )
                        VALUES (
                            @episodeId, @seriesId, @libraryId, @wantedStatus, @wantedReason,
                            @episodeCurrentQuality, @episodeTargetQuality, @episodeQualityCutoffMet,
                            NULL, NULL, @lastSearchResult, @updatedUtc
                        )
                        ON CONFLICT(episode_id) DO UPDATE SET
                            library_id = excluded.library_id,
                            wanted_status = excluded.wanted_status,
                            wanted_reason = excluded.wanted_reason,
                            -- COALESCE, not a plain overwrite: an episode that
                            -- is being marked missing carries no quality, and
                            -- must not blank what a previous import recorded.
                            current_quality = COALESCE(excluded.current_quality, current_quality),
                            target_quality = COALESCE(excluded.target_quality, target_quality),
                            -- And the same care for the flag, which was written
                            -- unconditionally at first. A rescan of a healthy
                            -- library reports "has a file" without deciding a
                            -- cutoff, so every settled episode was un-settled
                            -- and looked upgradable again. Only an import that
                            -- actually met the cutoff may raise it; only an
                            -- episode losing its file may clear it.
                            quality_cutoff_met = CASE
                                WHEN excluded.quality_cutoff_met = 1 THEN 1
                                WHEN excluded.wanted_status = 'missing' THEN 0
                                ELSE quality_cutoff_met
                            END,
                            last_search_result = excluded.last_search_result,
                            updated_utc = excluded.updated_utc;
                        """;
                    // The quality the series row has been given all along. An
                    // episode that held a file and recorded no quality showed
                    // none on its own page and gave the upgrade check nothing
                    // to compare against, so a covered episode never settled.
                    // This episode's own quality when the caller knows it, and
                    // the series' answer only as a fallback.
                    var episodeQuality = episode.HasFile
                        ? (string.IsNullOrWhiteSpace(episode.CurrentQuality) ? currentQuality : episode.CurrentQuality)
                        : null;

                    // And its own cutoff, decided by the caller from that
                    // quality. Inheriting the series verdict marked every
                    // episode of an incomplete show as not-met - including the
                    // ones holding a file at the cutoff quality - so they never
                    // settled and looked permanently upgradable.
                    var episodeCutoffMet = episode.HasFile
                        && (episode.QualityCutoffMet ?? qualityCutoffMet);

                    // #654: an episode with a file used to be written
                    // `covered` outright, whatever its quality, so a 720p
                    // episode under a 1080p cutoff with upgrade-until-cutoff
                    // on was never searched again. This is the same decision
                    // the film side and the series row above already make —
                    // met (or upgrades off) settles, short of it and still
                    // upgrading keeps looking.
                    var episodeWantedStatus = !episode.HasFile
                        ? WantedStatuses.Missing
                        : episodeCutoffMet || !upgradeUntilCutoff
                            ? WantedStatuses.Covered
                            : WantedStatuses.Upgrade;

                    // The reason says what actually happened: a season pack
                    // episode, one found sitting in an existing library scan,
                    // or an ordinary completed-download import — not the one
                    // sentence every path used to share regardless of source.
                    var episodeOrigin = !episode.HasFile
                        ? "Episode is missing from the library."
                        : episode.FromSeasonPack
                            ? "Episode imported from a season pack."
                            : string.Equals(episodeImportSource, "existing-library", StringComparison.OrdinalIgnoreCase)
                                ? "Episode found in your existing library."
                                : "Episode imported.";
                    var episodeWantedReason = episodeWantedStatus == WantedStatuses.Upgrade
                        && !string.IsNullOrWhiteSpace(episodeQuality)
                        && !string.IsNullOrWhiteSpace(targetQuality)
                        ? $"{episodeOrigin} It's currently {episodeQuality}. Deluno will keep looking until it reaches {targetQuality}."
                        : episodeOrigin;

                    AddParameter(upsertEpisodeWanted, "@episodeId", episodeId);
                    AddParameter(upsertEpisodeWanted, "@seriesId", seriesId);
                    AddParameter(upsertEpisodeWanted, "@libraryId", libraryId);
                    AddParameter(upsertEpisodeWanted, "@wantedStatus", episodeWantedStatus);
                    AddParameter(upsertEpisodeWanted, "@wantedReason", episodeWantedReason);
                    AddParameter(
                        upsertEpisodeWanted,
                        "@lastSearchResult",
                        episode.HasFile ? (string.IsNullOrWhiteSpace(request.ResultNote) ? episodeOrigin : request.ResultNote.Trim()) : null);
                    AddParameter(upsertEpisodeWanted, "@episodeCurrentQuality", episodeQuality);
                    AddParameter(upsertEpisodeWanted, "@episodeTargetQuality", targetQuality);
                    AddParameter(upsertEpisodeWanted, "@episodeQualityCutoffMet", episodeCutoffMet ? 1 : 0);
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

    private static async Task<IReadOnlyList<ImportedEpisodeItem>> ExpandSeasonPacksAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        IReadOnlyList<ImportedSeasonPackItem> seasonPacks,
        CancellationToken cancellationToken)
    {
        var expanded = new List<ImportedEpisodeItem>();

        foreach (var pack in seasonPacks
                     .Where(item => item.SeasonNumber >= 0)
                     .OrderBy(item => item.SeasonNumber))
        {
            var declared = (pack.Episodes ?? [])
                .Where(item => item.SeasonNumber == pack.SeasonNumber && item.EpisodeNumber >= 0)
                .GroupBy(item => (item.SeasonNumber, item.EpisodeNumber))
                .Select(group => group.First())
                .OrderBy(item => item.EpisodeNumber)
                .ToArray();
            if (declared.Length == 0)
            {
                // "Show.S01" names a season but proves nothing about which
                // files the pack actually contains. Expanding that label to
                // the whole provider catalogue invented coverage whenever a
                // pack was incomplete or the client exposed only one file.
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT season_number, episode_number
                FROM episode_entries
                WHERE series_id = @seriesId
                  AND season_number = @seasonNumber
                ORDER BY episode_number;
                """;
            AddParameter(command, "@seriesId", seriesId);
            AddParameter(command, "@seasonNumber", pack.SeasonNumber);

            var cataloguedEpisodes = new HashSet<int>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cataloguedEpisodes.Add(reader.GetInt32(1));
            }

            foreach (var episode in declared.Where(item => cataloguedEpisodes.Contains(item.EpisodeNumber)))
            {
                expanded.Add(episode with
                {
                    HasFile = true,
                    FilePath = NormalizeText(episode.FilePath) ?? NormalizeText(pack.FilePath),
                    FileSizeBytes = episode.FileSizeBytes ?? pack.FileSizeBytes,
                    FromSeasonPack = true
                });
            }
        }

        return expanded;
    }

    private static async Task<IReadOnlyList<ImportedEpisodeItem>> ResolveAlternateEpisodesAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        IReadOnlyList<ImportedEpisodeNumberingItem> alternateEpisodes,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ImportedEpisodeItem>();
        foreach (var alternate in alternateEpisodes)
        {
            var scheme = SeriesNumberingSchemes.Normalize(alternate.NumberingScheme);
            var column = scheme switch
            {
                SeriesNumberingSchemes.Absolute when alternate.AbsoluteNumber is not null => "absolute_number",
                SeriesNumberingSchemes.AirDate when alternate.AirDate is not null => "airdate_key",
                SeriesNumberingSchemes.Scene when alternate.SceneSeasonNumber is not null && alternate.SceneEpisodeNumber is not null => "scene_key",
                _ => null
            };
            if (column is null)
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = column == "scene_key"
                ? "SELECT id, season_number, episode_number FROM episode_entries WHERE series_id = @seriesId AND scene_season_number = @sceneSeasonNumber AND scene_episode_number = @sceneEpisodeNumber;"
                : $"SELECT id, season_number, episode_number FROM episode_entries WHERE series_id = @seriesId AND {column} = @numberingKey;";
            AddParameter(command, "@seriesId", seriesId);
            if (column == "scene_key")
            {
                AddParameter(command, "@sceneSeasonNumber", alternate.SceneSeasonNumber);
                AddParameter(command, "@sceneEpisodeNumber", alternate.SceneEpisodeNumber);
            }
            else
            {
                AddParameter(
                    command,
                    "@numberingKey",
                    column == "airdate_key"
                        ? alternate.AirDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : alternate.AbsoluteNumber);
            }

            var matches = new List<(string Id, int SeasonNumber, int EpisodeNumber)>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                matches.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
                if (matches.Count > 1)
                {
                    break;
                }
            }

            // No guesswork: an unknown or duplicate alternate key is left
            // unmatched and remains visible in the import issue/review path.
            if (matches.Count != 1)
            {
                continue;
            }

            var match = matches[0];
            resolved.Add(new ImportedEpisodeItem(
                match.SeasonNumber,
                match.EpisodeNumber,
                alternate.HasFile,
                alternate.FilePath,
                alternate.FileSizeBytes,
                alternate.AbsoluteNumber,
                alternate.AirDate,
                alternate.SceneSeasonNumber,
                alternate.SceneEpisodeNumber));
        }

        return resolved;
    }

    /// <summary>
    /// Re-decides the stored rung of each show for every library that holds it.
    /// The one call every writer of <c>episode_entries.monitored</c> or
    /// <c>has_file</c> ends with, inside its own transaction, so the stored
    /// column automatic search reads never lags the episodes (#1072).
    /// </summary>
    private async Task RefreshSeriesRungsAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        IEnumerable<string> seriesIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var seriesId in seriesIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libraryIds = new List<string>();
            using (var libraries = connection.CreateCommand())
            {
                libraries.Transaction = transaction;
                libraries.CommandText = "SELECT DISTINCT library_id FROM series_wanted_state WHERE series_id = @seriesId;";
                AddParameter(libraries, "@seriesId", seriesId);
                using var libraryReader = await libraries.ExecuteReaderAsync(cancellationToken);
                while (await libraryReader.ReadAsync(cancellationToken))
                {
                    libraryIds.Add(libraryReader.GetString(0));
                }
            }

            foreach (var libraryId in libraryIds)
            {
                await RefreshSeriesRungAsync(connection, transaction, seriesId, libraryId, now, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Re-decides one show's rung from its episodes and stores it. The
    /// decision and its SQL live in <see cref="SeriesRungWriter"/>, because
    /// a profile change re-decides the same rung from outside this class.
    /// </summary>
    private Task RefreshSeriesRungAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        string libraryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => SeriesRungWriter.RefreshAsync(connection, transaction, seriesId, libraryId, now, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> ListRecordedFileQualitiesAsync(
        string libraryId,
        CancellationToken cancellationToken)
    {
        var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return recorded;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT episode.file_path, wanted.current_quality
            FROM episode_entries AS episode
            INNER JOIN episode_wanted_state AS wanted ON wanted.episode_id = episode.id
            WHERE wanted.library_id = @libraryId
              AND episode.file_path IS NOT NULL
              AND wanted.current_quality IS NOT NULL;
            """;
        AddParameter(command, "@libraryId", libraryId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            recorded[reader.GetString(0)] = reader.GetString(1);
        }

        return recorded;
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
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            using var episode = connection.CreateCommand();
            episode.Transaction = transaction;
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
            wanted.Transaction = transaction;
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
            await RefreshSeriesRungsAsync(connection, transaction, [seriesId], now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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

    /// <summary>
    /// The one writer behind "this episode's (or show's) file is gone" (#1065):
    /// the file fields, the status and the monitored flag change in a single
    /// transaction, so a crash can never leave an episode Missing and still
    /// monitored. Matched by path, so a row that changed since it was read
    /// (a new import replaced the file) is never cleared by mistake.
    /// </summary>
    public async Task<bool> MarkTrackedFileGoneAsync(
        string seriesId,
        string? episodeId,
        string libraryId,
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var marked = await MarkTrackedFileGoneInTransactionAsync(
            connection, transaction, seriesId, episodeId, libraryId, filePath, cancellationToken);
        if (marked)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return marked;
    }

    /// <summary>
    /// <see cref="MarkTrackedFileGoneAsync"/> for many files in one transaction
    /// (#1070). One answer per item, in order; an item that no longer matches is false.
    /// </summary>
    public async Task<IReadOnlyList<bool>> MarkTrackedFilesGoneAsync(
        IReadOnlyList<SeriesFileGoneRequest> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var results = new List<bool>(items.Count);
        foreach (var item in items)
        {
            results.Add(await MarkTrackedFileGoneInTransactionAsync(
                connection, transaction, item.SeriesId, item.EpisodeId, item.LibraryId, item.FilePath, cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task<IReadOnlyList<EpisodeFileLink>> ListEpisodeFileLinksAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT e.id, e.series_id, s.title, COALESCE(w.library_id, ''), e.season_number, e.episode_number, e.file_path
            FROM episode_entries e
            INNER JOIN series_entries s ON s.id = e.series_id
            LEFT JOIN episode_wanted_state w ON w.episode_id = e.id
            WHERE e.has_file = 1 AND e.file_path IS NOT NULL AND e.season_number > 0;
            """;
        var results = new List<EpisodeFileLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new EpisodeFileLink(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6)));
        }

        return results;
    }

    public async Task<bool> ClearMislabelledEpisodeFileAsync(
        string episodeId,
        string filePath,
        CancellationToken cancellationToken,
        string? reason = null)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().ToString("O");

        string? seriesId;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT series_id FROM episode_entries WHERE id = @episodeId AND has_file = 1 AND file_path = @filePath;";
            AddParameter(read, "@episodeId", episodeId);
            AddParameter(read, "@filePath", filePath);
            seriesId = (await read.ExecuteScalarAsync(cancellationToken)) as string;
        }

        if (seriesId is null)
        {
            return false;
        }

        using (var episode = connection.CreateCommand())
        {
            episode.Transaction = transaction;
            episode.CommandText =
                """
                UPDATE episode_entries
                SET has_file = 0,
                    file_path = NULL,
                    file_size_bytes = NULL,
                    imported_utc = NULL,
                    quality_cutoff_met = 0,
                    updated_utc = @now
                WHERE id = @episodeId
                  AND file_path = @filePath;
                """;
            AddParameter(episode, "@episodeId", episodeId);
            AddParameter(episode, "@filePath", filePath);
            AddParameter(episode, "@now", now);
            await episode.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var wanted = connection.CreateCommand())
        {
            wanted.Transaction = transaction;
            wanted.CommandText =
                """
                UPDATE episode_wanted_state
                SET wanted_status = 'missing',
                    wanted_reason = @reason,
                    current_quality = NULL,
                    quality_cutoff_met = 0,
                    next_eligible_search_utc = NULL,
                    updated_utc = @now
                WHERE episode_id = @episodeId;
                """;
            AddParameter(wanted, "@episodeId", episodeId);
            AddParameter(wanted, "@reason", reason ?? "The file held here was a copy of another episode, so it went to the recycle bin.");
            AddParameter(wanted, "@now", now);
            await wanted.ExecuteNonQueryAsync(cancellationToken);
        }

        // The show-level note may name the same file; leave nothing that reads as "removed outside Deluno".
        await DetachSeriesLevelFileRecordsAsync(connection, transaction, seriesId, filePath, recycleBinOnly: false, cancellationToken);
        await RefreshSeriesRungsAsync(connection, transaction, [seriesId], timeProvider.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<WrongEpisodeFileLinkRepair>> RepairWrongEpisodeFileLinksAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var linked = new List<(string EpisodeId, string SeriesId, string Title, int Season, int Episode, string FilePath)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                """
                SELECT e.id, e.series_id, s.title, e.season_number, e.episode_number, e.file_path
                FROM episode_entries e
                INNER JOIN series_entries s ON s.id = e.series_id
                WHERE e.has_file = 1
                  AND e.file_path IS NOT NULL
                  AND EXISTS (
                      SELECT 1 FROM episode_entries other
                      WHERE other.series_id = e.series_id
                        AND other.file_path = e.file_path
                        AND other.has_file = 1
                        AND other.id <> e.id);
                """;
            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                linked.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5)));
            }
        }

        var repairs = new List<WrongEpisodeFileLinkRepair>();
        foreach (var group in linked.GroupBy(item => (item.SeriesId, item.FilePath)))
        {
            // The library file's own name is the evidence: it is written from the
            // episode the import was for. No SxxEyy token in it, no repair.
            var named = SeriesNumberingResolver
                .ParseFileName(Path.GetFileName(group.Key.FilePath), SeriesNumberingSchemes.Standard)
                .Matches
                .Where(match => match.SeasonNumber is not null && match.EpisodeNumber is not null)
                .Select(match => (Season: match.SeasonNumber!.Value, Episode: match.EpisodeNumber!.Value))
                .ToHashSet();
            if (named.Count == 0 || !group.Any(item => named.Contains((item.Season, item.Episode))))
            {
                continue;
            }

            var kept = string.Join(", ", group
                .Where(item => named.Contains((item.Season, item.Episode)))
                .OrderBy(item => item.Season).ThenBy(item => item.Episode)
                .Select(item => $"S{item.Season:00}E{item.Episode:00}"));
            foreach (var wrong in group.Where(item => !named.Contains((item.Season, item.Episode))))
            {
                var ownFile = await FindEpisodesOwnFileAsync(
                    connection, wrong.SeriesId, wrong.FilePath, wrong.Season, wrong.Episode, cancellationToken);
                repairs.Add(new WrongEpisodeFileLinkRepair(
                    wrong.SeriesId, wrong.Title, wrong.EpisodeId, wrong.Season, wrong.Episode, wrong.FilePath, kept, ownFile));
            }
        }

        if (repairs.Count == 0)
        {
            return repairs;
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var repair in repairs)
        {
            using var episode = connection.CreateCommand();
            episode.Transaction = transaction;
            episode.CommandText =
                """
                UPDATE episode_entries
                SET has_file = 0,
                    file_path = NULL,
                    file_size_bytes = NULL,
                    imported_utc = NULL,
                    quality_cutoff_met = 0,
                    updated_utc = @now
                WHERE id = @episodeId
                  AND file_path = @filePath;
                """;
            AddParameter(episode, "@episodeId", repair.EpisodeId);
            AddParameter(episode, "@filePath", repair.FilePath);
            AddParameter(episode, "@now", now.ToString("O"));
            if (await episode.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                continue;
            }

            using var wanted = connection.CreateCommand();
            wanted.Transaction = transaction;
            wanted.CommandText =
                """
                UPDATE episode_wanted_state
                SET wanted_status = 'missing',
                    wanted_reason = 'The file that was linked here belongs to another episode, so the link was cleared.',
                    current_quality = NULL,
                    quality_cutoff_met = 0,
                    updated_utc = @now
                WHERE episode_id = @episodeId;
                """;
            AddParameter(wanted, "@episodeId", repair.EpisodeId);
            AddParameter(wanted, "@now", now.ToString("O"));
            await wanted.ExecuteNonQueryAsync(cancellationToken);

            if (repair.RelinkedFilePath is { } ownFile)
            {
                // The episode's own file is in the show's folder (#1174): link it
                // rather than leave the episode to be downloaded again.
                var detected = LibraryQualityDecider.DetectQuality(Path.GetFileName(ownFile));
                using var relink = connection.CreateCommand();
                relink.Transaction = transaction;
                relink.CommandText =
                    """
                    UPDATE episode_entries
                    SET has_file = 1,
                        file_path = @ownFile,
                        file_size_bytes = @size,
                        imported_utc = @now,
                        updated_utc = @now
                    WHERE id = @episodeId;
                    UPDATE episode_wanted_state
                    SET wanted_status = 'covered',
                        wanted_reason = 'Its own file was found in the show''s folder and linked.',
                        current_quality = @quality,
                        updated_utc = @now
                    WHERE episode_id = @episodeId;
                    """;
                AddParameter(relink, "@episodeId", repair.EpisodeId);
                AddParameter(relink, "@ownFile", ownFile);
                AddParameter(relink, "@size", new FileInfo(ownFile).Length);
                AddParameter(relink, "@quality", detected);
                AddParameter(relink, "@now", now.ToString("O"));
                await relink.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await RefreshSeriesRungsAsync(
            connection, transaction, repairs.Select(repair => repair.SeriesId), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // A linked file is judged by the profile that now governs its title, so the
        // cutoff and upgrade answer is the one every other screen reads.
        foreach (var seriesId in repairs.Where(repair => repair.RelinkedFilePath is not null).Select(repair => repair.SeriesId).Distinct())
        {
            await SharedMediaState.ReevaluateTitleUnderProfileAsync(
                MediaKind.Series,
                seriesId,
                await SharedMediaState.GetTitleQualityProfileIdAsync(MediaKind.Series, seriesId, cancellationToken),
                cancellationToken);
        }

        return repairs;
    }

    public async Task<IReadOnlyList<RelinkedEpisodeFile>> RelinkEpisodesWithTheirOwnFilesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(DelunoDatabaseNames.Series, cancellationToken);

        // Shows with an episode that shows no file, and a folder they already keep files in.
        var wanted = new List<(string SeriesId, string Title, string EpisodeId, int Season, int Episode)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText =
                """
                SELECT e.series_id, s.title, e.id, e.season_number, e.episode_number
                FROM episode_entries e
                INNER JOIN series_entries s ON s.id = e.series_id
                WHERE e.has_file = 0
                  AND EXISTS (SELECT 1 FROM episode_entries other
                              WHERE other.series_id = e.series_id AND other.has_file = 1 AND other.file_path IS NOT NULL);
                """;
            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                wanted.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4)));
            }
        }

        var linked = new List<RelinkedEpisodeFile>();
        var now = timeProvider.GetUtcNow();
        foreach (var show in wanted.GroupBy(item => item.SeriesId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? anyTracked = null;
            var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var files = connection.CreateCommand())
            {
                files.CommandText = "SELECT file_path FROM episode_entries WHERE series_id = @seriesId AND has_file = 1 AND file_path IS NOT NULL;";
                AddParameter(files, "@seriesId", show.Key);
                using var reader = await files.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    tracked.Add(reader.GetString(0));
                    anyTracked ??= reader.GetString(0);
                }
            }

            var folder = ShowFolderOf(anyTracked);
            if (folder is null)
            {
                continue;
            }

            Dictionary<(int Season, int Episode), List<string>> byNumber = [];
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                             .Where(path => RepairVideoExtensions.Contains(Path.GetExtension(path)))
                             .Where(path => !tracked.Contains(path))
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    var numbers = SeriesNumberingResolver
                        .ParseFileName(Path.GetFileName(path), SeriesNumberingSchemes.Standard).Matches
                        .Where(match => match.SeasonNumber is not null && match.EpisodeNumber is not null)
                        .ToList();
                    // Only a file whose name gives exactly one episode: a pack or a double
                    // episode file is the import's business, not a guess made here.
                    if (numbers.Count != 1)
                    {
                        continue;
                    }

                    var key = (numbers[0].SeasonNumber!.Value, numbers[0].EpisodeNumber!.Value);
                    if (!byNumber.TryGetValue(key, out var list))
                    {
                        byNumber[key] = list = [];
                    }

                    list.Add(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue; // A folder that cannot be read is not searched; nothing is changed.
            }

            foreach (var episode in show.OrderBy(item => item.Season).ThenBy(item => item.Episode))
            {
                var own = byNumber.GetValueOrDefault((episode.Season, episode.Episode))?.FirstOrDefault();
                if (own is null)
                {
                    continue;
                }

                var detected = LibraryQualityDecider.DetectQuality(Path.GetFileName(own));
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                using var relink = connection.CreateCommand();
                relink.Transaction = transaction;
                relink.CommandText =
                    """
                    UPDATE episode_entries
                    SET has_file = 1, file_path = @ownFile, file_size_bytes = @size,
                        imported_utc = @now, missing_detected_utc = NULL, updated_utc = @now
                    WHERE id = @episodeId AND has_file = 0;
                    UPDATE episode_wanted_state
                    SET wanted_status = 'covered',
                        wanted_reason = 'Its own file was found in the show''s folder and linked.',
                        current_quality = @quality,
                        updated_utc = @now
                    WHERE episode_id = @episodeId;
                    """;
                AddParameter(relink, "@episodeId", episode.EpisodeId);
                AddParameter(relink, "@ownFile", own);
                AddParameter(relink, "@size", new FileInfo(own).Length);
                AddParameter(relink, "@quality", detected);
                AddParameter(relink, "@now", now.ToString("O"));
                if (await relink.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    continue;
                }

                await RefreshSeriesRungsAsync(connection, transaction, [show.Key], now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                linked.Add(new RelinkedEpisodeFile(show.Key, episode.Title, episode.EpisodeId, episode.Season, episode.Episode, own));
            }
        }

        foreach (var seriesId in linked.Select(item => item.SeriesId).Distinct())
        {
            await SharedMediaState.ReevaluateTitleUnderProfileAsync(
                MediaKind.Series,
                seriesId,
                await SharedMediaState.GetTitleQualityProfileIdAsync(MediaKind.Series, seriesId, cancellationToken),
                cancellationToken);
        }

        return linked;
    }

    /// <summary>The folder a show keeps its files in: the folder of one of them, or its parent when that is a season folder.</summary>
    private static string? ShowFolderOf(string? trackedFilePath)
    {
        if (string.IsNullOrWhiteSpace(trackedFilePath))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(trackedFilePath);
        if (folder is not null
            && System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileName(folder), @"^(season|series|s)[\s._-]*\d+$|^specials?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            folder = Path.GetDirectoryName(folder);
        }

        return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) ? folder : null;
    }

    private static readonly HashSet<string> RepairVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    /// <summary>
    /// The episode's own file (#1174): a video in the show's folder whose name gives that
    /// season and episode and which no episode of the show is linked to. The show's folder is
    /// the folder of the file the wrong link pointed at, or its parent when that is a season
    /// folder. Null when there is none.
    /// </summary>
    private async Task<string?> FindEpisodesOwnFileAsync(
        DbConnection connection,
        string seriesId,
        string linkedFilePath,
        int season,
        int episode,
        CancellationToken cancellationToken)
    {
        try
        {
            var folder = Path.GetDirectoryName(linkedFilePath);
            if (folder is not null
                && System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(folder), @"^(season|series|s)[\s._-]*\d+$|^specials?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                folder = Path.GetDirectoryName(folder);
            }

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            var candidates = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => RepairVideoExtensions.Contains(Path.GetExtension(path)))
                .Where(path => SeriesNumberingResolver
                    .ParseFileName(Path.GetFileName(path), SeriesNumberingSchemes.Standard)
                    .Matches
                    .Any(match => match.SeasonNumber == season && match.EpisodeNumber == episode))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var candidate in candidates)
            {
                using var taken = connection.CreateCommand();
                taken.CommandText = "SELECT 1 FROM episode_entries WHERE series_id = @seriesId AND has_file = 1 AND file_path = @path LIMIT 1;";
                AddParameter(taken, "@seriesId", seriesId);
                AddParameter(taken, "@path", candidate);
                if (await taken.ExecuteScalarAsync(cancellationToken) is null)
                {
                    return candidate;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be read is simply not searched; the episode is Missing again.
        }

        return null;
    }

    private async Task<bool> MarkTrackedFileGoneInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string seriesId,
        string? episodeId,
        string libraryId,
        string filePath,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToString("O");

        if (!string.IsNullOrWhiteSpace(episodeId))
        {
            using var episode = connection.CreateCommand();
            episode.Transaction = transaction;
            episode.CommandText =
                """
                UPDATE episode_entries
                SET has_file = 0,
                    monitored = 0,
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
            AddParameter(episode, "@now", now);
            if (await episode.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return false;
            }

            using var wanted = connection.CreateCommand();
            wanted.Transaction = transaction;
            wanted.CommandText =
                """
                UPDATE episode_wanted_state
                SET wanted_status = 'missing',
                    wanted_reason = 'The file is gone from disk, so Deluno stopped wanting this episode.',
                    updated_utc = @now
                WHERE episode_id = @episodeId
                  AND library_id = @libraryId;
                """;
            AddParameter(wanted, "@episodeId", episodeId);
            AddParameter(wanted, "@libraryId", libraryId);
            AddParameter(wanted, "@now", now);
            await wanted.ExecuteNonQueryAsync(cancellationToken);
            await RefreshSeriesRungsAsync(connection, transaction, [seriesId], timeProvider.GetUtcNow(), cancellationToken);
            return true;
        }

        // A show-level file record is only a note of the latest file Deluno holds for the show. When that
        // file is gone the note is corrected, nothing more: the show is never unmonitored for it, because
        // what is gone is one file, and the episodes each carry their own file and their own flag.
        return await DetachSeriesLevelFileRecordsAsync(
            connection, transaction, seriesId, filePath, recycleBinOnly: false, cancellationToken) > 0;
    }

    /// <summary>
    /// Corrects the show-level file notes (series_wanted_state) that name a file the show no longer holds:
    /// the note moves to another file an episode still holds, or is cleared. Never changes the show's
    /// monitored flag. Matches the exact path, or (recycleBinOnly) any path inside a recycle bin folder, whatever
    /// the row's has_file says: an older library check cleared the flag and kept the path, which left a stale note.
    /// </summary>
    private async Task<int> DetachSeriesLevelFileRecordsAsync(
        DbConnection connection,
        DbTransaction transaction,
        string? seriesId,
        string? filePath,
        bool recycleBinOnly,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToString("O");
        var affected = new List<(string SeriesId, string LibraryId)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                $"""
                SELECT series_id, library_id
                FROM series_wanted_state
                WHERE file_path IS NOT NULL
                  AND (@seriesId IS NULL OR series_id = @seriesId)
                  AND {(recycleBinOnly ? "file_path LIKE '%.deluno-recycle-bin%'" : "file_path = @filePath COLLATE NOCASE")};
                """;
            AddParameter(read, "@seriesId", seriesId);
            if (!recycleBinOnly)
            {
                AddParameter(read, "@filePath", filePath);
            }

            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                affected.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (affectedSeries, libraryId) in affected)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE series_wanted_state
                SET file_path = (
                        SELECT e.file_path FROM episode_entries e
                        WHERE e.series_id = @seriesId AND e.has_file = 1 AND e.file_path IS NOT NULL
                          AND e.file_path NOT LIKE '%.deluno-recycle-bin%'
                        ORDER BY e.imported_utc DESC LIMIT 1),
                    file_size_bytes = (
                        SELECT e.file_size_bytes FROM episode_entries e
                        WHERE e.series_id = @seriesId AND e.has_file = 1 AND e.file_path IS NOT NULL
                          AND e.file_path NOT LIKE '%.deluno-recycle-bin%'
                        ORDER BY e.imported_utc DESC LIMIT 1),
                    updated_utc = @now
                WHERE series_id = @seriesId AND library_id = @libraryId;
                """;
            AddParameter(update, "@seriesId", affectedSeries);
            AddParameter(update, "@libraryId", libraryId);
            AddParameter(update, "@now", now);
            await update.ExecuteNonQueryAsync(cancellationToken);

            using var flag = connection.CreateCommand();
            flag.Transaction = transaction;
            flag.CommandText =
                "UPDATE series_wanted_state SET has_file = CASE WHEN file_path IS NULL THEN 0 ELSE 1 END WHERE series_id = @seriesId AND library_id = @libraryId;";
            AddParameter(flag, "@seriesId", affectedSeries);
            AddParameter(flag, "@libraryId", libraryId);
            await flag.ExecuteNonQueryAsync(cancellationToken);
        }

        if (affected.Count > 0)
        {
            await RefreshSeriesRungsAsync(
                connection, transaction, affected.Select(item => item.SeriesId).Distinct().ToArray(),
                timeProvider.GetUtcNow(), cancellationToken);
        }

        return affected.Count;
    }

    public async Task<int> DetachSeriesFileRecordsAsync(
        string? seriesId,
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var count = await DetachSeriesLevelFileRecordsAsync(
            connection, transaction, seriesId, filePath, recycleBinOnly: false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<int> ClearSeriesFileRecordsInRecycleBinAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var count = await DetachSeriesLevelFileRecordsAsync(
            connection, transaction, null, null, recycleBinOnly: true, cancellationToken);

        // A show that is monitored never "stopped being wanted": an older library check wrote that reason on shows
        // it then left monitored. The reason is worked out again from the episodes, as every other writer does.
        var stale = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT DISTINCT w.series_id
                FROM series_wanted_state w
                INNER JOIN series_entries s ON s.id = w.series_id
                WHERE s.monitored = 1
                  AND w.wanted_reason LIKE '%stopped wanting this show%';
                """;
            using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stale.Add(reader.GetString(0));
            }
        }

        if (stale.Count > 0)
        {
            await RefreshSeriesRungsAsync(connection, transaction, stale, timeProvider.GetUtcNow(), cancellationToken);
            count += stale.Count;
        }

        await transaction.CommitAsync(cancellationToken);
        return count;
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
        // Filed under the indexer's id (#1077), asked while its name still is its name.
        var indexerId = await IndexerIdentity.ResolveIdAsync(databaseConnectionFactory, indexerName, cancellationToken);

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
                    id, series_id, episode_id, library_id, trigger_kind, outcome, release_name, indexer_name, indexer_id, details_json, created_utc
                )
                VALUES (
                    @id, @seriesId, @episodeId, @libraryId, @triggerKind, @outcome, @releaseName, @indexerName, @indexerId, @detailsJson, @createdUtc
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
            AddParameter(history, "@indexerId", indexerId);
            AddParameter(history, "@detailsJson", SearchHistoryDetails.Trim(detailsJson));
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
        // A search that ends with no release adds a row to the bell; a later one that finds something removes it.
        realtimeEventPublisher.RequestAttentionChanged("noRelease");
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

    public Task<int> ReevaluateLibraryWantedStateAsync(
        string libraryId,
        string? cutoffQuality,
        bool upgradeUntilCutoff,
        bool upgradeUnknownItems,
        CancellationToken cancellationToken)
        // See the note on the movie repository: the local copy of this routine
        // had drifted from the shared rule and wrote a row at a time. One
        // rule, one place.
        => (sharedMediaStateRepository ?? new SqliteMediaStateRepository(databaseConnectionFactory, timeProvider, realtimeEventPublisher))
            .ReevaluateLibraryWantedStateAsync(
                MediaKind.Series,
                libraryId,
                cutoffQuality,
                upgradeUntilCutoff,
                upgradeUnknownItems,
                cancellationToken);

    public async Task<SeriesImportRecoverySummary> GetImportRecoverySummaryAsync(CancellationToken cancellationToken, int limit = 500, bool includeDetails = true)
    {
        var summary = await LoadImportRecoverySummaryAsync(cancellationToken, limit, includeDetails);
        // The fallback below reads every case; trim it to what was asked for.
        return summary.RecentCases.Count <= Math.Max(limit, 0) && includeDetails
            ? summary
            : summary with
            {
                RecentCases = summary.RecentCases
                    .Take(Math.Max(limit, 0))
                    .Select(item => includeDetails ? item : item with { DetailsJson = ImportRecoveryKeyDetails.Trim(item.DetailsJson) })
                    .ToArray()
            };
    }

    private async Task<SeriesImportRecoverySummary> LoadImportRecoverySummaryAsync(CancellationToken cancellationToken, int limit, bool includeDetails)
    {
        if (sharedMediaStateRepository is not null)
        {
            var sharedSummary = await sharedMediaStateRepository.GetImportRecoverySummaryAsync(
                MediaKind.Series,
                cancellationToken,
                limit,
                includeDetails);
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

        // Counted separately from the page below (#649): a person can only
        // dismiss or retry what the screen lists, so these must reflect every
        // open case, not just the ones that fit in RecentCases.
        var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var kindCommand = connection.CreateCommand())
        {
            kindCommand.CommandText =
                """
                SELECT failure_kind, COUNT(*)
                FROM series_import_recovery_cases
                WHERE status = 'open'
                GROUP BY failure_kind;
                """;
            using var kindReader = await kindCommand.ExecuteReaderAsync(cancellationToken);
            while (await kindReader.ReadAsync(cancellationToken))
            {
                kindCounts[kindReader.GetString(0)] = Convert.ToInt32(kindReader.GetInt64(1));
            }
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
                LIMIT 500;
                """;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                openCases.Add(ReadImportRecoveryCase(reader));
            }
        }

        return new SeriesImportRecoverySummary(
            OpenCount: openCount,
            QualityCount: kindCounts.GetValueOrDefault("quality"),
            UnmatchedCount: kindCounts.GetValueOrDefault("unmatched"),
            CorruptCount: kindCounts.GetValueOrDefault("corrupt"),
            DownloadFailedCount: kindCounts.GetValueOrDefault("downloadFailed"),
            ImportFailedCount: kindCounts.GetValueOrDefault("importFailed"),
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

        // The same failure again is the same case (#596). One import retried
        // three times opened three identical cases, and a person had to
        // dismiss one fact three times. A retry sends the same request, so the
        // details match too; another download of the same release does not,
        // and keeps its own case to settle with it.
        using (var open = connection.CreateCommand())
        {
            open.CommandText =
                """
                SELECT id FROM series_import_recovery_cases
                WHERE status = 'open' AND title = @title AND failure_kind = @failureKind AND summary = @summary AND details_json IS @detailsJson
                ORDER BY detected_utc DESC
                LIMIT 1;
                """;
            AddParameter(open, "@title", item.Title);
            AddParameter(open, "@failureKind", item.FailureKind);
            AddParameter(open, "@summary", item.Summary);
            AddParameter(open, "@detailsJson", item.DetailsJson);
            if (await open.ExecuteScalarAsync(cancellationToken) is string openId)
            {
                using var refresh = connection.CreateCommand();
                refresh.CommandText =
                    "UPDATE series_import_recovery_cases SET recommended_action = @recommendedAction, detected_utc = @detectedUtc WHERE id = @id;";
                AddParameter(refresh, "@id", openId);
                AddParameter(refresh, "@recommendedAction", item.RecommendedAction);
                AddParameter(refresh, "@detectedUtc", item.DetectedUtc.ToString("O"));
                await refresh.ExecuteNonQueryAsync(cancellationToken);
                await AddImportRecoveryEventAsync(openId, "case_refreshed", "The same import failed again.", null, cancellationToken);
                return item with { Id = openId };
            }
        }

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
        await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishRecoveryCaseChangedAsync(
            new RecoveryCaseChangedEvent(item.Id, "opened", item.FailureKind, item.Title, "series", null, timeProvider.GetUtcNow().ToString("O")),
            cancellationToken));

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
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (deleted)
        {
            await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishRecoveryCaseChangedAsync(
                new RecoveryCaseChangedEvent(id, "closed", null, null, "series", null, timeProvider.GetUtcNow().ToString("O")),
                cancellationToken));
        }

        return deleted;
    }

    public async Task<SeriesImportRecoveryCase?> GetImportRecoveryCaseAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT id, title, failure_kind, status, summary, recommended_action, details_json, detected_utc, resolved_utc
            FROM series_import_recovery_cases
            WHERE id = @id;
            """;
        AddParameter(select, "@id", id);
        using var reader = await select.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadImportRecoveryCase(reader) : null;
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

        await realtimeEventPublisher.PublishSafelyAsync(publisher => publisher.PublishRecoveryCaseChangedAsync(
            new RecoveryCaseChangedEvent(id, "closed", null, null, "series", null, now.ToString("O")),
            cancellationToken));

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

    /// <summary>
    /// One settle for both catalogues, in <see cref="SqliteMediaStateRepository"/>:
    /// the predicate is what changed for #563, and it must change once.
    /// </summary>
    public Task<int> SettleImportRecoveryCasesAsync(
        ImportRecoveryCaseScope scope,
        string reason,
        CancellationToken cancellationToken)
        => SharedMediaState.SettleImportRecoveryCasesAsync(MediaKind.Series, scope, reason, cancellationToken);

    public Task<IReadOnlyList<SettledRecoveryCase>> SettleImportRecoveryCasesListedAsync(
        ImportRecoveryCaseScope scope,
        string reason,
        CancellationToken cancellationToken)
        => SharedMediaState.SettleImportRecoveryCasesListedAsync(MediaKind.Series, scope, reason, cancellationToken);

    public Task<IReadOnlyList<SettledRecoveryCase>> SettleCasesOfTitlesHoldingFilesAsync(CancellationToken cancellationToken)
        => SharedMediaState.SettleCasesOfTitlesHoldingFilesAsync(MediaKind.Series, cancellationToken);

    public Task<TitleFileHolds> GetTitleFileHoldsAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken)
        => SharedMediaState.GetTitleFileHoldsAsync(MediaKind.Series, entityIds, cancellationToken);

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

    public async Task<string?> GetQualityProfileIdAsync(string seriesId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT quality_profile_id FROM series_entries WHERE id = @id LIMIT 1;";
        AddParameter(command, "@id", seriesId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : value.ToString();
    }

    public async Task<bool> UpdateQualityProfileAsync(string seriesId, string? qualityProfileId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE series_entries SET quality_profile_id = @qualityProfileId, updated_utc = @now WHERE id = @id;";
        AddParameter(command, "@id", seriesId);
        AddParameter(command, "@qualityProfileId", qualityProfileId);
        AddParameter(command, "@now", DateTimeOffset.UtcNow.ToString("O"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (changed)
        {
            // The show is now judged by this profile (#1157): re-decide each
            // episode it owns against it, so the show page, the Upgrades card
            // and the sweep all agree at once.
            await (sharedMediaStateRepository ?? new SqliteMediaStateRepository(databaseConnectionFactory, timeProvider, realtimeEventPublisher))
                .ReevaluateTitleUnderProfileAsync(MediaKind.Series, seriesId, qualityProfileId, cancellationToken);
        }

        return changed;
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
        string? wantedStatus = null,
        CatalogueFilters? filters = null,
        bool requireMonitored = true)
    {
        var items = new List<EpisodeSearchEligibilityItem>();
        var customFilterSql = CatalogueKeyset.CustomFilters(filters, MediaKind.Series, "s", "start_year");
        var customFilter = string.IsNullOrWhiteSpace(customFilterSql) ? string.Empty : $"AND {customFilterSql}";
        // requireMonitored is independent of ignoreRetryWindow: a person's own
        // retry of one failed episode ignores both, but every automatic
        // targeted search (add-triggered, health-replacement, the scheduled
        // cycle) ignores at most the retry window and must never reach an
        // episode whose series is unmonitored (#653).
        var monitoredFilter = requireMonitored ? "AND s.monitored = 1" : string.Empty;

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                e.id, e.series_id, e.season_number, e.episode_number, e.title,
                ews.last_search_utc, ews.next_eligible_search_utc, ews.wanted_status
            FROM episode_wanted_state ews
            INNER JOIN episode_entries e ON e.id = ews.episode_id
            INNER JOIN series_entries s ON s.id = e.series_id
            WHERE ews.library_id = @libraryId
              -- 'missing', not 'wanted'. This read a word nothing has ever
              -- written, so the query matched no row in production and its test
              -- had to seed the value by hand to pass (#300, #303).
              AND ews.wanted_status IN ('missing', 'upgrade')
              AND (@wantedStatus IS NULL OR ews.wanted_status = @wantedStatus)
              AND (@ignoreRetryWindow = 1 OR ews.next_eligible_search_utc IS NULL OR ews.next_eligible_search_utc <= @now)
              AND e.monitored = 1
              {monitoredFilter}
              {customFilter}
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
        CatalogueKeyset.BindCustomFilters(command, filters, MediaKind.Series, now);

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
                NextEligibleSearchUtc: reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
                WantedStatus: reader.GetString(7)));
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
        // The episode's own quality (#591). This read the show's, so in a show
        // with some episodes held every missing one was judged as holding that
        // quality: its release was refused as a downgrade or held for review,
        // and the gaps in a partly-held show were never filled. The show's
        // quality stands in only for an episode that holds a file with no
        // quality recorded against it.
        command.CommandText =
            """
            SELECT COALESCE(
                ews.current_quality,
                CASE WHEN e.has_file = 1 THEN sws.current_quality END)
            FROM episode_wanted_state ews
            INNER JOIN episode_entries e ON e.id = ews.episode_id
            LEFT JOIN series_wanted_state sws ON sws.series_id = ews.series_id AND sws.library_id = ews.library_id
            WHERE ews.episode_id = @episodeId
            LIMIT 1;
            """;

        AddParameter(command, "@episodeId", episodeId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : value.ToString();
    }

    public async Task<IReadOnlyList<string>> ListAllEpisodeFilePathsAsync(CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM episode_entries WHERE has_file = 1 AND file_path IS NOT NULL;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    public async Task<string?> GetEpisodeFilePathAsync(
        string episodeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT file_path FROM episode_entries WHERE id = @episodeId AND has_file = 1 LIMIT 1;";
        AddParameter(command, "@episodeId", episodeId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : value.ToString();
    }

    public async Task<long?> GetEpisodeFileSizeBytesAsync(
        string episodeId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT file_size_bytes FROM episode_entries WHERE id = @episodeId AND has_file = 1 LIMIT 1;";
        AddParameter(command, "@episodeId", episodeId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<(string EpisodeId, string FilePath)>> ListEpisodeFilePathsAsync(
        string seriesId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, file_path FROM episode_entries WHERE series_id = @seriesId AND has_file = 1 AND file_path IS NOT NULL;";
        AddParameter(command, "@seriesId", seriesId);

        var results = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }

    public Task<bool> OtherSeriesHaveFilesUnderAsync(
        string seriesId,
        string directory,
        CancellationToken cancellationToken) =>
        Deluno.Platform.TitleFolderNeighbours.OtherTitleHasFilesUnderAsync(
            databaseConnectionFactory, null, seriesId, directory, cancellationToken);

    public async Task<bool> UpdateEpisodeFileLocationAsync(
        string episodeId,
        string previousFilePath,
        string newFilePath,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE episode_entries SET file_path = @newFilePath WHERE id = @episodeId AND file_path = @previousFilePath;";
        AddParameter(command, "@episodeId", episodeId);
        AddParameter(command, "@previousFilePath", previousFilePath);
        AddParameter(command, "@newFilePath", newFilePath);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task UpdateEpisodeFileLocationsAsync(
        IReadOnlyList<(string EpisodeId, string PreviousFilePath, string NewFilePath)> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
        {
            return;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (episodeId, previousFilePath, newFilePath) in changes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE episode_entries SET file_path = @newFilePath WHERE id = @episodeId AND file_path = @previousFilePath;";
            AddParameter(command, "@episodeId", episodeId);
            AddParameter(command, "@previousFilePath", previousFilePath);
            AddParameter(command, "@newFilePath", newFilePath);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // The show's own row remembers one file's path too (real imports fill it, and the file-gone check reads it). It
            // follows the rename in the same transaction, or the next check finds "its file" gone and unmonitors the show (#1072).
            using var wanted = connection.CreateCommand();
            wanted.Transaction = transaction;
            wanted.CommandText =
                """
                UPDATE series_wanted_state SET file_path = @newFilePath
                WHERE file_path = @previousFilePath COLLATE NOCASE
                  AND series_id = (SELECT series_id FROM episode_entries WHERE id = @episodeId);
                """;
            AddParameter(wanted, "@episodeId", episodeId);
            AddParameter(wanted, "@previousFilePath", previousFilePath);
            AddParameter(wanted, "@newFilePath", newFilePath);
            await wanted.ExecuteNonQueryAsync(cancellationToken);

            // Everything else that remembers this file's path follows in the same transaction (#1323): the episode's
            // subtitle scan, and the show's stored release evidence for the file.
            foreach (var (table, keyColumn, keySql) in new[]
            {
                ("episode_subtitle_scan", "episode_id", "@episodeId"),
                ("media_preference_evaluations", "media_id", "(SELECT series_id FROM episode_entries WHERE id = @episodeId)")
            })
            {
                using var related = connection.CreateCommand();
                related.Transaction = transaction;
                related.CommandText =
                    $"UPDATE {table} SET file_path = @newFilePath WHERE {keyColumn} = {keySql} AND file_path = @previousFilePath COLLATE NOCASE;";
                AddParameter(related, "@episodeId", episodeId);
                AddParameter(related, "@previousFilePath", previousFilePath);
                AddParameter(related, "@newFilePath", newFilePath);
                await related.ExecuteNonQueryAsync(cancellationToken);
            }

            // The subtitle files beside the episode keep their own names and move with its folder.
            var oldDirectory = DirectoryOf(previousFilePath);
            var newDirectory = DirectoryOf(newFilePath);
            if (oldDirectory.Length > 0 && newDirectory.Length > 0 && !string.Equals(oldDirectory, newDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var sidecars = new List<(long RowId, string Path)>();
                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = "SELECT rowid, file_path FROM episode_subtitle_state WHERE episode_id = @episodeId AND file_path IS NOT NULL;";
                    AddParameter(select, "@episodeId", episodeId);
                    await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var path = reader.GetString(1);
                        if (path.Length > oldDirectory.Length
                            && path.StartsWith(oldDirectory, StringComparison.OrdinalIgnoreCase)
                            && (path[oldDirectory.Length] == '/' || path[oldDirectory.Length] == '\\'))
                        {
                            sidecars.Add((reader.GetInt64(0), path));
                        }
                    }
                }

                foreach (var (rowId, path) in sidecars)
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE episode_subtitle_state SET file_path = @filePath WHERE rowid = @rowId;";
                    AddParameter(update, "@filePath", newDirectory + path[oldDirectory.Length..]);
                    AddParameter(update, "@rowId", rowId);
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static string DirectoryOf(string path)
    {
        var cut = path.LastIndexOfAny(['/', '\\']);
        return cut <= 0 ? string.Empty : path[..cut];
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

    private static (string SeriesType, string NumberingScheme, string NumberingSource) ReadNumberingHeader(
        System.Data.Common.DbDataReader reader,
        int firstOrdinal)
        => (
            SeriesTypes.Normalize(reader.IsDBNull(firstOrdinal) ? null : reader.GetString(firstOrdinal)),
            SeriesNumberingSchemes.Normalize(reader.IsDBNull(firstOrdinal + 1) ? null : reader.GetString(firstOrdinal + 1)),
            SeriesNumberingSources.Normalize(reader.IsDBNull(firstOrdinal + 2) ? null : reader.GetString(firstOrdinal + 2)));

    private async Task<(string SeriesType, string NumberingScheme, string NumberingSource)> GetNumberingHeaderAsync(
        string seriesId,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT series_type, numbering_scheme, numbering_source
            FROM series_entries
            WHERE id = @seriesId
            LIMIT 1;
            """;
        AddParameter(command, "@seriesId", seriesId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadNumberingHeader(reader, 0)
            : DefaultNumberingHeader;
    }

    /// <summary>
    /// The series' own "monitor new episodes" preset (#801), for display and
    /// editing - the read half of <see cref="ShouldMonitorNewEpisodesAsync"/>,
    /// opening its own connection rather than sharing a sync's transaction.
    /// </summary>
    private async Task<bool> GetMonitorNewEpisodesAsync(string seriesId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT monitor_new_episodes FROM series_entries WHERE id = @seriesId LIMIT 1;";
        AddParameter(command, "@seriesId", seriesId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not long value || value == 1;
    }

    /// <summary>
    /// Changes the series' "monitor new episodes" preset (#801) at any time,
    /// not only at series add time - the series screen/drawer's own control.
    /// </summary>
    public async Task<bool> UpdateMonitorNewEpisodesAsync(
        string seriesId,
        bool monitorNewEpisodes,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE series_entries SET monitor_new_episodes = @value WHERE id = @seriesId;";
        AddParameter(command, "@value", monitorNewEpisodes ? 1 : 0);
        AddParameter(command, "@seriesId", seriesId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Every series' seasons, with episode counts and monitor state, for the
    /// cross-series Season Pass screen (#791).
    /// </summary>
    public async Task<IReadOnlyList<SeriesSeasonPassItem>> ListSeasonPassAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.title, e.season_number, COUNT(*) AS episode_count, SUM(e.monitored) AS monitored_count
            FROM series_entries s
            JOIN episode_entries e ON e.series_id = s.id
            GROUP BY s.id, s.title, e.season_number
            ORDER BY s.title COLLATE NOCASE, e.season_number;
            """;

        var results = new List<SeriesSeasonPassItem>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SeriesSeasonPassItem(
                SeriesId: reader.GetString(0),
                SeriesTitle: reader.GetString(1),
                SeasonNumber: (int)reader.GetInt64(2),
                EpisodeCount: (int)reader.GetInt64(3),
                MonitoredCount: (int)reader.GetInt64(4)));
        }

        return results;
    }

    /// <summary>
    /// Bulk-toggles episode monitoring at the season or series level (#791).
    /// One statement per scope rather than one per episode - a series-wide
    /// scope can cover hundreds of episodes, and the scope list itself is
    /// bounded by what a person selected on screen, not by library size.
    /// </summary>
    public async Task<int> SetSeasonPassMonitoringAsync(
        IReadOnlyList<SeasonPassScope> scopes,
        bool monitored,
        CancellationToken cancellationToken)
    {
        if (scopes.Count == 0)
        {
            return 0;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var now = timeProvider.GetUtcNow().ToString("O");
        var updated = 0;

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var scope in scopes)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = scope.SeasonNumber is null
                ? "UPDATE episode_entries SET monitored = @monitored, updated_utc = @now WHERE series_id = @seriesId;"
                : "UPDATE episode_entries SET monitored = @monitored, updated_utc = @now WHERE series_id = @seriesId AND season_number = @seasonNumber;";
            AddParameter(command, "@monitored", monitored ? 1 : 0);
            AddParameter(command, "@now", now);
            AddParameter(command, "@seriesId", scope.SeriesId);
            if (scope.SeasonNumber is not null)
            {
                AddParameter(command, "@seasonNumber", scope.SeasonNumber.Value);
            }

            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await RefreshSeriesRungsAsync(
            connection,
            transaction,
            scopes.Select(scope => scope.SeriesId),
            timeProvider.GetUtcNow(),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static DateOnly? ParseAirDateKey(string? value)
        => DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;

    private static void ValidateMapping(SeriesNumberingMapping mapping)
    {
        if (string.IsNullOrWhiteSpace(mapping.EpisodeId))
        {
            throw new ArgumentException("A numbering mapping must identify an episode.", nameof(mapping));
        }

        if (mapping.AbsoluteNumber is <= 0 or > 9999)
        {
            throw new ArgumentException("Absolute episode numbers must be between 1 and 9999.", nameof(mapping));
        }

        if ((mapping.SceneSeasonNumber is null) != (mapping.SceneEpisodeNumber is null))
        {
            throw new ArgumentException("Scene season and episode numbers must be supplied together.", nameof(mapping));
        }

        if (mapping.SceneSeasonNumber is < 0 or > 999 || mapping.SceneEpisodeNumber is <= 0 or > 9999)
        {
            throw new ArgumentException("Scene season and episode numbers are outside the supported range.", nameof(mapping));
        }
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
            UpdatedUtc: ParseTimestamp(reader.GetString(17)),
            AvailableUtc: ReadOptionalAvailableUtc(reader),
            FilePath: reader.FieldCount > 19 ? (reader.IsDBNull(19) ? null : reader.GetString(19)) : null,
            FileSizeBytes: reader.FieldCount > 20 && !reader.IsDBNull(20) ? reader.GetInt64(20) : null,
            RuntimeMinutes: reader.FieldCount > 21 && !reader.IsDBNull(21) ? reader.GetInt32(21) : null);
    }

    private static DateTimeOffset? ReadOptionalAvailableUtc(System.Data.Common.DbDataReader reader)
        => reader.FieldCount <= 18 || reader.IsDBNull(18)
            ? null
            : DateTimeOffset.TryParse(reader.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
                ? value
                : null;

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

        // A show added or later marked unmonitored is a person saying "not
        // this one" — a fresh episode the catalogue learns about afterwards
        // is not wanted either, the same as Sonarr leaves it. Read once per
        // sync rather than once per episode; the series' own flag does not
        // change mid-transaction.
        var seriesMonitored = await IsSeriesMonitoredAsync(connection, transaction, seriesId, cancellationToken);
        // #801: a series can ask that episodes it discovers on a refresh come
        // out unmonitored, independently of the series itself being monitored.
        // Read once per sync for the same reason seriesMonitored is.
        var monitorNewEpisodes = await ShouldMonitorNewEpisodesAsync(connection, transaction, seriesId, cancellationToken);
        // #848: a series added from a list can ask for less than "every
        // episode" up front. Only ever narrows the two checks above — it
        // never turns a switched-off series or preset back on.
        var listMonitorMode = await GetListMonitorModeAsync(connection, transaction, seriesId, cancellationToken);
        var mostRecentSeason = episodes.Max(item => item.SeasonNumber);
        // #991: the season this sync knows about with the lowest number that
        // is not Specials (season 0) — falling back to season 0 itself when
        // that is genuinely all a show has.
        var firstSeasonNumber = episodes
            .Select(item => item.SeasonNumber)
            .Where(season => season > 0)
            .DefaultIfEmpty(episodes.Min(item => item.SeasonNumber))
            .Min();

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
                        created_utc, updated_utc, absolute_number, scene_season_number,
                        scene_episode_number, airdate_key, numbering_source
                    )
                    VALUES (
                        @id, @seriesId, @seasonId, @seasonNumber, @episodeNumber, @title, @overview, @airDateUtc,
                        @monitored, 0, 0, @source, @syncedUtc,
                        @createdUtc, @updatedUtc, @absoluteNumber, @sceneSeasonNumber,
                        @sceneEpisodeNumber, @airDateKey, @numberingSource
                    )
                    ON CONFLICT(series_id, season_number, episode_number) DO UPDATE SET
                        season_id = COALESCE(episode_entries.season_id, excluded.season_id),
                        title = excluded.title,
                        overview = excluded.overview,
                        air_date_utc = excluded.air_date_utc,
                        -- A refresh that has no absolute number (TMDb's list has none) must
                        -- not blank one an earlier TheTVDB or scene-map refresh learned.
                        absolute_number = CASE WHEN episode_entries.numbering_source = 'owner'
                            THEN episode_entries.absolute_number
                            ELSE COALESCE(excluded.absolute_number, episode_entries.absolute_number) END,
                        scene_season_number = CASE WHEN episode_entries.numbering_source = 'owner'
                            THEN episode_entries.scene_season_number ELSE excluded.scene_season_number END,
                        scene_episode_number = CASE WHEN episode_entries.numbering_source = 'owner'
                            THEN episode_entries.scene_episode_number ELSE excluded.scene_episode_number END,
                        airdate_key = CASE WHEN episode_entries.numbering_source = 'owner'
                            THEN episode_entries.airdate_key ELSE excluded.airdate_key END,
                        numbering_source = CASE WHEN episode_entries.numbering_source = 'owner'
                            THEN episode_entries.numbering_source ELSE excluded.numbering_source END,
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
                // arrive with nine "missing" specials to chase (#243). An
                // unmonitored series is the same call, one level up: its
                // episodes are never wanted until the series itself is
                // monitored again (#653). Only the default differs — the
                // per-episode switch still governs from here on.
                var listModeAllows = ListMonitorModeAllows(listMonitorMode, episode, mostRecentSeason, firstSeasonNumber, now);
                AddParameter(upsert, "@monitored", episode.SeasonNumber == 0 || !seriesMonitored || !monitorNewEpisodes || !listModeAllows ? 0 : 1);
                AddParameter(upsert, "@title", NormalizeText(episode.Title));
                AddParameter(upsert, "@overview", NormalizeText(episode.Overview));
                AddParameter(upsert, "@airDateUtc", episode.AirDateUtc?.ToString("O"));
                AddParameter(upsert, "@source", source);
                AddParameter(upsert, "@syncedUtc", stamp);
                AddParameter(upsert, "@createdUtc", stamp);
                AddParameter(upsert, "@updatedUtc", stamp);
                AddParameter(upsert, "@absoluteNumber", episode.AbsoluteNumber);
                AddParameter(upsert, "@sceneSeasonNumber", episode.SceneSeasonNumber);
                AddParameter(upsert, "@sceneEpisodeNumber", episode.SceneEpisodeNumber);
                AddParameter(upsert, "@airDateKey", episode.AirDateUtc?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                AddParameter(upsert, "@numberingSource", SeriesNumberingSources.Normalize(episode.NumberingSource));

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

        // The episodes and their air dates just changed, so every library's rung
        // for this show did too. Found on the physical rig: a show imported with
        // one episode on disk and no air dates stored "Deluno has not catalogued
        // this show's episodes yet", and kept saying it after metadata added all
        // of them, because only the import path recomputed the rung.
        var libraryIds = new List<string>();
        using (var libraries = connection.CreateCommand())
        {
            libraries.Transaction = transaction;
            libraries.CommandText = "SELECT DISTINCT library_id FROM series_wanted_state WHERE series_id = @seriesId;";
            AddParameter(libraries, "@seriesId", seriesId);
            using var reader = await libraries.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                libraryIds.Add(reader.GetString(0));
            }
        }

        foreach (var libraryId in libraryIds)
        {
            await RefreshSeriesRungAsync(connection, transaction, seriesId, libraryId, now, cancellationToken);
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
        bool includeUnmonitored,
        IReadOnlyList<string>? tagNames,
        CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        // A show's own tags — not the episode's — are what a reader means by
        // "this show's tag", so the tag clause keys on the series id.
        var monitoredClause = includeUnmonitored ? "1 = 1" : "e.monitored = 1";
        var tagClause = MediaTagFilter.BuildClause("e.series_id", tagNames, command);
        command.CommandText =
            $"""
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
              AND {monitoredClause}
              AND {tagClause}
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

    public async Task<(IReadOnlyList<WantedEpisodeItem> Items, int Total)> ListWantedEpisodesPageAsync(
        string wantedStatus,
        bool? monitored,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        // Missing is "no file and not otherwise classified"; the cutoff list is
        // the episodes whose file the profile wants replaced.
        var statusClause = wantedStatus == "upgrade"
            ? "w.wanted_status = 'upgrade'"
            : "e.has_file = 0 AND COALESCE(w.wanted_status, 'missing') = 'missing'";
        var monitoredClause = monitored is null ? string.Empty : "AND e.monitored = @monitored";

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var total = 0;
        using (var count = connection.CreateCommand())
        {
            count.CommandText =
                $"""
                SELECT COUNT(*)
                FROM episode_entries e
                INNER JOIN series_entries s ON s.id = e.series_id
                LEFT JOIN episode_wanted_state w ON w.episode_id = e.id
                WHERE {statusClause} {monitoredClause};
                """;
            if (monitored is not null)
            {
                AddParameter(count, "@monitored", monitored.Value ? 1 : 0);
            }

            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT
                e.id, e.series_id, s.title, e.season_number, e.episode_number, e.title,
                e.air_date_utc, e.monitored,
                COALESCE(w.wanted_status, 'missing'),
                COALESCE(w.wanted_reason, 'Episode is missing from the library.'),
                w.last_search_utc, w.next_eligible_search_utc
            FROM episode_entries e
            INNER JOIN series_entries s ON s.id = e.series_id
            LEFT JOIN episode_wanted_state w ON w.episode_id = e.id
            WHERE {statusClause} {monitoredClause}
            ORDER BY
                CASE WHEN e.air_date_utc IS NULL THEN 1 ELSE 0 END,
                e.air_date_utc DESC,
                s.title ASC,
                e.season_number ASC,
                e.episode_number ASC,
                e.id ASC
            LIMIT @take OFFSET @skip;
            """;
        if (monitored is not null)
        {
            AddParameter(command, "@monitored", monitored.Value ? 1 : 0);
        }

        AddParameter(command, "@take", take);
        AddParameter(command, "@skip", skip);

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

        return (items, total);
    }

    public async Task<MediaDashboardCounts> GetDashboardCountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenReadOnlyConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        var facets = await CountCatalogueFacetsAsync(connection, null, null, "all", null, null, cancellationToken);

        var byLibrary = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT w.library_id, COALESCE(SUM(e.file_size_bytes), 0)
            FROM episode_entries e
            INNER JOIN episode_wanted_state w ON w.episode_id = e.id
            WHERE e.has_file = 1
            GROUP BY w.library_id;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            byLibrary[reader.GetString(0)] = reader.GetInt64(1);
        }

        return new MediaDashboardCounts(facets, byLibrary.Values.Sum(), byLibrary, await FacetsByLibraryAsync(connection, cancellationToken));
    }

    /// <summary>The library page's own facet counts for each library that holds a title, so the Live shelf's chips match the page they link to (#1103).</summary>
    private async Task<IReadOnlyDictionary<string, CatalogueFacets>> FacetsByLibraryAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT library_id FROM series_wanted_state;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var result = new Dictionary<string, CatalogueFacets>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            result[id] = await CountCatalogueFacetsAsync(connection, null, id, "all", null, null, cancellationToken);
        }

        return result;
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
            DailyCounts.ImportFailuresByDay("series_import_recovery_cases"),
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

    /// <summary>
    /// Whether the series itself is monitored right now — a missing series
    /// (should never happen mid-sync, but a foreign key is not a guarantee)
    /// defaults to unmonitored, the conservative reading.
    /// </summary>
    private static async Task<bool> IsSeriesMonitoredAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT monitored FROM series_entries WHERE id = @seriesId LIMIT 1;";
        AddParameter(command, "@seriesId", seriesId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value && value == 1;
    }

    /// <summary>
    /// The series' own "monitor new episodes" preset (#801) - whether an
    /// episode this refresh discovers for the first time should come out
    /// monitored. A missing series defaults to on, the same conservative
    /// direction as the column's own migration default: before this preset
    /// existed, a newly discovered episode was always monitored.
    /// </summary>
    private static async Task<bool> ShouldMonitorNewEpisodesAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT monitor_new_episodes FROM series_entries WHERE id = @seriesId LIMIT 1;";
        AddParameter(command, "@seriesId", seriesId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not long value || value == 1;
    }

    /// <summary>
    /// The list monitor mode (#848) this series was added under, or null if
    /// it was not added from a list (or the list chose "All"). Read once per
    /// sync, the same as <see cref="ShouldMonitorNewEpisodesAsync"/>.
    /// </summary>
    private static async Task<string?> GetListMonitorModeAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string seriesId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT list_monitor_mode FROM series_entries WHERE id = @seriesId LIMIT 1;";
        AddParameter(command, "@seriesId", seriesId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    /// <summary>
    /// Whether a newly discovered episode passes the list's chosen monitor
    /// mode (#848). Null (not added from a list, or the list chose "All")
    /// and <see cref="Deluno.Intake.Contracts.IntakeMonitorModes.All"/> both
    /// always pass — today's behaviour. Every episode this show discovers is
    /// freshly inserted with no file yet, so "Missing" and "All" agree here;
    /// "Existing" is the mirror of that and never passes for a brand new
    /// episode, by design — it is for a list added over a title Deluno
    /// already holds files for.
    /// </summary>
    private static bool ListMonitorModeAllows(
        string? listMonitorMode,
        CatalogueEpisodeItem episode,
        int mostRecentSeason,
        int firstSeasonNumber,
        DateTimeOffset now)
        => listMonitorMode switch
        {
            null or IntakeMonitorModes.All or IntakeMonitorModes.Missing => true,
            IntakeMonitorModes.Future => episode.AirDateUtc is null || episode.AirDateUtc > now,
            IntakeMonitorModes.Existing => false,
            IntakeMonitorModes.Recent => episode.SeasonNumber == mostRecentSeason,
            // #991: "First season" (Sonarr's firstSeason preset) - every
            // episode of the lowest-numbered non-Specials season.
            IntakeMonitorModes.FirstSeason => episode.SeasonNumber == firstSeasonNumber,
            // #991: "Pilot" (Sonarr's pilot preset) - only the very first
            // episode of that first season.
            IntakeMonitorModes.Pilot => episode.SeasonNumber == firstSeasonNumber && episode.EpisodeNumber == 1,
            // #991: "None" (Sonarr's none preset) - the series is added, but
            // nothing is chased for it until a person monitors something.
            IntakeMonitorModes.None => false,
            _ => true
        };

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
            // A download the client never showed, or showed and never
            // finished, did not reach an import. These fell through to
            // "importFailed" and were counted and labelled as failed imports.
            "grab-timeout" => "downloadFailed",
            "detection-timeout" => "downloadFailed",
            "importfailed" => "importFailed",
            "import failed" => "importFailed",
            // Kept as its own kind rather than folded into "downloadFailed"
            // (#927): Try again always searches for another copy here, never
            // the hand-off-again branch "downloadFailed" takes once a
            // download has been detected - a vanished download has nothing
            // left with a processor to hand off.
            "vanished-downloading" => "vanished-downloading",
            // Processor-fault and processor-hold kinds (#511, #914, #919,
            // #921, #929, #930): each is a distinct case with its own
            // recommended action, and without a case here every one of them
            // silently became "importFailed" -- storing the same case under a
            // different name than the one every caller, test and screen reads
            // it back by.
            "processedfileisless" => "processedFileIsLess",
            "processor-output-missing" => "processor-output-missing",
            "processor-unmatched-output" => "processor-unmatched-output",
            "processor-lost" => "processor-lost",
            "processor-timeout" => "processor-timeout",
            "processor-stalled" => "processor-stalled",
            "processor-failed" => "processor-failed",
            "processor-held" => "processor-held",
            "processor-refuses-every-copy" => "processor-refuses-every-copy",
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

    public async Task<IReadOnlyList<(string EpisodeId, string SeriesId, int SeasonNumber, int EpisodeNumber)>> ListEpisodeNumbersAsync(
        IReadOnlyList<string> episodeIds,
        CancellationToken cancellationToken)
    {
        var ids = episodeIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
        var rows = new List<(string, string, int, int)>();
        if (ids.Length == 0)
        {
            return rows;
        }

        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        var names = new List<string>(ids.Length);
        for (var index = 0; index < ids.Length; index++)
        {
            names.Add($"@id{index}");
            AddParameter(command, $"@id{index}", ids[index]);
        }

        command.CommandText = $"SELECT id, series_id, season_number, episode_number FROM episode_entries WHERE id IN ({string.Join(',', names)});";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        return rows;
    }

    public async Task<string?> GetEpisodeSeriesIdAsync(string episodeId, CancellationToken cancellationToken)
    {
        await using var connection = await databaseConnectionFactory.OpenConnectionAsync(
            DelunoDatabaseNames.Series,
            cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT series_id FROM episode_entries WHERE id = @episodeId LIMIT 1;";
        AddParameter(command, "@episodeId", episodeId);

        return await command.ExecuteScalarAsync(cancellationToken) as string;
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
            UpdatedUtc: item.UpdatedUtc,
            AvailableUtc: item.AvailableUtc,
            FilePath: item.FilePath,
            FileSizeBytes: item.FileSizeBytes,
            RuntimeMinutes: item.RuntimeMinutes);

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
            CreatedUtc: item.CreatedUtc,
            IndexerId: item.IndexerId);

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
