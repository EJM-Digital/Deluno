namespace Deluno.Downloader.Postprocessing;

/// <summary>
/// One step in the post-processing pipeline (rename, flatten, sample
/// filter, etc.). Each step transforms a set of files in place, returning
/// the new set after its work.
/// </summary>
public interface IPostProcessor
{
    /// <summary>
    /// Process the given files in-place under <paramref name="workingDir"/>.
    /// Returns the post-step file set (may be a strict subset, e.g.
    /// after sample filtering).
    /// </summary>
    Task<IReadOnlyList<string>> ProcessAsync(
        string workingDir,
        IReadOnlyList<string> files,
        CancellationToken ct);
}
