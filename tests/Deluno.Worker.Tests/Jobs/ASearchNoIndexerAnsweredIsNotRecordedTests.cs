using System.Text.Json;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #598: an episode search that no indexer answered, because each was busy
/// pacing other searches, runs again shortly and records nothing.
///
/// <para><b>Why this file exists.</b> On the soak rig a show's Search now
/// queued sixty episode searches at once. Breaking Bad S02E01's search asked
/// neither indexer, was recorded as searched, and waited an hour; its release
/// was never grabbed.</para>
/// </summary>
public sealed class ASearchNoIndexerAnsweredIsNotRecordedTests
{
    [Fact]
    public async Task An_episode_search_whose_indexers_were_all_busy_is_deferred_without_a_record()
    {
        var libraries = new Mock<ILibrariesRepository>();
        libraries.Setup(repository => repository.ListLibrariesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var series = new Mock<ISeriesCatalogRepository>();
        var pipeline = new Mock<IAcquisitionDecisionPipeline>();
        pipeline
            .Setup(p => p.PlanAsync(It.IsAny<AcquisitionDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcquisitionDecisionPlan(
                new MediaSearchPlan(null, [], "No indexer was asked for Breaking Bad: each was still busy with other searches.", MediaSearchReasons.IndexersBusy),
                Deluno.Quality.MediaPolicyCatalog.CurrentVersion,
                "checked",
                "No indexer was asked.",
                2,
                2,
                null,
                false,
                null,
                []));
        var handler = new EpisodeSearchJobHandler(
            libraries.Object,
            Mock.Of<IQualityRepository>(),
            series.Object,
            Mock.Of<IJobQueueRepository>(),
            pipeline.Object,
            Mock.Of<IDownloadClientGrabService>(),
            Mock.Of<IActivityFeedRepository>(),
            TimeProvider.System);
        var payload = JsonSerializer.Serialize(new
        {
            episodeId = "e-201",
            seriesId = "series-bb",
            libraryId = "library-tv",
            seasonNumber = 2,
            episodeNumber = 1,
            title = "Seven Thirty-Seven"
        });

        var deferred = await Assert.ThrowsAsync<JobDeferredException>(
            () => handler.HandleAsync(TestJobs.Create("episode.search", payloadJson: payload), CancellationToken.None));

        Assert.Equal(TimeSpan.FromMinutes(3), deferred.Delay);
        series.Verify(
            repository => repository.RecordSearchAttemptAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
