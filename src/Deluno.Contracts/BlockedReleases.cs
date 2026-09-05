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
/// What a search candidate is matched against: the release name and the
/// indexer, normalised. Not the infohash — a candidate does not have one yet,
/// and by the time it does the decision has already been made.
/// </param>
/// <param name="Title">
/// Recorded for the person reading the list rather than for matching. A file
/// with no video stream is a bad file whichever title it was fetched for.
/// </param>
/// <param name="ReasonCode">
/// The import or grab failure that caused it, so the rules screen can say which
/// setting produced this block and the list can be filtered by cause.
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
    DateTimeOffset BlockedUtc);

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
}
