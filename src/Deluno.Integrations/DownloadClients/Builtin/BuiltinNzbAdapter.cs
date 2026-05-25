using Deluno.Downloader.Engine;
using Deluno.Downloader.Persistence;
using Deluno.Platform.Contracts;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.DownloadClients.Builtin;

/// <summary>
/// Adapter for the <c>"deluno-nzb"</c> protocol value. Grab requests
/// land a new row in <c>downloader.jobs</c> with state=Queued; telemetry
/// reads that table and projects job state into the existing
/// <see cref="DownloadClientTelemetrySnapshot"/> shape.
///
/// Job execution (kicking off <c>StreamingNzbDownloader</c> for queued
/// jobs) belongs in a hosted-service worker not yet wired in this
/// phase.
/// </summary>
public sealed class BuiltinNzbAdapter(
    IJobRepository jobs,
    TimeProvider time,
    ILogger<BuiltinNzbAdapter> logger) : IBuiltinDownloaderAdapter
{
    public string Protocol => "deluno-nzb";

    public async Task<DownloadClientGrabResult> GrabAsync(
        DownloadClientItem client, DownloadClientGrabRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var jobId = Guid.NewGuid().ToString("N");

        var job = new JobRecord(
            Id: jobId,
            Protocol: DownloadProtocol.Nzb,
            DisplayName: request.ReleaseName,
            SourcePath: request.DownloadUrl,
            SourceKind: "nzb",
            Category: request.Category ?? client.MoviesCategory ?? client.TvCategory,
            Priority: client.Priority,
            State: JobLifecycleState.Queued,
            StateReason: "Awaiting orchestrator worker (Phase 5 polish).",
            Paused: false,
            PasswordProtected: null,
            DownloadDir: client.EndpointUrl ?? string.Empty,
            OutputDir: null,
            TotalBytes: 0,
            DownloadedBytes: 0,
            UploadedBytes: 0,
            DispatchId: request.DispatchId,
            LibraryId: null,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null);

        await jobs.UpsertAsync(job, ct);
        logger.LogInformation(
            "Queued built-in NZB job {JobId} for dispatch {DispatchId}: {Title}.",
            jobId, request.DispatchId, job.DisplayName);

        return new DownloadClientGrabResult(
            ClientId: client.Id,
            ReleaseName: request.ReleaseName,
            Succeeded: true,
            Status: "queued",
            Message: "Queued for the built-in NZB downloader. Execution worker is a Phase 5 polish item — job will remain at Queued state until that lands.");
    }

    public async Task<DownloadClientTelemetrySnapshot?> GetSnapshotAsync(
        DownloadClientItem client, DateTimeOffset capturedUtc, CancellationToken ct)
    {
        var active = await jobs.ListByStateAsync(
            new[] {
                JobLifecycleState.Queued,
                JobLifecycleState.Fetching,
                JobLifecycleState.Reassembled,
                JobLifecycleState.Verify,
                JobLifecycleState.Verified,
                JobLifecycleState.Repair,
                JobLifecycleState.Extracting,
                JobLifecycleState.Extracted,
                JobLifecycleState.PostProcessed,
                JobLifecycleState.ImportPending,
            }, limit: 100, ct);

        var queue = active
            .Where(j => j.Protocol == DownloadProtocol.Nzb)
            .Select(j => new DownloadQueueItem(
                Id: j.Id,
                ClientId: client.Id,
                ClientName: client.Name,
                Protocol: Protocol,
                MediaType: "movie", // fallback; real value belongs to dispatch resolution
                Title: j.DisplayName,
                ReleaseName: j.DisplayName,
                Category: j.Category ?? string.Empty,
                Status: MapState(j.State),
                Progress: j.TotalBytes == 0 ? 0 : (double)j.DownloadedBytes / j.TotalBytes,
                SpeedMbps: 0,
                EtaSeconds: 0,
                SizeBytes: j.TotalBytes,
                DownloadedBytes: j.DownloadedBytes,
                Peers: 0,
                IndexerName: string.Empty,
                ErrorMessage: j.StateReason,
                AddedUtc: j.CreatedAt,
                SourcePath: j.OutputDir ?? j.DownloadDir))
            .ToList();

        var capabilities = new DownloadClientTelemetryCapabilities(
            SupportsQueue: true,
            SupportsHistory: true,
            SupportsPauseResume: true,
            SupportsRemove: true,
            SupportsRecheck: false,
            SupportsImportPath: true,
            AuthMode: "in-process");

        var summary = new DownloadTelemetrySummary(
            ActiveCount: queue.Count(q => q.Status == "downloading"),
            QueuedCount: queue.Count(q => q.Status == "queued"),
            CompletedCount: 0, // history is its own list; this is the active-queue count
            StalledCount: queue.Count(q => q.Status == "stalled"),
            ProcessingCount: queue.Count(q => q.Status == "processing"),
            ImportReadyCount: queue.Count(q => q.Status == "importReady"),
            TotalSpeedMbps: 0);

        return new DownloadClientTelemetrySnapshot(
            ClientId: client.Id,
            ClientName: client.Name,
            Protocol: Protocol,
            EndpointUrl: "in-process",
            HealthStatus: "healthy",
            LastHealthMessage: "Built-in NZB engine; execution worker pending.",
            Capabilities: capabilities,
            Summary: summary,
            Queue: queue,
            History: Array.Empty<DownloadClientHistoryItem>(),
            CapturedUtc: capturedUtc);
    }

    public async Task<DownloadClientActionResult> ExecuteActionAsync(
        DownloadClientItem client, string action, string queueItemId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(queueItemId, ct);
        if (job is null)
            return new DownloadClientActionResult(client.Id, queueItemId, action, false, "Job not found.");

        switch (action.ToLowerInvariant())
        {
            case "pause":
                await jobs.TransitionAsync(queueItemId, JobLifecycleState.Paused, "user requested pause", time.GetUtcNow(), ct);
                return new DownloadClientActionResult(client.Id, queueItemId, action, true, "Paused.");
            case "resume":
                await jobs.TransitionAsync(queueItemId, JobLifecycleState.Queued, "user requested resume", time.GetUtcNow(), ct);
                return new DownloadClientActionResult(client.Id, queueItemId, action, true, "Resumed.");
            case "delete":
                await jobs.TransitionAsync(queueItemId, JobLifecycleState.Failed, "user requested delete", time.GetUtcNow(), ct);
                return new DownloadClientActionResult(client.Id, queueItemId, action, true, "Deleted.");
            default:
                return new DownloadClientActionResult(client.Id, queueItemId, action, false,
                    $"Built-in NZB adapter doesn't support action '{action}'.");
        }
    }

    private static string MapState(JobLifecycleState s) => s switch
    {
        JobLifecycleState.Queued        => "queued",
        JobLifecycleState.Fetching      => "downloading",
        JobLifecycleState.Reassembled   => "processing",
        JobLifecycleState.Verify        => "processing",
        JobLifecycleState.Verified      => "processing",
        JobLifecycleState.Repair        => "processing",
        JobLifecycleState.Extracting    => "processing",
        JobLifecycleState.Extracted     => "processing",
        JobLifecycleState.PostProcessed => "importReady",
        JobLifecycleState.ImportPending => "importReady",
        JobLifecycleState.Done          => "imported",
        JobLifecycleState.Seeding       => "imported",
        JobLifecycleState.Paused        => "stalled",
        JobLifecycleState.Failed        => "importFailed",
        _ => "queued",
    };
}
