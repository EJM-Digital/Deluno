namespace Deluno.Contracts;

/// <summary>
/// When a processor has refused enough copies of one title that fetching
/// another is a waste (#576, #583).
///
/// <para>A processor that refuses everything walks the whole result list:
/// reject, block, search, grab, download, submit, reject — each round a full
/// download's bandwidth and disk. #576 stopped the search queued straight after
/// a rejection and said Deluno had stopped looking, but the title was still
/// missing, so the scheduled search picked it up again and the rounds went on.
/// The rejection and the search now ask the same question here.</para>
///
/// <para>Counted from the blocklist rather than stored a second time, so the
/// person's way out is the one the recovery case already names: unblock a
/// refused copy, the count falls under the limit, and searching resumes by
/// itself.</para>
/// </summary>
public static class ProcessorRefusals
{
    /// <summary>
    /// Enough that a genuinely bad release or two does not stop a title
    /// arriving; few enough that a processor refusing everything cannot spend a
    /// fortnight downloading the whole result list.
    /// </summary>
    public const int MostPerTitle = 5;

    /// <summary>How many copies of one title a processor has refused.</summary>
    public static int Count(IEnumerable<BlockedRelease> refusedForTitle)
        => refusedForTitle.Count(release =>
            string.Equals(release.ReasonCode, ImportFailurePolicy.ProcessorRejected, StringComparison.OrdinalIgnoreCase)
            && string.Equals(release.State, BlockedReleaseStates.Refused, StringComparison.OrdinalIgnoreCase));

    public static bool StopSearching(int refused) => refused >= MostPerTitle;

    /// <summary>What a held search says on the title, in one sentence.</summary>
    public static string WhySearchIsHeld(int refused)
        => $"Not searched: the processor has refused {refused} copies of this. Unblock one, or import one from the blocklist, and searching resumes.";
}
