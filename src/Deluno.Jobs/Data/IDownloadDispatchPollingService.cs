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
    int RecoveryCasesRecorded,
    int AlertsResolved = 0,
    /// <summary>
    /// A sent release that never appeared was looked up by hash/id and name
    /// across the client's whole queue and history, and that lookup found a
    /// reason: found under another category, a duplicate the client already
    /// held, or a release the client itself rejected (#911, cleanup sheet
    /// B4). Not counted in <see cref="GrabTimeoutsDetected"/> — this is the
    /// download resolving, not ending.
    /// </summary>
    int GrabAppearanceResolved = 0);
