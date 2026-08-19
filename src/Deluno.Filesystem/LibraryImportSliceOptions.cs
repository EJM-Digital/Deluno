namespace Deluno.Filesystem;

/// <summary>
/// How much work one import slice does before handing back.
///
/// These are the numbers that keep a multi-hour import inside the job system:
/// a slice has to finish comfortably inside its lease, and a batch has to be
/// large enough that the transaction cost disappears but small enough that a
/// crash loses very little.
/// </summary>
/// <param name="MaxItemsPerSlice">
/// Entries examined before the slice hands back and queues its continuation.
/// </param>
/// <param name="MaxSliceDuration">
/// The wall-clock cap on a slice, which is what actually bounds it when the
/// disk is slow. Sized well inside the two-minute job lease so a slow slice
/// never looks like a stalled worker.
/// </param>
/// <param name="MovieBatchSize">Movies written per transaction.</param>
/// <param name="SeriesBatchSize">
/// Shows written per transaction. Far smaller than movies because a show is not
/// one row — The Simpsons alone is 885 episode rows plus its seasons, so twenty
/// shows can already be thousands of rows in one transaction.
/// </param>
public sealed record LibraryImportSliceOptions(
    int MaxItemsPerSlice,
    TimeSpan MaxSliceDuration,
    int MovieBatchSize,
    int SeriesBatchSize)
{
    public static LibraryImportSliceOptions Default { get; } = new(
        MaxItemsPerSlice: 2000,
        MaxSliceDuration: TimeSpan.FromSeconds(20),
        MovieBatchSize: 250,
        SeriesBatchSize: 20);
}
