using Deluno.Contracts;
using Deluno.Platform.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #1240: a torrent Deluno meant to keep seeding and could not is a visible problem with its reason, on System, Health.
/// </summary>
public sealed class KeepSeedingProblemHealthCheckTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(1);

    private static IKeepSeedingStore Store(params KeepSeedingProblem[] problems)
    {
        var store = new Mock<IKeepSeedingStore>();
        store.Setup(item => item.ListProblemsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(problems);
        return store.Object;
    }

    [Fact]
    public async Task With_nothing_wrong_it_reports_nothing()
        => Assert.Empty(await new KeepSeedingProblemHealthCheck(Store()).RunAsync(CancellationToken.None));

    [Fact]
    public async Task A_refused_move_is_a_problem_with_the_clients_reason_and_a_retry_note()
    {
        var problem = new KeepSeedingProblem("c:q", "c", "My qBittorrent", "q", "Sintel", KeepSeedingProblemKinds.Refused,
            "Deluno could not move Sintel to keep in My qBittorrent: qBittorrent does not have a category named keep.", "keep", Now, Now, 2);

        var outcome = Assert.Single(await new KeepSeedingProblemHealthCheck(Store(problem)).RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal(HealthCheckCategories.DownloadClient, outcome.Category);
        Assert.Equal("keep-seeding:c:q", outcome.Id);
        Assert.Equal(problem.Message, outcome.Message);
        Assert.Contains("could not be moved to keep seeding", outcome.Name);
        Assert.Contains("My qBittorrent", outcome.Cause);
        Assert.Contains("tries again at every check", outcome.Fix);
    }

    [Fact]
    public async Task A_torrent_with_no_category_anywhere_says_where_to_set_one()
    {
        var problem = new KeepSeedingProblem("c:q", "c", "My qBittorrent", "q", "Sintel", KeepSeedingProblemKinds.NeedsCategory,
            "Sintel is from a private tracker and no keep-seeding category is set for it.", null, Now, Now, 1);

        var outcome = Assert.Single(await new KeepSeedingProblemHealthCheck(Store(problem)).RunAsync(CancellationToken.None));

        Assert.Contains("no place to keep seeding", outcome.Name);
        Assert.Contains("Keep seeding screen", outcome.Fix);
    }
}
