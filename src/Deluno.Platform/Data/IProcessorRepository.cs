using Deluno.Platform.Contracts;

namespace Deluno.Platform.Data;

public interface IProcessorRepository
{
    Task<ProcessorHandoffItem> EnsureProcessorHandoffAsync(
        CreateProcessorHandoffRequest request,
        CancellationToken cancellationToken);

    Task<ProcessorHandoffItem?> FindProcessorHandoffAsync(
        string libraryId,
        string? handoffId,
        string? sourcePath,
        CancellationToken cancellationToken);

    Task<ProcessorHandoffItem?> GetProcessorHandoffAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// The older hand-off an id belonged to before a release sent again gave its source path a new id (#1208), or
    /// null when the id was never replaced. A processor's late report for such an id is settled against this, never
    /// against the hand-off that replaced it.
    /// </summary>
    Task<SupersededProcessorHandoff?> FindSupersededProcessorHandoffAsync(
        string libraryId,
        string handoffId,
        CancellationToken cancellationToken)
        => Task.FromResult<SupersededProcessorHandoff?>(null);

    /// <summary>Marks a replaced hand-off as settled by the processor's report. True the first time only.</summary>
    Task<bool> SettleSupersededProcessorHandoffAsync(
        string handoffId,
        string reportStatus,
        CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <summary>
    /// The newest hand-off made for a download, found by the client and the
    /// client's own id for the item (its hash or id), not by where its files
    /// are. A download the client moves changes its path, and a path match then
    /// misses the hand-off that was already dealt with (#1155).
    /// </summary>
    Task<ProcessorHandoffItem?> FindProcessorHandoffForDownloadAsync(
        string clientId,
        string queueItemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every hand-off made for a release name, in any state, newest first. Another
    /// download of the same release may already hold the hand-off.
    /// </summary>
    Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsForReleaseAsync(
        string releaseName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Points a hand-off that has not been sent yet at the folder its download
    /// is in now (#1150). A download moved by its client after the hand-off
    /// was made would otherwise be sent from where it used to be. False when
    /// the hand-off is not waiting or another hand-off already holds that path.
    /// </summary>
    Task<bool> RepointWaitingProcessorHandoffAsync(
        string id,
        string sourcePath,
        CancellationToken cancellationToken);

    Task<ProcessorHandoffItem?> UpdateProcessorHandoffAsync(
        string id,
        string status,
        string? outputPath,
        string? importJobId,
        string? failureMessage,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what the processor last said about a hand-off. Does not move the
    /// hand-off's <c>updated_utc</c>: asking is not a change.
    /// </summary>
    Task RecordProcessorObservationAsync(
        string id,
        ProcessorHandoffObservation observation,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsAsync(
        string? libraryId,
        int take,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every hand-off in one of the given statuses, across every library.
    ///
    /// <para>Unlimited, because the statuses that matter are few by definition —
    /// a processor holds a handful of files, not a history. The newest-first
    /// listing above is a page of history, and asking a processor for a status
    /// does not move a hand-off up it, so anything genuinely stuck sank out of
    /// that page and was never seen again (#571).</para>
    /// </summary>
    Task<IReadOnlyList<ProcessorHandoffItem>> ListProcessorHandoffsWithStatusAsync(
        IReadOnlyList<string> statuses,
        CancellationToken cancellationToken);

    /// <summary>Every hand-off still with a processor.</summary>
    Task<IReadOnlyList<ProcessorHandoffItem>> ListOpenProcessorHandoffsAsync(CancellationToken cancellationToken)
        => ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.WithProcessor, cancellationToken);

    /// <summary>What Deluno owes a processor about one hand-off, if anything (#580).</summary>
    Task<ProcessorOutcomeRecord?> GetProcessorOutcomeAsync(string handoffId, CancellationToken cancellationToken);

    /// <summary>Writes the whole record, creating it or replacing it.</summary>
    Task SaveProcessorOutcomeAsync(ProcessorOutcomeRecord record, CancellationToken cancellationToken);

    /// <summary>Every outcome still owed whose next attempt is due by <paramref name="nowUtc"/>.</summary>
    Task<IReadOnlyList<ProcessorOutcomeRecord>> ListDueProcessorOutcomesAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    /// <summary>The outcome records for these hand-offs, for showing beside them.</summary>
    Task<IReadOnlyList<ProcessorOutcomeRecord>> ListProcessorOutcomesAsync(
        IReadOnlyCollection<string> handoffIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessorConnectionItem>> ListProcessorConnectionsAsync(CancellationToken cancellationToken);
    Task<ProcessorConnectionItem?> GetProcessorConnectionAsync(string id, CancellationToken cancellationToken);
    Task<ProcessorConnectionItem?> FindProcessorConnectionByNameAsync(string? name, CancellationToken cancellationToken);

    /// <summary>
    /// The connection a library or hand-off is linked to (#1077). Looks it up by
    /// id, so renaming the connection changes nothing; the name is tried only
    /// for a row that never matched a connection.
    /// </summary>
    Task<ProcessorConnectionItem?> FindProcessorConnectionAsync(string? id, string? nameFallback, CancellationToken cancellationToken);

    Task<ProcessorConnectionItem> CreateProcessorConnectionAsync(
        CreateProcessorConnectionRequest request,
        CancellationToken cancellationToken);

    Task<ProcessorConnectionItem?> UpdateProcessorConnectionAsync(
        string id,
        UpdateProcessorConnectionRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteProcessorConnectionAsync(string id, CancellationToken cancellationToken);

    Task<ProcessorConnectionItem?> RecordProcessorConnectionHealthAsync(
        string id,
        string status,
        string? message,
        CancellationToken cancellationToken);

    /// <summary>Every path pair configured for this processor connection (#962), highest priority first.</summary>
    Task<IReadOnlyList<ProcessorPathMappingItem>> ListProcessorPathMappingsAsync(
        string processorConnectionId,
        CancellationToken cancellationToken);

    Task<ProcessorPathMappingItem?> CreateProcessorPathMappingAsync(
        string processorConnectionId,
        CreateProcessorPathMappingRequest request,
        CancellationToken cancellationToken);

    Task<bool> DeleteProcessorPathMappingAsync(
        string processorConnectionId,
        string id,
        CancellationToken cancellationToken);
}
