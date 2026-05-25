namespace Deluno.Downloader.Engine;

/// <summary>
/// The lifecycle state machine from the architecture doc, encoded as an
/// enum. Single shared shape for both protocols, with one
/// torrent-specific addition (<see cref="Seeding"/>).
///
/// Diagram (see doc §Download Lifecycle State Machine):
/// <code>
///   Queued → Fetching → Reassembled → Verify → Verified
///                                           ↘ Repair → Verified (NZB only)
///                            → Extracting → Extracted
///                            → PostProcessed → ImportPending → Done
///                                                            ↘ Seeding → Done (torrent)
///   Any state → Failed | Paused
/// </code>
/// </summary>
public enum JobLifecycleState
{
    Queued,
    Fetching,
    Reassembled,
    Verify,
    Verified,
    Repair,       // NZB only — par2 repair after partial-fetch
    Extracting,
    Extracted,
    PostProcessed,
    ImportPending,
    Done,
    Seeding,      // Torrent only — continues uploading after Done
    Failed,
    Paused,
}

/// <summary>
/// State-transition policy. Encodes which transitions are legal so
/// callers can't accidentally jump from Fetching to Done. Validated by
/// the orchestrator + by unit tests in <c>Deluno.Downloader.Tests</c>.
/// </summary>
public static class JobLifecycleTransitions
{
    /// <summary>
    /// Returns true if <paramref name="to"/> is a legal next state from
    /// <paramref name="from"/>. Failure and Paused are reachable from
    /// every non-terminal state.
    /// </summary>
    public static bool IsLegal(JobLifecycleState from, JobLifecycleState to, DownloadProtocol protocol)
    {
        // Universal: every non-terminal state can go to Failed or Paused.
        if (from is not (JobLifecycleState.Done or JobLifecycleState.Failed)
            && to is JobLifecycleState.Failed or JobLifecycleState.Paused)
            return true;

        // Resume from Paused returns to whatever was happening — we model
        // that as "Paused can go to any non-terminal active state".
        if (from is JobLifecycleState.Paused
            && to is not (JobLifecycleState.Paused or JobLifecycleState.Done))
            return true;

        // From Failed: only Retry (back to Queued) is legal.
        if (from is JobLifecycleState.Failed && to is JobLifecycleState.Queued)
            return true;

        return (from, to) switch
        {
            (JobLifecycleState.Queued,        JobLifecycleState.Fetching)      => true,
            (JobLifecycleState.Fetching,      JobLifecycleState.Reassembled)   => true,
            (JobLifecycleState.Reassembled,   JobLifecycleState.Verify)        => true,
            (JobLifecycleState.Reassembled,   JobLifecycleState.Extracting)    => true,  // when no verify needed (rare)
            (JobLifecycleState.Verify,        JobLifecycleState.Verified)      => true,
            (JobLifecycleState.Verify,        JobLifecycleState.Repair)        => protocol == DownloadProtocol.Nzb,
            (JobLifecycleState.Repair,        JobLifecycleState.Verified)      => protocol == DownloadProtocol.Nzb,
            (JobLifecycleState.Verified,      JobLifecycleState.Extracting)    => true,
            (JobLifecycleState.Verified,      JobLifecycleState.PostProcessed) => true,  // no archive present
            (JobLifecycleState.Extracting,    JobLifecycleState.Extracted)     => true,
            (JobLifecycleState.Extracted,     JobLifecycleState.PostProcessed) => true,
            (JobLifecycleState.PostProcessed, JobLifecycleState.ImportPending) => true,
            (JobLifecycleState.ImportPending, JobLifecycleState.Done)          => true,
            (JobLifecycleState.Done,          JobLifecycleState.Seeding)       => protocol == DownloadProtocol.Torrent,
            (JobLifecycleState.Seeding,       JobLifecycleState.Done)          => protocol == DownloadProtocol.Torrent,
            _ => false,
        };
    }

    /// <summary>Same as <see cref="IsLegal"/> but throws on illegal transitions.</summary>
    public static void EnsureLegal(JobLifecycleState from, JobLifecycleState to, DownloadProtocol protocol)
    {
        if (!IsLegal(from, to, protocol))
            throw new InvalidOperationException(
                $"Illegal lifecycle transition for {protocol}: {from} -> {to}.");
    }

    public static bool IsTerminal(JobLifecycleState state)
        => state is JobLifecycleState.Done or JobLifecycleState.Failed;
}
