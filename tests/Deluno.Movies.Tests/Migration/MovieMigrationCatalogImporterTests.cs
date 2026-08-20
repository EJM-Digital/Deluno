using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Migration;
using Deluno.Platform.Migration;
using Moq;

namespace Deluno.Movies.Tests.Migration;

public sealed class MovieMigrationCatalogImporterTests
{
    [Fact]
    public async Task ImportAsync_checks_each_incoming_title_without_reading_the_whole_catalogue()
    {
        var repository = new Mock<IMovieCatalogRepository>(MockBehavior.Strict);
        repository.Setup(item => item.FindExistingIdAsync(
                "Dune Part Two", 2024, "tt15239678", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        repository.Setup(item => item.AddAsync(It.IsAny<CreateMovieRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Movie("movie-1", "Dune Part Two"));
        repository.Setup(item => item.EnsureWantedStateAsync(
                "movie-1", "movies-main", "missing", It.IsAny<string>(), false, null, null, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await new MovieMigrationCatalogImporter(repository.Object).ImportAsync(
            Request("movies", "Dune Part Two", 2024, "tt15239678"),
            CancellationToken.None);

        Assert.Equal("created", Assert.Single(result.Applied).Result);
        repository.Verify(item => item.FindExistingIdAsync(
            "Dune Part Two", 2024, "tt15239678", null, null, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.ListAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MigrationCatalogImportRequest Request(string mediaType, string title, int year, string imdbId)
        => new(
            "radarr",
            "Radarr",
            [new MigrationCatalogTitle(mediaType, title, year, imdbId, null, null, true, false, "/media/movies")],
            [new MigrationCatalogLibrary("movies-main", mediaType, "/media/movies", "Movies")]);

    private static MovieListItem Movie(string id, string title)
        => new(id, title, 2024, "tt15239678", true, false, null, null, null, null, null, null, null, [], null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
