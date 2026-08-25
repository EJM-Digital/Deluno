using Deluno.Filesystem;

namespace Deluno.Worker.Services;

/// <summary>
/// Prevents the folder watcher from treating a file that is still being copied
/// into the processor output folder as an importable result.
/// </summary>
public static class ProcessorOutputReadiness
{
    public static bool IsReady(string path) => ImportFileReadiness.IsReady(path);
}
