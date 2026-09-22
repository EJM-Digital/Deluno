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
    /// Every hand-off still with a processor, across every library.
    ///
    /// <para>Unlimited, because these are few by definition — a processor holds
    /// a handful of files, not a history. A removal has to find its title's
    /// open hand-offs exactly, and the newest-first listing above would miss one
    /// on a busy install (#563).</para>
    /// </summary>
    Task<IReadOnlyList<ProcessorHandoffItem>> ListOpenProcessorHandoffsAsync(CancellationToken cancellationToken);

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
}
