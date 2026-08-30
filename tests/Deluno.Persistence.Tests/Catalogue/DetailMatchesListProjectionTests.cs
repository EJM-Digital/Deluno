using System.Reflection;
using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// A detail page must never know less about a title than the grid it was opened
/// from.
///
/// <para>It did. <c>GetByIdAsync</c> and <c>ListPageAsync</c> return the same
/// record — <c>MovieListItem</c>, <c>SeriesListItem</c> — from two separate
/// projections, and the detail one quietly carried fewer columns. The film with
/// the only real file in the lab therefore had the emptiest header on the site:
/// path, size, codecs, runtime and release group all came back null on the one
/// screen that exists to show them. James: <i>"Big buck bunny is the only one
/// with real files and how can it be the thinnest"</i>, then <i>"lets make sure
/// this doesnt happen ever again please for anything and everything movies and
/// tv"</i>.</para>
///
/// <para><b>This is the third instance of one shape.</b> The wanted-state fields
/// went the same way — <c>MediaEntryDetails.LibraryId</c> carries a comment about
/// a detail page losing the library and the cutoff past the 25th title. Fixing
/// each field-group as it is noticed does not stop the next one, because the two
/// projections are written by hand in different files and nothing compares
/// them.</para>
///
/// <para>So this compares them. It walks <b>every property by reflection</b>
/// rather than naming the ones we happen to remember: a field added to the list
/// projection and forgotten on the detail one fails here without anybody
/// thinking to write a test for it. That is the whole point — the defect is
/// always a field somebody did not think of.</para>
/// </summary>
public sealed class DetailMatchesListProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-30T00:00:00Z");

    /// <summary>A provider answering with everything it can answer with.</summary>
    private static MetadataSearchResult Answer(string mediaType) => new(
        Provider: "tmdb",
        ProviderId: "157336",
        MediaType: mediaType,
        Title: "Interstellar",
        OriginalTitle: "Interstellar",
        Year: 2014,
        Overview: "They went looking for a new home.",
        PosterUrl: "/api/metadata/artwork/poster",
        BackdropUrl: "/api/metadata/artwork/backdrop",
        Rating: 8.4,
        Ratings: [],
        Genres: ["Adventure", "Drama"],
        ImdbId: "tt0816692",
        ExternalUrl: null,
        Certification: "PG-13",
        Studio: "Legendary Pictures",
        Network: "HBO",
        Collection: "The Nolan Collection",
        OriginalLanguage: "en",
        Status: mediaType == "movies" ? "Released" : "Ended",
        Keywords: ["space travel"],
        RuntimeMinutes: 169,
        Popularity: 91.2,
        VoteCount: 36_000);

    [Fact]
    public async Task A_films_detail_carries_everything_its_row_on_the_shelf_does()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateMoviesAsync(storage);

        // **The title must hold a file.** Without one the file facts are null on
        // BOTH projections, so a null-skip excuses them and the test passes while
        // guarding nothing — which is exactly what it did on the first run, until
        // FilePath was deleted from the detail projection and it stayed green.
        //
        // A fixture that does not exercise a field does not defend it.
        await movies.ImportExistingAsync(
            libraryId: "library-1",
            title: "Interstellar",
            releaseYear: 2014,
            wantedStatus: WantedStatuses.Covered,
            wantedReason: "imported",
            currentQuality: "Bluray-1080p",
            targetQuality: "Bluray-1080p",
            qualityCutoffMet: true,
            unmonitorWhenCutoffMet: false,
            filePath: @"C:\Library\Movies\Interstellar (2014)\Interstellar.mkv",
            fileSizeBytes: 61_878_609,
            cancellationToken: CancellationToken.None);

        var created = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        await movies.UpdateMetadataAsync(created.Id, Answer("movies"), CancellationToken.None);

        var fromShelf = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        var fromDetail = await movies.GetByIdAsync(created.Id, CancellationToken.None);

        AssertDetailIsNotPoorer(fromShelf, fromDetail);
    }

    [Fact]
    public async Task A_shows_detail_carries_everything_its_row_on_the_shelf_does()
    {
        using var storage = TestStorage.Create();
        var series = await CreateSeriesAsync(storage);

        var created = await series.AddAsync(
            new CreateSeriesRequest("Interstellar", 2014, "tt0816692"),
            CancellationToken.None);
        await series.UpdateMetadataAsync(created.Id, Answer("tv"), CancellationToken.None);

        var fromShelf = Assert.Single(
            (await series.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        var fromDetail = await series.GetByIdAsync(created.Id, CancellationToken.None);

        AssertDetailIsNotPoorer(fromShelf, fromDetail);
    }

    /// <summary>
    /// Every property the shelf answered with, the detail must answer with too.
    ///
    /// <para>Reflection rather than a written list, because a written list only
    /// ever contains the fields somebody remembered — and the defect is always
    /// the one they did not. A property the shelf leaves null is not asserted:
    /// this says the detail is not <i>poorer</i>, which is the actual rule, not
    /// that the two are byte-identical.</para>
    /// </summary>
    private static void AssertDetailIsNotPoorer<T>(T fromShelf, T? fromDetail) where T : class
    {
        Assert.NotNull(fromDetail);

        var poorer = new List<string>();
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var onShelf = property.GetValue(fromShelf);
            if (onShelf is null) continue;

            // A collection the shelf filled and the detail did not is the same
            // failure as a null, and `Equals` on two lists is reference equality.
            var onDetail = property.GetValue(fromDetail);
            if (onDetail is null)
            {
                poorer.Add($"{property.Name}: shelf has '{onShelf}', detail has null");
                continue;
            }

            if (onShelf is System.Collections.ICollection shelfItems
                && onDetail is System.Collections.ICollection detailItems)
            {
                if (shelfItems.Count > detailItems.Count)
                {
                    poorer.Add($"{property.Name}: shelf has {shelfItems.Count}, detail has {detailItems.Count}");
                }
                continue;
            }

            if (!onShelf.Equals(onDetail))
            {
                poorer.Add($"{property.Name}: shelf has '{onShelf}', detail has '{onDetail}'");
            }
        }

        Assert.True(
            poorer.Count == 0,
            "The detail projection knows less than the shelf's about the same title:\n  "
            + string.Join("\n  ", poorer));
    }

    private static async Task<SqliteMovieCatalogRepository> CreateMoviesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }

    private static async Task<SqliteSeriesCatalogRepository> CreateSeriesAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(Now);
        await new SeriesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteSeriesCatalogRepository(storage.Factory, clock);
    }
}
