namespace Deluno.Contracts;

/// <summary>
/// A release Deluno has decided not to use again, and why.
///
/// <para>DESIGN-007 decisions 1 and 2: a download that turns out to be junk
/// means that exact copy is refused, future searches skip it and say so, and
/// the refusal lasts until somebody clears it. James chose permanence
/// deliberately, and it is only safe because nothing is hidden — every block
/// is listed, with its reason, and can be undone.</para>
/// </summary>
/// <param name="ReleaseKey">
/// What a search candidate is matched against when no hash is known: the
/// release name and the indexer, normalised. Kept even now that a hash key
/// also exists (<see cref="BlockedReleaseKeys.ForHash"/>), because a search
/// candidate does not always carry one yet, and every block made before #874
/// only has this key stored.
/// </param>
/// <param name="Title">
/// Recorded for the person reading the list rather than for matching. A file
/// with no video stream is a bad file whichever title it was fetched for.
/// </param>
/// <param name="ReasonCode">
/// The import or grab failure that caused it, so the rules screen can say which
/// setting produced this block and the list can be filtered by cause.
/// </param>
/// <param name="State">
/// Whether this is a decision or a question. See <see cref="BlockedReleaseStates"/>.
/// </param>
public sealed record BlockedRelease(
    string Id,
    string ReleaseKey,
    string ReleaseName,
    string IndexerName,
    string MediaType,
    string? EntityId,
    string? Title,
    string ReasonCode,
    string Reason,
    string? TorrentHashOrItemId,
    string? DownloadClientId,
    string? DownloadClientName,
    DateTimeOffset BlockedUtc,
    string State = BlockedReleaseStates.Refused,
    /// <summary>
    /// Whether a person clicked Refuse or Deluno decided this on its own
    /// (#800). Sonarr says this explicitly on its blocklist entries; Deluno's
    /// did not before this, though the two call sites that ever set it
    /// manually already existed and used the same reason code for it.
    /// </summary>
    string Origin = BlockedReleaseOrigins.Automatic)
{
    /// <summary>
    /// Whether searches skip this release from every indexer rather than only
    /// the one it came from.
    ///
    /// <para>Always true (#874). James decided the key is the release's
    /// identity — its hash when one is known, and its name everywhere else —
    /// whatever the refusal reason: a stall on one indexer is exactly as
    /// disqualifying on another. Kept as a property rather than a literal so
    /// the blocklist screen and its tests read the same true it always was
    /// under #585, without a client-side rule to keep in sync.</para>
    /// </summary>
    public bool EveryIndexer => true;
}

/// <summary>
/// A blocklist entry is either a decision Deluno made or a question it is
/// asking.
///
/// <para>The second kind exists because the rules screen offers <i>ask me</i>,
/// and "ask me" has to leave something behind or it is a slower way of doing
/// nothing. A proposal is recorded with its reason, changes nothing about what
/// searches do, and waits.</para>
/// </summary>
public static class BlockedReleaseStates
{
    /// <summary>Decided. Searches skip it and say so.</summary>
    public const string Refused = "refused";

    /// <summary>Asked. Searches are unaffected until somebody answers.</summary>
    public const string Proposed = "proposed";
}

/// <summary>
/// Whether a block was a person's decision or Deluno's own (#800).
/// </summary>
public static class BlockedReleaseOrigins
{
    /// <summary>A person clicked Refuse, or removed a download from the Queue and said it failed.</summary>
    public const string Manual = "manual";

    /// <summary>Deluno decided this on its own — a stalled download, a client-reported failure, a rejected import, or a processor rejection.</summary>
    public const string Automatic = "automatic";
}

public static class BlockedReleaseKeys
{
    /// <summary>
    /// One key for one release, stable across the difference between what an
    /// indexer prints and what a person reads.
    ///
    /// <para>Case and surrounding whitespace are discarded; nothing else is.
    /// Release names carry meaning in their punctuation — a dot-separated name
    /// and a space-separated one are usually the same release, but stripping
    /// separators would also merge "S01E01" with "S01E011", so the conservative
    /// form is used and the cost of missing a match is one extra download
    /// rather than a wrongly refused release.</para>
    /// </summary>
    public static string For(string releaseName, string? indexerName)
        => $"{releaseName.Trim().ToLowerInvariant()}|{(indexerName ?? string.Empty).Trim().ToLowerInvariant()}";

    /// <summary>
    /// The key that matches this release from any indexer (#585, #874).
    ///
    /// <para>Never stored: it is added when the blocklist is read, for every
    /// refused release, whatever the reason. James decided the blocklist key
    /// is the release's identity across every indexer, not a judgement about
    /// which reasons are "about the file" — a stall on one indexer is refused
    /// the same as a bad file on another, and searches are told the same
    /// either way. <c>*</c> stands in for the indexer, which no real indexer
    /// is named.</para>
    /// </summary>
    public static string ForEveryIndexer(string releaseName)
        => For(releaseName, "*");

    /// <summary>
    /// The key a torrent's info hash or an NZB's id/GUID matches on, whatever
    /// indexer offers it and whatever the release is named this time (#802,
    /// #874). James decided the identity of a release is its content, not its
    /// listing: a retitled re-upload of the same file is still that file, and
    /// the same title with a different hash is a different file.
    ///
    /// <para>Checked before <see cref="For"/> and <see cref="ForEveryIndexer"/>,
    /// never instead of them — a candidate with no known hash yet (a torrent
    /// offered only as a .torrent link, or an indexer that prints no GUID) has
    /// nothing to check this against, and still needs the name-based keys.</para>
    /// </summary>
    public static string ForHash(string torrentHashOrItemId)
        => $"hash|{torrentHashOrItemId.Trim().ToLowerInvariant()}";
}
