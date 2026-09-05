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
        DateTimeOffset? previouslyFetchedUtc = null)
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
                $"{clientHoldingRelease} already has a download for {title}, so sending it again would do nothing.",
                "A download client keeps one copy of a release. Asking it for the same one is accepted and then ignored.",
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
                ClearEffect: $"Makes {fetchedFrom} forget the release, so it will accept it again."));
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
                $"Deluno is waiting until {nextEligible:u} before searching again, after an earlier attempt found nothing.",
                $"Last attempt: {wanted.LastSearchResult ?? "not recorded"}.",
                CanClear: true,
                ClearEffect: "Clears the retry delay so it can be searched now."));
        }

        if (wanted?.AvailableUtc is { } available && available > now)
        {
            blockers.Add(new AcquisitionBlocker(
                AcquisitionBlockerKinds.NotYetAvailable,
                "deluno",
                $"{title} is not obtainable yet — Deluno has it as available from {available:u}.",
                "Searching before then spends indexer requests on something nobody has.",
                CanClear: false,
                ClearEffect: "Change the library's availability rule if you want Deluno to look anyway."));
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
