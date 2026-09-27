using Deluno.Connections.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>One proxy's reachability test, either passed or not, with a plain-English reason (#868).</summary>
public sealed record ProxyReachabilityResult(bool Reachable, string Message);

/// <summary>
/// Actually attempts a request through a configured indexer proxy (#748),
/// rather than reading a status nothing else has set. Split out of
/// <see cref="ProxyHealthCheck"/> so the check itself can be tested without a
/// real network call.
/// </summary>
public interface IProxyReachabilityTester
{
    Task<ProxyReachabilityResult> TestAsync(ProxyItem proxy, CancellationToken cancellationToken);
}
