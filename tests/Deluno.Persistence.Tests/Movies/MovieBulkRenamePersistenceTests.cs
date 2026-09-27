using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Infrastructure.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Movies;

/// <summary>
/// #788: the movie rename-apply endpoint renames a title's folder on disk
/// itself, then records the new path through
/// <see cref="SqliteMovieCatalogRepository.UpdateFileLocationAsync"/>. This
/// pins that persistence contract, including the safety check that it only
/// touches the row whose path still matches what the caller last saw.
/// </summary>
public sealed class MovieBulkRenamePersistenceTests
{
    [Fact]
    public async Task UpdateFileLocationAsync_records_the_new_path_when_it_matches_the_previous_one()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        var oldPath = @"C:\movies-main\Dune Part Two (2024)\Dune.mkv";
        await repository.EnsureWantedStateAsync(
            movie.Id, "movies-main", "covered", "Imported.", hasFile: true,
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p", qualityCutoffMet: true, CancellationToken.None);
        await repository.ImportExistingAsync(
            "movies-main", movie.Title, 2024, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: oldPath, fileSizeBytes: 1, CancellationToken.None, movieId: movie.Id);

        var newPath = @"C:\movies-main\Dune Part Two (2024) [renamed]\Dune.mkv";
        Assert.True(await repository.UpdateFileLocationAsync(movie.Id, oldPath, newPath, CancellationToken.None));

        var after = await repository.GetByIdAsync(movie.Id, CancellationToken.None);
        Assert.Equal(newPath, after!.FilePath);
    }

    [Fact]
    public async Task UpdateFileLocationAsync_refuses_a_row_whose_stored_path_no_longer_matches()
    {
        using var storage = TestStorage.Create();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
        await new MoviesSchemaInitializer(storage.Factory, new SqliteDatabaseMigrator(storage.Factory, timeProvider), NullLogger<MoviesSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        var repository = new SqliteMovieCatalogRepository(storage.Factory, timeProvider);

        var movie = await repository.AddAsync(new CreateMovieRequest("Dune Part Two", 2024, "tt15239678"), CancellationToken.None);
        var actualPath = @"C:\movies-main\Dune Part Two (2024)\Dune.mkv";
        await repository.EnsureWantedStateAsync(
            movie.Id, "movies-main", "covered", "Imported.", hasFile: true,
            currentQuality: "WEB 1080p", targetQuality: "WEB 1080p", qualityCutoffMet: true, CancellationToken.None);
        await repository.ImportExistingAsync(
            "movies-main", movie.Title, 2024, "covered", "Imported.", "WEB 1080p", "WEB 1080p", true,
            filePath: actualPath, fileSizeBytes: 1, CancellationToken.None, movieId: movie.Id);

        var updated = await repository.UpdateFileLocationAsync(
            movie.Id, "C:\\wrong\\path.mkv", @"C:\movies-main\Dune Part Two (2024) [renamed]\Dune.mkv", CancellationToken.None);

        Assert.False(updated);
        var after = await repository.GetByIdAsync(movie.Id, CancellationToken.None);
        Assert.Equal(actualPath, after!.FilePath);
    }
}
