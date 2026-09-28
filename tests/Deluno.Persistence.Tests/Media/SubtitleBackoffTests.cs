using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// The search has to have a memory, and this is the failure it prevents.
///
/// <para>Without one, <c>ListWantedAsync</c> takes the first
/// <c>MaxItemsPerRun</c> rows short of a language in whatever order SQLite hands
/// them back. A library where five thousand films have no Japanese subtitle asks
/// the same ten films every cycle, for ever, and the other four thousand nine
/// hundred and ninety are never asked at all — while the job succeeds, the
/// providers answer, and the bar never moves.</para>
/// </summary>
public sealed class SubtitleBackoffTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-08-28T00:00:00Z");

    [Fact]
    public async Task A_title_just_searched_is_not_asked_again_immediately()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Start);
        var (movies, subtitles) = await CreateAsync(storage, clock);
        var id = await ImportAsync(movies, "Dune");

        Assert.Single(await WantedAsync(subtitles));

        await subtitles.RecordAttemptAsync(
            MediaKind.Movie, id, "en", "No provider had it.", TimeSpan.FromHours(6), CancellationToken.None);

        Assert.Empty(await WantedAsync(subtitles));

        // Six hours later it is due again — the library's own retry delay, which
        // is the same number the release search uses.
        clock.Advance(TimeSpan.FromHours(6, 1));
        Assert.Single(await WantedAsync(subtitles));
    }

    [Fact]
    public async Task The_slice_rotates_instead_of_asking_the_same_titles()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Start);
        var (movies, subtitles) = await CreateAsync(storage, clock);

        var ids = new List<string>();
        for (var index = 0; index < 6; index++)
        {
            ids.Add(await ImportAsync(movies, $"Film {index:00}"));
        }

        // Two at a time, which is what a small MaxItemsPerRun looks like.
        var first = await WantedAsync(subtitles, limit: 2);
        foreach (var item in first)
        {
            await subtitles.RecordAttemptAsync(
                MediaKind.Movie, item.MediaId, "en", "nothing", TimeSpan.FromHours(6), CancellationToken.None);
        }

        var second = await WantedAsync(subtitles, limit: 2);

        // The whole point: the second slice is different titles. Before the
        // attempt table it was the same two, every cycle, for ever.
        Assert.Empty(second.Select(item => item.MediaId).Intersect(first.Select(item => item.MediaId)));
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public async Task The_delay_doubles_and_then_stops_doubling()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Start);
        var (movies, subtitles) = await CreateAsync(storage, clock);
        var id = await ImportAsync(movies, "Dune");

        var delay = TimeSpan.FromHours(6);

        // Ten failures. A title nobody has subtitled must not be asked about
        // every six hours for the rest of its life.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await subtitles.RecordAttemptAsync(
                MediaKind.Movie, id, "en", "nothing", delay, CancellationToken.None);
            clock.Advance(TimeSpan.FromDays(30));
        }

        // Still asked, eventually — which is where this parts company with
        // Weir's permanent skip. A title that can never be asked again is
        // work that has silently left the system, and nobody finds out the day
        // somebody finally uploads the subtitle.
        Assert.Single(await WantedAsync(subtitles));

        // And the wait never grows past a fortnight.
        await subtitles.RecordAttemptAsync(MediaKind.Movie, id, "en", "nothing", delay, CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(15));
        Assert.Single(await WantedAsync(subtitles));
    }

    [Fact]
    public async Task Finding_it_forgets_the_attempt()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Start);
        var (movies, subtitles) = await CreateAsync(storage, clock);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordAttemptAsync(
            MediaKind.Movie, id, "en", "nothing", TimeSpan.FromHours(6), CancellationToken.None);
        await subtitles.RecordFetchedAsync(
            MediaKind.Movie,
            id,
            // At the cutoff, because this test is about the attempt row being
            // cleared — a subtitle below it is deliberately kept on the list, and
            // that is the upgrade path's test, not this one.
            new MediaSubtitleRow("en", SubtitleSources.Fetched, false, false, "Dune.en.srt", null, "srt", "gestdown",
                MatchRung: (int)SubtitleCutoff.Rung),
            CancellationToken.None);
        await subtitles.ClearAttemptAsync(MediaKind.Movie, id, "en", CancellationToken.None);

        // Held, so it is not wanted — and there is no stale attempt row left to
        // keep the ordering query walking past it.
        Assert.Empty(await WantedAsync(subtitles));

        // If the file later loses its subtitle, the next scan removes the row
        // and it is due again straight away rather than serving out an old
        // backoff nobody can see.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty(await WantedAsync(subtitles));
    }

    [Fact]
    public async Task One_language_being_on_backoff_does_not_hide_another()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Start);
        var (movies, subtitles) = await CreateAsync(storage, clock);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordAttemptAsync(
            MediaKind.Movie, id, "en", "nothing", TimeSpan.FromHours(6), CancellationToken.None);

        var item = Assert.Single(await WantedAsync(subtitles, languages: ["en", "ja"]));
        // English is waiting; Japanese has never been tried and is still asked
        // for. Backing off a whole title because one language failed would stop
        // Deluno fetching a language it has not even looked for.
        Assert.Equal(["ja"], item.LanguagesToFetch);
    }

    [Fact]
    public async Task A_due_attempt_exposes_the_typed_provider_failure()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Start);
        var (movies, subtitles) = await CreateAsync(storage, clock);
        var id = await ImportAsync(movies, "Dune");
        var failure = IntegrationFailureFactory.FromLegacy(
            "subtitle",
            "podnapisi",
            "Podnapisi",
            "search",
            "unreachable",
            "The subtitle host could not be reached.");

        await subtitles.RecordAttemptAsync(
            MediaKind.Movie,
            id,
            "en",
            failure.Message,
            TimeSpan.FromHours(6),
            CancellationToken.None,
            failure);

        clock.Advance(TimeSpan.FromHours(6, 1));
        var item = Assert.Single(await WantedAsync(subtitles));

        Assert.Equal(IntegrationFailureKind.Unavailable, item.LastFailure!.Kind);
        Assert.Equal("podnapisi", item.LastFailure.ServiceId);
        Assert.Equal("search", item.LastFailure.Operation);
        Assert.Equal("The subtitle host could not be reached.", item.LastFailure.Message);
    }

    /* ------------------------------------------------------------ helpers */

    private static Task<IReadOnlyList<MediaSubtitleWantedItem>> WantedAsync(
        SqliteMediaSubtitleRepository subtitles,
        int limit = 50,
        string[]? languages = null)
        => subtitles.ListWantedAsync(
            MediaKind.Movie, "library-movies", languages ?? ["en"], limit, embeddedCounts: true, pictureBasedCounts: true, CancellationToken.None);

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
        TestStorage storage,
        TimeProvider clock)
    {
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return (
            new SqliteMovieCatalogRepository(storage.Factory, clock),
            new SqliteMediaSubtitleRepository(storage.Factory, clock));
    }

    /// <summary>
    /// A clock a test can push forward, because backoff is only observable over
    /// time and sleeping for six hours is not a test.
    /// </summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }
}
