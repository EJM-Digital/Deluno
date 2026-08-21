using Deluno.Infrastructure.Storage.Migrations;
using Deluno.Contracts;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

public sealed class MediaStateRepositoryTests
{
    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Shared_store_preserves_wanted_state_behaviour_for_both_media_kinds(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var now = new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedTimeProvider(now);
        await InitializeSchemaAsync(storage, timeProvider, kind);

        var mediaId = await AddMediaAsync(storage, timeProvider, kind);
        var repository = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        await repository.EnsureWantedStateAsync(
            kind,
            mediaId,
            "main",
            "missing",
            "No accepted file exists.",
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        var summary = await repository.GetWantedSummaryAsync(kind, CancellationToken.None);
        var item = Assert.Single(summary.RecentItems);
        Assert.Equal(1, summary.TotalWanted);
        Assert.Equal(mediaId, item.Id);
        Assert.Equal("missing", item.WantedStatus);

        Assert.Single(await repository.ListEligibleWantedAsync(
            kind,
            "main",
            take: 10,
            now,
            ignoreRetryWindow: false,
            CancellationToken.None));

        var deferredUntil = now.AddHours(4);
        Assert.True(await repository.DeferWantedSearchAsync(
            kind,
            mediaId,
            "main",
            deferredUntil,
            CancellationToken.None));
        Assert.Empty(await repository.ListEligibleWantedAsync(
            kind,
            "main",
            take: 10,
            now,
            ignoreRetryWindow: false,
            CancellationToken.None));

        Assert.True(await repository.SkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));
        Assert.True(await repository.ConsumeSkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));
        Assert.False(await repository.ConsumeSkipNextWantedSearchAsync(
            kind,
            mediaId,
            "main",
            CancellationToken.None));

