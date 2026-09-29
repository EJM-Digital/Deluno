using Deluno.Contracts;
using Deluno.Worker.Services;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Shows a background job that has stopped after an error (#1080). Deluno keeps
/// running without it; the row can be snoozed like any other, and goes away
/// once Deluno is restarted and the job runs again.
/// </summary>
public sealed class BackgroundServiceHealthCheck(IBackgroundServiceFailureState state) : IHealthCheck
{
    public string Category => HealthCheckCategories.System;

    public Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HealthCheckOutcome> outcomes =
        [
            .. state.Failures.Select(failure => HealthCheckOutcome.Problem(
                $"system:background-service:{failure.TypeName}",
                Category,
                $"{failure.DisplayName} (background job)",
                $"Deluno's {failure.DisplayName.ToLowerInvariant()} job stopped after an error at {failure.FailedUtc:u} and is not running. The rest of Deluno carries on.",
                cause: failure.Reason,
                fix: "Restart Deluno to start it again. If it stops again, Deluno's logs say why.")),
            .. state.Problems.Select(problem => HealthCheckOutcome.Problem(
                $"system:background-problem:{problem.Key}",
                Category,
                problem.Title,
                problem.Message,
                cause: problem.Reason,
                fix: "This goes away by itself once it works again. If it stays, restart Deluno; Deluno's logs say why.")),
        ];
        return Task.FromResult(outcomes);
    }
}
