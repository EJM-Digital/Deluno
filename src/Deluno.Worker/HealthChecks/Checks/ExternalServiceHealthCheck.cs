using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Media servers, subtitle providers and Seerr, reachable (#909). Before this
/// they were tested only when a person opened the connection and pressed
/// Test; this reads that same stored verdict onto the shared health board so
/// a server that has been down since before the app last opened is not
/// invisible until somebody happens to look.
/// </summary>
public sealed class ExternalServiceHealthCheck(
    IMediaServerRepository mediaServers,
    ISubtitleProviderRepository subtitleProviders,
    ISeerrRepository seerr) : IHealthCheck
{
    public string Category => HealthCheckCategories.MediaServer;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<HealthCheckOutcome>();

        foreach (var server in (await mediaServers.ListAsync(cancellationToken)).Where(item => item.IsEnabled))
        {
            outcomes.Add(Outcome(
                $"media-server:{server.Id}", HealthCheckCategories.MediaServer,
                $"{server.Name} (media server)", server.Name, server.HealthStatus, server.LastHealthMessage));
        }

        foreach (var provider in (await subtitleProviders.ListAsync(cancellationToken)).Where(item => item.IsEnabled))
        {
            outcomes.Add(Outcome(
                $"subtitle-provider:{provider.Id}", HealthCheckCategories.SubtitleProvider,
                $"{provider.Name} (subtitle provider)", provider.Name, provider.HealthStatus, provider.LastHealthMessage));
        }

        foreach (var connection in (await seerr.ListAsync(cancellationToken)).Where(item => item.IsEnabled))
        {
            outcomes.Add(Outcome(
                $"seerr:{connection.Id}", HealthCheckCategories.Seerr,
                $"{connection.Name} (Seerr)", connection.Name, connection.HealthStatus, connection.LastHealthMessage));
        }

        return outcomes;
    }

    private static HealthCheckOutcome Outcome(
        string id, string category, string name, string displayName, string healthStatus, string? lastMessage)
        => string.Equals(healthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
            ? HealthCheckOutcome.Ok(id, category, name, $"{displayName} is reachable.")
            : HealthCheckOutcome.Problem(
                id, category, name,
                lastMessage is { Length: > 0 } message ? $"{displayName}: {message}" : $"{displayName} is not answering.",
                cause: $"Deluno could not confirm {displayName}'s last test.",
                fix: $"Open Connections and test {displayName}.");
}
