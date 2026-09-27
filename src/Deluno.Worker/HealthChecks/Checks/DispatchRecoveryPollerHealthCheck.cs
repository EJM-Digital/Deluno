using Deluno.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// The dispatch-recovery poller's own circuit breaker (#909) -- it could
/// already pause itself for fifteen minutes after repeated failures, and did
/// so silently: a log line nobody watching a screen would see. This shows it
/// on the same board as everything else.
/// </summary>
public sealed class DispatchRecoveryPollerHealthCheck(IDownloadDispatchCircuitBreakerState circuitBreaker) : IHealthCheck
{
    public string Category => HealthCheckCategories.System;

    public Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        const string id = "system:dispatch-recovery-poller";
        const string name = "Dispatch recovery poller";

        IReadOnlyList<HealthCheckOutcome> result = circuitBreaker.IsOpen
            ? [HealthCheckOutcome.Problem(
                id, Category, name,
                $"Deluno paused checking downloads for grab, detection and import timeouts after {circuitBreaker.ConsecutiveFailures} failed passes in a row.{(circuitBreaker.ResumesAtUtc is { } resumes ? $" It resumes on its own at {resumes:u}." : string.Empty)}",
                cause: "Repeated failures reading download state tripped a safety pause.",
                fix: "Check Deluno's logs for what the polling pass is failing on. It resumes on its own; no action is required unless it keeps recurring.")]
            : [HealthCheckOutcome.Ok(id, Category, name, "Running on its normal schedule.")];
        return Task.FromResult(result);
    }
}
