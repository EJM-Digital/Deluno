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
