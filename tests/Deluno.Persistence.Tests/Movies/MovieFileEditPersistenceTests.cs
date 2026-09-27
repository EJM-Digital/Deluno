using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #782: the Files tab's per-file edit action (quality override on the
/// tracked file) and delete action (clears the record once the file is gone
/// from disk). These pin the two repository methods behind them.
/// </summary>
public sealed class MovieFileEditPersistenceTests
{
    private static async Task<(SqliteMovieCatalogRepository Repository, MovieListItem Movie)> CreateMovieWithFileAsync(
        TestStorage storage, FixedTimeProvider timeProvider, string filePath)
    {
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        await repository.EnsureWantedStateAsync(
            movie.Id, "movies-main", "covered", "Imported.", hasFile: true,
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p", qualityCutoffMet: true, CancellationToken.None);
        await repository.ImportExistingAsync(
            "movies-main", movie.Title, 2024, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: filePath, fileSizeBytes: 42, CancellationToken.None, movieId: movie.Id);

        return (repository, movie);
    }

    [Fact]
    public async Task UpdateCurrentQualityOverrideAsync_corrects_the_stored_quality()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var (repository, movie) = await CreateMovieWithFileAsync(storage, timeProvider, @"C:\movies-main\Dune Part Two (2024)\Dune.mkv");

        Assert.True(await repository.UpdateCurrentQualityOverrideAsync(movie.Id, "Remux 2160p", CancellationToken.None));

        var after = await repository.GetByIdAsync(movie.Id, CancellationToken.None);
        Assert.Equal("Remux 2160p", after!.CurrentQuality);
    }

    [Fact]
    public async Task UpdateCurrentQualityOverrideAsync_does_nothing_for_a_movie_with_no_file()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);
        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Three", 2026, "tt00000001"), CancellationToken.None);

        Assert.False(await repository.UpdateCurrentQualityOverrideAsync(movie.Id, "Remux 2160p", CancellationToken.None));
    }

    [Fact]
    public async Task ClearFileAsync_clears_the_record_once_the_path_matches()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var filePath = @"C:\movies-main\Dune Part Two (2024)\Dune.mkv";
        var (repository, movie) = await CreateMovieWithFileAsync(storage, timeProvider, filePath);

        Assert.True(await repository.ClearFileAsync(movie.Id, filePath, CancellationToken.None));

        var after = await repository.GetByIdAsync(movie.Id, CancellationToken.None);
        Assert.False(after!.HasFile);
        Assert.Null(after.FilePath);
        Assert.Null(after.FileSizeBytes);
        Assert.Null(after.CurrentQuality);
    }

    [Fact]
    public async Task ClearFileAsync_refuses_a_stale_path()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        var filePath = @"C:\movies-main\Dune Part Two (2024)\Dune.mkv";
        var (repository, movie) = await CreateMovieWithFileAsync(storage, timeProvider, filePath);

        Assert.False(await repository.ClearFileAsync(movie.Id, @"C:\wrong\path.mkv", CancellationToken.None));

        var after = await repository.GetByIdAsync(movie.Id, CancellationToken.None);
        Assert.Equal(filePath, after!.FilePath);
    }
}
