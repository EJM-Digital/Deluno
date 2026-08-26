namespace Deluno.Integrations.DownloadClients;

/// <summary>
/// The one place a queue is turned into counts.
///
/// It was written out twice — once per client adapter, once again after the
/// telemetry service re-derives statuses — which is how the two copies came to
/// have to agree by hand. They also carry an invariant worth stating rather than
/// defending downstream: every status counted by
/// <see cref="DownloadTelemetrySummary.WaitingForProcessorCount"/> is also
/// counted by <see cref="DownloadTelemetrySummary.ProcessingCount"/>, so the
/// import share is their difference and can never be negative (#280).
/// </summary>
public static class DownloadQueueSummary
{
    /// <summary>Past the download, before the library.</summary>
    public static bool IsProcessing(string status) =>
        status is DownloadQueueStatuses.Processing
            or DownloadQueueStatuses.Processed
            or DownloadQueueStatuses.ProcessingFailed
            or DownloadQueueStatuses.WaitingForProcessor
            or DownloadQueueStatuses.ImportQueued;

    /// <summary>Waiting on a processor: a strict subset of <see cref="IsProcessing"/>.</summary>
    public static bool IsWaitingForProcessor(string status) =>
        status == DownloadQueueStatuses.WaitingForProcessor;

    public static DownloadTelemetrySummary Of(IEnumerable<DownloadQueueItem> queue)
    {
        var items = queue as IReadOnlyCollection<DownloadQueueItem> ?? queue.ToArray();
        return new DownloadTelemetrySummary(
            ActiveCount: items.Count(item => item.Status == DownloadQueueStatuses.Downloading),
            QueuedCount: items.Count(item => item.Status == DownloadQueueStatuses.Queued),
            CompletedCount: items.Count(item => item.Status == DownloadQueueStatuses.Completed),
            StalledCount: items.Count(item => item.Status == DownloadQueueStatuses.Stalled),
            ProcessingCount: items.Count(item => IsProcessing(item.Status)),
            ImportReadyCount: items.Count(item => item.Status is DownloadQueueStatuses.ImportReady or DownloadQueueStatuses.Completed),
            TotalSpeedMbps: Math.Round(items.Sum(item => item.SpeedMbps), 1),
            WaitingForProcessorCount: items.Count(item => IsWaitingForProcessor(item.Status)),
            TotalUploadSpeedMbps: Math.Round(items.Sum(item => item.UploadSpeedMbps), 1));
    }
}
