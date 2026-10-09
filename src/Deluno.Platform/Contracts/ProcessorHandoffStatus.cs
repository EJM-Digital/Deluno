namespace Deluno.Platform.Contracts;

/// <summary>
/// How a processor answered a question about a hand-off (#511).
///
/// <para>Kept as distinct cases because each one means something different to
/// the file. <see cref="NotFound"/> is "I never received it", which is a
/// problem. <see cref="Unreachable"/> is "I could not ask", which is not an
/// answer at all and must never be read as one: a processor switched off for the
/// weekend does not get its files given up on.</para>
/// </summary>
public enum ProcessorAnswer
{
    Answered,
    NotFound,
    Forbidden,
    Busy,
    Unreachable,
    Unsupported,

    /// <summary>
    /// It answered, and said no in a way that saying the same thing again will
    /// not change: the request itself was wrong. Only an outcome report can be
    /// refused this way (#580).
    /// </summary>
    Refused,

    /// <summary>
    /// It answered that it keeps an answer it already has, and says so in a code rather than in words: the
    /// outcome Deluno reports now differs from the one it was told before, and no repeat will change that.
    /// Not a failure on either side (#1155).
    /// </summary>
    Settled
}

/// <summary>What the processor can be asked, read from its capabilities endpoint.</summary>
public sealed record ProcessorStatusSupport(
    ProcessorAnswer Answer,
    bool CanReportStatus,
    bool CanCancel,
    string? Message)
{
    /// <summary>
    /// Whether the processor wants to be told what became of a file it handed
    /// back (#578). A processor that does not advertise this is told nothing
    /// and keeps its copy until its own retention reclaims it, which is how it
    /// works with a media manager that says nothing at all.
    /// </summary>
    public bool CanBeToldTheOutcome { get; init; }

    /// <summary>
    /// The folder the processor says it is watching, from an optional
    /// <c>watchedPath</c> field on its capabilities answer (#909). Null means
    /// the processor did not say -- shown as "not reported by the processor"
    /// rather than as a problem, since plenty of processors will never add
    /// this field and are not thereby broken.
    /// </summary>
    public string? WatchedPath { get; init; }

    /// <summary>The processor advertises a <c>library-folders</c> capability: one watched folder per library, read from its own list (#1144).</summary>
    public bool HasLibraryFolders { get; init; }

    public static readonly ProcessorStatusSupport None = new(ProcessorAnswer.Unsupported, false, false, null);
}

