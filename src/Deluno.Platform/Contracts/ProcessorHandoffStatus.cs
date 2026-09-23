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
    Unsupported
}

/// <summary>What the processor can be asked, read from its capabilities endpoint.</summary>
public sealed record ProcessorStatusSupport(
    ProcessorAnswer Answer,
    bool CanReportStatus,
    bool CanCancel,
    string? Message)
{
    public static readonly ProcessorStatusSupport None = new(ProcessorAnswer.Unsupported, false, false, null);
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
        Waiting, Submitted, Accepted, Started, Completed, Failed, Rejected, TimedOut, Stalled, Lost, Cancelled, Released, Dismissed
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
           && (status is Rejected || CanReleaseOriginal(status) || !string.IsNullOrWhiteSpace(failureMessage));

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
