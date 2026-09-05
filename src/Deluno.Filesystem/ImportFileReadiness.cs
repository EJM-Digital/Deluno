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

    /// <param name="timeProvider">
    /// Supplied only by tests. The age rule reads the wall clock, which made
    /// the test for it a race: pinning the file's write time to "now" leaves no
    /// margin, so two seconds of real time between arranging the file and
    /// checking it made the file legitimately old and failed a test that had
    /// found nothing wrong. Pinning the write time to the *future* removes the
    /// race and the test with it — a negative age is rejected by any positive
    /// threshold, so the assertion would survive the rule being deleted. The
    /// clock is the only thing that can be held still without also making the
    /// test meaningless.
    /// </param>
    public static bool IsReady(string path) => IsReady(path, TimeProvider.System);

    public static bool IsReady(string path, TimeProvider? timeProvider)
    {
        try
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var before = new FileInfo(path);
            if (!before.Exists || before.Length == 0 || now - before.LastWriteTimeUtc < MinimumStableAge)
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
