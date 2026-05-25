using Deluno.Platform.Contracts;

namespace Deluno.Integrations.DownloadClients.Builtin;

/// <summary>
/// Adapter between the existing <see cref="IDownloadClientGrabService"/>
/// + <see cref="IDownloadClientTelemetryService"/> seams and the
/// in-process <c>Deluno.Downloader</c> engine.
///
/// Two adapter implementations live in this namespace:
/// <c>BuiltinNzbAdapter</c> for protocol value <c>"deluno-nzb"</c>,
/// <c>BuiltinTorrentAdapter</c> for protocol value
/// <c>"deluno-torrent"</c>. They translate the existing remote-client
/// shapes into in-process engine calls.
///
/// Phase 5 scope: adapters land jobs in the downloader.db <c>jobs</c>
/// table with state=Queued and expose a telemetry view derived from
/// that table. The hosted-service worker that actually executes queued
/// jobs (kicks off StreamingNzbDownloader / drives MonoTorrent through
/// the lifecycle state machine) is the next polish step.
/// </summary>
public interface IBuiltinDownloaderAdapter
{
    /// <summary>The protocol value this adapter handles (e.g. "deluno-nzb").</summary>
    string Protocol { get; }

    Task<DownloadClientGrabResult> GrabAsync(
        DownloadClientItem client,
        DownloadClientGrabRequest request,
        CancellationToken ct);

    Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(
        DownloadClientItem client,
        DateTimeOffset capturedUtc,
        CancellationToken ct);

    Task<DownloadClientActionResult> ExecuteActionAsync(
        DownloadClientItem client,
        string action,
        string queueItemId,
        CancellationToken ct);
}
