using Deluno.Platform.Migration;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Migration;
using Moq;

namespace Deluno.Series.Tests.Migration;

public sealed class SeriesMigrationCatalogImporterTests
{
    [Fact]
    public async Task ImportAsync_checks_each_incoming_title_without_reading_the_whole_catalogue()
    {
        var repository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        repository.Setup(item => item.FindExistingIdAsync(
                "Severance", 2022, "tt11280740", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        repository.Setup(item => item.AddAsync(It.IsAny<CreateSeriesRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Series("series-1", "Severance"));
        repository.Setup(item => item.EnsureWantedStateAsync(
                "series-1", "tv-main", "missing", It.IsAny<string>(), false, null, null, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await new SeriesMigrationCatalogImporter(repository.Object).ImportAsync(
            Request("tv", "Severance", 2022, "tt11280740"),
            CancellationToken.None);

        Assert.Equal("created", Assert.Single(result.Applied).Result);
        repository.Verify(item => item.FindExistingIdAsync(
            "Severance", 2022, "tt11280740", null, null, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(item => item.ListAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static MigrationCatalogImportRequest Request(string mediaType, string title, int year, string imdbId)
        => new(
            "sonarr",
            "Sonarr",
            [new MigrationCatalogTitle(mediaType, title, year, imdbId, null, null, true, false, "/media/tv")],
            [new MigrationCatalogLibrary("tv-main", mediaType, "/media/tv", "TV")]);

    private static SeriesListItem Series(string id, string title)
        => new(id, title, 2022, "tt11280740", true, false, null, null, null, null, null, null, null, [], null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
