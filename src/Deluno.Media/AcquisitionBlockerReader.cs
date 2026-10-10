using Deluno.Contracts;

namespace Deluno.Media;

/// <summary>
/// Reads every reason a title will not be fetched, and says them plainly.
///
/// <para><b>Composed rather than owned.</b> None of these facts is new: the
/// wanted row already knows the title has a file, the dispatch already knows a
/// client has it, the hand-off already knows the processor is holding it. What
/// was missing was anywhere that put them together and answered the question a
/// person actually asks, which is not "what is the wanted status" but "why is
/// this not downloading".</para>
///
/// <para>Deliberately a reader. It states what is true; it does not decide
/// whether to search, because two places deciding that is how they end up
/// disagreeing.</para>
/// </summary>
public static class AcquisitionBlockerReader
{
    /// <param name="wanted">
    /// The title's wanted row, or null when no library tracks it yet.
    /// </param>
    /// <param name="clientHoldingRelease">
    /// The download client that already has a release for this title, when one
    /// does. Named rather than boolean because clearing it means going to that
    /// client, and a person needs to know which.
    /// </param>
    /// <param name="processorHoldingFile">
    /// The processor a hand-off is still with, when it has not finished.
    /// </param>
    /// <param name="isImportExcluded">
    /// Whether an import list or collection has been told not to re-add this.
    /// </param>
    /// <param name="now">
    /// Read once by the caller so every window in one answer is measured
    /// against the same instant.
    /// </param>
    public static AcquisitionBlockersResponse Read(
        string mediaId,
        string mediaType,
        string title,
        MediaWantedItem? wanted,
        string? clientHoldingRelease,
        string? processorHoldingFile,
        bool isImportExcluded,
        bool nextSearchSkipped,
        DateTimeOffset now,
        string? previouslyFetchedFrom = null,
        DateTimeOffset? previouslyFetchedUtc = null,
        int blockedReleaseCount = 0)
    {
        var blockers = new List<AcquisitionBlocker>();

        if (wanted is { HasFile: true, QualityCutoffMet: true })
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.AlreadyHeld,
                "deluno",
                $"{title} is already here at the quality you asked for, so Deluno is not looking for another copy.",
                $"Holding {wanted.CurrentQuality ?? "an unrecorded quality"} against a target of {wanted.TargetQuality ?? "none"}.",
                CanClear: false,
                ClearEffect: "Lower the profile's cutoff, or delete the file, if you want a different copy."));
        }

        if (clientHoldingRelease is { Length: > 0 })
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.DownloadInFlight,
                clientHoldingRelease,
                // #1138: a download just started is progress, not a reason the title will not download.
                $"Downloading in {clientHoldingRelease}.",
                $"{clientHoldingRelease} has {title} on its way. A download client keeps one copy of a release, so sending the same one again would be accepted and then ignored.",
                CanClear: true,
                ClearEffect: $"Removes that download from {clientHoldingRelease}, so the release can be fetched again."));
        }

        if (processorHoldingFile is { Length: > 0 })
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.ProcessorHoldingFile,
                processorHoldingFile,
                $"{processorHoldingFile} still has this file, so Deluno is waiting rather than fetching it again.",
                "A hand-off that has not finished is why the import has not run.",
                CanClear: true,
                ClearEffect: $"Forgets the hand-off so the file can be sent to {processorHoldingFile} again."));
        }

        // Fetched before, and gone now.
        //
        // Suppressed while something is downloading: if a client has this
        // release in hand right now, "you fetched this once" is not the answer
        // to why it is not arriving, and two blockers naming the same client
        // would read as two problems.
        //
        // The wording claims only what Deluno knows. It cannot see a download
        // client's memory without asking, and the client may be unreachable or
        // may have been cleared by hand — so this says what Deluno did and what
        // it no longer has, and offers the override, rather than asserting a
        // fact about somebody else's state.
        if (previouslyFetchedFrom is { Length: > 0 } fetchedFrom &&
            wanted is not { HasFile: true } &&
            clientHoldingRelease is not { Length: > 0 })
        {
            var when = previouslyFetchedUtc is { } fetchedUtc
                ? $" on {fetchedUtc:d MMMM yyyy}"
                : string.Empty;

            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.PreviouslyDownloaded,
                fetchedFrom,
                $"Deluno fetched {title} through {fetchedFrom}{when}, and the file is no longer here.",
                $"A download client refuses a release it remembers — a torrent client by its infohash, a usenet client from its history — so the next attempt is accepted and then quietly ignored. Deluno cannot see {fetchedFrom}'s memory without asking it, so this may already be clear.",
                CanClear: true,
                ClearEffect: $"Removes the old copy of the release, with its files, from {fetchedFrom}, so it will accept it again."));
        }

        // The blocklist answering for itself.
        //
        // Refusing releases is a mechanism that can become the problem: refuse
        // enough of them and a search finds nothing, with the reason sitting in
        // a list nobody thought to open. So it is stated wherever somebody asks
        // why a title is not arriving — carefully, because Deluno cannot know
        // from here whether a good candidate still exists. It reports the fact
        // and offers the undo rather than claiming to be the cause.
        if (blockedReleaseCount > 0 && wanted is not { HasFile: true })
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.ReleasesBlocked,
                "deluno",
                blockedReleaseCount == 1
                    ? $"One release for {title} has been refused, so searches have one fewer option."
                    : $"{blockedReleaseCount} releases for {title} have been refused, so searches have fewer options.",
                "Refused after it failed to download, process or import. If every copy anyone offers has been refused, a search will find nothing and report exactly that.",
                CanClear: true,
                ClearEffect: blockedReleaseCount == 1
                    ? "Un-refuses that release, so searches can use it again."
                    : $"Un-refuses all {blockedReleaseCount}, so searches can use them again."));
        }

        if (isImportExcluded)
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.ImportExcluded,
                "deluno",
                $"{title} is on the exclusion list, so import lists and collections will not add it back.",
                "This is usually added when a title is removed with the exclusion option ticked.",
                CanClear: true,
                ClearEffect: "Removes the exclusion, so lists and collections may add it again."));
        }

        if (nextSearchSkipped)
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.SearchSkipped,
                "deluno",
                "The next scheduled search for this title was set to be skipped.",
                "Manual search is unaffected; only the next automatic pass is.",
                CanClear: true,
                ClearEffect: "Puts it back into the next scheduled search."));
        }

        if (wanted?.NextEligibleSearchUtc is { } nextEligible && nextEligible > now)
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.SearchDeferred,
                "deluno",
                // The old wording asserted the last attempt "found nothing".
                // It says that whatever happened - and the common case on this
                // path is the opposite: Deluno found candidates, chose one, and
                // is holding it for a person. Do not claim an outcome the
                // detail underneath is about to contradict.
                $"Deluno will search again in about {PlainWait(nextEligible - now)}.",
                LastAttemptDetail(wanted.LastSearchResult),
                CanClear: true,
                ClearEffect: "Clears the retry delay so it can be searched now.",
                UntilUtc: nextEligible));
        }

        if (wanted?.AvailableUtc is { } available && available > now)
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.NotYetAvailable,
                "deluno",
                $"{title} is not obtainable yet — Deluno expects it to be available in about {PlainWait(available - now)}.",
                "Searching before then spends indexer requests on something nobody has.",
                CanClear: false,
                ClearEffect: "Change the library's availability rule if you want Deluno to look anyway.",
                UntilUtc: available));
        }

        return new AcquisitionBlockersResponse(
            mediaId,
            mediaType,
            title,
            blockers,
            NothingIsBlocking: blockers.Count == 0,
            Summary: Describe(title, blockers),
            CanForce: blockers.Any(blocker => blocker.CanClear));
    }

    /// <summary>
    /// What the last search actually said, quoted as it was recorded. The
    /// recorded result is already a sentence, so appending a full stop to it
    /// produced "below your cutoff.." on screen.
    /// </summary>
    /// <summary>A wait in everyday words: "5 minutes", "1 hour", "6 hours", "2 days".</summary>
    internal static string PlainWait(TimeSpan wait)
    {
        static string Count(double value, string unit) => $"{(int)Math.Round(value)} {unit}{((int)Math.Round(value) == 1 ? "" : "s")}";
        if (wait < TimeSpan.FromMinutes(1)) return "a minute";
        if (wait < TimeSpan.FromHours(1)) return Count(wait.TotalMinutes, "minute");
        if (wait < TimeSpan.FromHours(48)) return Count(wait.TotalHours, "hour");
        return Count(wait.TotalDays, "day");
    }

    private static string LastAttemptDetail(string? lastSearchResult)
    {
        var recorded = lastSearchResult?.Trim();
        if (string.IsNullOrEmpty(recorded))
        {
            return "Last attempt: not recorded.";
        }

        return recorded.EndsWith('.') || recorded.EndsWith('!') || recorded.EndsWith('?')
            ? $"Last attempt: {recorded}"
            : $"Last attempt: {recorded}.";
    }

    /// <summary>
    /// One sentence for the whole answer. Counts rather than a list, because a
    /// screen that reads out five clauses is one nobody finishes.
    /// </summary>
    private static string Describe(string title, IReadOnlyList<AcquisitionBlocker> blockers)
    {
        if (blockers.Count == 0)
        {
            return $"Nothing is stopping {title} from being downloaded.";
        }

        var clearable = blockers.Count(blocker => blocker.CanClear);
        var first = blockers[0].Summary;

        if (blockers.Count == 1)
        {
            return clearable == 1
                ? $"{first} You can override this."
                : first;
        }

        var others = blockers.Count - 1;
        var rest = others == 1 ? "one other reason" : $"{others} other reasons";

        return clearable > 0
            ? $"{first} There is also {rest}, and {clearable} of the {blockers.Count} can be overridden."
            : $"{first} There is also {rest}, none of which Deluno can clear for you.";
    }
}