/// <summary>
/// One entry of a processor's <c>library-folders</c> capability (#1144): the folder it watches for the library its
/// workflow was set up from. <see cref="ManagerLibraryKey"/> is the Deluno library id, or null for a workflow made
/// some other way; <see cref="FoldersFromManager"/> says the processor took the folders from Deluno. Generic: any
/// processor may publish this list. <see cref="MinimumFileSizeBytes"/> is the optional <c>minimum_file_size_bytes</c>: the
/// processor refuses files strictly smaller than it, so a hand-off names none of them (#1199); null when absent.
/// </summary>
public sealed record ProcessorLibraryFolder(string? ManagerLibraryKey, string? WatchedFolder, bool FoldersFromManager, string? OutputFolder = null, long? MinimumFileSizeBytes = null)
{
    /// <summary>
    /// The entry for one library. Two entries carry the same key only when someone copied a workflow by hand, so
    /// the one whose folders still come from Deluno wins, otherwise the first.
    /// </summary>
    public static ProcessorLibraryFolder? ForLibrary(IEnumerable<ProcessorLibraryFolder> folders, string libraryId)
    {
        var matching = folders.Where(folder => string.Equals(folder.ManagerLibraryKey, libraryId, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.FirstOrDefault(folder => folder.FoldersFromManager) ?? matching.FirstOrDefault();
    }
}

/// <summary>What became of a file a processor handed back (#578).</summary>
public enum ProcessorHandoffOutcome
{
    /// <summary>Deluno has the file. The processor's copy is no longer needed.</summary>
    Imported,

    /// <summary>
    /// It will not be imported, by any route, ever.
    ///
    /// <para>Final, and only sent when it is: a terminal refusal, or attempts
    /// exhausted, or the title it was for is gone. A failure Deluno will try
    /// again is not reported at all, because a processor that dropped its copy
    /// on one of those would leave the retry nothing to import.</para>
    /// </summary>
    NotImported
}

/// <param name="Released">Whether the processor let go of its copy.</param>
public sealed record ProcessorOutcomeResult(ProcessorAnswer Answer, bool Released, string? Message);

/// <summary>
/// What Deluno has to tell a processor about one hand-off, and how far it has
/// got with telling it (#580).
///
/// <para>Written down before the first attempt rather than after, so an answer
/// that does not land is still owed. The outcome exists so a processor can let
/// go of its copy; one that is lost to a restart or a dropped request is a copy
/// the processor keeps for ever, and no screen counted it.</para>
///
/// <para>Keyed by the hand-off's id and kept apart from the hand-off row,
/// because a new download of the same release restarts that row under a new id
/// and the processor still keys the old outcome on the old one.</para>
/// </summary>
/// <param name="Outcome"><c>imported</c> or <c>not-imported</c>, as it is sent.</param>
/// <param name="OccurredUtc">
/// When it happened, sent unchanged on every attempt: the same report again
/// must be the same report.
/// </param>
/// <param name="Delivery">One of <see cref="ProcessorOutcomeDeliveries"/>.</param>
/// <param name="NextAttemptUtc">When the next attempt is due, while it is still owed.</param>
/// <param name="ProcessorSaid">The processor's last words about it, or why it could not be asked.</param>
public sealed record ProcessorOutcomeRecord(
    string HandoffId,
    string ProcessorName,
    string LibraryId,
    string ReleaseName,
    string Outcome,
    string? ImportedPath,
    string? Reason,
    DateTimeOffset OccurredUtc,
    string Delivery,
    int Attempts,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? NextAttemptUtc,
    bool Released,
    string? ProcessorSaid,
    DateTimeOffset RecordedUtc,
    string? ProcessorConnectionId = null)
{
    public const string Imported = "imported";
    public const string NotImported = "not-imported";

    public static string Word(ProcessorHandoffOutcome outcome)
        => outcome == ProcessorHandoffOutcome.Imported ? Imported : NotImported;

    public ProcessorHandoffOutcome AsOutcome()
        => Outcome == Imported ? ProcessorHandoffOutcome.Imported : ProcessorHandoffOutcome.NotImported;
}

/// <summary>How far telling a processor an outcome has got (#580).</summary>
public static class ProcessorOutcomeDeliveries
{
    /// <summary>Not yet acknowledged. Deluno will try again.</summary>
    public const string Pending = "pending";

    /// <summary>The processor acknowledged it.</summary>
    public const string Delivered = "delivered";

    /// <summary>
    /// The processor answered in a way no repeat will change: it has no record
    /// of the hand-off, or it could not read the report.
    /// </summary>
    public const string Refused = "refused";

    /// <summary>
    /// The processor keeps the answer it was first given, and said so by code. Deluno stops telling it; there
    /// is nothing left to fix on either side (#1155).
    /// </summary>
    public const string Settled = "settled";

    /// <summary>
    /// Nobody acknowledged it within <see cref="GiveUpAfter"/>, or the
    /// processor it was for is no longer connected or no longer listening.
    /// </summary>
    public const string GivenUp = "given-up";

    /// <summary>
    /// A week. Long enough that a processor switched off for a long weekend,
    /// or waiting on a fixed secret, still hears; short enough that a
    /// processor which is gone for good is not asked every hour for ever.
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);

    private static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    /// <summary>
    /// Where an answer leaves the report. Only a true answer ends it: a
    /// processor that could not be reached, is still finishing, or refused the
    /// secret may say yes next time.
    /// </summary>
    /// <remarks>
    /// A 409 stays owed even when a processor means "a different outcome is
    /// already recorded". Telling that apart would mean reading one
    /// processor's wording, and Deluno does not write code for one processor.
    /// It costs a few needless attempts before <see cref="GiveUpAfter"/>, and
    /// Deluno only ever sends one outcome per hand-off.
    /// </remarks>
    public static string After(ProcessorAnswer answer)
        => answer switch
        {
            ProcessorAnswer.Answered => Delivered,
            ProcessorAnswer.NotFound or ProcessorAnswer.Refused or ProcessorAnswer.Unsupported => Refused,
            ProcessorAnswer.Settled => Settled,
            _ => Pending
        };

    /// <summary>
    /// A minute after the first failure, doubling to an hour. A restart is
    /// over within the first few; an outage is not asked about every minute.
    /// </summary>
    public static TimeSpan WaitAfter(int attempts)
    {
        var minutes = Math.Pow(2, Math.Clamp(attempts - 1, 0, 10));
        return TimeSpan.FromMinutes(Math.Min(minutes, LongestWait.TotalMinutes));
    }

    public static bool IsOwed(string? delivery) => delivery == Pending;
}

/// <summary>
/// The processor's own account of one hand-off.
/// </summary>
/// <param name="State">
/// <c>queued</c>, <c>scheduled</c>, <c>working</c>, <c>completed</c>,
/// <c>passed-through</c>, <c>failed</c>, <c>rejected</c> or <c>cancelled</c>,
/// lower-cased; anything else is kept as sent.
/// </param>
/// <param name="LastChangedUtc">
/// When the processor's state for the file last changed: a retry, work
/// starting, a new queue position. Not every progress tick.
/// </param>
public sealed record ProcessorHandoffStatusResult(
    ProcessorAnswer Answer,
    string? State = null,
    int? QueuePosition = null,
    DateTimeOffset? ScheduledForUtc = null,
    DateTimeOffset? LastChangedUtc = null,
    string? OutputPath = null,
    string? Message = null);

public sealed record ProcessorHandoffCancelResult(ProcessorAnswer Answer, string? Message);

public static class ProcessorHandoffStates
{
    public const string Queued = "queued";
    public const string Scheduled = "scheduled";
    public const string Working = "working";
    public const string Completed = "completed";
    public const string PassedThrough = "passed-through";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";

