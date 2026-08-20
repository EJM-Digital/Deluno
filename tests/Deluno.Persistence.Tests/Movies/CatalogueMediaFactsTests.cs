using Deluno.Contracts;
using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// The fields the library list displays actually arriving in the list.
///
/// Every one of these was a control the interface offered and the API never
/// answered: a size column read from a metadata blob, a codec sort with nothing
/// behind it, a total that always said zero. These tests are the record that
/// they now come from somewhere real.
/// </summary>
public sealed class CatalogueMediaFactsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-20T00:00:00Z");

    [Fact]
    public async Task What_the_file_name_says_is_stored_with_the_file_and_returned_with_the_list()
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
                    UnmonitorWhenCutoffMet: false,
                    FilePath: @"D:\Media\Arrival (2016)\Arrival.2016.1080p.BluRay.x264.DTS-HD.MA.5.1-SPARKS.mkv",
                    FileSizeBytes: 8L * 1024 * 1024 * 1024)
            ],
            CancellationToken.None);

        var item = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        Assert.Equal("H.264", item.VideoCodec);
        Assert.Equal("DTS-HD", item.AudioCodec);
        Assert.Equal("5.1", item.AudioChannels);
        Assert.Equal("SPARKS", item.ReleaseGroup);
        Assert.Equal(8L * 1024 * 1024 * 1024, item.FileSizeBytes);
        Assert.Equal("Bluray-1080p", item.CurrentQuality);
        Assert.EndsWith("SPARKS.mkv", item.FilePath);
    }

    [Fact]
    public async Task Replacing_the_file_replaces_its_facts_rather_than_keeping_the_old_ones()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, @"D:\Media\Arrival.2016.720p.HDTV.XviD.AAC2.0-OLD.mkv", 2L * 1024 * 1024 * 1024);
        await ImportAsync(movies, @"D:\Media\Arrival.2016.2160p.BluRay.x265.TrueHD.7.1-NEW.mkv", 40L * 1024 * 1024 * 1024);

        var item = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        // An upgrade is a different file. Carrying the old codec forward would
        // describe a file that is no longer there.
        Assert.Equal("HEVC", item.VideoCodec);
        Assert.Equal("TrueHD", item.AudioCodec);
        Assert.Equal("7.1", item.AudioChannels);
        Assert.Equal("NEW", item.ReleaseGroup);
    }

    [Fact]
    public async Task Runtime_popularity_and_votes_come_from_the_provider_and_survive_a_refresh_that_omits_them()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        var added = await movies.AddAsync(new CreateMovieRequest("Arrival", 2016, null), CancellationToken.None);

        await movies.UpdateMetadataAsync(
            added.Id, "tmdb", "329865", "Arrival", "A linguist is recruited.", null, null, 7.6,
            "Science Fiction", null, "tt2543164", "{}", CancellationToken.None,
            runtimeMinutes: 116, popularity: 42.5, voteCount: 18_000);

        var item = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        Assert.Equal(116, item.RuntimeMinutes);
        Assert.Equal(42.5, item.Popularity);
        Assert.Equal(18_000, item.VoteCount);

        // A later refresh from a provider that does not report runtime must not
        // blank the runtime an earlier one did report.
        await movies.UpdateMetadataAsync(
            added.Id, "tmdb", "329865", "Arrival", "A linguist is recruited.", null, null, 7.7,
            "Science Fiction", null, "tt2543164", "{}", CancellationToken.None);

        var refreshed = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        Assert.Equal(116, refreshed.RuntimeMinutes);
        Assert.Equal(42.5, refreshed.Popularity);
    }

    [Fact]
    public async Task Bitrate_is_derived_from_size_and_runtime_and_is_absent_without_both()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);

        await ImportAsync(movies, @"D:\Media\Arrival.2016.1080p.BluRay.x264-GROUP.mkv", 6L * 1024 * 1024 * 1024);

        // No runtime yet, so no bitrate. A guess would be worse than nothing.
        var withoutRuntime = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
        Assert.Null(withoutRuntime.ApproximateBitrateMbps);

        await movies.UpdateMetadataAsync(
            withoutRuntime.Id, "tmdb", "329865", null, null, null, null, null, null, null, null, "{}",
            CancellationToken.None, runtimeMinutes: 116);

        var withRuntime = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        // 6 GiB over 116 minutes is about 7.4 Mbps.
        Assert.NotNull(withRuntime.ApproximateBitrateMbps);
        Assert.InRange(withRuntime.ApproximateBitrateMbps.Value, 7.3, 7.5);
    }

    [Fact]
    public async Task A_title_with_no_file_and_no_metadata_reports_nothing_rather_than_zero()
    {
        using var storage = TestStorage.Create();
        var movies = await CreateAsync(storage);
        await movies.AddAsync(new CreateMovieRequest("Nothing Known", 1994, null), CancellationToken.None);

        var item = Assert.Single(
            (await movies.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);

        // Zero is a claim. Null is the truth, and the difference decides whether
        // a "total size" reads 0.0 TB or says it does not know.
        Assert.Null(item.FileSizeBytes);
        Assert.Null(item.VideoCodec);
        Assert.Null(item.RuntimeMinutes);
        Assert.Null(item.Popularity);
        Assert.Null(item.ApproximateBitrateMbps);
        Assert.Null(item.FilePath);
    }

    private static Task ImportAsync(IMovieCatalogRepository movies, string filePath, long sizeBytes)
        => movies.ImportExistingBatchAsync(
            "library-movies",
            [
                new ExistingMovieImportRequest(
                    Title: "Arrival",
                    ReleaseYear: 2016,
                    WantedStatus: "covered",
                    WantedReason: "Imported from your existing library.",
                    CurrentQuality: null,
                    TargetQuality: null,
                    QualityCutoffMet: false,
                    UnmonitorWhenCutoffMet: false,
                    FilePath: filePath,
                    FileSizeBytes: sizeBytes)
            ],
            CancellationToken.None);

    private static async Task<SqliteMovieCatalogRepository> CreateAsync(TestStorage storage)
    {
        var timeProvider = new FixedTimeProvider(Now);
        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        return new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
    }
}
