using Deluno.Connections.Data;
using Deluno.Contracts;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Every configured download client, reachable (#909). This does not test
/// anything itself -- a client is already tested when it is saved and
/// re-tested by its own circuit breaker once its backoff opens (see
/// <c>DownloadClientGrabService</c>). This check only reads that verdict onto
/// the shared health board every fifteen minutes, so a client that has been
/// unhealthy since before the last restart still shows up here rather than
/// only in the Connections screen.
/// </summary>
public sealed class DownloadClientHealthCheck(IConnectionsRepository connections, TimeProvider timeProvider) : IHealthCheck
{
    /// <summary>
    /// How long a client that has never been tested is left alone (#1055). One that was
    /// added seconds ago is being tested by whatever added it (the installer's
    /// connecting pass tests each client, up to ten tries), and reading it in that gap
    /// reported "Not tested yet" for a client that was healthy a moment later.
    /// </summary>
    public static readonly TimeSpan NewClientGrace = TimeSpan.FromMinutes(5);

    public string Category => HealthCheckCategories.DownloadClient;

    public static string NoProviderMessage(string clientName) => $"{clientName} has no Usenet provider set up.";

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var clients = await connections.ListDownloadClientsAsync(cancellationToken);
        var outcomes = new List<HealthCheckOutcome>(clients.Count);
        var now = timeProvider.GetUtcNow();

        foreach (var client in clients.Where(item => item.IsEnabled))
        {
            if (client.LastHealthTestUtc is null
                && !string.Equals(client.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                && now - client.CreatedUtc < NewClientGrace)
            {
                continue;
            }

            var id = $"download-client:{client.Id}";
            var name = $"{client.Name} (download client)";

            // A Usenet client that answers but has no news server is a setup problem, not one that is down (#1198).
            if (string.Equals(client.LastHealthMessage?.Trim(), Deluno.Connections.Usenet.UsenetProviderText.NoProvider, StringComparison.Ordinal))
            {
                outcomes.Add(HealthCheckOutcome.Problem(
                    id,
                    Category,
                    name,
                    NoProviderMessage(client.Name),
                    cause: $"{client.Name} is running, but it has no Usenet provider (news server) to download from.",
                    fix: "Add your Usenet provider under Find & Download, Download clients. Deluno writes it to every Usenet client.",
                    detailsJson: $$"""{"clientId":"{{client.Id}}","protocol":"{{client.Protocol}}"}"""));
                continue;
            }

            outcomes.Add(string.Equals(client.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                ? HealthCheckOutcome.Ok(id, Category, name, $"{client.Name} is reachable.")
                : HealthCheckOutcome.Problem(
                    id,
                    Category,
                    name,
                    client.LastHealthMessage is { Length: > 0 } message
                        ? $"{client.Name}: {message}"
                        : $"{client.Name} is not answering.",
                    cause: client.SecretUnreadable
                        ? "The saved password was stored on another computer, so this computer cannot read it."
                        : "Deluno could not confirm this download client's last connection test.",
                    fix: client.SecretUnreadable
                        ? $"Open {client.Name} in Connections, enter its password again and save. Grabs to it are held until then."
                        : $"Open Connections and test {client.Name}. Grabs to it are held until it passes.",
                    detailsJson: $$"""{"clientId":"{{client.Id}}","protocol":"{{client.Protocol}}"}"""));
        }

        return outcomes;
    }
}
