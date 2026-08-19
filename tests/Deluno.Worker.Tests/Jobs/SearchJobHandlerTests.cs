using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

public sealed class SearchJobHandlerTests
{
    [Fact]
    public async Task EpisodeSearchJobHandler_missing_episode_id_skips_without_touching_any_repository()
    {
        var librariesRepository = new Mock<ILibrariesRepository>(MockBehavior.Strict);
        var qualityRepository = new Mock<IQualityRepository>(MockBehavior.Strict);
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);
        var acquisitionPipeline = new Mock<IAcquisitionDecisionPipeline>(MockBehavior.Strict);
        var downloadClientGrabService = new Mock<IDownloadClientGrabService>(MockBehavior.Strict);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);

        var handler = new EpisodeSearchJobHandler(
            librariesRepository.Object,
            qualityRepository.Object,
            seriesCatalogRepository.Object,
            jobQueueRepository.Object,
            acquisitionPipeline.Object,
            downloadClientGrabService.Object,
            activityFeedRepository.Object,
            TimeProvider.System);

        var message = await handler.HandleAsync(TestJobs.Create("episode.search", payloadJson: "not json"), CancellationToken.None);

        Assert.Equal("Finished searching for episode.", message);
    }

    [Fact]
    public async Task LibrarySearchJobHandler_missing_library_name_skips_without_touching_any_repository()
    {
        var librariesRepository = new Mock<ILibrariesRepository>(MockBehavior.Strict);
        var qualityRepository = new Mock<IQualityRepository>(MockBehavior.Strict);
        var jobQueueRepository = new Mock<IJobQueueRepository>(MockBehavior.Strict);
        var movieCatalogRepository = new Mock<Deluno.Movies.Data.IMovieCatalogRepository>(MockBehavior.Strict);
        var seriesCatalogRepository = new Mock<ISeriesCatalogRepository>(MockBehavior.Strict);
        var acquisitionPipeline = new Mock<IAcquisitionDecisionPipeline>(MockBehavior.Strict);
        var downloadClientGrabService = new Mock<IDownloadClientGrabService>(MockBehavior.Strict);
        var activityFeedRepository = new Mock<IActivityFeedRepository>(MockBehavior.Strict);

        var handler = new LibrarySearchJobHandler(
            librariesRepository.Object,
            qualityRepository.Object,
            jobQueueRepository.Object,
            movieCatalogRepository.Object,
            seriesCatalogRepository.Object,
            acquisitionPipeline.Object,
            downloadClientGrabService.Object,
            activityFeedRepository.Object,
            TimeProvider.System);

        var message = await handler.HandleAsync(TestJobs.Create("library.search", payloadJson: "not json"), CancellationToken.None);

        Assert.Equal("Finished checking a library.", message);
    }
}
