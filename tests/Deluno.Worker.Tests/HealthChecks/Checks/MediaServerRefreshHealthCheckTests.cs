using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #920: a server whose refresh keeps failing shows once here - never once
/// per missed title - and disappears the moment its backlog clears.
/// </summary>
public sealed class MediaServerRefreshHealthCheckTests
{
    private static MediaServerConnection Server(string id, string name)
        => new(id, "plex", name, "http://192.168.1.51:32400", "token", true, "unreachable", null, null, null, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Nothing_pending_reports_nothing()
    {
        var backlog = new Mock<IMediaServerRefreshBacklogRepository>();
        backlog.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var servers = new Mock<IMediaServerRepository>();
        var check = new MediaServerRefreshHealthCheck(backlog.Object, servers.Object);

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task A_server_failing_since_a_time_is_reported_once_naming_that_time()
    {
        var firstFailed = DateTimeOffset.Parse("2026-09-27T21:04:00Z");
        var backlog = new Mock<IMediaServerRefreshBacklogRepository>();
        backlog.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new MediaServerRefreshBacklogItem("s1", "movies", firstFailed, firstFailed.AddMinutes(5), 2,
                [new MediaServerPathChange("/media/Movies/Sintel.mkv", MediaServerChangeKind.Created), new MediaServerPathChange("/media/Movies/Big Buck Bunny.mkv", MediaServerChangeKind.Created)],
                firstFailed)
        ]);
        var servers = new Mock<IMediaServerRepository>();
        servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Server("s1", "Plex")]);
        var check = new MediaServerRefreshHealthCheck(backlog.Object, servers.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal(HealthCheckCategories.MediaServer, outcome.Category);
        Assert.Contains("Plex hasn't answered since", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("2 changed files", outcome.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removed_server_still_shown_backlog_is_named_generically_rather_than_erroring()
    {
        var firstFailed = DateTimeOffset.UtcNow.AddHours(-1);
        var backlog = new Mock<IMediaServerRefreshBacklogRepository>();
        backlog.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new MediaServerRefreshBacklogItem("gone", "tv", firstFailed, firstFailed.AddMinutes(5), 1, [new MediaServerPathChange("/media/TV/Show/S01E01.mkv", MediaServerChangeKind.Created)], firstFailed)
        ]);
        var servers = new Mock<IMediaServerRepository>();
        servers.Setup(repo => repo.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var check = new MediaServerRefreshHealthCheck(backlog.Object, servers.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Contains("removed media server", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }
}
