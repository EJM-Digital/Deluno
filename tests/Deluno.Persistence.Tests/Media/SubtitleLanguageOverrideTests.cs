using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// #843: a title's own subtitle-language override, stored and resolved
/// independently of its library's setting — for both catalogues, since a
/// movie's override sits on the movie itself and a show's sits on the show,
/// never on one episode.
/// </summary>
public sealed class SubtitleLanguageOverrideTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    [Fact]
    public async Task A_movie_with_no_override_resolves_to_nothing()
    {
        using var storage = TestStorage.Create();
        var (movies, overrides) = await CreateMoviesAsync(storage);
        var id = await ImportMovieAsync(movies, "Dune");

        Assert.Null(await overrides.GetAsync(MediaKind.Movie, id, CancellationToken.None));
    }

    [Fact]
    public async Task Setting_a_movies_override_replaces_what_the_library_would_use()
    {
        using var storage = TestStorage.Create();
        var (movies, overrides) = await CreateMoviesAsync(storage);
        var id = await ImportMovieAsync(movies, "Dune");

        await overrides.SetAsync(MediaKind.Movie, id, new SubtitleLanguageOverride(["ja", "en"], "first"), CancellationToken.None);

        var stored = await overrides.GetAsync(MediaKind.Movie, id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(["ja", "en"], stored!.Languages);
        Assert.Equal("first", stored.Mode);

        var (languages, mode) = SubtitleLanguagePreference.Resolve(["en"], "all", stored);
        Assert.Equal(["ja", "en"], languages);
        Assert.Equal("first", mode);
    }

    [Fact]
    public async Task Clearing_a_movies_override_goes_back_to_the_library_default()
    {
        using var storage = TestStorage.Create();
        var (movies, overrides) = await CreateMoviesAsync(storage);
        var id = await ImportMovieAsync(movies, "Dune");

        await overrides.SetAsync(MediaKind.Movie, id, new SubtitleLanguageOverride(["ja"], "all"), CancellationToken.None);
        await overrides.ClearAsync(MediaKind.Movie, id, CancellationToken.None);

        Assert.Null(await overrides.GetAsync(MediaKind.Movie, id, CancellationToken.None));

        var (languages, mode) = SubtitleLanguagePreference.Resolve(["en"], "all", null);
        Assert.Equal(["en"], languages);
        Assert.Equal("all", mode);
    }

    [Fact]
    public async Task A_shows_override_sits_on_the_show_not_one_episode()
    {
        using var storage = TestStorage.Create();
        var (series, overrides) = await CreateSeriesAsync(storage);
        var id = await ImportSeriesAsync(series, "Severance");

        await overrides.SetAsync(MediaKind.Series, id, new SubtitleLanguageOverride(["ko"], "all"), CancellationToken.None);

        var stored = await overrides.GetAsync(MediaKind.Series, id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(["ko"], stored!.Languages);

        var (languages, _) = SubtitleLanguagePreference.Resolve(["en"], "all", stored);
        Assert.Equal(["ko"], languages);
    }

    private static async Task<string> ImportMovieAsync(IMovieCatalogRepository movies, string title)
    {
        await movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: title,
                    ReleaseYear: 2021,
                    WantedStatus: WantedStatuses.Covered,
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "WEB 2160p",
                    TargetQuality: "WEB 2160p",
                    QualityCutoffMet: true,
                    FilePath: $@"D:\Media\{title}\{title}.mkv",
                    FileSizeBytes: 8_000_000_000)
            ],
            CancellationToken.None);

        var page = await movies.ListPageAsync(new CatalogueQuery(Search: title), CancellationToken.None);
        return page.Items.Single(item => item.Title == title).Id;
    }

    private static async Task<string> ImportSeriesAsync(ISeriesCatalogRepository series, string title)
    {
        var created = await series.AddAsync(
            new CreateSeriesRequest(Title: title, StartYear: 2022, ImdbId: null, Monitored: true),
            CancellationToken.None);
        return created.Id;
    }

    private static async Task<(SqliteMovieCatalogRepository Movies, SqliteMediaSubtitleOverrideRepository Overrides)> CreateMoviesAsync(
        TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return (
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            new SqliteMediaSubtitleOverrideRepository(storage.Factory, timeProvider));
    }

    private static async Task<(SqliteSeriesCatalogRepository Series, SqliteMediaSubtitleOverrideRepository Overrides)> CreateSeriesAsync(
        TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return (
            new SqliteSeriesCatalogRepository(storage.Factory, timeProvider),
            new SqliteMediaSubtitleOverrideRepository(storage.Factory, timeProvider));
    }
}
