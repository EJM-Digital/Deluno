using Deluno.Integrations.DownloadClients;
using Deluno.Recovery.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Worker.Services;

/// <summary>
/// The one thing the sharing rule is allowed to do to a download client: ask it
/// to let go of a finished item and the data with it (#288).
///
/// It goes through the client's own action path rather than the filesystem
/// deliberately. Deleting a file a torrent client still believes it owns leaves
/// the torrent registered against data that has vanished, so the client errors
/// it, sharing stops, and on a private site that costs the user their ratio or
/// their account (#287). Asking the client means the client tidies its own file
/// and forgets the torrent in one step.
///
/// <para><b>A scope per call, not a captured service.</b> This gateway is a
/// singleton and <see cref="IDownloadClientTelemetryService"/> is scoped, so
/// taking it as a constructor argument captured one scope's instance — and
/// every reclaim for the life of the process then ran against that first
/// scope's database connections. Nothing complained: the container only reports
/// it when scope validation is on, which the web host does not turn on. It was
/// found by validating the composition in a test, after the same composition
/// turned out to be missing four modules entirely.</para>
/// </summary>
public sealed class DownloadClientReclaimGateway(IServiceScopeFactory scopeFactory) : IDownloadClientActionGateway
{
    public async Task<DownloadClientRemovalResult> RemoveWithDataAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var telemetry = scope.ServiceProvider.GetRequiredService<IDownloadClientTelemetryService>();
        var result = await telemetry.ReclaimCompletedAsync(clientId, queueItemId, cancellationToken);

        return new DownloadClientRemovalResult(result.Succeeded, result.Message);
    }
}
