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
    public async Task Off_reports_a_suggestion_with_a_fix()
    {
        var repository = new Mock<IAllowedHostsRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AllowedHostsSettings.Off);
        var check = new AllowedHostsHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(AllowedHostsHealthCheck.Id, outcome.Id);
        Assert.Equal(HealthCheckStatuses.Suggestion, outcome.Status);
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

    [Theory]
    [InlineData(1, "Deluno only answers on 1 chosen address.")]
    [InlineData(2, "Deluno only answers on 2 chosen addresses.")]
    public async Task On_says_how_many_addresses_in_the_right_number(int count, string expected)
    {
        var repository = new Mock<IAllowedHostsRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AllowedHostsSettings(true, Enumerable.Range(1, count).Select(n => $"host-{n}").ToArray()));
        var check = new AllowedHostsHealthCheck(repository.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(expected, outcome.Message);
        Assert.Equal(AllowedHostsHealthCheck.Id, outcome.Id);
        Assert.Equal(HealthCheckCategories.System, outcome.Category);
    }
}
