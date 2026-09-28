using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Worker.Jobs;
using Deluno.Worker.Tests.Support;
using Moq;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #875, cleanup sheet B1: <see cref="DownloadClientRetryGrabJobHandler"/> is
/// the retry itself — it must resend the exact same release to the exact
/// same client, keep telling the user while the problem persists, never
/// blocklist a connectivity failure, and eventually stop retrying on its own
/// once a client has had a fair, bounded number of chances to come back.
/// </summary>
public sealed class DownloadClientRetryGrabJobHandlerTests
{
    private const string Release = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
    private const string DownloadUrl = "http://fixture.invalid/release.nzb";
    private const string CandidateHash = "abc123hash-nzo-guid";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-22T00:00:00Z");

    [Fact]
    public async Task A_successful_retry_resends_the_same_release_and_records_it()
    {
        var world = new World(succeeds: true);

        var result = await world.Handler().HandleAsync(world.Job(attemptNumber: 1), CancellationToken.None);

        world.Grabs.Verify(
            grab => grab.GrabAsync(
                "client-1",
                It.Is<DownloadClientGrabRequest>(request => request.ReleaseName == Release && request.DownloadUrl == DownloadUrl),
                It.IsAny<CancellationToken>()),
            Times.Once);
        world.ActivityFeed.Verify(
            activity => activity.RecordActivityAsync(
                "download.client.connectivity-retry-succeeded",
                It.Is<string>(message => message.Contains(Release)),
                null, null, "movie", "movie-1", It.IsAny<CancellationToken>()),
            Times.Once);
        world.Scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(Release, result);
    }

    [Fact]
    public async Task A_repeated_connectivity_failure_schedules_another_attempt_and_never_blocklists()
    {
        var world = new World(succeeds: false, failureKind: IntegrationFailureKind.Unavailable);

        await world.Handler().HandleAsync(world.Job(attemptNumber: 2), CancellationToken.None);

        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request => request.PayloadJson != null && request.PayloadJson.Contains("\"AttemptNumber\":3")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        world.BlockedReleases.Verify(repository => repository.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
        world.ActivityFeed.Verify(
            activity => activity.RecordActivityAsync(
                "download.client.connectivity-retry-scheduled",
                It.IsAny<string>(),
                null, null, "movie", "movie-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task The_final_attempt_stops_retrying_but_still_tells_the_user()
    {
        var world = new World(succeeds: false, failureKind: IntegrationFailureKind.Unavailable);

        await world.Handler().HandleAsync(world.Job(attemptNumber: SearchExecutionSupport.MaxConnectivityRetryAttempts), CancellationToken.None);

        world.Scheduler.Verify(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        world.ActivityFeed.Verify(
            activity => activity.RecordActivityAsync(
                "download.client.connectivity-retry-exhausted",
                It.Is<string>(message => message.Contains(Release)),
                null, null, "movie", "movie-1", It.IsAny<CancellationToken>()),
            Times.Once);
        world.BlockedReleases.Verify(repository => repository.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// If the client's answer changes to a dead release on retry, the
    /// dead-release path (#876) still applies: blocklisted, and searched for
    /// a replacement straight away rather than resent or left to the next
    /// ordinary search cycle.
    /// </summary>
    [Fact]
    public async Task A_dead_release_answer_on_retry_is_blocklisted_instead_of_retried_again()
    {
        var world = new World(succeeds: false, failureKind: null, status: DownloadQueueStatuses.DeadRelease);

        await world.Handler().HandleAsync(world.Job(attemptNumber: 1), CancellationToken.None);

        world.BlockedReleases.Verify(repository => repository.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()), Times.Once);
        world.BlockedReleases.Verify(
            repository => repository.BlockAsync(
                It.Is<BlockedRelease>(release => release.TorrentHashOrItemId == CandidateHash),
                It.IsAny<CancellationToken>()),
            Times.Once);
        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request => request.PayloadJson != null && request.PayloadJson.Contains("\"AttemptNumber\"")),
                It.IsAny<CancellationToken>()),
            Times.Never);
        world.Scheduler.Verify(
            scheduler => scheduler.EnqueueAsync(
                It.Is<EnqueueJobRequest>(request => request.JobType == "library.search.movies" && request.PayloadJson != null && request.PayloadJson.Contains("\"triggeredBy\":\"health-replacement\"")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private sealed class World
    {
        public Mock<IDownloadClientGrabService> Grabs { get; } = new();
        public Mock<IJobQueueRepository> Jobs { get; } = new();
        public Mock<IActivityFeedRepository> ActivityFeed { get; } = new();
        public Mock<IJobScheduler> Scheduler { get; } = new();
        public Mock<IBlockedReleaseRepository> BlockedReleases { get; } = new();

        public World(bool succeeds, IntegrationFailureKind? failureKind = null, string? status = null)
        {
            var effectiveStatus = status ?? (succeeds ? "sent" : "failed");
            var result = new DownloadClientGrabResult("client-1", Release, succeeds, effectiveStatus,
                succeeds ? "Sent." : "SABnzbd could not be reached.")
            {
                ExternalId = succeeds ? "nzo-1" : null,
                Failure = !succeeds
                    ? IntegrationFailureFactory.FromLegacy("download-client", "client-1", "SABnzbd", "grab", null, "SABnzbd could not be reached.")
                        with { Kind = failureKind ?? IntegrationFailureKind.RejectedAction }
                    : null
            };
            Grabs.Setup(grab => grab.GrabAsync(It.IsAny<string>(), It.IsAny<DownloadClientGrabRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(result);
            Scheduler.Setup(scheduler => scheduler.EnqueueAsync(It.IsAny<EnqueueJobRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((EnqueueJobRequest request, CancellationToken _) => TestJobs.Create(request.JobType, request.PayloadJson));
            BlockedReleases.Setup(repository => repository.BlockAsync(It.IsAny<BlockedRelease>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((BlockedRelease release, CancellationToken _) => release);
        }

        public DownloadClientRetryGrabJobHandler Handler()
            => new(Grabs.Object, Jobs.Object, ActivityFeed.Object, Scheduler.Object, new FixedClock(Now), BlockedReleases.Object);

        public JobQueueItem Job(int attemptNumber)
            => TestJobs.Create(
                SearchExecutionSupport.DownloadClientRetryGrabJobType,
                payloadJson: JsonSerializer.Serialize(new
                {
                    DownloadClientId = "client-1",
                    DownloadClientName = "SABnzbd",
                    LibraryId = "library-1",
                    MediaType = "movies",
                    EntityType = "movie",
                    EntityId = "movie-1",
                    Title = "Tears of Steel",
                    ReleaseName = Release,
                    DownloadUrl,
                    Category = "movies",
                    IndexerName = "Fixture",
                    IndexerId = (string?)null,
                    DispatchId = (string?)null,
                    Priority = (int?)null,
                    Password = (string?)null,
                    TorrentFile = (byte[]?)null,
                    AttemptNumber = attemptNumber,
                    TorrentHashOrItemId = CandidateHash
                }));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