    /// <summary>States in which the processor still intends to deliver something.</summary>
    public static bool IsStillWorkingOnIt(string? state)
        => state is Queued or Scheduled or Working or Completed or PassedThrough;
}

/// <summary>
/// Hand-off statuses Deluno records. The first four mean the file is with the
/// processor; the rest are how that ended.
/// </summary>
public static class ProcessorHandoffStatuses
{
    public const string Waiting = "waiting";
    public const string Submitted = "submitted";
    public const string Accepted = "accepted";
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Rejected = "rejected";

    /// <summary>A processor that cannot report status went quiet for longer than the library allows.</summary>
    public const string TimedOut = "timed-out";

    /// <summary>The processor reported no change for longer than the library allows.</summary>
    public const string Stalled = "stalled";

    /// <summary>The processor says it never received the hand-off.</summary>
    public const string Lost = "lost";

    /// <summary>The processor stopped work on it, at Deluno's request or its own.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// The processor says the download was gone before it could clean it (#1186). Asks a person only for a new
    /// copy: there is no file to send again or to import as it is.
    /// </summary>
    public const string SourceGone = "source-gone";

    /// <summary>
    /// Deluno imports the original download instead of waiting for a processed
    /// copy — by the library's rule or by a person choosing it.
    /// </summary>
    public const string Released = "released";

    /// <summary>
    /// A person looked at a hand-off that ended badly and decided nothing more
    /// needs doing about it. It stays as history and stops being counted as
    /// something that needs them.
    /// </summary>
    public const string Dismissed = "dismissed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Waiting, Submitted, Accepted, Started, Completed, Failed, Rejected, TimedOut, Stalled, Lost, Cancelled, Released, Dismissed, SourceGone
    };

