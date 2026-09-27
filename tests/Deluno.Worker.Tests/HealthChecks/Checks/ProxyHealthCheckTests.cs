using Deluno.Contracts;
using Deluno.Worker.HealthChecks.Checks;

namespace Deluno.Worker.Tests.HealthChecks.Checks;

/// <summary>
/// Deluno has no outbound or indexer proxy configuration surface yet (#868),
/// so this check has exactly one behaviour today: report "not in use", not a
/// failure. Once proxy settings exist, this check gains real pass/fail
/// paths -- see the comment on <see cref="ProxyHealthCheck"/>.
/// </summary>
public sealed class ProxyHealthCheckTests
{
    [Fact]
    public async Task Reports_not_in_use_because_no_proxy_can_be_configured_yet()
    {
        var check = new ProxyHealthCheck();

        var outcomes = await check.RunAsync(CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal(HealthCheckCategories.Proxy, check.Category);
        Assert.Equal(HealthCheckStatuses.Ok, outcome.Status);
        Assert.Contains("not in use", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }
}
