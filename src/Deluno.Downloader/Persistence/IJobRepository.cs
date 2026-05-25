using Deluno.Downloader.Engine;

namespace Deluno.Downloader.Persistence;

/// <summary>
/// Read/write access to the shared <c>jobs</c> and <c>state_transitions</c>
/// tables in <c>downloader.db</c>. Protocol-specific tables get their
/// own repositories (e.g. <c>INzbSegmentRepository</c>,
/// <c>ITorrentMetadataRepository</c>) so the schema can evolve
/// independently per protocol.
///
/// All transitions go through <see cref="TransitionAsync"/>, which writes
/// both the new state on <c>jobs.state</c> AND a row to
/// <c>state_transitions</c> in the same transaction. This guarantees an
/// audit trail without callers having to remember.
/// </summary>
public interface IJobRepository
{
    Task<JobRecord?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<JobRecord>> ListByStateAsync(
        IReadOnlyList<JobLifecycleState> states,
        int limit,
        CancellationToken ct);
    Task<IReadOnlyList<JobRecord>> ListPriorityOrderedAsync(
        JobLifecycleState state,
        int limit,
        CancellationToken ct);

    Task UpsertAsync(JobRecord job, CancellationToken ct);

    /// <summary>
    /// Move a job to a new lifecycle state, validating the transition is
    /// legal for its protocol and writing both <c>jobs.state</c> and a
    /// <c>state_transitions</c> row in one SQLite transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the transition isn't legal per
    /// <see cref="JobLifecycleTransitions.IsLegal"/>, or if the job
    /// doesn't exist.
    /// </exception>
    Task TransitionAsync(
        string jobId,
        JobLifecycleState to,
        string? reason,
        DateTimeOffset occurredAt,
        CancellationToken ct);

    /// <summary>Returns the transition history for a job, oldest first.</summary>
    Task<IReadOnlyList<StateTransitionRecord>> GetTransitionsAsync(
        string jobId, CancellationToken ct);

    /// <summary>
    /// Archive a finished job: insert a summary row into <c>history</c>
    /// with a canonical <c>dedupe_key</c> (computed via
    /// <see cref="JobHistoryDedupeKey"/>) and delete the live <c>jobs</c>
    /// row + cascade children. One SQLite transaction so the live row
    /// can't disappear without the history row materialising.
    ///
    /// Callers pass torrent infohashes when available (extracted from
    /// <see cref="Torrent.Engine.TorrentJobHandle"/>); pass null for NZB
    /// jobs — the dedupe_key will be computed from (display_name +
    /// total_bytes) instead.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the job
    /// doesn't exist or isn't in a terminal state (Done/Failed). Use
    /// <see cref="TransitionAsync"/> to reach terminal first.</exception>
    Task ArchiveAsync(
        string jobId,
        string? torrentInfohashV1Hex,
        string? torrentInfohashV2Hex,
        CancellationToken ct);
}