    /// <summary>With the processor, as far as Deluno knows.</summary>
    public static bool IsOpen(string? status)
        => status is Submitted or Accepted or Started;

    /// <summary>
    /// The file is with the processor or about to be: open, or waiting to be
    /// sent. Every other status is an ending.
    /// </summary>
    public static bool IsWithProcessor(string? status)
        => status is Waiting || IsOpen(status);

    /// <summary>
    /// Endings after which a processed file can still turn up and should still
    /// be imported: the processor may have delivered after Deluno stopped
    /// waiting. Not <see cref="Released"/> — the original is already on its way
    /// in — nor the endings where the processor said nothing is coming.
    /// </summary>
    public static bool AcceptsLateOutput(string? status)
        => IsOpen(status) || status is Waiting or TimedOut or Stalled or Lost;

    /// <summary>
    /// The statuses <see cref="IsWithProcessor"/> and
    /// <see cref="AcceptsLateOutput"/> answer true for, so a query can ask the
    /// database for exactly those rather than reading a page of history and
    /// filtering it (#571).
    /// </summary>
    public static readonly IReadOnlyList<string> WithProcessor = [Waiting, Submitted, Accepted, Started];

    public static readonly IReadOnlyList<string> AcceptingLateOutput =
        [Waiting, Submitted, Accepted, Started, TimedOut, Stalled, Lost];

    /// <summary>
    /// Endings where the hand-off went wrong: the processor failed or refused it,
    /// went quiet, stopped reporting, or never received it (#1099). Not
    /// <see cref="Cancelled"/>, which a person or Deluno can ask for.
    /// </summary>
    public static readonly IReadOnlyList<string> WentWrong = [Failed, Rejected, TimedOut, Stalled, Lost, SourceGone];

    /// <summary>Where a person can choose to import the original instead.</summary>
    public static bool CanReleaseOriginal(string? status)
        => status is Failed or TimedOut or Stalled or Lost or Cancelled;

    /// <summary>
    /// What Transfers asks a person about: an ending, not already dismissed,
    /// that either failed or carries a reason.
    ///
    /// <para><c>handoffNeedsPerson</c> in <c>apps/web/src/lib/needs-action.ts</c>
    /// is the same rule on the screen.</para>
    /// </summary>
    public static bool NeedsPerson(string? status, string? failureMessage)
        => status is not Dismissed
           && !IsWithProcessor(status)
           && (status is Rejected or SourceGone || CanReleaseOriginal(status) || !string.IsNullOrWhiteSpace(failureMessage));

    /// <summary>
    /// Anything a person is asked about, they can say they are finished with.
    ///
    /// <para>It was a hand-picked list of endings, and the list disagreed with
    /// what Transfers counts. A hand-off that ended <see cref="Completed"/>
    /// carrying a reason — the processor delivered after its title was removed —
    /// was counted as needing a person and offered no way to end it, because
    /// retry, import-original and dismiss each wanted a different status (#570).
    /// A hand-off that finished well and has nothing to say is still not
    /// dismissable: there is nothing to dismiss.</para>
    /// </summary>
    public static bool CanDismiss(string? status, string? failureMessage = null)
        => NeedsPerson(status, failureMessage);
}

/// <summary>
/// Deluno's latest reading of what the processor says about a hand-off.
/// </summary>
/// <param name="AnsweringSinceUtc">
/// When the processor last started answering after being unreachable. The
/// no-progress clock never starts earlier than this, so time the processor spent
/// switched off does not count against the file.
/// </param>
public sealed record ProcessorHandoffObservation(
    string? State,
    string? Message,
    int? QueuePosition,
    DateTimeOffset? ScheduledForUtc,
    DateTimeOffset? LastChangedUtc,
    DateTimeOffset CheckedUtc,
    DateTimeOffset? UnreachableSinceUtc,
    DateTimeOffset? AnsweringSinceUtc);
