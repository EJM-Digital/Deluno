using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;
using static Deluno.Infrastructure.Storage.SqliteRecordHelpers;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// The two numbers under a poster — asked for, and held — arriving from the one
/// store both catalogues share.
///
/// The point of most of these is that the answer must be the same shape for a
/// movie and for a show while being a different sum: a movie is judged over its
/// one file, a show over the episodes it holds. DESIGN-001 settled that
/// vocabulary and DESIGN-002 settled that the state behind it is written once.
/// </summary>
public sealed class SubtitleBarPersistenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T00:00:00Z");

    [Fact]
    public async Task A_shelf_that_wants_nothing_gets_no_bar_and_costs_no_query()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage, new StubPreferences());

        await ImportMovieAsync(movies, "Arrival", 2016, @"D:\Media\Arrival (2016)\Arrival (2016).mkv");

        var item = Assert.Single((await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        // Zero and zero is what every title reads until somebody turns this on,
        // and it is what makes the bar draw nothing rather than draw red.
        Assert.Equal(0, item.SubtitleLanguagesWanted);
        Assert.Equal(0, item.SubtitleLanguagesHeld);
    }

    [Fact]
    public async Task A_movie_holding_a_wanted_language_reads_one_of_two_rather_than_none()
    {
        using var storage = TestStorage.Create();
        var preferences = new StubPreferences(("library-movies", ["en", "ja"], SubtitleLanguageModes.All));
        var movies = await CreateMoviesAsync(storage, preferences);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        await ImportMovieAsync(movies, "Arrival", 2016, @"D:\Media\Arrival (2016)\Arrival (2016).mkv");
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items[0].Id;

        await RecordAsync(subtitles, MediaKind.Movie, id, [
            Embedded("en"),
            // Not asked for. It is a fact about the file and it is stored, but
            // it cannot fill a bar that nobody asked it to fill.
            Embedded("de")
        ]);

        var item = Assert.Single((await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        Assert.Equal(2, item.SubtitleLanguagesWanted);
        Assert.Equal(1, item.SubtitleLanguagesHeld);
    }

    [Fact]
    public async Task A_forced_track_is_not_coverage()
    {
        using var storage = TestStorage.Create();
        var preferences = new StubPreferences(("library-movies", ["en"], SubtitleLanguageModes.All));
        var movies = await CreateMoviesAsync(storage, preferences);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        await ImportMovieAsync(movies, "Dune", 2021, @"D:\Media\Dune (2021)\Dune (2021).mkv");
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items[0].Id;

        await RecordAsync(subtitles, MediaKind.Movie, id, [Embedded("en", forced: true)]);

        // Four lines of Fremen is not an English subtitle track, and calling it
        // one would tell somebody they were done and stop Deluno looking.
        var item = Assert.Single((await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        Assert.Equal(1, item.SubtitleLanguagesWanted);
        Assert.Equal(0, item.SubtitleLanguagesHeld);
    }

    [Fact]
    public async Task A_hearing_impaired_track_is_coverage_and_is_not_counted_twice_beside_a_plain_one()
    {
        using var storage = TestStorage.Create();
        var preferences = new StubPreferences(("library-movies", ["en"], SubtitleLanguageModes.All));
        var movies = await CreateMoviesAsync(storage, preferences);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        await ImportMovieAsync(movies, "Dune", 2021, @"D:\Media\Dune (2021)\Dune (2021).mkv");
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items[0].Id;

        await RecordAsync(subtitles, MediaKind.Movie, id, [
            Embedded("en"),
            Embedded("en", hearingImpaired: true)
        ]);

        var item = Assert.Single((await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        Assert.Equal(1, item.SubtitleLanguagesWanted);
        Assert.Equal(1, item.SubtitleLanguagesHeld);
    }

    [Fact]
    public async Task First_mode_asks_for_one_language_however_many_are_listed()
    {
        using var storage = TestStorage.Create();
        var preferences = new StubPreferences(("library-movies", ["en", "es", "fr"], SubtitleLanguageModes.First));
        var movies = await CreateMoviesAsync(storage, preferences);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        await ImportMovieAsync(movies, "Roma", 2018, @"D:\Media\Roma (2018)\Roma (2018).mkv");
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items[0].Id;

        await RecordAsync(subtitles, MediaKind.Movie, id, [Embedded("es"), Embedded("fr")]);

        // "English, or Spanish if English cannot be found; do not fetch both."
        // Two of them held is still one asked for and one satisfied.
        var item = Assert.Single((await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        Assert.Equal(1, item.SubtitleLanguagesWanted);
        Assert.Equal(1, item.SubtitleLanguagesHeld);
    }

    [Fact]
    public async Task A_show_sums_over_the_episodes_it_holds_and_never_past_them()
    {
        using var storage = TestStorage.Create();
        var preferences = new StubPreferences(("library-tv", ["en", "ja"], SubtitleLanguageModes.All));
        var timeProvider = new FixedTimeProvider(Now);
        var series = await CreateSeriesAsync(storage, timeProvider, preferences);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        var show = await series.AddAsync(new CreateSeriesRequest("Shogun", 2024, "tt2788316"), CancellationToken.None);
        await AttachLibraryAsync(storage, show.Id, "library-tv");

        var aired = new List<string>();
        for (var episode = 1; episode <= 3; episode++)
        {
            aired.Add(await InsertEpisodeAsync(storage, show.Id, episode, Now.AddDays(-10), hasFile: episode <= 2));
        }

        // An episode that has not aired, with a file, must not enlarge the bar:
        // the divisor above it is the aired episodes the show holds.
        var future = await InsertEpisodeAsync(storage, show.Id, 4, Now.AddDays(10), hasFile: true);

        await RecordAsync(subtitles, MediaKind.Series, aired[0], [Embedded("en"), Embedded("ja")]);
        await RecordAsync(subtitles, MediaKind.Series, aired[1], [Embedded("en")]);
        // Held by an episode with no file at all — nothing to subtitle.
        await RecordAsync(subtitles, MediaKind.Series, aired[2], [Embedded("en")]);
        await RecordAsync(subtitles, MediaKind.Series, future, [Embedded("en"), Embedded("ja")]);

        var item = Assert.Single((await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        // Two aired episodes with files, two languages each: four asked for,
        // three held.
        Assert.Equal(2, item.AiredWithFileCount);
        Assert.Equal(2, item.SubtitleLanguagesWanted);
        Assert.Equal(3, item.SubtitleLanguagesHeld);
    }

    [Fact]
    public async Task A_rescan_replaces_what_the_file_has_and_keeps_what_Deluno_fetched()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage, new StubPreferences());
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        await ImportMovieAsync(movies, "Arrival", 2016, @"D:\Media\Arrival (2016)\Arrival (2016).mkv");
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items[0].Id;

        await subtitles.RecordScanAsync(
            MediaKind.Movie,
            id,
            new MediaSubtitleScan(@"D:\Media\Arrival (2016)\Arrival (2016).mkv", 100, "succeeded", 2, Now),
            [
                new MediaSubtitleRow("en", SubtitleSources.Fetched, false, false, @"D:\Media\Arrival (2016)\Arrival (2016).en.srt", null, "srt", "opensubtitles"),
                new MediaSubtitleRow("de", SubtitleSources.Embedded, false, false, null, 3, "subrip", null)
            ],
            CancellationToken.None);

        // A later folder scan finds Deluno's own subtitle as an ordinary file
        // beside the video, and the German track has gone with a replaced file.
        await subtitles.RecordScanAsync(
            MediaKind.Movie,
            id,
            new MediaSubtitleScan(@"D:\Media\Arrival (2016)\Arrival (2016).mkv", 200, "succeeded", 1, Now.AddDays(1)),
            [new MediaSubtitleRow("en", SubtitleSources.External, false, false, @"D:\Media\Arrival (2016)\Arrival (2016).en.srt", null, "srt", null)],
            CancellationToken.None);

        var rows = await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None);
        var english = Assert.Single(rows);
        Assert.Equal("en", english.Language);
        // Where it came from is what a blacklist and an upgrade will need, and
        // a rescan must not turn Deluno's own work into an anonymous file.
        Assert.Equal("opensubtitles", english.Provider);
    }

    [Fact]
    public async Task Only_files_that_are_new_or_changed_are_offered_for_reading()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage, new StubPreferences());
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        await ImportMovieAsync(movies, "Arrival", 2016, @"D:\Media\Arrival (2016)\Arrival (2016).mkv", sizeBytes: 100);
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items[0].Id;

        Assert.Single(await subtitles.ListPendingScansAsync(MediaKind.Movie, "library-movies", 50, CancellationToken.None));

        await subtitles.RecordScanAsync(
            MediaKind.Movie,
            id,
            new MediaSubtitleScan(@"D:\Media\Arrival (2016)\Arrival (2016).mkv", 100, "succeeded", 0, Now),
            [],
            CancellationToken.None);

        // Read once, and not read again — this is the difference between a
        // background pass nobody notices and one that re-probes the library
        // every cycle.
        Assert.Empty(await subtitles.ListPendingScansAsync(MediaKind.Movie, "library-movies", 50, CancellationToken.None));

        // An upgrade keeps the name and changes everything else about the file,
        // subtitle tracks included.
        await ImportMovieAsync(movies, "Arrival", 2016, @"D:\Media\Arrival (2016)\Arrival (2016).mkv", sizeBytes: 900);
        Assert.Single(await subtitles.ListPendingScansAsync(MediaKind.Movie, "library-movies", 50, CancellationToken.None));
    }

    [Fact]
    public async Task A_file_read_without_ffprobe_is_read_again_once_ffprobe_is_there()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage, new StubPreferences());
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        await ImportMovieAsync(movies, "Arrival", 2016, @"D:\Media\Arrival (2016)\Arrival (2016).mkv");
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items[0].Id;

        // Only the subtitles beside it could be seen, so the tracks inside it
        // are still unknown. An install gains ffprobe at some point — the lab
        // rig did — and everything it half read has to be finished.
        await subtitles.RecordScanAsync(
            MediaKind.Movie,
            id,
            new MediaSubtitleScan(@"D:\Media\Arrival (2016)\Arrival (2016).mkv", 1024, "unavailable", 0, Now),
            [],
            CancellationToken.None);

        Assert.Single(await subtitles.ListPendingScansAsync(MediaKind.Movie, "library-movies", 50, CancellationToken.None));

        await subtitles.RecordScanAsync(
            MediaKind.Movie,
            id,
            new MediaSubtitleScan(@"D:\Media\Arrival (2016)\Arrival (2016).mkv", 1024, "succeeded", 1, Now),
            [Embedded("en")],
            CancellationToken.None);

        Assert.Empty(await subtitles.ListPendingScansAsync(MediaKind.Movie, "library-movies", 50, CancellationToken.None));
    }

    [Fact]
    public async Task A_file_ffprobe_could_not_parse_is_not_read_again_every_cycle()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage, new StubPreferences());
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory);

        await ImportMovieAsync(movies, "Arrival", 2016, @"D:\Media\Arrival (2016)\Arrival (2016).mkv");
        var id = (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items[0].Id;

        await subtitles.RecordScanAsync(
            MediaKind.Movie,
            id,
            new MediaSubtitleScan(@"D:\Media\Arrival (2016)\Arrival (2016).mkv", 1024, "failed", 0, Now),
            [],
            CancellationToken.None);

        // A missing binary is an environment state that changes. A file ffprobe
        // cannot parse is a fact about the file, and retrying it every cycle
        // would read a corrupt file forever.
        Assert.Empty(await subtitles.ListPendingScansAsync(MediaKind.Movie, "library-movies", 50, CancellationToken.None));
    }

    /* ------------------------------------------------------------ helpers */

    private static MediaSubtitleRow Embedded(string language, bool forced = false, bool hearingImpaired = false)
        => new(language, SubtitleSources.Embedded, forced, hearingImpaired, null, 2, "subrip", null);

    private static Task RecordAsync(
        IMediaSubtitleRepository subtitles,
        MediaKind kind,
        string mediaId,
        IReadOnlyList<MediaSubtitleRow> rows)
        => subtitles.RecordScanAsync(
            kind,
            mediaId,
            new MediaSubtitleScan($"D:\\Media\\{mediaId}.mkv", 1, "succeeded", rows.Count, Now),
            rows,
            CancellationToken.None);

    private static async Task<SqliteMovieCatalogRepository> CreateMoviesAsync(
        TestStorage storage,
        ILibrarySubtitlePreferences preferences)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, timeProvider, null, preferences);
    }

    private static async Task<SqliteSeriesCatalogRepository> CreateSeriesAsync(
        TestStorage storage,
        TimeProvider timeProvider,
        ILibrarySubtitlePreferences preferences)
    {
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, null, preferences);
    }

    private static Task ImportMovieAsync(
        IMovieCatalogRepository movies,
        string title,
        int year,
        string filePath,
        long sizeBytes = 1024)
        => movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: title,
                    ReleaseYear: year,
                    WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "Bluray-1080p",
                    TargetQuality: "Bluray-1080p",
                    QualityCutoffMet: true,
                    UnmonitorWhenCutoffMet: false,
                    FilePath: filePath,
                    FileSizeBytes: sizeBytes)
            ],
            CancellationToken.None);

    private static async Task AttachLibraryAsync(TestStorage storage, string seriesId, string libraryId)
    {
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO series_wanted_state (series_id, library_id, wanted_status, wanted_reason, has_file, updated_utc)
            VALUES (@seriesId, @libraryId, 'missing', 'Seeded by a test.', 0, @updatedUtc);
            """;
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@libraryId", libraryId);
        AddParameter(command, "@updatedUtc", Now.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<string> InsertEpisodeAsync(
        TestStorage storage,
        string seriesId,
        int episodeNumber,
        DateTimeOffset airDateUtc,
        bool hasFile)
    {
        var id = Guid.CreateVersion7().ToString("N");
        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Series, CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO episode_entries (
                id, series_id, season_id, season_number, episode_number, title, air_date_utc,
                monitored, has_file, quality_cutoff_met, file_path, file_size_bytes, created_utc, updated_utc
            ) VALUES (
                @id, @seriesId, NULL, 1, @episodeNumber, @title, @airDateUtc,
                1, @hasFile, 1, @filePath, 1024, @createdUtc, @updatedUtc
            );
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@seriesId", seriesId);
        AddParameter(command, "@episodeNumber", episodeNumber);
        AddParameter(command, "@title", $"Episode {episodeNumber}");
        AddParameter(command, "@airDateUtc", airDateUtc.ToString("O"));
        AddParameter(command, "@hasFile", hasFile ? 1 : 0);
        AddParameter(command, "@filePath", hasFile ? $@"D:\TV\Shogun\S01E{episodeNumber:00}.mkv" : null);
        AddParameter(command, "@createdUtc", Now.ToString("O"));
        AddParameter(command, "@updatedUtc", Now.ToString("O"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return id;
    }

    private sealed class StubPreferences(params (string LibraryId, string[] Languages, string Mode)[] libraries)
        : ILibrarySubtitlePreferences
    {
        public Task<IReadOnlyDictionary<string, LibrarySubtitlePreference>> GetSubtitlePreferencesAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, LibrarySubtitlePreference>>(
                libraries.ToDictionary(
                    library => library.LibraryId,
                    library => new LibrarySubtitlePreference(library.LibraryId, library.Languages, library.Mode),
                    StringComparer.OrdinalIgnoreCase));
    }
}
