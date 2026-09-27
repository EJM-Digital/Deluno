using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// #840's append-only attempt history — every search attempt gets its own
/// row, unlike <c>movie_subtitle_attempt</c>'s single upserted row per
/// language, and old rows are trimmed back to a retention cap rather than
/// growing without bound.
/// </summary>
public sealed class SubtitleAttemptHistoryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");

    [Fact]
    public async Task Every_attempt_gets_its_own_row_newest_first()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Now);
        var (movies, subtitles) = await CreateAsync(storage, clock);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordAttemptHistoryAsync(MediaKind.Movie, id, "en", "gestdown", "Rejected: not found", CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await subtitles.RecordAttemptHistoryAsync(MediaKind.Movie, id, "en", "podnapisi", "Downloaded: any release", CancellationToken.None);

        var history = await subtitles.ListAttemptHistoryAsync(MediaKind.Movie, id, CancellationToken.None);

        Assert.Equal(2, history.Count);
        // Newest first - the second attempt is what a person reads at the top.
        Assert.Equal("podnapisi", history[0].Provider);
        Assert.Equal("gestdown", history[1].Provider);
    }

    [Fact]
    public async Task History_is_trimmed_back_to_the_retention_cap()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Now);
        var (movies, subtitles) = await CreateAsync(storage, clock);
        var id = await ImportAsync(movies, "Dune");

        for (var i = 0; i < 30; i++)
        {
            await subtitles.RecordAttemptHistoryAsync(MediaKind.Movie, id, "en", "gestdown", $"Attempt {i}", CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        var history = await subtitles.ListAttemptHistoryAsync(MediaKind.Movie, id, CancellationToken.None);

        // Capped, and it is the most recent ones that survive, not the first.
        Assert.True(history.Count <= 25);
        Assert.Equal("Attempt 29", history[0].Outcome);
    }

    [Fact]
    public async Task A_different_titles_history_is_never_trimmed_by_this_ones()
    {
        using var storage = TestStorage.Create();
        var clock = new MutableTimeProvider(Now);
        var (movies, subtitles) = await CreateAsync(storage, clock);
        var dune = await ImportAsync(movies, "Dune");
        var arrival = await ImportAsync(movies, "Arrival");

        for (var i = 0; i < 30; i++)
        {
            await subtitles.RecordAttemptHistoryAsync(MediaKind.Movie, dune, "en", "gestdown", $"Attempt {i}", CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        await subtitles.RecordAttemptHistoryAsync(MediaKind.Movie, arrival, "en", "gestdown", "Only attempt", CancellationToken.None);

        var arrivalHistory = await subtitles.ListAttemptHistoryAsync(MediaKind.Movie, arrival, CancellationToken.None);
        Assert.Single(arrivalHistory);
    }

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
        TestStorage storage, MutableTimeProvider timeProvider)
    {
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return (
            new SqliteMovieCatalogRepository(storage.Factory, timeProvider),
            new SqliteMediaSubtitleRepository(storage.Factory, timeProvider));
    }

    /// <summary>A clock a test can push forward, so rows sort in a known order.</summary>
    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }
}
