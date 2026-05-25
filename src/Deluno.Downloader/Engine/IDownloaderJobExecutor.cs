namespace Deluno.Downloader.Engine;

/// <summary>
/// Per-protocol job executor. <see cref="DownloaderJobExecutionService"/>
/// polls the jobs table, picks the right executor by protocol, and
/// hands it a queued job to drive end-to-end.
///
/// Each executor is responsible for:
/// <list type="bullet">
///   <item><description>Reading any protocol-specific config (NZB servers, torrent listen port, etc.).</description></item>
///   <item><description>Transitioning the job through the lifecycle states via <see cref="Persistence.IJobRepository.TransitionAsync"/>.</description></item>
///   <item><description>Returning success/failure; lifecycle Transition errors are caller-handled.</description></item>
/// </list>
///
/// Executors do NOT raise dispatch-detection / import-outcome events
/// themselves — that's done by the polling heartbeat worker reading
/// our telemetry adapter once the job reaches PostProcessed. This
/// keeps a single source of truth for "when does the import pipeline
/// see this download?" (the existing telemetry-polling code path).
/// </summary>
public interface IDownloaderJobExecutor
{
    DownloadProtocol Protocol { get; }

    Task ExecuteAsync(JobRecord job, CancellationToken ct);
}
