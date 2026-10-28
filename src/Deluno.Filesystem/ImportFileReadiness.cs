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

    /// <summary>What a pack preview says of a member that is still being written; the preview then holds it back from the plan, so readiness reads it here.</summary>
    public const string PackMemberStillBeingWrittenReason = "Source file is still being written or locked.";

    public static bool IsPreviewReady(ImportPreviewResponse preview)
    {
        if (!preview.SourceExists)
        {
            return false;
        }

        // A reviewed season pack's source path is a directory. Checking that
        // directory with FileInfo always reports a zero-length non-file and
        // incorrectly blocks the job. The executable unit is every member of
        // the authoritative pack plan, so each source file must be stable -
        // including a file that will be skipped rather than imported (#652):
        // it still has to be a settled file before Deluno can tell it does
        // not resolve to a canonical episode, not one still arriving.
        if (preview.Pack is { } pack)
        {
            var packSourcePaths = pack.Files.Select(file => file.SourcePath)
                .Concat((pack.SkippedFiles ?? []).Select(file => file.SourcePath))
                .ToArray();
            // A member that is still arriving is named in the pack's block reasons and left out of both lists, so the
            // lists alone would read a pack with one unsettled member as ready.
            if (pack.BlockReasons.Any(reason => reason.Contains(PackMemberStillBeingWrittenReason, StringComparison.Ordinal)))
            {
                return false;
            }

            return packSourcePaths.Length > 0 && packSourcePaths.All(IsReady);
        }

        return IsReady(preview.SourcePath);
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

    /// <summary>
    /// How long an import waits for a file to settle before it gives the job
    /// back to be tried again.
    ///
    /// <para>A processor hands its output over and then finishes with it: Weir
    /// moves its file into place and puts the original's timestamps back
    /// afterwards. The import started in that moment and failed a second later,
    /// so every processed import failed its first attempt and read as failed
    /// until the retry (#562). Seconds of waiting are cheaper than an attempt,
    /// and nothing else is waiting on this worker.</para>
    /// </summary>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Waits, briefly, for a file that is nearly ready. Answers as soon as it is.
    /// </summary>
    public static async Task<bool> WaitUntilReadyAsync(
        string path,
        TimeSpan window,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var giveUpAt = clock.GetUtcNow() + window;
        while (true)
        {
            if (IsReady(path, clock))
            {
                return true;
            }

            if (clock.GetUtcNow() >= giveUpAt)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken).ConfigureAwait(false);
        }
    }

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

            // Shared with a writer's handle: qBittorrent and Deluge (libtorrent)
            // hold a finished torrent's file open for writing while it seeds,
            // so refusing to share with them meant a torrent imported without
            // a processor was never ready while it seeded - found live, #606.
            // An active writer is still caught, by the age and the size and
            // write time staying put across the read.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
