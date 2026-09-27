using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #784: the movie detail page's new Edit surface needs to show the quality
/// profile a title is currently assigned, not just blindly overwrite it.
/// <see cref="SqliteMovieCatalogRepository.UpdateQualityProfileAsync"/> had
/// been write-only - nothing read <c>movie_entries.quality_profile_id</c> back
/// - which <see cref="SqliteMovieCatalogRepository.GetQualityProfileIdAsync"/>
/// now closes.
/// </summary>
public sealed class MovieQualityProfileOverridePersistenceTests
{
    [Fact]
    public async Task GetQualityProfileIdAsync_returns_null_before_any_profile_is_set()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);

        Assert.Null(await repository.GetQualityProfileIdAsync(movie.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetQualityProfileIdAsync_reads_back_what_UpdateQualityProfileAsync_stored()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        Assert.True(await repository.UpdateQualityProfileAsync(movie.Id, "profile-4k", CancellationToken.None));

        Assert.Equal("profile-4k", await repository.GetQualityProfileIdAsync(movie.Id, CancellationToken.None));
    }
}
