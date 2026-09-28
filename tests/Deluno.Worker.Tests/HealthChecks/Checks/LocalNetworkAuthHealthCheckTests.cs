using Deluno.Contracts;
using Deluno.Security.Contracts;
using Deluno.Security.Data;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #993: System -> Health says when "skip sign-in for local devices" is on,
/// and warns if Allowed Hosts is off at the same time.
/// </summary>
public sealed class LocalNetworkAuthHealthCheckTests
{
    [Fact]
    public async Task Off_reports_ok()
    {
        var repository = new Mock<ILocalNetworkAuthRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(LocalNetworkAuthSettings.Off);
        var allowedHosts = new Mock<IAllowedHostsRepository>();
        var check = new LocalNetworkAuthHealthCheck(repository.Object, allowedHosts.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Contains("Off", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_with_allowed_hosts_off_is_a_problem()
    {
        var repository = new Mock<ILocalNetworkAuthRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalNetworkAuthSettings(true, [], [], []));
        var allowedHosts = new Mock<IAllowedHostsRepository>();
        allowedHosts.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(AllowedHostsSettings.Off);
        var check = new LocalNetworkAuthHealthCheck(repository.Object, allowedHosts.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.NotNull(outcome.Fix);
    }

    [Fact]
    public async Task On_with_allowed_hosts_on_is_ok()
    {
        var repository = new Mock<ILocalNetworkAuthRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalNetworkAuthSettings(true, [], [], []));
        var allowedHosts = new Mock<IAllowedHostsRepository>();
        allowedHosts.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AllowedHostsSettings(true, ["localhost"]));
        var check = new LocalNetworkAuthHealthCheck(repository.Object, allowedHosts.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Contains("On", outcome.Message, StringComparison.Ordinal);
    }
}
