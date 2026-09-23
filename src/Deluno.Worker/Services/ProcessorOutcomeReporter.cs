using Deluno.Filesystem;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Platform;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// Tells a processor what became of the file it handed back (#578).
///
/// <para><b>Why this exists.</b> A processor cannot know. Deluno's answer to a
/// completed report only says an import was queued, and a queued import can
/// still fail — so the processor keeps its copy, and nothing ever tells it
/// otherwise. Its output folder only grows, and Deluno reaching in to tidy it
/// would be the wrong side of the boundary: a processor serves media managers
/// that cannot talk to it at all, so cleaning up after itself has to be its own
/// job.</para>
///
/// <para>What Deluno owes is the fact, not the housekeeping.</para>
/// </summary>
public sealed class ProcessorOutcomeReporter(
    IProcessorRepository processorRepository,
    IProcessorConnectionService processorConnections,
    IActivityFeedRepository activityFeed,
    TimeProvider timeProvider,
    ILogger<ProcessorOutcomeReporter> logger)
{
    /// <summary>
    /// Writes down what the processor is owed and says it (#580). What is not
    /// heard now is said again by <see cref="SendDueAsync"/>.
    ///
    /// <para>A processor that does not advertise it is told nothing, and keeps
    /// its copy until its own retention reclaims it. That is the path that has
    /// to work for every other media manager, so Deluno must not assume it is
    /// being listened to — but one that could not be asked is still owed.</para>
    ///
    /// <para>Never throws and never fails an import: a processor disagreeing
    /// about its own bookkeeping is not a reason to undo work that has already
    /// happened.</para>
    /// </summary>
    public async Task ReportAsync(
        string handoffId,
        ProcessorHandoffOutcome outcome,
        string? importedPath,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var handoff = await processorRepository.GetProcessorHandoffAsync(handoffId, cancellationToken);
            if (handoff is null) return;

            await ProcessorOutcomeDelivery.RecordAndSendAsync(
                processorRepository,
                processorConnections,
                activityFeed,
                handoff,
                outcome,
                importedPath,
                reason,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not tell the processor what became of hand-off {HandoffId}.", handoffId);
        }
    }

    /// <summary>
    /// Says again every outcome still owed whose next attempt is due (#580).
    ///
    /// <para>One failure does not stop the rest: each report is its own
    /// processor's business.</para>
    /// </summary>
    public async Task SendDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var record in await processorRepository.ListDueProcessorOutcomesAsync(now, cancellationToken))
        {
            try
            {
                await ProcessorOutcomeDelivery.SendAsync(
                    processorRepository,
                    processorConnections,
                    activityFeed,
                    record,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not tell the processor what became of hand-off {HandoffId}.", record.HandoffId);
            }
        }
    }

    /// <summary>
    /// Whether a failed import is one the processor should hear about.
    ///
    /// <para>"Not imported" means final, and a processor takes it as permission
    /// to remove its copy — so a failure Deluno is going to retry must stay
    /// silent, or the retry finds nothing. A refusal of the file itself is
    /// final on the first attempt, because a second attempt reads the same
    /// bytes; everything else is only final once there are no attempts left.</para>
    ///
    /// <para>The counter is raised when the job is leased, so <c>Attempts</c>
    /// already counts the one running now. Reading it as the number of attempts
    /// finished would report a failure as final one attempt early, which is the
    /// exact shape of the data loss this rule exists to prevent.</para>
    /// </summary>
    public static bool WillNotTryAgain(ImportPipelineResult result, JobQueueItem job)
        => result.StatusCode == 400 || job.Attempts >= job.MaxAttempts;
}
