using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

public sealed class QualityRecalculateJobHandlerTests
{
    private const string ValidPayload =
        """
        {"libraryId":"lib-1","libraryName":"Movies","mediaType":"movies","cutoffQuality":"WEB 1080p","upgradeUntilCutoff":true,"upgradeUnknownItems":false}
        """;

    [Fact]
    public async Task MoviesQualityRecalculateJobHandler_missing_payload_skips_without_touching_the_repository()
    {
        var movieCatalogRepository = new Mock<IMovieCatalogRepository>(MockBehavior.Strict);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        var handler = new MoviesQualityRecalculateJobHandler(movieCatalogRepository.Object, activityFeedRepository.Object);

        var message = await handler.HandleAsync(TestJobs.Create("movies.quality.recalculate", payloadJson: "not json"), CancellationToken.None);

        Assert.Equal("Finished refreshing movie quality decisions.", message);
    }

    [Fact]
    public async Task MoviesQualityRecalculateJobHandler_well_formed_payload_recalculates_and_records_activity()
    {
        var movieCatalogRepository = new Mock<IMovieCatalogRepository>();
        movieCatalogRepository
            .Setup(repository => repository.ReevaluateLibraryWantedStateAsync("lib-1", "WEB 1080p", true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);
        var activityFeedRepository = new Mock<IActivityFeedRepository>();
        var handler = new MoviesQualityRecalculateJobHandler(movieCatalogRepository.Object, activityFeedRepository.Object);

        var message = await handler.HandleAsync(TestJobs.Create("movies.quality.recalculate", payloadJson: ValidPayload), CancellationToken.None);

        Assert.Equal("Finished refreshing quality decisions for Movies.", message);
        activityFeedRepository.Verify(
            repository => repository.RecordActivityAsync(
                "library.quality.recalculated",
                It.Is<string>(message => message.Contains("4 movie records")),
                null,
                It.IsAny<string>(),
                "library",
                "lib-1",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SeriesQualityRecalculateJobHandler_missing_payload_skips_without_touching_the_repository()
    {
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);
        var handler = new SeriesQualityRecalculateJobHandler(seriesCatalogRepository.Object, activityFeedRepository.Object);

        var message = await handler.HandleAsync(TestJobs.Create("series.quality.recalculate", payloadJson: "not json"), CancellationToken.None);

        Assert.Equal("Finished refreshing TV quality decisions.", message);
    }

    [Fact]
    public async Task SeriesQualityRecalculateJobHandler_well_formed_payload_recalculates_and_records_activity()
    {
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>();
        seriesCatalogRepository
            .Setup(repository => repository.ReevaluateLibraryWantedStateAsync("lib-1", "WEB 1080p", true, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        var activityFeedRepository = new Mock<IActivityFeedRepository>();
        var handler = new SeriesQualityRecalculateJobHandler(seriesCatalogRepository.Object, activityFeedRepository.Object);

        var message = await handler.HandleAsync(TestJobs.Create("series.quality.recalculate", payloadJson: ValidPayload), CancellationToken.None);

        Assert.Equal("Finished refreshing quality decisions for Movies.", message);
        activityFeedRepository.Verify(
            repository => repository.RecordActivityAsync(
                "library.quality.recalculated",
                It.Is<string>(message => message.Contains("1 TV show record")),
                null,
                It.IsAny<string>(),
                "library",
                "lib-1",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
