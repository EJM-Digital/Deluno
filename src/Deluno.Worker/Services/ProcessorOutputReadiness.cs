using Deluno.Filesystem;

namespace Deluno.Worker.Services;

/// <summary>
/// Prevents the folder watcher from treating a file that is still being copied
/// into the processor output folder as an importable result.
/// </summary>
public static class ProcessorOutputReadiness
{
    // Two overloads rather than an optional parameter: this is passed around as
    // a Func<string, bool>, and a default argument stops the method group
    // matching it.
    public static bool IsReady(string path) => ImportFileReadiness.IsReady(path);

    /// <summary>A finished download's file is ready to hand to a processor, even while its client still holds it open (#1519).</summary>
    public static bool IsReadyToHandOver(string path) => ImportFileReadiness.IsReadyToHandOver(path);

    public static bool IsReady(string path, TimeProvider timeProvider)
        => ImportFileReadiness.IsReady(path, timeProvider);
}
