using Deluno.Contracts;
using Deluno.Security.Data;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// While Allowed Hosts (#939) is off, Deluno still answers a request
/// addressed to any hostname -- exactly what every earlier release did, and
/// what a brand-new install keeps doing until setup or the Security page
/// turns it on (#936). This says so once, in plain words, with a one-click
/// Fix the System -> Health screen can route straight to the Security page.
/// </summary>
public sealed class AllowedHostsHealthCheck(IAllowedHostsRepository repository) : IHealthCheck
{
    public const string Id = "security:allowed-hosts";
    private const string Name = "Allowed Hosts";

    public string Category => HealthCheckCategories.System;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.GetAsync(cancellationToken);

        IReadOnlyList<HealthCheckOutcome> result = settings.Enabled
            ? [HealthCheckOutcome.Ok(Id, Category, Name, $"Deluno only answers on {settings.AllowedHosts.Count} chosen address{(settings.AllowedHosts.Count == 1 ? "" : "es")}.")]
            : [HealthCheckOutcome.Problem(
                Id, Category, Name,
                "Deluno answers on any address. Choose which ones it should use.",
                cause: "Allowed Hosts is off, so Deluno accepts a request no matter which hostname or IP address it was sent to.",
                fix: "Open Security and tick the addresses Deluno should answer on.")];
        return result;
    }
}
