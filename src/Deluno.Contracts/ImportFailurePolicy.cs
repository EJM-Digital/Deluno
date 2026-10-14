using System.Text.Json.Serialization;

namespace Deluno.Contracts;

/// <summary>What Deluno does with the release when an import fails.</summary>
/// <remarks>
/// Sent as its name rather than its number, matching
/// <see cref="IntegrationFailureKind"/>. A settings screen whose stored values
/// are ordinals breaks the day somebody inserts a member in the middle.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
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
    Immediately,

    /// <summary>
    /// Decide nothing; put it in front of a person and wait.
    ///
    /// <para>Only ever chosen by the user — nothing ships with it. It exists
    /// because the right harshness depends on the library: somebody on a flaky
    /// share would rather look at each one than have Deluno quietly narrow its
    /// own options. The failure is recorded as a <b>proposed</b> refusal, which
    /// searches ignore, and stays that way until somebody answers.</para>
    /// </summary>
    AskMe
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

    /// <summary>
    /// The release is for another show of the same name, or carries another episode's title (#1166, #1173). Any
    /// copy of that release is just as wrong, so it is refused at once.
    /// </summary>
    public const string WrongRelease = "wrongRelease";

    /// <summary>Failed, with no recorded reason.</summary>
    public const string ImportFailed = "importFailed";

    /// <summary>Deluno compared the two and kept the one already held.</summary>
    public const string ReplacementRejected = "replacementRejected";

    /// <summary>
    /// What the processor handed back is materially less than what it was given
    /// — much shorter, or missing all its audio or video (#574).
    ///
    /// <para>Nothing about the release: the download was fine and a fresh copy
    /// of it would be fine too. It is the processor that needs looking at, so
    /// this never refuses the release.</para>
    /// </summary>
    public const string ProcessedFileIsLess = "processedFileIsLess";

    /// <summary>The environment, not the release.</summary>
    public const string MissingLibraryRoot = "missingLibraryRoot";

    /// <summary>
    /// The destination volume would drop below the configured minimum free
    /// space if the import completed (#765). Nothing about the release: a
    /// fresh copy of the same file would fail the same way until space is
    /// freed or the floor is lowered.
    /// </summary>
    public const string InsufficientFreeSpace = "insufficientFreeSpace";

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

    /// <summary>
    /// The source path has a <c>..</c> in it, so the import refused to touch it (#1306). The path came from a client or
    /// processor, not from the release, so nothing about the release is at fault: never refused, never retried on its
    /// own, shown in plain words, and the file is left exactly where it is.
    /// </summary>
    public const string UnsafeSourcePath = "unsafeSourcePath";

    /// <summary>
    /// The processing stage could not use the file and deleted it (#497).
    ///
    /// <para>Not an import failure and not in <see cref="KnownReasons"/>: the
    /// person already chose this outcome when they set the processor's reject
    /// policy, so there is no rule here for them to answer again. It is still a
    /// reason a release is refused, so the blocklist has words for it and the
    /// client is told to forget the copy.</para>
    /// </summary>
    public const string ProcessorRejected = "processorRejected";

    /// <summary>
    /// The download stopped: stalled, in error, or nothing arriving, for long
    /// enough to reach the strike threshold (#560).
    ///
    /// <para>Not in <see cref="KnownReasons"/> for the same reason as
    /// <see cref="ProcessorRejected"/>: the person answered it already, with
    /// "Block this release" under Failed downloads. Nothing was imported, so
    /// Deluno cannot say whether the swarm, the tracker or the client was at
    /// fault, and the copy is left in the client.</para>
    /// </summary>
    public const string DownloadStalled = "downloadStalled";

    /// <summary>
    /// The download client itself said this release failed — NZBGet's
    /// FAILURE, DELETED or WARNING history statuses, SABnzbd's Failed status,
    /// or a client's failure webhook (#647). Unlike <see cref="ImportFailed"/>,
    /// nothing is inferred from a timeout: the client answered directly, so
    /// there is nothing to prove twice before refusing the release.
    /// </summary>
    public const string ClientReportedFailure = "clientReportedFailure";

    /// <summary>
    /// A person said so directly — the Queue's "It's bad" removal reason
    /// (cleanup sheet J1/J2, #924). Filed the same as a file Deluno read and
    /// rejected itself: a person looking at the download decided it is bad in
    /// exactly the sense <see cref="NoVideoStream"/> and the others are, so it
    /// refuses every indexer's copy of the release rather than only the one
    /// downloaded, and needs no retry to prove it twice.
    /// </summary>
    /// <remarks>
    /// Not in <see cref="KnownReasons"/> for the same reason as
    /// <see cref="ProcessorRejected"/> and <see cref="DownloadStalled"/>: the
    /// person already answered the question this table exists to answer.
    /// </remarks>
    public const string ManualFailure = "manualFailure";

    /// <summary>
    /// The download client refused the release outright, at grab time, before
    /// anything was downloaded — a dead link, or a file it could not parse
    /// (#876, cleanup sheet B2). Nothing was fetched, so unlike
    /// <see cref="ClientReportedFailure"/> there is no payload to remove or
    /// delete; only the release itself is refused so a later search picks a
    /// different one.
    /// </summary>
    public const string DeadOrBrokenLink = "deadOrBrokenLink";

    /// <summary>
    /// Checkpoint A (#917): opened before a processor or the library ever
    /// sees it and found wrong — lower resolution than the name claims, far
    /// from the known runtime, missing every track in a wanted language, or
    /// will not open or play. Every one of those is Deluno reading the file
    /// for itself, so a second copy of the same release would read exactly
    /// as wrong: refused immediately and its payload removed, the same as
    /// <see cref="NoVideoStream"/> and <see cref="MediaProbeRejected"/>.
    /// </summary>
    public const string DownloadIntegrityFailed = "downloadIntegrityFailed";

    /// <summary>Every reason this table has an answer for.</summary>
    public static readonly IReadOnlyList<string> KnownReasons =
    [
        NoVideoStream, LikelySample, UnsupportedFile,
        MediaProbeRejected, MediaProbeUnreadable,
        Unmatched, WrongRelease, ImportFailed, ReplacementRejected,
        MissingLibraryRoot, MissingSource, Permission, IoError,
        HardlinkUnavailable, HardlinkFailed, SamePath, Conflict,
        ReplacementOwnershipMismatch, ProcessedFileIsLess, ClientReportedFailure,
        DeadOrBrokenLink, InsufficientFreeSpace, DownloadIntegrityFailed, UnsafeSourcePath
    ];

    /// <summary>
    /// Whether this release is refused, and after how many tries.
    /// </summary>
    public static BlockDecision BlockFor(string reasonCode) => reasonCode switch
    {
        // Deluno has read the file and knows what it is. A second attempt at
        // the same release fetches the same bytes.
        NoVideoStream or LikelySample or UnsupportedFile or MediaProbeRejected or WrongRelease => BlockDecision.Immediately,

        // The release claimed to be an upgrade and, on inspection, was lower
        // resolution or a shorter cut than what is already held. It
        // misrepresented itself, and every copy of it lies the same way.
        ReplacementRejected => BlockDecision.Immediately,

        // The client did not go quiet and leave Deluno guessing — it said
        // outright that this release failed. Nothing is proven twice about a
        // fact the client already stated.
        ClientReportedFailure => BlockDecision.Immediately,

        // The client refused the release at grab time, before anything was
        // downloaded. A dead link or an unparseable file fetches the same
        // dead link or unparseable file a second time.
        DeadOrBrokenLink => BlockDecision.Immediately,

        // Checkpoint A opened the file itself and found it wrong. A second
        // copy of the same release reads exactly the same way.
        DownloadIntegrityFailed => BlockDecision.Immediately,

        // Deluno cannot say whose fault it was, so it proves it twice.
        ImportFailed or MediaProbeUnreadable => BlockDecision.AfterOneRetry,

        // The download was fine and a fresh copy of it would be too. What went
        // wrong is downstream of the release, so refusing it would blocklist
        // something for a processor's mistake.
        ProcessedFileIsLess => BlockDecision.Never,

        // Everything else says something about this installation rather than
        // about the release. Refusing here is how a blocklist fills with things
        // that were never the file's fault.
        _ => BlockDecision.Never
    };

    /// <summary>
    /// The same question, with the user's answers laid over the shipped ones.
    ///
    /// <para>The overrides are passed in rather than looked up, so this class
    /// stays pure and the table stays assertable. A reason nobody has an opinion
    /// about falls through to the default, which is why a new failure kind needs
    /// no migration and no settings change — it arrives with an answer.</para>
    /// </summary>
    public static BlockDecision BlockFor(string reasonCode, IReadOnlyDictionary<string, BlockDecision> overrides)
        => overrides.TryGetValue(reasonCode, out var chosen) ? chosen : BlockFor(reasonCode);

    public static bool ShouldBlock(string reasonCode, int priorFailuresOfSameRelease)
        => ShouldBlock(BlockFor(reasonCode), priorFailuresOfSameRelease);

    public static bool ShouldBlock(BlockDecision decision, int priorFailuresOfSameRelease) => decision switch
    {
        BlockDecision.Immediately => true,
        BlockDecision.AfterOneRetry => priorFailuresOfSameRelease >= 1,
        _ => false
    };

    /// <summary>
    /// Which heading this failure belongs under, and — more usefully — whose
    /// fault the failure says it was.
    ///
    /// <para>Derived from the same shape as <see cref="BlockFor(string)"/>
    /// rather than from a second list, so a reason cannot be refused
    /// immediately while being filed under "your setup".</para>
    /// </summary>
    public static string CategoryFor(string reasonCode) => reasonCode switch
    {
        NoVideoStream or LikelySample or UnsupportedFile or MediaProbeRejected or WrongRelease or ProcessorRejected or ManualFailure or DeadOrBrokenLink or DownloadIntegrityFailed => FailureCategories.BadFile,
        ReplacementRejected => FailureCategories.NotAFailure,
        Unmatched or ImportFailed or MediaProbeUnreadable or DownloadStalled or ClientReportedFailure => FailureCategories.CannotSay,
        _ => FailureCategories.YourSetup
    };

    /// <summary>
    /// Whether the downloaded file is worth keeping.
    ///
    /// <para>Only where Deluno knows it is not what was wanted. Everything else
    /// might be environmental, and deleting on a guess cannot be undone.
    /// <see cref="MediaProbeRejected"/> belongs here for the same reason as
    /// <see cref="NoVideoStream"/> and <see cref="LikelySample"/>: Deluno opened
    /// the file and found it broken, so a second copy would arrive exactly as
    /// broken (#918, cleanup sheet E4 — "if something is unreadable and
    /// corrupt don't even retry").</para>
    /// </summary>
    public static bool ShouldDeletePayload(string reasonCode)
        => reasonCode is NoVideoStream or LikelySample or MediaProbeRejected or ReplacementRejected or ProcessorRejected or DownloadIntegrityFailed;

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
