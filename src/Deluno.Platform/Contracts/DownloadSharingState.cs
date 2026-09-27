namespace Deluno.Platform.Contracts;

/// <summary>
/// One finished download the client is still holding on to, and the sentence
/// explaining why (#288).
///
/// This is the answer to "why is my drive full" without opening a torrent
/// client. The title is already safely in the library; this is the *other*
/// copy, the one the download client is still sharing with other people.
/// </summary>
/// <param name="Detail">
/// Written for a person, not a log — "2 days left". It is the evaluator's own
/// words, recorded as it decided them, so what the dashboard says and what
/// Deluno will do can never disagree. It states only what the reader does not
/// already know: the surface showing it has a heading saying these are finished
/// and still sharing, so repeating that on every row would be noise.
/// </param>
/// <param name="NeedsYou">
/// True when the rule can no longer be met and Deluno was told to ask rather
/// than act. Everything else here is simply waiting and wants nothing.
/// </param>
/// <param name="SharesLibraryCopy">
/// True when this copy and the library's are one set of file data — same drive,
/// single-copy links in use. Sharing then costs no disk at all, and saying it
/// uses gigabytes would be a lie.
/// </param>
/// <param name="LostHardlink">
/// True only when configuration said this should be sharing (same drive,
/// hardlinks on) but the real files say otherwise — the library's copy was
/// upgraded or deleted out from under it (#819, #926). Distinct from a
/// download that was never going to share (different drives, or hardlinks
/// off): that one costs space by design and is not a problem to flag.
/// </param>
public sealed record DownloadSharingHold(
    string ClientId,
    string ClientName,
    string QueueItemId,
    string Title,
    string Detail,
    long SizeBytes,
    bool NeedsYou,
    bool SharesLibraryCopy,
    bool LostHardlink = false);

/// <summary>
/// Everything the download clients are still holding after import, as of the
/// last time the worker looked.
/// </summary>
/// <param name="ExtraBytes">
/// Disk these holds are keeping that the library copy does not already account
/// for. Zero on an install where downloads and library share one set of file
/// data, which is the whole reason that arrangement is worth having.
/// </param>
/// <param name="DriveNote">
/// Plain English about where the two copies live, present only when they are
/// genuinely two — the one place that fact changes what a user should do.
/// </param>
/// <param name="ObservedUtc">Null when the worker has not run a sharing pass yet.</param>
public sealed record DownloadSharingSnapshot(
    IReadOnlyList<DownloadSharingHold> Holds,
    long ExtraBytes,
    string? DriveNote,
    DateTimeOffset? ObservedUtc)
{
    public static DownloadSharingSnapshot Empty { get; } = new([], 0, null, null);
}
