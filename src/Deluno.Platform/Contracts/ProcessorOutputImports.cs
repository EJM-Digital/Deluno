using Deluno.Libraries.Contracts;

namespace Deluno.Platform.Contracts;

/// <summary>The import queued for a processor's output.</summary>
/// <param name="DispatchId">The grab the output was traced back to, when Deluno could.</param>
public sealed record ProcessorOutputImport(string JobId, string? DispatchId, string? EntityId);

/// <summary>
/// Queueing the import of a processed file (#526). Two things find a processed
/// file - the processor's completion callback and the output-folder watcher -
/// and both must import it as the title that was grabbed, into the library that
/// grabbed it. One implementation, behind an interface because naming it from
/// the catalogue needs the catalogues this module does not own.
/// </summary>
public interface IProcessorOutputImportQueue
{
    /// <param name="source">What found the output, for the job's history.</param>
    Task<ProcessorOutputImport> QueueAsync(
        LibraryItem library,
        ProcessorHandoffItem handoff,
        string outputPath,
        string source,
        CancellationToken cancellationToken);
}
