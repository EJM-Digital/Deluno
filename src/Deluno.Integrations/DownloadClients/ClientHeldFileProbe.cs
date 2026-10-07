using Deluno.Contracts;

namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// Answers "does a client still hold this file?" from the clients' own lists
/// (#1171). Queue, history and the items Deluno did not send are all
/// included, because a file another tool added is just as much in use.
/// </summary>
public sealed class ClientHeldFileProbe(IDownloadClientTelemetryService telemetry) : IClientHeldFileProbe
{
    public async Task<bool> IsHeldByAnyClientAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var snapshots = await telemetry.GetSnapshotsForLeftoverScanAsync(cancellationToken);
            return snapshots.Any(snapshot =>
                snapshot.Queue.Any(item => ClientHeldFiles.Claims(item.SourcePath, filePath))
                || snapshot.History.Any(item => ClientHeldFiles.Claims(item.SourcePath, filePath))
                || snapshot.OutsideSourcePaths.Any(path => ClientHeldFiles.Claims(path, filePath)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return true;
        }
    }
}
