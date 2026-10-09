using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Whether each configured indexer proxy (#748) is actually reachable (#868).
///
/// <para>Before #748 there was no proxy configuration surface at all, so this
/// always reported "not in use". Now that proxies can be configured on their
/// own page, this reads them and asks <see cref="IProxyReachabilityTester"/>
/// to attempt a real request through each enabled one, one named, dismissable
/// row per proxy -- "not in use" only when none is configured.</para>
/// </summary>
public sealed class ProxyHealthCheck(IConnectionsRepository connections, IProxyReachabilityTester tester) : IHealthCheck
{
    private const string NotInUseId = "system:proxy";
    private const string NotInUseName = "Outbound and indexer proxy";

    public string Category => HealthCheckCategories.Proxy;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var proxies = (await connections.ListProxiesAsync(cancellationToken))
            .Where(proxy => proxy.IsEnabled)
            .ToArray();

        if (proxies.Length == 0)
        {
            return [HealthCheckOutcome.Ok(
                NotInUseId,
                Category,
                NotInUseName,
                "Not in use: Deluno has no outbound or indexer proxy configured.")];
        }

        var outcomes = new List<HealthCheckOutcome>(proxies.Length);
        foreach (var proxy in proxies)
        {
            outcomes.Add(await TestAsync(proxy, cancellationToken));
        }

        return outcomes;
    }

    private async Task<HealthCheckOutcome> TestAsync(ProxyItem proxy, CancellationToken cancellationToken)
    {
        var id = $"proxy:{proxy.Id}";
        var name = $"{proxy.Name} (proxy)";
        var usedBy = proxy.AssignedIndexerNames.Count > 0
            ? $" Used by {string.Join(", ", proxy.AssignedIndexerNames)}."
            : " Not assigned to an indexer yet.";

        ProxyReachabilityResult result;
        try
        {
            result = await tester.TestAsync(proxy, cancellationToken);
        }
        catch (Exception exception)
        {
            return HealthCheckOutcome.Unknown(
                id, Category, name,
                $"Deluno could not test {proxy.Name}: {exception.Message}");
        }

        return result.Reachable
            ? HealthCheckOutcome.Ok(id, Category, name, $"{result.Message}{usedBy}")
            : HealthCheckOutcome.Problem(
                id, Category, name,
                $"{proxy.Name}: {result.Message}{usedBy}",
                cause: $"Deluno could not complete a request through {proxy.Name}.",
                fix: $"Confirm {proxy.Name} is running and its address is correct, then check again.");
    }
}
