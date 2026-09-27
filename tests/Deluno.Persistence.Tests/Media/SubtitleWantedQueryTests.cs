using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Integrations.Subtitles;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// What Deluno decides is still outstanding, and where that parts company with
/// what the bar paints.
///
/// <para>Two queries read the subtitle store: the rollup that paints the bar, and
/// this one that decides what to fetch. They agree on <c>forced = 0</c> and on
/// which languages were asked for, and these tests hold them to it.</para>
///
/// <para><b>They deliberately disagree about one thing.</b> The bar answers "can
/// I watch this tonight"; this query answers "is Deluno finished". Since the
/// cutoff arrived those are different questions — a subtitle on disk that nobody
/// can prove was cut for your release is watchable and not finished, which is
/// what DESIGN-001's green already meant. So a held language can still be
/// outstanding, and the tests below say so both ways round.</para>
/// </summary>
public sealed class SubtitleWantedQueryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-28T00:00:00Z");

    [Fact]
    public async Task Only_the_languages_a_file_is_actually_short_of()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(MediaKind.Movie, id, Row("en"), CancellationToken.None);

        var wanted = await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en", "ja"], 50, embeddedCounts: true, CancellationToken.None);

        var item = Assert.Single(wanted);
        // English is held, so it is not asked for again. Asking anyway is what
        // spends a daily allowance on a file that already has it.
        Assert.Equal(["ja"], item.LanguagesToFetch);
        Assert.Equal("Dune", item.Title);
    }

    /// <summary>
    /// A subtitle that is watchable but not provably in time stays on the list.
    ///
    /// <para>the owner: <i>"we need the best method, no point spreading lies about
    /// subs that may be out of sync etc etc."</i> So a file with English on disk
    /// at the bottom rung is still asked about — it is covered tonight, and it is
    /// not finished with.</para>
    /// </summary>
    [Fact]
    public async Task A_subtitle_below_the_cutoff_is_still_outstanding()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(
            MediaKind.Movie, id, Row("en", SubtitleMatch.AnyRelease), CancellationToken.None);

        var item = Assert.Single(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: true, CancellationToken.None));

        Assert.Equal(["en"], item.LanguagesToFetch);
    }

    /// <summary>
    /// Bazarr's shipped default is not Deluno's. Same source passes there and is
    /// still short of the cutoff here, which is the whole of the decision.
    /// </summary>
    [Fact]
    public async Task Same_source_is_not_good_enough_to_stop_looking()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(
            MediaKind.Movie, id, Row("en", SubtitleMatch.SameSource), CancellationToken.None);

        var item = Assert.Single(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: true, CancellationToken.None));

        Assert.Equal(["en"], item.LanguagesToFetch);
    }

    /// <summary>
    /// The bar keeps its green for a subtitle below the cutoff, and only its gold
    /// is withheld.
    ///
    /// <para>The failure this guards against is somebody making <i>held</i>
    /// follow the cutoff for symmetry. Every title Deluno was still improving
    /// would lose its green, and the shelf would read as though nothing had been
    /// fetched at all. Held answers "can I watch tonight"; settled answers "has
    /// Deluno finished"; only the second one moved.</para>
    /// </summary>
    [Theory]
    [InlineData(SubtitleMatch.AnyRelease, 1, 0)]
    [InlineData(SubtitleMatch.SameSource, 1, 0)]
    [InlineData(SubtitleMatch.MadeForThisFile, 1, 1)]
    public async Task Held_survives_the_cutoff_and_only_settled_answers_to_it(
        SubtitleMatch rung,
        int expectedHeld,
        int expectedSettled)
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(MediaKind.Movie, id, Row("en", rung), CancellationToken.None);

        await using var connection = await storage.Factory.OpenConnectionAsync(DelunoDatabaseNames.Movies);
        var held = await CatalogueSubtitleRollup.ReadAsync(
            connection, MediaKind.Movie, [id], ["en"], Now, CancellationToken.None);

        Assert.Equal(expectedHeld, held[id].Languages);
        Assert.Equal(expectedSettled, held[id].Settled);
    }

    [Fact]
    public async Task A_file_that_holds_everything_asked_for_is_not_returned()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(MediaKind.Movie, id, Row("en"), CancellationToken.None);

        Assert.Empty(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: true, CancellationToken.None));
    }

    [Fact]
    public async Task A_forced_track_is_not_coverage_here_either()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        // A file whose only English is forced has English for four lines of
        // Elvish. The bar does not count it, and neither does this — if one of
        // them did, they would disagree about the same title.
        await subtitles.RecordFetchedAsync(
            MediaKind.Movie, id, Row("en") with { Forced = true }, CancellationToken.None);

        var item = Assert.Single(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: true, CancellationToken.None));

        Assert.Equal(["en"], item.LanguagesToFetch);
    }

    [Fact]
    public async Task Hearing_impaired_is_coverage_because_it_is_watchable()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(
            MediaKind.Movie, id, Row("en") with { HearingImpaired = true }, CancellationToken.None);

        Assert.Empty(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: true, CancellationToken.None));
    }

    [Fact]
    public async Task A_library_that_asked_for_nothing_runs_no_query_at_all()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        await ImportAsync(movies, "Dune");

        // The same rule the rollup follows and the reason a library nobody has
        // asked for subtitles pays nothing for the feature.
        Assert.Empty(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", [], 50, embeddedCounts: true, CancellationToken.None));
    }

    [Fact]
    public async Task A_title_with_no_file_is_not_asked_about()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        await movies.AddAsync(new CreateMovieRequest("Sicario", 2015, null), CancellationToken.None);

        // A title with no file holds no subtitles to be short of, which is
        // DESIGN-002's position and the reason its bar is absent rather than red.
        Assert.Empty(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: true, CancellationToken.None));
    }

    [Fact]
    public async Task The_slice_is_bounded_because_a_library_can_be_twenty_thousand()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        for (var index = 0; index < 12; index++)
        {
            await ImportAsync(movies, $"Film {index:00}");
        }

        var wanted = await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 5, embeddedCounts: true, CancellationToken.None);

        Assert.Equal(5, wanted.Count);
    }

    [Fact]
    public async Task An_embedded_track_stops_counting_when_the_library_says_so()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(
            MediaKind.Movie,
            id,
            Row("en") with { Source = SubtitleSources.Embedded },
            CancellationToken.None);

        // The default, and what Deluno has always done: a track inside the
        // container is coverage.
        Assert.Empty(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: true, CancellationToken.None));

        // Off, because a player handles the two differently and an embedded
        // track cannot be swapped or corrected (#321). Now the sidecar is wanted.
        var item = Assert.Single(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: false, CancellationToken.None));
        Assert.Equal(["en"], item.LanguagesToFetch);
    }

    /// <summary>
    /// An embedded track has no release to check, so it can never climb the
    /// match ladder on its own — before #894 that meant a language the
    /// container already carried stayed "outstanding" forever and Deluno kept
    /// asking providers for it. It has no upgrade to look for, so it is
    /// settled the moment it is found, at whatever rung it was recorded at.
    /// </summary>
    [Fact]
    public async Task An_embedded_track_is_settled_even_at_the_lowest_rung()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(
            MediaKind.Movie,
            id,
            Row("en", SubtitleMatch.AnyRelease) with { Source = SubtitleSources.Embedded },
            CancellationToken.None);

        Assert.Empty(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: true, CancellationToken.None));
    }

    /// <summary>
    /// Turning embedded coverage off already throws the row out of
    /// <c>heldPredicate</c>, so the settled exception for embedded tracks must
    /// never reopen what that setting closed: the language stays outstanding.
    /// </summary>
    [Fact]
    public async Task Embedded_settling_never_reopens_when_the_library_excludes_embedded()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(
            MediaKind.Movie,
            id,
            Row("en", SubtitleMatch.AnyRelease) with { Source = SubtitleSources.Embedded },
            CancellationToken.None);

        var item = Assert.Single(await subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", ["en"], 50, embeddedCounts: false, CancellationToken.None));
        Assert.Equal(["en"], item.LanguagesToFetch);
    }

    [Fact]
    public void The_bar_and_the_fetcher_read_the_same_held_predicate()
    {
        // Not an assertion about SQL text for its own sake. These are the two
        // queries that decide whether a title looks covered and whether Deluno
        // keeps looking, and DESIGN-001 spent a run undoing four copies of one
        // rule. They share the fragment, so this only has to prove the switch
        // reaches it.
        Assert.DoesNotContain("sub.source", CatalogueSubtitleRollup.HeldPredicate(embeddedCounts: true), StringComparison.Ordinal);
        Assert.Contains(SubtitleSources.Embedded, CatalogueSubtitleRollup.HeldPredicate(embeddedCounts: false), StringComparison.Ordinal);

        // Forced is never coverage either way round.
        Assert.Contains("sub.forced = 0", CatalogueSubtitleRollup.HeldPredicate(embeddedCounts: true), StringComparison.Ordinal);
        Assert.Contains("sub.forced = 0", CatalogueSubtitleRollup.HeldPredicate(embeddedCounts: false), StringComparison.Ordinal);
    }

    /* ------------------------------------------------------------ helpers */

    /// <summary>
    /// A subtitle Deluno is finished with, unless a test says otherwise.
    ///
    /// <para>The rung matters now: at the cutoff means settled, below it means
    /// held-and-still-looking. Defaulting to the cutoff keeps every test that
    /// predates the ladder testing what it was written to test — whether a
    /// language counts as covered at all — rather than accidentally testing the
    /// upgrade path.</para>
    /// </summary>
    private static MediaSubtitleRow Row(string language, SubtitleMatch match = SubtitleCutoff.Rung)
        => new(language, "fetched", Forced: false, HearingImpaired: false, FilePath: "x.srt", StreamIndex: null,
            Codec: "srt", Provider: "gestdown", MatchRung: (int)match);

    private static async Task<string> ImportAsync(IMovieCatalogRepository movies, string title)
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

    private static async Task<(SqliteMovieCatalogRepository Movies, SqliteMediaSubtitleRepository Subtitles)> CreateAsync(
        TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return (
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            new SqliteMediaSubtitleRepository(storage.Factory, timeProvider));
    }
}

