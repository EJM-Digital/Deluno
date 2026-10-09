using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;

namespace Deluno.Worker.HealthChecks.Checks;

/// <summary>
/// Each download client's own download folder free space, where the client's
/// API actually reports it (#909). Every adapter that can ask its client
/// folds the same low-disk warning into its snapshot's health (see
/// <c>DownloadClientHelpers.EvaluateDiskSpaceHealth</c>, #663) and now also
/// carries the raw number on <see cref="DownloadClientTelemetrySnapshot.FreeSpaceBytes"/>;
/// this reads that snapshot rather than asking the client twice. uTorrent's
/// WebUI has no free-space call at all, so it says exactly that instead of
/// guessing.
/// </summary>
public sealed class ClientDownloadFolderFreeSpaceHealthCheck(IDownloadClientTelemetryService telemetry) : IHealthCheck
{
    /// <summary>Protocols whose API has no free-space call for Deluno to read at all.</summary>
    private static readonly HashSet<string> ProtocolsWithNoFreeSpaceApi = new(StringComparer.OrdinalIgnoreCase)
    {
        "utorrent"
    };

    public string Category => HealthCheckCategories.DownloadClient;

    public async Task<IReadOnlyList<HealthCheckOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var overview = await telemetry.GetLatestOverviewAsync(DownloadTelemetryFreshness.Screen, cancellationToken);
        var outcomes = new List<HealthCheckOutcome>(overview.Clients.Count);

        foreach (var client in overview.Clients)
        {
            var id = $"download-client:{client.ClientId}:free-space";
            var name = $"{client.ClientName}'s download folder space";

            if (ProtocolsWithNoFreeSpaceApi.Contains(client.Protocol))
            {
                outcomes.Add(HealthCheckOutcome.Unknown(
                    id, Category, name,
                    $"{client.ClientName} doesn't report free space: uTorrent's WebUI has no call for it."));
                continue;
            }

            if (client.FreeSpaceBytes is not { } freeBytes)
            {
                outcomes.Add(HealthCheckOutcome.Unknown(
                    id, Category, name,
                    $"Deluno could not read free space from {client.ClientName} on its last check."));
                continue;
            }

            // #663's disk check folds a low-space warning into the snapshot's
            // own health/message, so a low reading here has already been
            // through the same 5 GB / 5% rule the client's other health
            // reporting uses.
            var lowSpace = client.LastHealthMessage is { Length: > 0 } message &&
                           message.Contains("free — downloads will start failing", StringComparison.OrdinalIgnoreCase);
            var freeGb = Math.Round(freeBytes / 1_000_000_000d, 1);

            outcomes.Add(lowSpace
                ? HealthCheckOutcome.Problem(
                    id, Category, name,
                    client.LastHealthMessage!.Contains(client.ClientName, StringComparison.OrdinalIgnoreCase) ? client.LastHealthMessage : $"{client.ClientName}: {client.LastHealthMessage}",
                    cause: $"{client.ClientName}'s own download folder is running low on space.",
                    fix: $"Free up space where {client.ClientName} downloads to, or move some of its downloads elsewhere.")
                : HealthCheckOutcome.Ok(id, Category, name, $"{client.ClientName} reports {freeGb:0.#} GB free."));
        }

        return outcomes;
    }
}
