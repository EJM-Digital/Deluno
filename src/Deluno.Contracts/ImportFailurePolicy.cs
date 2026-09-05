namespace Deluno.Contracts;

/// <summary>What Deluno does with the release when an import fails.</summary>
public enum BlockDecision
{
    /// <summary>Nothing about the release was at fault. Never refuse it.</summary>
    Never,

    /// <summary>
    /// Try it once more, and refuse it if it fails the same way again.
    /// </summary>
    /// <remarks>
    /// James: <i>"I dont want it trying again later and continue to try if it
    /// comes up with the same behaviour... I think we need to be harsher... one
    /// retry before it blocks and adds to the list"</i>.
    /// </remarks>
    AfterOneRetry,

    /// <summary>
    /// Deluno knows the file is not what was wanted. No second attempt is worth
    /// anybody's bandwidth.
    /// </summary>
    Immediately
}

/// <summary>
/// The decision table from DESIGN-007, in the one place it is made.
///
/// <para><b>These are shipped defaults, not law.</b> James: <i>"I think it
/// should be case by case and should be configurable options for the user in
/// some blocklist / failure management section of the app"</i>. Every row here
/// is what Deluno does until somebody changes it, and the Failure and blocklist
/// console changes it.</para>
///
/// <para>Pure and static on purpose. The whole value of writing the table down
/// is that it can be walked and asserted, so a reason nobody decided is a test
/// failure rather than a silent default.</para>
/// </summary>
public static class ImportFailurePolicy
{
    /// <summary>The file itself was wrong, and any copy of it will be.</summary>
    public const string NoVideoStream = "noVideoStream";
    public const string LikelySample = "likelySample";
    public const string UnsupportedFile = "unsupportedFile";

    /// <summary>The playability check read the file and rejected it.</summary>
    public const string MediaProbeRejected = "mediaProbeRejected";

    /// <summary>Deluno could not read the file at that moment. Says nothing about it.</summary>
    public const string MediaProbeUnreadable = "mediaProbeUnreadable";

    /// <summary>Downloaded fine; Deluno could not work out which title it is.</summary>
    public const string Unmatched = "unmatched";

    /// <summary>Failed, with no recorded reason.</summary>
    public const string ImportFailed = "importFailed";

    /// <summary>Deluno compared the two and kept the one already held.</summary>
    public const string ReplacementRejected = "replacementRejected";

    /// <summary>The environment, not the release.</summary>
    public const string MissingLibraryRoot = "missingLibraryRoot";

    /// <summary>
    /// The move or copy itself threw. Still downloading, locked by another
    /// process, or on a network path that went away — none of which is the
    /// release's doing.
    /// </summary>
    /// <remarks>
    /// This one was missing from the first draft of the table, and the guard
    /// that reads the pipeline's own call sites found it. Which is the entire
    /// argument for that guard existing.
    /// </remarks>
    public const string IoError = "io";
    public const string MissingSource = "missingSource";
    public const string Permission = "permission";
    public const string HardlinkUnavailable = "hardlinkUnavailable";
    public const string HardlinkFailed = "hardlinkFailed";
    public const string SamePath = "samePath";
    public const string Conflict = "conflict";
    public const string ReplacementOwnershipMismatch = "replacementOwnershipMismatch";

    /// <summary>Every reason this table has an answer for.</summary>
    public static readonly IReadOnlyList<string> KnownReasons =
    [
        NoVideoStream, LikelySample, UnsupportedFile,
        MediaProbeRejected, MediaProbeUnreadable,
        Unmatched, ImportFailed, ReplacementRejected,
        MissingLibraryRoot, MissingSource, Permission, IoError,
        HardlinkUnavailable, HardlinkFailed, SamePath, Conflict,
        ReplacementOwnershipMismatch
    ];

    /// <summary>
    /// Whether this release is refused, and after how many tries.
    /// </summary>
    public static BlockDecision BlockFor(string reasonCode) => reasonCode switch
    {
        // Deluno has read the file and knows what it is. A second attempt at
        // the same release fetches the same bytes.
        NoVideoStream or LikelySample or UnsupportedFile or MediaProbeRejected => BlockDecision.Immediately,

        // The release claimed to be an upgrade and, on inspection, was lower
        // resolution or a shorter cut than what is already held. It
        // misrepresented itself, and every copy of it lies the same way.
        ReplacementRejected => BlockDecision.Immediately,

        // Deluno cannot say whose fault it was, so it proves it twice.
        ImportFailed or MediaProbeUnreadable => BlockDecision.AfterOneRetry,

        // Everything else says something about this installation rather than
        // about the release. Refusing here is how a blocklist fills with things
        // that were never the file's fault.
        _ => BlockDecision.Never
    };

    public static bool ShouldBlock(string reasonCode, int priorFailuresOfSameRelease) => BlockFor(reasonCode) switch
    {
        BlockDecision.Immediately => true,
        BlockDecision.AfterOneRetry => priorFailuresOfSameRelease >= 1,
        _ => false
    };

    /// <summary>
    /// Whether the downloaded file is worth keeping.
    ///
    /// <para>Only where Deluno knows it is not what was wanted. Everything else
    /// might be environmental, and deleting on a guess cannot be undone.</para>
    /// </summary>
    public static bool ShouldDeletePayload(string reasonCode)
        => reasonCode is NoVideoStream or LikelySample or ReplacementRejected;

    /// <summary>
    /// Whether this counts against the download client's health record.
    ///
    /// <para>Exactly one does. The client said the download was complete and
    /// the file was not there — that is the client's fault, and it is what the
    /// three-strike policy exists to catch. Counting a bad <em>file</em> against
    /// the client is how a healthy client gets blamed and eventually
    /// remediated for somebody else's rubbish.</para>
    /// </summary>
    public static bool CountsAsClientStrike(string reasonCode) => reasonCode is MissingSource;

    /// <summary>
    /// Whether a person has to look at it. Raised where Deluno has a usable
    /// file and cannot decide alone what to do with it.
    /// </summary>
    public static bool RaisesRecoveryCase(string reasonCode)
        => reasonCode is Unmatched or ImportFailed or Conflict or ReplacementOwnershipMismatch;

    /// <summary>
    /// Whether to stop searching this library for now.
    ///
    /// <para>A missing root, a permission error or a broken hardlink setup will
    /// fail identically for every title. Carrying on is how you get a hundred
    /// failed imports and one root cause.</para>
    /// </summary>
    public static bool StopsSearching(string reasonCode)
        => reasonCode is MissingLibraryRoot or Permission or HardlinkUnavailable or HardlinkFailed;

    /// <summary>
    /// Whether it was a failure at all.
    ///
    /// <para>Keeping the copy already held is Deluno working correctly. Filing
    /// it as an import failure made the dashboard report a fault every time the
    /// guard did its job.</para>
    /// </summary>
    public static bool IsFailure(string reasonCode) => reasonCode is not ReplacementRejected;
}