/// <summary>
/// What Deluno ships, and the one thing Weir's registry got wrong.
/// </summary>
public sealed class SubtitleProviderRegistryTests
{
    [Fact]
    public void Six_providers_and_only_one_OpenSubtitles()
    {
        var registry = new SubtitleProviderRegistry(
        [
            new Deluno.Integrations.Subtitles.Providers.GestdownSubtitleProvider(null!),
            new Deluno.Integrations.Subtitles.Providers.PodnapisiSubtitleProvider(null!),
            new Deluno.Integrations.Subtitles.Providers.OpenSubtitlesSubtitleProvider(null!),
            new Deluno.Integrations.Subtitles.Providers.SubDlSubtitleProvider(null!),
            new Deluno.Integrations.Subtitles.Providers.SubSourceSubtitleProvider(null!),
            new Deluno.Integrations.Subtitles.Providers.Subf2mSubtitleProvider(null!)
        ]);

        Assert.Equal(6, registry.All.Count);

        // Weir listed opensubtitles_org and opensubtitles_com as two
        // providers with two sets of credentials, and both keys mapped to one
        // handler that posts to the .com API. It was one source counted twice.
        Assert.Single(registry.All, provider =>
            provider.DisplayName.Contains("OpenSubtitles", StringComparison.OrdinalIgnoreCase));

        // The two that need no account come first, so a new install finds
        // something before it is asked to sign up for anything.
        Assert.Equal(["gestdown", "podnapisi"], registry.All.Take(2).Select(provider => provider.Key));
    }

    [Fact]
    public void Every_provider_says_what_it_needs_and_what_it_covers()
    {
        var registry = new SubtitleProviderRegistry(
        [
            new Deluno.Integrations.Subtitles.Providers.GestdownSubtitleProvider(null!),
            new Deluno.Integrations.Subtitles.Providers.PodnapisiSubtitleProvider(null!)
        ]);

        // A TV-only source, declared rather than discovered by asking it about a
        // film and counting the empty answer as a failure.
        Assert.Equal(SubtitleProviderScope.TvOnly, registry.Find("gestdown")!.Scope);
        Assert.Equal(SubtitleProviderScope.Both, registry.Find("podnapisi")!.Scope);
        // Optional credentials are a third state, and the screen says which:
        // "needs an account" and "an account gets you more" are different.
        Assert.True(registry.Find("podnapisi")!.CredentialsOptional);

        foreach (var provider in registry.All)
        {
            Assert.NotEmpty(provider.DisplayName);
            // The line a person reads when deciding whether to turn it on, and
            // the place the two fragile ones admit what they are.
            Assert.NotEmpty(provider.Description);
        }
    }
}
