using Deluno.Contracts;

namespace Deluno.Worker.HealthChecks;

/// <summary>
/// One kind of health check (#909) -- one download client, one library root,
/// the processor, and so on. A single implementation can, and usually does,
/// report several rows: one per download client it finds configured, for
/// example, rather than one row per protocol.
/// </summary>
public interface IHealthCheck
{
    /// <summary>Which category this check belongs to -- what "run right after it changes" asks for by name.</summary>
    string Category { get; }

    Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken);
}