        var metrics = await repository.GetDailyMetricsAsync(
            kind,
            DateOnly.FromDateTime(now.UtcDateTime),
            DateOnly.FromDateTime(now.UtcDateTime),
            CancellationToken.None);
        Assert.Equal(1, metrics.TitlesAdded[now.ToString("yyyy-MM-dd")]);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Shared_store_reads_import_recovery_summary_for_both_media_kinds(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
            await repository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(
                    "Movie",
                    "quality",
                    "Below cutoff",
                    "Review quality"),
                CancellationToken.None);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider);
            await repository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(
                    "Series",
                    "unmatched",
                    "No match",
                    "Review the title"),
                CancellationToken.None);
        }

        var summary = await new SqliteMediaStateRepository(storage.Factory, timeProvider)
            .GetImportRecoverySummaryAsync(kind, CancellationToken.None);

        Assert.Equal(1, summary.OpenCount);
        Assert.Single(summary.RecentCases);
        Assert.Equal(kind == MediaKind.Movie ? 1 : 0, summary.QualityCount);
        Assert.Equal(kind == MediaKind.Series ? 1 : 0, summary.UnmatchedCount);
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Engine_repositories_route_metadata_updates_through_shared_store(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);
        var shared = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, shared);
            var movie = await repository.AddAsync(
                new CreateMovieRequest("Before", 2026, "tt0000003"),
                CancellationToken.None);

            var updated = await repository.UpdateMetadataAsync(
                movie.Id,
                "tmdb",
                "123",
                "After",
                "Updated overview",
                "poster.jpg",
                "backdrop.jpg",
                8.5,
                "Drama",
                "https://example.test/movie",
                "tt0000004",
                "{\"ratings\":[]}",
                CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("Before", updated.Title);
            Assert.Equal("Updated overview", updated.Overview);
            Assert.Equal("tt0000004", updated.ImdbId);
            Assert.Equal("tmdb", updated.MetadataProvider);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, shared);
            var series = await repository.AddAsync(
                new CreateSeriesRequest("Before", 2026, "tt0000005"),
                CancellationToken.None);

            var updated = await repository.UpdateMetadataAsync(
                series.Id,
                "tmdb",
                "456",
                "After",
                "Updated overview",
                "poster.jpg",
                "backdrop.jpg",
                8.5,
                "Drama",
                "https://example.test/series",
                "tt0000006",
                "{\"ratings\":[]}",
                CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("Before", updated.Title);
            Assert.Equal("Updated overview", updated.Overview);
            Assert.Equal("tt0000006", updated.ImdbId);
            Assert.Equal("tmdb", updated.MetadataProvider);
        }
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Engine_repositories_route_add_through_shared_store(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);
        var shared = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, shared);
            var created = await repository.AddAsync(
                new CreateMovieRequest(
                    "  Created movie  ",
                    2026,
                    "tt0000010",
                    Monitored: false,
                    MetadataProvider: "tmdb",
                    MetadataProviderId: "101",
                    OriginalTitle: "Created film",
                    Overview: "Created overview",
                    Rating: 8.5,
                    Genres: "Drama",
                    MetadataJson: "{\"source\":\"test\"}"),
                CancellationToken.None);
            var duplicate = await repository.AddAsync(
                new CreateMovieRequest("created movie", 2026, "tt0000099"),
                CancellationToken.None);

            Assert.Equal(created.Id, duplicate.Id);
            Assert.Equal("Created movie", created.Title);
            Assert.False(created.Monitored);
            Assert.Equal("tmdb", created.MetadataProvider);
            Assert.Equal("101", created.MetadataProviderId);
            Assert.Equal("Created overview", created.Overview);
            Assert.Equal(8.5, created.Rating);

            Assert.True(await repository.UpdateReleaseDatesAsync(
                created.Id,
                new DateOnly(2026, 8, 22),
                new DateOnly(2026, 8, 23),
                null,
                CancellationToken.None));

            var reloaded = await repository.GetByIdAsync(created.Id, CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal(new DateOnly(2026, 8, 22), reloaded.InCinemasDate);
            Assert.Equal(new DateOnly(2026, 8, 23), reloaded.DigitalReleaseDate);
            Assert.Null(reloaded.PhysicalReleaseDate);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, shared);
            var created = await repository.AddAsync(
                new CreateSeriesRequest(
                    "  Created series  ",
                    2026,
                    "tt0000011",
                    Monitored: false,
                    MetadataProvider: "tmdb",
                    MetadataProviderId: "102",
                    OriginalTitle: "Created show",
                    Overview: "Created overview",
                    Rating: 8.5,
                    Genres: "Drama",
                    MetadataJson: "{\"source\":\"test\"}"),
                CancellationToken.None);
            var duplicate = await repository.AddAsync(
                new CreateSeriesRequest("created series", 2026, "tt0000099"),
                CancellationToken.None);

            Assert.Equal(created.Id, duplicate.Id);
            Assert.Equal("Created series", created.Title);
            Assert.False(created.Monitored);
            Assert.Equal("tmdb", created.MetadataProvider);
            Assert.Equal("102", created.MetadataProviderId);
            Assert.Equal("Created overview", created.Overview);
            Assert.Equal(8.5, created.Rating);
        }
    }

    [Theory]
    [InlineData(MediaKind.Movie)]
    [InlineData(MediaKind.Series)]
    public async Task Engine_repositories_route_existing_import_through_shared_store(MediaKind kind)
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero));
        await InitializeSchemaAsync(storage, timeProvider, kind);
        var shared = new SqliteMediaStateRepository(storage.Factory, timeProvider);

        if (kind == MediaKind.Movie)
        {
            var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider, shared);
            var created = await repository.ImportExistingBatchAsync(
                "library-movies",
                [
                    new ExistingMovieImportRequest(
                        "Imported movie",
                        2016,
                        "covered",
                        "Imported from disk.",
                        "Bluray-1080p",
                        "Bluray-1080p",
                        QualityCutoffMet: true,
                        UnmonitorWhenCutoffMet: false,
                        @"D:\Media\Imported movie (2016)\Imported.movie.2016.1080p.BluRay.x264-GROUP.mkv",
                        1024)
                ],
                CancellationToken.None);

            Assert.Equal(1, created);
            var item = Assert.Single(
                (await repository.ListPageAsync(new CatalogueQuery(), CancellationToken.None)).Items);
            Assert.Equal("Imported movie", item.Title);
            Assert.True(item.HasFile);
            Assert.Equal("H.264", item.VideoCodec);
            Assert.Equal("GROUP", item.ReleaseGroup);
        }
        else
        {
            var repository = new SqliteSeriesCatalogRepository(storage.Factory, timeProvider, shared);
            var created = await repository.ImportExistingBatchAsync(
                "library-series",
                [
                    new ExistingSeriesImportRequest(
                        "Imported series",
                        2019,
                        "covered",
                        "Imported from disk.",
                        "WEB-1080p",
                        "WEB-1080p",
                        QualityCutoffMet: false,
                        UnmonitorWhenCutoffMet: false,
                        @"D:\Media\Imported series (2019)\Imported.series.S01E01.1080p.WEB-DL.mkv",
                        2048,
                        [
                            new ImportedEpisodeItem(1, 1, true, @"D:\Media\Imported series\S01E01.mkv", 1024),
                            new ImportedEpisodeItem(1, 2, true, @"D:\Media\Imported series\S01E02.mkv", 1024)
                        ])
                ],
                CancellationToken.None);

            Assert.Equal(1, created);
            var series = Assert.Single(await repository.ListAsync(CancellationToken.None));
            var detail = await repository.GetInventoryDetailAsync(series.Id, CancellationToken.None);
            Assert.NotNull(detail);
            Assert.Equal(2, detail.EpisodeCount);
            Assert.Equal(2, detail.ImportedEpisodeCount);
        }
    }

    private static async Task InitializeSchemaAsync(
        TestStorage storage,
        TimeProvider timeProvider,
        MediaKind kind)
    {
        var migrator = new SqliteDatabaseMigrator(storage.Factory, timeProvider);
        if (kind == MediaKind.Movie)
        {
            await new MoviesSchemaInitializer(
                storage.Factory,
                migrator,
                NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        }
        else
        {
            await new SeriesSchemaInitializer(
                storage.Factory,
                migrator,
                NullLogger<SeriesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);
        }
    }

    private static async Task<string> AddMediaAsync(
        TestStorage storage,
        TimeProvider timeProvider,
        MediaKind kind)
    {
        if (kind == MediaKind.Movie)
        {
            var movie = await new SqliteMovieCatalogRepository(storage.Factory, timeProvider).AddAsync(
                new CreateMovieRequest("Shared movie", 2026, "tt0000001"),
                CancellationToken.None);
            return movie.Id;
        }

        var series = await new SqliteSeriesCatalogRepository(storage.Factory, timeProvider).AddAsync(
            new CreateSeriesRequest("Shared series", 2026, "tt0000002"),
            CancellationToken.None);
        return series.Id;
    }
}
