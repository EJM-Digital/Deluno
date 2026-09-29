using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Platform;

/// <summary>What stopping a wait did, and what to say about it.</summary>
/// <param name="Ended">The hand-off as it now stands, or null if it was gone.</param>
/// <param name="Busy">
/// The processor had started work, so nothing was changed. Interrupting a
/// running processor can leave the same file imported twice.
/// </param>
public sealed record ProcessorHandoffStopped(ProcessorHandoffItem? Ended, bool Busy, string Message);

/// <summary>
/// Ending a hand-off the processor is still holding (#566).
///
/// <para>A person does this from the hand-off's own drawer, and removing a title
/// does it for every hand-off that title left behind (#563) — two callers, one
/// body, because "what the processor was told" is the part that is easy to get
/// subtly different in a second copy.</para>
/// </summary>
public static class ProcessorHandoffCancellation
{
    /// <summary>
    /// Asks the processor to drop the work, then ends the hand-off.
    ///
    /// <para>Unreachable is not a reason to keep waiting: the caller has said to
    /// stop, and the record says the processor was not told. Busy is, and is
    /// reported back rather than forced.</para>
    /// </summary>
    /// <param name="why">
    /// What to say happened, given the processor's name — "You stopped waiting
    /// for Weir", "Inception was removed from Deluno".
    /// </param>
    /// <param name="ending">
    /// How it ends. <see cref="ProcessorHandoffStatuses.Cancelled"/> leaves it on
    /// Transfers as something the person can still act on — they stopped it, so
    /// they may want the original file after all. A removed title has nothing
    /// left to act on, so that ends
    /// <see cref="ProcessorHandoffStatuses.Dismissed"/>: settling its cases and
    /// leaving a counted row behind would just move the problem (#563).
    /// </param>
    /// <param name="discardEvenIfBusy">
    /// End the hand-off even when the processor says it has already started
    /// work, rather than leaving it running (#903). Interrupting the
    /// processor is still not attempted — that can leave the same file
    /// imported twice — but there is no title left for its output to be
    /// imported into, so the hand-off is dismissed now and a late completion
    /// report is refused as one arriving after the hand-off already ended
    /// (see the processor-events endpoint), rather than left <c>started</c>
    /// forever waiting for output nobody will use.
    /// </param>
    public static async Task<ProcessorHandoffStopped> StopWaitingAsync(
        IProcessorRepository repository,
        IProcessorConnectionService? processorConnections,
        IActivityFeedRepository activityFeed,
        ProcessorHandoffItem handoff,
        Func<string, string> why,
        CancellationToken cancellationToken,
        string ending = ProcessorHandoffStatuses.Cancelled,
        bool discardEvenIfBusy = false)
    {
        var connection = await repository.FindProcessorConnectionAsync(handoff.ProcessorConnectionId, handoff.ProcessorName, cancellationToken);
        var told = connection is { IsEnabled: true } && processorConnections is not null
            ? await processorConnections.CancelHandoffAsync(connection, handoff.Id, cancellationToken)
            : null;
        var processorName = connection?.Name ?? handoff.ProcessorName ?? "the processor";
        var busy = told?.Answer == ProcessorAnswer.Busy;
        if (busy && !discardEvenIfBusy)
        {
            return new ProcessorHandoffStopped(
                null,
                Busy: true,
                $"{processorName} has started work on {handoff.ReleaseName}, so Deluno will not interrupt it. {told!.Message}".TrimEnd());
        }

        var summary = busy
            ? $"{why(processorName)}. {processorName} had already started work on {handoff.ReleaseName}; its output will not be imported and Deluno will not wait for it."
            : told?.Answer == ProcessorAnswer.Answered
                ? $"{why(processorName)}, and it was asked to drop {handoff.ReleaseName}."
                : $"{why(processorName)}. It could not be told, so it may still have {handoff.ReleaseName}.";

        var ended = await repository.UpdateProcessorHandoffAsync(
            handoff.Id,
            ending,
            null,
            null,
            summary,
            cancellationToken);
        if (ended is null)
        {
            return new ProcessorHandoffStopped(null, Busy: busy, summary);
        }

        await activityFeed.RecordActivityAsync(
            "processing.cancelled",
            summary,
            JsonSerializer.Serialize(new
            {
                HandoffId = handoff.Id,
                handoff.LibraryId,
                handoff.ReleaseName,
                handoff.SourcePath,
                PreviousStatus = handoff.Status,
                ProcessorTold = told?.Answer.ToString() ?? "no connection",
                ProcessorSaid = told?.Message
            }),
            null,
            "processor-handoff",
            handoff.Id,
            cancellationToken);

        return new ProcessorHandoffStopped(ended, Busy: busy, summary);
    }

}

/// <summary>The hand-offs one sweep ended, and the releases they were about.</summary>
public sealed record ProcessorHandoffsEnded(
    IReadOnlyCollection<string> HandoffIds,
    IReadOnlyCollection<string> ReleaseNames);
