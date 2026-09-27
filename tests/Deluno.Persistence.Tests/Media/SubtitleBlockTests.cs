using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// A subtitle Deluno refuses to offer again for one file (#897): the
/// blocklist itself, and the identity a fetched row is written with so a
/// later refusal can name the exact download it means.
/// </summary>
public sealed class SubtitleBlockTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T00:00:00Z");

    [Fact]
    public async Task A_blocked_subtitle_is_listed_with_its_reason()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.BlockSubtitleAsync(
            MediaKind.Movie, id, "en", "gestdown|abc123", "gestdown", "Dune.2021.WEB", "Out of sync with the video.",
            CancellationToken.None);

        var blocked = await subtitles.ListBlockedAsync(MediaKind.Movie, id, CancellationToken.None);
        var entry = Assert.Single(blocked);
        Assert.Equal("en", entry.Language);
        Assert.Equal("gestdown|abc123", entry.BlockKey);
        Assert.Equal("gestdown", entry.ProviderKey);
        Assert.Equal("Out of sync with the video.", entry.Reason);
    }

    [Fact]
    public async Task Blocked_keys_come_back_for_a_search_to_skip()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.BlockSubtitleAsync(
            MediaKind.Movie, id, "en", "gestdown|abc123", "gestdown", null, "Corrupt download.",
            CancellationToken.None);

        var keys = await subtitles.ListBlockedKeysAsync(MediaKind.Movie, id, CancellationToken.None);
        Assert.Contains("gestdown|abc123", keys);
        Assert.DoesNotContain("gestdown|xyz789", keys);
    }

    [Fact]
    public async Task Blocking_the_same_candidate_twice_updates_the_reason_rather_than_duplicating()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.BlockSubtitleAsync(
            MediaKind.Movie, id, "en", "gestdown|abc123", "gestdown", null, "Corrupt download.", CancellationToken.None);
        await subtitles.BlockSubtitleAsync(
            MediaKind.Movie, id, "en", "gestdown|abc123", "gestdown", null, "You removed this subtitle file.", CancellationToken.None);

        var blocked = await subtitles.ListBlockedAsync(MediaKind.Movie, id, CancellationToken.None);
        var entry = Assert.Single(blocked);
        Assert.Equal("You removed this subtitle file.", entry.Reason);
    }

    [Fact]
    public async Task Unblocking_removes_it_and_says_whether_one_was_there()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.BlockSubtitleAsync(
            MediaKind.Movie, id, "en", "gestdown|abc123", "gestdown", null, "Corrupt download.", CancellationToken.None);

        Assert.True(await subtitles.UnblockSubtitleAsync(MediaKind.Movie, id, "gestdown|abc123", CancellationToken.None));
        Assert.False(await subtitles.UnblockSubtitleAsync(MediaKind.Movie, id, "gestdown|abc123", CancellationToken.None));
        Assert.Empty(await subtitles.ListBlockedAsync(MediaKind.Movie, id, CancellationToken.None));
    }

    /// <summary>
    /// A fetched row remembers the exact candidate it came from, so a later
    /// "reject this one" can name it — not just "some gestdown subtitle for
    /// English", which would also refuse a different, better upload.
    /// </summary>
    [Fact]
    public async Task A_fetched_subtitle_keeps_the_identity_of_the_candidate_it_came_from()
    {
        using var storage = TestStorage.Create();
        var (movies, subtitles) = await CreateAsync(storage);
        var id = await ImportAsync(movies, "Dune");

        await subtitles.RecordFetchedAsync(
            MediaKind.Movie,
            id,
            new MediaSubtitleRow(
                "en", "fetched", Forced: false, HearingImpaired: false, FilePath: "x.srt", StreamIndex: null,
                Codec: "srt", Provider: "gestdown", MatchRung: 0, SourceKey: SubtitleBlockKeys.For("gestdown", "abc123")),
            CancellationToken.None);

        var rows = await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal("gestdown|abc123", row.SourceKey);
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
