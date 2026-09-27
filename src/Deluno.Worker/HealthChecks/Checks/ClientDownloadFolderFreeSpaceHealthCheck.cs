using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Each download client's own download folder free space, where the client's
/// API actually reports it (#909). NZBGet and SABnzbd already fold a low-disk
/// warning into their telemetry snapshot's health (see
/// <c>DownloadClientHelpers.EvaluateDiskSpaceHealth</c>, #663); this reads
/// that same snapshot rather than asking the client twice. Protocols that
/// have no free-space field in their API say so plainly instead of guessing.
/// </summary>
public sealed class ClientDownloadFolderFreeSpaceHealthCheck(IDownloadClientTelemetryService telemetry) : IHealthCheck
{
    /// <summary>Protocols whose snapshot health already folds in a real free-space reading (#663).</summary>
    private static readonly HashSet<string> ProtocolsThatReportFreeSpace = new(StringComparer.OrdinalIgnoreCase)
    {
        "nzbget", "sabnzbd"
    };

    public string Category => HealthCheckCategories.DownloadClient;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var overview = await telemetry.GetOverviewAsync(cancellationToken);
        var outcomes = new List<HealthCheckOutcome>(overview.Clients.Count);

        foreach (var client in overview.Clients)
        {
            var id = $"download-client:{client.ClientId}:free-space";
            var name = $"{client.ClientName}'s download folder space";

            if (!ProtocolsThatReportFreeSpace.Contains(client.Protocol))
            {
                outcomes.Add(HealthCheckOutcome.Unknown(
                    id, Category, name,
                    $"Not reported by {client.ClientName}: {client.Protocol} does not tell Deluno how much space its download folder has left."));
                continue;
            }

            // #663's disk check folds a low-space warning into the snapshot's
            // own health/message, so a low reading here has already been
            // through the same 5 GB / 5% rule the client's other health
            // reporting uses.
            var lowSpace = client.LastHealthMessage is { Length: > 0 } message &&
                           message.Contains("free — downloads will start failing", StringComparison.OrdinalIgnoreCase);

            outcomes.Add(lowSpace
                ? HealthCheckOutcome.Problem(
                    id, Category, name, client.LastHealthMessage!,
                    cause: "The download client's own download folder is running low on space.",
                    fix: $"Free up space where {client.ClientName} downloads to, or move some of its downloads elsewhere.")
                : HealthCheckOutcome.Ok(id, Category, name, $"{client.ClientName} reports enough free space."));
        }

        return outcomes;
    }
}
