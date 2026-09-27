using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Worker.HealthChecks.Checks;
using Moq;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// #868: each configured indexer proxy (#748) tested for real, one named,
/// dismissable row per proxy -- "not in use" only when none is configured.
/// <see cref="IProxyReachabilityTester"/> is faked here so these tests do not
/// depend on any real network or proxy process; its own real behaviour is
/// exercised by <c>HttpProxyReachabilityTesterTests</c>.
/// </summary>
public sealed class ProxyHealthCheckTests
{
    [Fact]
    public async Task Reports_not_in_use_when_no_proxy_is_configured()
    {
        var check = new ProxyHealthCheck(ConnectionsReturning([]), TesterAlwaysReturning(reachable: true));

        var outcomes = await check.RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckCategories.Proxy, check.Category);
        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Contains("not in use", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reports_not_in_use_when_every_configured_proxy_is_disabled()
    {
        var proxy = Proxy(isEnabled: false);
        var check = new ProxyHealthCheck(ConnectionsReturning([proxy]), TesterAlwaysReturning(reachable: true));

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Contains("not in use", Assert.Single(outcomes).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reachable_proxy_reports_ok_and_names_the_indexers_using_it()
    {
        var proxy = Proxy(assignedIndexerNames: ["NZBgeek"]);
        var check = new ProxyHealthCheck(ConnectionsReturning([proxy]), TesterAlwaysReturning(reachable: true, message: "Reached it."));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Equal($"proxy:{proxy.Id}", outcome.Id);
        Assert.Contains("Reached it.", outcome.Message);
        Assert.Contains("NZBgeek", outcome.Message);
    }

    [Fact]
    public async Task An_unreachable_proxy_reports_a_problem_with_cause_and_fix()
    {
        var proxy = Proxy();
        var check = new ProxyHealthCheck(ConnectionsReturning([proxy]), TesterAlwaysReturning(reachable: false, message: "Connection refused."));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Problem, outcome.Status);
        Assert.Contains("Connection refused.", outcome.Message);
        Assert.NotNull(outcome.Cause);
        Assert.NotNull(outcome.Fix);
    }

    [Fact]
    public async Task An_unassigned_proxy_says_so_rather_than_naming_no_one()
    {
        var proxy = Proxy(assignedIndexerNames: []);
        var check = new ProxyHealthCheck(ConnectionsReturning([proxy]), TesterAlwaysReturning(reachable: true, message: "Reached it."));

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Contains("Not assigned", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_tester_that_throws_reports_unknown_rather_than_failing_the_whole_pass()
    {
        var proxy = Proxy();
        var tester = new Mock<IProxyReachabilityTester>();
        tester.Setup(instance => instance.TestAsync(proxy, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var check = new ProxyHealthCheck(ConnectionsReturning([proxy]), tester.Object);

        var outcome = Assert.Single(await check.RunAsync(CancellationToken.None));

        Assert.Equal(HealthCheckStatuses.Unknown, outcome.Status);
        Assert.Contains("boom", outcome.Message);
    }

    [Fact]
    public async Task Multiple_configured_proxies_each_get_their_own_row()
    {
        var first = Proxy(name: "Proxy A");
        var second = Proxy(name: "Proxy B");
        var check = new ProxyHealthCheck(ConnectionsReturning([first, second]), TesterAlwaysReturning(reachable: true));

        var outcomes = await check.RunAsync(CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.Contains(outcomes, outcome => outcome.Id == $"proxy:{first.Id}");
        Assert.Contains(outcomes, outcome => outcome.Id == $"proxy:{second.Id}");
    }

    private static ProxyItem Proxy(
        string name = "Home SOCKS5",
        bool isEnabled = true,
        IReadOnlyList<string>? assignedIndexerNames = null)
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        return new ProxyItem(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            ProxyType: ProxyTypes.Socks5,
            Address: "socks5://192.168.1.1:1080",
            Username: null,
            Password: null,
            IsEnabled: isEnabled,
            CreatedUtc: now,
            UpdatedUtc: now)
        {
            AssignedIndexerNames = assignedIndexerNames ?? []
        };
    }

    private static IConnectionsRepository ConnectionsReturning(IReadOnlyList<ProxyItem> proxies)
    {
        var repository = new Mock<IConnectionsRepository>();
        repository.Setup(instance => instance.ListProxiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(proxies);
        return repository.Object;
    }

    private static IProxyReachabilityTester TesterAlwaysReturning(bool reachable, string message = "Tested.")
    {
        var tester = new Mock<IProxyReachabilityTester>();
        tester.Setup(instance => instance.TestAsync(It.IsAny<ProxyItem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxyReachabilityResult(reachable, message));
        return tester.Object;
    }
}
