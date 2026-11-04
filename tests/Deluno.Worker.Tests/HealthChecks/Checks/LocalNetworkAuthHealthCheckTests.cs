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

    [Theory]
    [InlineData(1, "On. Devices on your local network skip sign-in. Narrowed to 1 chosen address.")]
    [InlineData(3, "On. Devices on your local network skip sign-in. Narrowed to 3 chosen addresses.")]
    public async Task On_with_a_chosen_list_says_how_many_addresses_in_the_right_number(int count, string expected)
    {
        var repository = new Mock<ILocalNetworkAuthRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalNetworkAuthSettings(true, [], Enumerable.Range(1, count).Select(n => $"192.168.1.{n}").ToArray(), []));
        var allowedHosts = new Mock<IAllowedHostsRepository>();
        allowedHosts.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AllowedHostsSettings(true, ["localhost"]));
        var check = new LocalNetworkAuthHealthCheck(repository.Object, allowedHosts.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(expected, outcome.Message);
    }

    [Fact]
    public async Task On_with_no_chosen_list_says_nothing_about_a_narrowing()
    {
        var repository = new Mock<ILocalNetworkAuthRepository>();
        repository.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalNetworkAuthSettings(true, [], [], []));
        var allowedHosts = new Mock<IAllowedHostsRepository>();
        allowedHosts.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AllowedHostsSettings(true, ["localhost"]));
        var check = new LocalNetworkAuthHealthCheck(repository.Object, allowedHosts.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal("On. Devices on your local network skip sign-in.", outcome.Message);
    }
}
