using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
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

        await Cleanup(blocklist.Object, clients.Object, Sharing()).CleanUpEverythingAsync(CancellationToken.None);

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

        await Cleanup(blocklist.Object, clients.Object, Sharing("hash-1")).CleanUpEverythingAsync(CancellationToken.None);

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

        await Cleanup(blocklist.Object, clients.Object, Sharing()).CleanUpEverythingAsync(CancellationToken.None);

        blocklist.Verify(list => list.MarkCleanedUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Nothing_to_clear_asks_nobody_anything()
    {
        var blocklist = Blocklist();
        var clients = Clients();

        await Cleanup(blocklist.Object, clients.Object, Sharing()).CleanUpEverythingAsync(CancellationToken.None);

        clients.VerifyNoOtherCalls();
    }

    /// <summary>
    /// The manual half. DESIGN-007: "nothing automatic is only automatic" —
    /// a refusal that predates the setting, or one whose client was off when
    /// the schedule came round, can be cleared by hand.
    /// </summary>
    [Fact]
    public async Task One_refusal_can_be_cleared_by_hand()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();

        var outcome = await Cleanup(blocklist.Object, clients.Object, Sharing())
            .CleanUpOneAsync("block-1", CancellationToken.None);

        Assert.Equal(RefusedDownloadCleanupOutcomes.Cleared, outcome);
        blocklist.Verify(list => list.MarkCleanedUpAsync("block-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// And the button does not get to overrule the sharing rule either. A
    /// manual override that ignored the tracker would be a good way to lose an
    /// account.
    /// </summary>
    [Fact]
    public async Task Clearing_by_hand_still_waits_for_the_sharing_rule()
    {
        var blocklist = Blocklist(Refused("hash-1"));
        var clients = Clients();

        var outcome = await Cleanup(blocklist.Object, clients.Object, Sharing("hash-1"))
            .CleanUpOneAsync("block-1", CancellationToken.None);

        Assert.Equal(RefusedDownloadCleanupOutcomes.StillSharing, outcome);
        clients.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Told apart, because they need different words on the screen: one is
    /// "there was nothing left to clear", the other is "that is not a thing".
    /// </summary>
    [Fact]
    public async Task A_refusal_with_nothing_left_to_clear_is_not_the_same_as_one_that_does_not_exist()
    {
        var blocklist = Blocklist();
        blocklist.Setup(list => list.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Refused("hash-1")]);

        var cleanup = Cleanup(blocklist.Object, Clients().Object, Sharing());

        Assert.Equal(
            RefusedDownloadCleanupOutcomes.NothingToClear,
            await cleanup.CleanUpOneAsync("block-1", CancellationToken.None));
        Assert.Equal(
            RefusedDownloadCleanupOutcomes.NotFound,
            await cleanup.CleanUpOneAsync("never-blocked", CancellationToken.None));
    }

    // ------------------------------------------------------------------ helpers

    private static RefusedDownloadCleanupService Cleanup(
        IBlockedReleaseRepository blocklist,
        IDownloadClientTelemetryService clients,
        IDownloadSharingRepository sharing)
        => new(blocklist, clients, sharing, NullLogger<RefusedDownloadCleanupService>.Instance);

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
