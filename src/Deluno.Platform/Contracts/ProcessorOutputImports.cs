using Deluno.Libraries.Contracts;

namespace Deluno.Platform.Contracts;

/// <summary>The import queued for a processor's output.</summary>
/// <param name="JobId">The import job; null when nothing was queued.</param>
/// <param name="DispatchId">The grab the output was traced back to, when Deluno could.</param>
/// <param name="AlreadyImported">
/// The download this output came from was already imported (#1155), so the
/// output is a second copy: nothing was queued, and the processor's copy can go.
/// </param>
/// <param name="NotImportedBecause">
/// Why nothing was queued: the title that was grabbed has been removed from
/// Deluno since, so importing would bring back a title someone removed.
/// </param>
public sealed record ProcessorOutputImport(string? JobId, string? DispatchId, string? EntityId, string? NotImportedBecause = null, bool AlreadyImported = false);

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
        CancellationToken cancellationToken,
        string? retriedBecause = null);

    /// <summary>
    /// Whether the download behind this hand-off was already imported (#1155):
    /// the latest fetch of that release from that client holds its file in the
    /// library. A genuine upgrade is a newer fetch and is not.
    /// </summary>
    Task<bool> WasAlreadyImportedAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken);

    /// <summary>
    /// When the title this hand-off's download is for was added, if it was added after the hand-off
    /// was made and holds no file (#1155): the title was removed and added again, the earlier import
    /// went into the one that was removed, and the processor's output is still owed. Null otherwise.
    /// </summary>
    Task<DateTimeOffset?> TitleCameBackWithoutItsFileAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the title this hand-off was for has its file now, imported through another download
    /// fetched after this hand-off was made (#1155). Nothing is owed to such a hand-off.
    /// </summary>
    Task<bool> TitleHasFileFromAnotherDownloadAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this finished hand-off still owes its title a file (#1155): its import job failed or gave up,
    /// and the title (the one that is there now) holds no file. Whatever the order of the title's return
    /// and the hand-off.
    /// </summary>
    Task<bool> OwesItsTitleItsFileAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this release was sent to the same client again after this hand-off was made, and that newer send has
    /// not been imported (#1242): a person said "I do not want it" and sent it again. The newer send's own hand-off may
    /// not exist yet, and whatever the processor reports about this older one belongs to the newer send.
    /// </summary>
    Task<bool> WasSentAgainAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken);
}
