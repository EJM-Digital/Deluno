using Deluno.Contracts;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #939: while Allowed Hosts is off, System -> Health shows one clear item
/// with a one-click Fix; once it is on, the check clears.
/// </summary>
public sealed class AllowedHostsHealthCheckTests
{
    [Fact]
    public async Task Off_reports_a_problem_with_a_fix()
    {
        var repository = new Mock<IAllowedHostsRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AllowedHostsSettings.Off);
        var check = new AllowedHostsHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(AllowedHostsHealthCheck.Id, outcome.Id);
        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Equal(HealthCheckCategories.System, outcome.Category);
        Assert.Contains("any address", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(outcome.Fix);
    }

    [Fact]
    public async Task On_reports_ok()
    {
        var repository = new Mock<IAllowedHostsRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AllowedHostsSettings(true, ["localhost", "my-pc"]));
        var check = new AllowedHostsHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
    }
}
