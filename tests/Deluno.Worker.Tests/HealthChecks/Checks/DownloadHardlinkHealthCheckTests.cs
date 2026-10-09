using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #819: a seeding download whose hardlink into the library was lost shows
/// once here, distinct from a missing library file, and never for a download
/// that was simply never going to share (different drives, hardlinks off).
/// </summary>
public sealed class DownloadHardlinkHealthCheckTests
{
    private static DownloadSharingSnapshot Snapshot(params DownloadSharingHold[] holds)
        => new(holds, holds.Sum(hold => hold.SizeBytes), null, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Nothing_lost_reports_nothing()
    {
        var sharing = new Mock<IDownloadSharingRepository>();
        sharing.Setup(repo => repo.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot(
            new DownloadSharingHold("c1", "qBittorrent", "q1", "Sintel", "2 days left", 1000, false, SharesLibraryCopy: true),
            new DownloadSharingHold("c1", "qBittorrent", "q2", "Big Buck Bunny", "On another drive", 2000, false, SharesLibraryCopy: false)));
        var check = new DownloadHardlinkHealthCheck(sharing.Object);

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_lost_hardlink_is_reported_once_and_distinctly()
    {
        var sharing = new Mock<IDownloadSharingRepository>();
        sharing.Setup(repo => repo.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot(
            new DownloadSharingHold("c1", "qBittorrent", "q1", "Big Buck Bunny",
                "No longer shared with your library - the library copy was upgraded or removed.",
                62_277_025_792, false, SharesLibraryCopy: false, LostHardlink: true)));
        var check = new DownloadHardlinkHealthCheck(sharing.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal(HealthCheckCategories.DownloadClient, outcome.Category);
        Assert.Contains("Big Buck Bunny", outcome.Message);
        Assert.Contains("no longer shares", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe to reclaim", outcome.Fix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_lost_hardlinks_read_differently()
    {
        var sharing = new Mock<IDownloadSharingRepository>();
        sharing.Setup(repo => repo.GetSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot(
            new DownloadSharingHold("c1", "qBittorrent", "q1", "Big Buck Bunny", "Lost.", 1000, false, SharesLibraryCopy: false, LostHardlink: true),
            new DownloadSharingHold("c1", "qBittorrent", "q2", "Sintel", "Lost.", 2000, false, SharesLibraryCopy: false, LostHardlink: true)));

        SameProblemReadsDifferentlyPerItemTests.AssertEachReadsDifferently(
            await new DownloadHardlinkHealthCheck(sharing.Object).RunAsync(CancellationToken.None));
    }
}
