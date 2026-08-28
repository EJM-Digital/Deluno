using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// "Everything whose only English subtitle is forced" — the question #307 was
/// opened for, and the one no other tool can express.
///
/// <para>Radarr states its own ceiling in its Custom Filters dialog: it filters
/// the properties of a movie, never the properties of the file you hold. A film
/// with an English track that carries nothing but signage has English subtitles
/// by any simple test, and cannot be watched in English.</para>
/// </summary>
public sealed class SubtitleFilterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-29T00:00:00Z");

    [Fact]
    public async Task A_language_held_only_as_forced_signage_is_not_a_language_you_can_watch_it_in()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory, new FixedTimeProvider(Now));

        var signage = await AddAsync(movies, "Arrival");
        var proper = await AddAsync(movies, "Sicario");

        // One film whose English is signage only, one with a real track. Both
        // "have English" by the naive test, which is the point.
        await RecordAsync(subtitles, signage, [Track("en", forced: true), Track("fr", forced: false)]);
        await RecordAsync(subtitles, proper, [Track("en", forced: false)]);

        Assert.Equal(
            ["Arrival", "Sicario"],
            await TitlesAsync(movies, "subtitleLanguage:has:en"));

        // The distinguishing half: only the film with a real track survives.
        Assert.Equal(["Sicario"], await TitlesAsync(movies, "subtitleLanguageFull:has:en"));

        // And the question a person actually types — "English, but not really".
        Assert.Equal(
            ["Arrival"],
            await TitlesAsync(movies, "subtitleLanguage:has:en", "subtitleLanguageFull:nothas:en"));
    }

    /// <summary>
    /// Missing is the same field with the other operator, not a second control.
    /// </summary>
    [Fact]
    public async Task What_is_missing_is_asked_of_the_same_field()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory, new FixedTimeProvider(Now));

        var french = await AddAsync(movies, "Arrival");
        await AddAsync(movies, "Sicario");

        await RecordAsync(subtitles, french, [Track("fr", forced: false)]);

        // Sicario has none at all; Arrival has French. Both are missing English,
        // and a title with no subtitle row must not fall out of the answer just
        // because its column is null.
        Assert.Equal(["Arrival", "Sicario"], await TitlesAsync(movies, "subtitleLanguage:nothas:en"));
        Assert.Equal(["Arrival"], await TitlesAsync(movies, "subtitleLanguage:has:fr"));
    }

    /// <summary>
    /// Whatever the file called the language, the shelf is asked in one
    /// vocabulary.
    ///
    /// <para>A container may say <c>eng</c>, a sidecar <c>en</c> and a provider
    /// <c>English</c>. They are folded to one code on the way in, so a person
    /// filtering for English does not have to guess which one their file used —
    /// and the cached column holds the folded value rather than whatever
    /// arrived.</para>
    /// </summary>
    [Fact]
    public async Task A_language_is_asked_for_in_one_vocabulary_whatever_the_file_called_it()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory, new FixedTimeProvider(Now));

        var container = await AddAsync(movies, "Arrival");
        var sidecar = await AddAsync(movies, "Sicario");

        await RecordAsync(subtitles, container, [Track("eng", forced: false)]);
        await RecordAsync(subtitles, sidecar, [Track("en", forced: false)]);

        Assert.Equal(["Arrival", "Sicario"], await TitlesAsync(movies, "subtitleLanguage:has:en"));
    }

    /// <summary>
    /// The trigger keeps up, which is why it is a trigger.
    /// </summary>
    [Fact]
    public async Task Fetching_a_subtitle_changes_what_the_shelf_can_be_asked()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        var subtitles = new SqliteMediaSubtitleRepository(storage.Factory, new FixedTimeProvider(Now));

        var movie = await AddAsync(movies, "Arrival");
        Assert.Empty(await TitlesAsync(movies, "subtitleLanguage:has:en"));

        await RecordAsync(subtitles, movie, [Track("en", forced: false)]);
        Assert.Equal(["Arrival"], await TitlesAsync(movies, "subtitleLanguage:has:en"));

        // And a rescan that finds nothing takes it away again, rather than
        // leaving a stale yes behind.
        await RecordAsync(subtitles, movie, []);
        Assert.Empty(await TitlesAsync(movies, "subtitleLanguage:has:en"));
    }

    private static MediaSubtitleRow Track(string language, bool forced)
        => new(language, "embedded", forced, HearingImpaired: false, FilePath: null, StreamIndex: 0, Codec: "subrip", Provider: null);

    private static Task RecordAsync(
        SqliteMediaSubtitleRepository subtitles,
        string movieId,
        IReadOnlyList<MediaSubtitleRow> rows)
        => subtitles.RecordScanAsync(
            MediaKind.Movie,
            movieId,
            new MediaSubtitleScan(@"D:\Media\file.mkv", 1024, "ok", rows.Count, Now),
            rows,
            CancellationToken.None);

    private static async Task<string> AddAsync(IMovieCatalogRepository movies, string title)
        => (await movies.AddAsync(new CreateMovieRequest(title, 2016, null), CancellationToken.None)).Id;

    private static async Task<string[]> TitlesAsync(IMovieCatalogRepository movies, params string[] conditions)
    {
        var filters = CatalogueFilters.Parse(MediaKind.Movie, conditions, out var errors);
        Assert.True(errors.Count == 0, string.Join("; ", errors));

        var page = await movies.ListPageAsync(
            new CatalogueQuery(Filters: filters, Sort: CatalogueSortFields.Title, Descending: false),
            CancellationToken.None);

        return [.. page.Items.Select(item => item.Title)];
    }

    private static async Task<SqliteMovieCatalogRepository> CreateAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }
}
