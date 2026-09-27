using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Jobs.Data;
using Deluno.Worker.Jobs;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #808: automatic search dispatches a grab exactly like a manual one, and
/// must explain the same plain-English tag-routing reason in Activity when a
/// title's tags actually sent it to this client — reusing the same
/// <see cref="IAcquisitionDecisionPipeline.ExplainDownloadClientRoutingAsync"/>
/// a manual/API grab already calls through <c>MediaGrabHandler</c>. Library,
/// episode, and season search handlers all funnel through
/// <see cref="SearchExecutionSupport.RecordAutomaticGrabRoutingAsync"/>
/// (season search inlines the same shape because it lives in a different
/// project), so this is the one place their shared behaviour is proven.
/// </summary>
public sealed class SearchExecutionSupportTagRoutingTests
{
    private static readonly DownloadClientGrabResult Sent = new("client-1", "Movie.2024.1080p", true, "sent", "Sent.");
    private static readonly DownloadClientGrabResult Failed = new("client-1", "Movie.2024.1080p", false, "failed", "Could not connect.");

    [Fact]
    public async Task A_tag_matched_client_is_explained_in_activity()
    {
        var activityFeed = new Mock<IActivityFeedRepository>();
        var pipeline = new Mock<IAcquisitionDecisionPipeline>();
        pipeline
            .Setup(p => p.ExplainDownloadClientRoutingAsync("client-1", It.Is<IReadOnlyList<string>>(tags => tags.Contains("anime")), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Sent to Anime Client because this title is tagged anime, which only Anime Client accepts.");

        await SearchExecutionSupport.RecordAutomaticGrabRoutingAsync(
            activityFeed.Object,
            pipeline.Object,
            Sent,
            "client-1",
            "Anime Client",
            ["anime"],
            "movie",
            "movie-1",
            CancellationToken.None);

        activityFeed.Verify(
            feed => feed.RecordActivityAsync(
                "movie.release.grabbed",
                It.Is<string>(message =>
                    message.Contains("Movie.2024.1080p") &&
                    message.Contains("Anime Client") &&
                    message.Contains("tagged anime")),
                null,
                null,
                "movie",
                "movie-1",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task An_untagged_client_writes_nothing_because_there_is_no_tag_routing_to_explain()
    {
        var activityFeed = new Mock<IActivityFeedRepository>();
        var pipeline = new Mock<IAcquisitionDecisionPipeline>();
        pipeline
            .Setup(p => p.ExplainDownloadClientRoutingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await SearchExecutionSupport.RecordAutomaticGrabRoutingAsync(
            activityFeed.Object,
            pipeline.Object,
            Sent,
            "client-1",
            "Ordinary Client",
            null,
            "series",
            "series-1",
            CancellationToken.None);

        activityFeed.Verify(
            feed => feed.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_failed_grab_writes_nothing_here_because_the_failure_paths_already_cover_activity()
    {
        var activityFeed = new Mock<IActivityFeedRepository>();
        var pipeline = new Mock<IAcquisitionDecisionPipeline>();

        await SearchExecutionSupport.RecordAutomaticGrabRoutingAsync(
            activityFeed.Object,
            pipeline.Object,
            Failed,
            "client-1",
            "Anime Client",
            ["anime"],
            "episode",
            "episode-1",
            CancellationToken.None);

        pipeline.Verify(p => p.ExplainDownloadClientRoutingAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        activityFeed.Verify(
            feed => feed.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
