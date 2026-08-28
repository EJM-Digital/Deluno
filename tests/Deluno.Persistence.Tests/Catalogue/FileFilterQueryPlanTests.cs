using Deluno.Contracts;
using Deluno.Infrastructure.Storage;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Catalogue;

/// <summary>
/// Filtering by the file does not read the whole library first.
///
/// <para>#307's own rule: <i>"Do not filter these through the pick directly —
/// that is a full scan wearing a seek's clothes, correct at eleven titles and
/// ruinous at twenty thousand."</i> The page reaches the wanted state through
/// <c>ws.rowid = (SELECT … LIMIT 1)</c>, which SQLite cannot index, so a
/// <c>WHERE</c> on the far side of it runs that pick for every title in the
/// library before it can discard one.</para>
///
/// <para>Seven filters shipped that way and were correct the whole time, which
/// is the point: nothing about the answer looks wrong. This is the only thing
/// that says so.</para>
/// </summary>
public sealed class FileFilterQueryPlanTests
{
    /// <summary>
    /// Every filter on both shelves, so one added without a cached column
    /// behind it fails here rather than at twenty thousand titles.
    /// </summary>
    public static TheoryData<string, string> EveryFilter()
    {
        var data = new TheoryData<string, string>();

        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
        {
            foreach (var field in CatalogueFilterFields.For(kind))
            {
                data.Add(kind.ToString(), field.Id);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFilter))]
    public void No_filter_picks_a_file_for_every_title_in_the_library(string kind, string fieldId)
    {
        var mediaKind = kind == nameof(MediaKind.Movie) ? MediaKind.Movie : MediaKind.Series;
        var field = CatalogueFilterFields.For(mediaKind).Single(candidate => candidate.Id == fieldId);

        // `ws` is the correlated pick — ws.rowid = (SELECT … LIMIT 1) — and
        // SQLite cannot index it. Naming it in a WHERE means running that pick
        // for every title before discarding one. Thirteen filters shipped that
        // way and every one was correct, which is the whole reason this is a
        // test and not a review comment.
        Assert.DoesNotContain("ws.", field.Column, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the page still returns the right rows, which the assertion above
    /// cannot tell you on its own.
    ///
    /// <para>A cached column that is never written is a filter that matches
    /// nothing — the defect this codebase keeps producing — so this imports a
    /// real file and asks for it back through each filter the file should
    /// satisfy.</para>
    /// </summary>
    [Fact]
    public async Task The_cached_facts_are_written_by_the_import_and_answer_the_filters()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: "Arrival",
                    ReleaseYear: 2016,
                    WantedStatus: "covered",
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "Bluray-1080p",
                    TargetQuality: "Bluray-1080p",
                    QualityCutoffMet: true,
                    UnmonitorWhenCutoffMet: true,
                    FilePath: @"D:\Media\Arrival (2016)\Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS.mkv",
                    FileSizeBytes: 8L * 1024 * 1024 * 1024)
            ],
            CancellationToken.None);

        foreach (var condition in new[]
                 {
                     "videoCodec:is:H.264",
                     "audioCodec:is:DTS-HD",
                     "audioChannels:is:5.1",
                     "releaseGroup:is:SPARKS",
                     // Derived from the path by the migration's own expression,
                     // which is the part most likely to be quietly wrong.
                     "container:is:mkv",
                     "path:has:Arrival",
                     "hasFile:is:true"
                 })
        {
            var filters = CatalogueFilters.Parse(MediaKind.Movie, [condition], out var errors);

            Assert.True(errors.Count == 0, $"{condition} was refused: {string.Join("; ", errors)}");

            var page = await movies.ListPageAsync(
                new CatalogueQuery(Filters: filters), CancellationToken.None);

            Assert.True(page.Items.Count == 1, $"{condition} matched no rows, so nothing wrote its cached column.");
        }
    }

    /// <summary>
    /// The trigger keeps up when the file changes, which is the whole reason it
    /// is a trigger and not a line in the import.
    /// </summary>
    [Fact]
    public async Task Replacing_the_file_moves_the_cached_facts_with_it()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, @"D:\Media\Arrival (2016)\Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS.mkv");
        await ImportAsync(movies, @"D:\Media\Arrival (2016)\Arrival.2016.2160p.WEB-DL.HEVC.EAC3.7.1-NTb.mp4");

        var stale = CatalogueFilters.Parse(MediaKind.Movie, ["videoCodec:is:H.264"], out _);
        Assert.Empty((await movies.ListPageAsync(new CatalogueQuery(Filters: stale), CancellationToken.None)).Items);

        var current = CatalogueFilters.Parse(MediaKind.Movie, ["container:is:mp4"], out _);
        Assert.Single((await movies.ListPageAsync(new CatalogueQuery(Filters: current), CancellationToken.None)).Items);
    }

    /// <summary>
    /// What Deluno decided reaches its columns too, and the two counts say
    /// something the page cannot.
    ///
    /// <para>The shelf shows the one wanted-state row it picked and is silent
    /// about the rest, so a title held in two libraries and a title held in one
    /// look identical. That is what these counts are for — and a count that is
    /// never written reads as "held in one library" for everything, which is a
    /// plausible answer and a wrong one.</para>
    /// </summary>
    [Fact]
    public async Task What_deluno_decided_reaches_its_columns_and_the_counts_can_see_a_second_copy()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, @"D:\Media\Arrival (2016)\Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS.mkv");

        foreach (var condition in new[]
                 {
                     "wantedReason:has:existing library",
                     "targetQuality:in:Bluray-1080p",
                     "cutoffMet:is:true",
                     "libraryCount:is:1",
                     "fileCount:is:1"
                 })
        {
            var filters = CatalogueFilters.Parse(MediaKind.Movie, [condition], out var errors);

            Assert.True(errors.Count == 0, $"{condition} was refused: {string.Join("; ", errors)}");

            var page = await movies.ListPageAsync(new CatalogueQuery(Filters: filters), CancellationToken.None);
            Assert.True(page.Items.Count == 1, $"{condition} matched no rows, so nothing wrote its cached column.");
        }

        // A second library holding the same title. Nothing on the page changes,
        // which is exactly why the count has to exist.
        await movies.ImportExistingBatchAsync(
            "library-movies-4k",
            [
                new ExistingMovieImportRequest(
                    Title: "Arrival",
                    ReleaseYear: 2016,
                    WantedStatus: "covered",
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "Bluray-2160p",
                    TargetQuality: "Bluray-2160p",
                    QualityCutoffMet: true,
                    UnmonitorWhenCutoffMet: true,
                    FilePath: @"E:K\Arrival (2016)\Arrival.2016.2160p.mkv",
                    FileSizeBytes: 40L * 1024 * 1024 * 1024)
            ],
            CancellationToken.None);

        var two = CatalogueFilters.Parse(MediaKind.Movie, ["libraryCount:min:2"], out _);
        Assert.Single((await movies.ListPageAsync(new CatalogueQuery(Filters: two), CancellationToken.None)).Items);
    }

    private static Task ImportAsync(IMovieCatalogRepository movies, string filePath)
        => movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: "Arrival",
                    ReleaseYear: 2016,
                    WantedStatus: "covered",
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: "Bluray-1080p",
                    TargetQuality: "Bluray-1080p",
                    QualityCutoffMet: true,
                    UnmonitorWhenCutoffMet: true,
                    FilePath: filePath,
                    FileSizeBytes: 8L * 1024 * 1024 * 1024)
            ],
            CancellationToken.None);

    private static async Task<SqliteMovieCatalogRepository> CreateAsync(TestStorage storage)
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-29T00:00:00Z"));

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, clock),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, clock);
    }
}
