namespace Deluno.Jobs.Data;

public interface IDownloadDispatchPollingService
{
    Task<DownloadDispatchPollingReport> PollAsync(CancellationToken cancellationToken);
}

public sealed record DownloadDispatchPollingReport(
    int UnresolvedDispatchesChecked,
    int GrabTimeoutsDetected,
    int DetectionTimeoutsDetected,
    int ImportTimeoutsDetected,
    int ImportFailuresDetected,
    int RecoveryCasesRecorded);
