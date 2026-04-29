using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

public sealed class MovieWantedStatePersistenceTests
{
    [Fact]
    public async Task EnsureWantedStateAsync_creates_and_updates_one_state_per_movie_and_library()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-04-29T02:00:00Z"));

        await new MoviesSchemaInitializer(
            storage.Factory,
            new SqliteDatabaseMigrator(storage.Factory, timeProvider),
            NullLogger<MoviesSchemaInitializer>.Instance).StartAsync(CancellationToken.None);

        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var movie = await repository.AddAsync(
            new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"),
            CancellationToken.None);

        await repository.EnsureWantedStateAsync(
            movie.Id,
            libraryId: "movies-main",
            wantedStatus: "missing",
            wantedReason: "No accepted file exists.",
            hasFile: false,
            currentQuality: null,
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        await repository.EnsureWantedStateAsync(
            movie.Id,
            libraryId: "movies-main",
            wantedStatus: "upgrade",
            wantedReason: "Existing file is below cutoff.",
            hasFile: true,
            currentQuality: "WEB 720p",
            targetQuality: "WEB 1080p",
            qualityCutoffMet: false,
            CancellationToken.None);

        var summary = await repository.GetWantedSummaryAsync(CancellationToken.None);
        var item = Assert.Single(summary.RecentItems);

        Assert.Equal(1, summary.TotalWanted);
        Assert.Equal(0, summary.MissingCount);
        Assert.Equal(1, summary.UpgradeCount);
        Assert.Equal(movie.Id, item.MovieId);
        Assert.Equal("movies-main", item.LibraryId);
        Assert.Equal("upgrade", item.WantedStatus);
        Assert.True(item.HasFile);
        Assert.Equal("WEB 720p", item.CurrentQuality);
        Assert.Equal("WEB 1080p", item.TargetQuality);
        Assert.NotNull(item.MissingSinceUtc);
    }
}
