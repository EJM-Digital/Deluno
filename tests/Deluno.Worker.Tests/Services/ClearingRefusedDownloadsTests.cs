using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Worker.Tests.Services;

/// <summary>
/// Clearing up after a release Deluno has refused.
///
/// <para>James found the hole: <i>"if we are refusing something, is it being
/// deleted and cleaned up so there are no traces of it"</i>. It was not. A
/// refused copy kept costing disk, kept sitting in the client's queue, and —
/// worst of the three — the client kept remembering it, so the day you
/// un-refused it the client would silently decline to fetch it. The original
/// trap, reappearing at the far end of its own fix.</para>
///
/// <para>What is guarded here is the waiting. The sharing rule owns a
/// finished download until the site's rule is met, and this pass has no
/// business overruling it — so a copy still under a hold is left alone and
/// tried again later. DESIGN-007 decisions 16 and 17.</para>
/// </summary>
public sealed class ClearingRefusedDownloadsTests
{
    [Fact]
    public async Task It_asks_the_client_to_forget_a_refused_release()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();

        await Planner().RunBlockedReleaseCleanupAsync(blocklist.Object, clients.Object, Sharing(), CancellationToken.None);

        // Forget, not delete: on a usenet client the history outlives the
        // transfer, and it is the history that refuses the release.
        clients.Verify(
            service => service.ExecuteActionAsync(
                "qbittorrent-main",
                It.Is<DownloadClientActionRequest>(request => request.Action == DownloadClientActions.Forget),
                It.IsAny<CancellationToken>()),
            Times.Once);
        blocklist.Verify(list => list.MarkCleanedUpAsync("block-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The one that matters. Deluno refused the release; the tracker still
    /// expects the seed. The rule that knows what the site wants wins.
    /// </summary>
    [Fact]
    public async Task It_leaves_a_copy_the_sharing_rule_still_needs()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();

        await Planner().RunBlockedReleaseCleanupAsync(
            blocklist.Object, clients.Object, Sharing("hash-1"), CancellationToken.None);

        clients.VerifyNoOtherCalls();
        blocklist.Verify(list => list.MarkCleanedUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A client that is off right now will not be off for ever, so nothing is
    /// marked done and the next pass tries again.
    /// </summary>
    [Fact]
    public async Task A_client_that_cannot_be_reached_is_tried_again_next_time()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = new Mock<IDownloadClientTelemetryService>();
        clients.Setup(service => service.ExecuteActionAsync(
                It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("The client is not answering."));

        await Planner().RunBlockedReleaseCleanupAsync(blocklist.Object, clients.Object, Sharing(), CancellationToken.None);

        blocklist.Verify(list => list.MarkCleanedUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Nothing_to_clear_asks_nobody_anything()
    {
        var blocklist = Blocklist();
        var clients = Clients();

        await Planner().RunBlockedReleaseCleanupAsync(blocklist.Object, clients.Object, Sharing(), CancellationToken.None);

        clients.VerifyNoOtherCalls();
    }

    // ------------------------------------------------------------------ helpers

    private static WorkPlanner Planner()
    {
        var jobs = new Mock<IJobQueueRepository>();
        jobs.Setup(repository => repository.TryClaimScheduledPassAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new WorkPlanner(
            NullLogger<WorkPlanner>.Instance,
            jobs.Object,
            new ConfigurationBuilder().Build(),
            TimeProvider.System);
    }

    private static Mock<IBlockedReleaseRepository> Blocklist(params BlockedRelease[] pending)
    {
        var blocklist = new Mock<IBlockedReleaseRepository>();
        blocklist.Setup(list => list.ListAwaitingCleanupAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(pending);
        return blocklist;
    }

    private static Mock<IDownloadClientTelemetryService> Clients()
    {
        var clients = new Mock<IDownloadClientTelemetryService>();
        clients.Setup(service => service.ExecuteActionAsync(
                It.IsAny<string>(), It.IsAny<DownloadClientActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string clientId, DownloadClientActionRequest request, CancellationToken _) =>
                new DownloadClientActionResult(clientId, request.QueueItemId, request.Action, true, "Forgotten."));
        return clients;
    }

    private static IDownloadSharingRepository Sharing(params string[] heldQueueItemIds)
    {
        var sharing = new Mock<IDownloadSharingRepository>();
        sharing.Setup(repository => repository.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DownloadSharingSnapshot(
                heldQueueItemIds
                    .Select(id => new DownloadSharingHold(
                        "qbittorrent-main", "qBittorrent", id, "Arrival", "2 days left", 0, false, false))
                    .ToArray(),
                0,
                null,
                DateTimeOffset.UnixEpoch));
        return sharing.Object;
    }

    private static BlockedRelease Refused(string queueItemId)
        => new(
            "block-1",
            BlockedReleaseKeys.For("Arrival.2016.2160p", "Nebula"),
            "Arrival.2016.2160p",
            "Nebula",
            "movies",
            "movie-1",
            "Arrival",
            ImportFailurePolicy.LikelySample,
            "It was a sample, not the film.",
            queueItemId,
            "qbittorrent-main",
            "qBittorrent",
            DateTimeOffset.UnixEpoch);
}
