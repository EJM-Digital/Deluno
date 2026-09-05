namespace Deluno.Contracts;

/// <summary>
/// Why a title will not download, said plainly, and what can be done about it.
///
/// <para><b>The gap this closes.</b> Every media manager accumulates records
/// that quietly stop a title being fetched again — a failed grab that was
/// blocklisted, an exclusion added when it was deleted, a download client that
/// still holds the release, a processor that has already handled the file.
/// Radarr's blocklist is the clearest example: a release that fails "will not
/// be automatically downloaded ever again", and entries "remain in the
/// blocklist forever unless you manually remove them". The mechanism is right —
/// without it a failed import becomes an endless re-download loop — but it is
/// silent. The user is left with a title that simply never arrives and no way
/// to find out why.</para>
///
/// <para>Deluno keeps the mechanism and drops the silence. Every reason a title
/// will not be fetched is a record with a sentence attached, and the ones that
/// are safe to clear say so, so that a person can override them deliberately
/// rather than by deleting things at random.</para>
/// </summary>
public sealed record AcquisitionBlocker(
    /// <summary>Stable identifier for this kind of blocker, for the UI to key on.</summary>
    string Kind,

    /// <summary>
    /// Which system is holding the record: <c>deluno</c>, or the name of the
    /// download client or processor. What a person has to be told, because
    /// clearing it may mean touching something other than Deluno.
    /// </summary>
    string Source,

    /// <summary>One line, in the words a person would use.</summary>
    string Summary,

    /// <summary>
    /// What is actually recorded — the release name, the status, the date.
    /// Enough to recognise the thing being described.
    /// </summary>
    string Detail,

    /// <summary>
    /// Whether Deluno can remove this itself. False is not a dead end: it means
    /// the answer is somewhere else, and <see cref="Summary"/> has to say
    /// where.
    /// </summary>
    bool CanClear,

    /// <summary>
    /// What clearing it would do, stated before it is done. A force is
    /// destructive across systems Deluno does not own, so it does not get to be
    /// a surprise.
    /// </summary>
    string? ClearEffect = null);

/// <summary>
/// Everything standing between a title and its next download.
/// </summary>
public sealed record AcquisitionBlockersResponse(
    string MediaId,
    string MediaType,
    string Title,
    IReadOnlyList<AcquisitionBlocker> Blockers,

    /// <summary>
    /// True when nothing is in the way. Not the same as "a download would
    /// succeed" — the indexers still have to have it — which is why the wording
    /// below is about obstacles rather than promises.
    /// </summary>
    bool NothingIsBlocking,

    /// <summary>
    /// The whole answer in one sentence, so a screen has something honest to
    /// show without having to compose it from the parts.
    /// </summary>
    string Summary,

    /// <summary>
    /// Whether a force would change anything. False when every blocker is one
    /// Deluno cannot clear, and offering the button anyway would be a lie.
    /// </summary>
    bool CanForce);

/// <summary>What a force actually did, named record by record.</summary>
public sealed record AcquisitionOverrideResponse(
    string MediaId,
    IReadOnlyList<string> Cleared,
    IReadOnlyList<string> CouldNotClear,
    bool SearchStarted,
    string Summary);

public static class AcquisitionBlockerKinds
{
    /// <summary>The library already holds this at or above its target.</summary>
    public const string AlreadyHeld = "already-held";

    /// <summary>A download for this title is already with a client.</summary>
    public const string DownloadInFlight = "download-in-flight";

    /// <summary>The processor still has the file, so the import has not run.</summary>
    public const string ProcessorHoldingFile = "processor-holding-file";

    /// <summary>An import list or collection is barred from re-adding it.</summary>
    public const string ImportExcluded = "import-excluded";

    /// <summary>The next scheduled search was deliberately skipped.</summary>
    public const string SearchSkipped = "search-skipped";

    /// <summary>Searching is deferred until a retry window opens.</summary>
    public const string SearchDeferred = "search-deferred";

    /// <summary>Not out yet, so there is nothing to find.</summary>
    public const string NotYetAvailable = "not-yet-available";

    /// <summary>
    /// Deluno fetched this once and no longer holds the file, so the download
    /// client may still be refusing the release.
    ///
    /// <para>The one this whole feature was built for, and the last to be
    /// added — because the source that finds blockers only looked at downloads
    /// that had not finished importing. A title that downloaded, imported and
    /// was then removed produced no blocker at all, which is precisely the
    /// case somebody hits when they delete a film and ask for it again.</para>
    /// </summary>
    public const string PreviouslyDownloaded = "previously-downloaded";
}
