namespace Deluno.Filesystem;

/// <summary>
/// A small, conservative guard used before Deluno reads an external file.
/// Download clients and processors can expose a path before the final bytes
/// have finished copying, so existence alone is not proof that the file is
/// safe to probe or import.
/// </summary>
public static class ImportFileReadiness
{
    public const int RetryableStatusCode = 425;

    private static readonly TimeSpan MinimumStableAge = TimeSpan.FromSeconds(2);

    public static bool IsPreviewReady(ImportPreviewResponse preview)
    {
        if (!preview.SourceExists)
        {
            return false;
        }

        // A reviewed season pack's source path is a directory. Checking that
        // directory with FileInfo always reports a zero-length non-file and
        // incorrectly blocks the job. The executable unit is every member of
        // the authoritative pack plan, so each source file must be stable.
        return preview.Pack is { } pack
            ? pack.Files.Count > 0 && pack.Files.All(file => IsReady(file.SourcePath))
            : IsReady(preview.SourcePath);
    }

    public static bool IsReady(string path)
    {
        try
        {
            var before = new FileInfo(path);
            if (!before.Exists || before.Length == 0 || DateTime.UtcNow - before.LastWriteTimeUtc < MinimumStableAge)
            {
                return false;
            }

            // An exclusive handle is the strongest signal that a download or
            // processor still owns the file. FileShare.Read allows a normal
            // read handle while still rejecting an active writer.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length != before.Length)
                {
                    return false;
                }
            }

            var after = new FileInfo(path);
            return after.Exists &&
                   after.Length == before.Length &&
                   after.LastWriteTimeUtc == before.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
