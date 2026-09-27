using Deluno.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Whether an outbound or indexer proxy is configured and reachable (#868).
///
/// <para>Deluno has no proxy configuration surface yet -- no settings field,
/// no per-indexer option, nothing a person can turn on -- so there is
/// currently nothing for this check to test. Per #868's acceptance, a check
/// with nothing configured to check must report "not in use", not a
/// failure, so that is what this always reports today. Once outbound or
/// indexer proxy settings exist, this check should read them and actually
/// attempt a request through the configured proxy instead of always
/// short-circuiting here -- the stable id and category are already in place
/// for that.</para>
/// </summary>
public sealed class ProxyHealthCheck : IHealthCheck
{
    private const string Id = "system:proxy";
    private const string Name = "Outbound and indexer proxy";

    public string Category => HealthCheckCategories.Proxy;

    public Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<HealthCheckOutcome>>(
        [
            HealthCheckOutcome.Ok(
                Id,
                Category,
                Name,
                "Not in use: Deluno has no outbound or indexer proxy configured.")
        ]);
}
