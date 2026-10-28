using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// The one place a hand-off goes out to its processor (#1253): the planner's pass and Try again both send through it, so
/// the path translation, the files named, the idempotency key, the connection's health and the Activity lines are the
/// same whoever pressed. The caller holds the hand-off's lease (<see cref="ProcessorHandoffSends"/>).
/// </summary>
internal static class ProcessorHandoffSubmission
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static async Task<(ProcessorHandoffItem Handoff, ProcessorSubmissionResult Submission)> SubmitAsync(
        ProcessorHandoffItem handoff,
        LibraryItem library,
        ProcessorConnectionItem connection,
        string title,
        string? dispatchId,
        IProcessorRepository processorRepository,
        IProcessorConnectionService processorConnectionService,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken cancellationToken)
    {
        // Deluno's own view of sourcePath, translated to the processor's (#962). The stored hand-off keeps Deluno's
        // path; only what goes out over the wire changes.
        var pathMappings = await processorRepository.ListProcessorPathMappingsAsync(connection.Id, cancellationToken);
        var outboundHandoff = await HandoffFileSelection.ForProcessorAsync(handoff, library, connection, pathMappings, processorConnectionService, activityFeedRepository, cancellationToken);

        // A send that got no answer waits and goes again after a back-off (#1385), with the key the first such send
        // carried, so a send the processor did get is not done twice; only the last try is a failure.
        var (tries, keyRevision) = ProcessorUnreachable.SoFar(
            await activityFeedRepository.ListActivityAsync(200, "processor-handoff", handoff.Id, cancellationToken));
        if (keyRevision is { } revision)
        {
            outboundHandoff = outboundHandoff with { UpdatedUtc = revision };
        }

        var submission = await processorConnectionService.SubmitAsync(connection, outboundHandoff, cancellationToken);
        var status = submission.Status;
        var failure = submission.IsAccepted ? null : submission.Message;
        var category = submission.IsAccepted ? "processing.submitted" : "processing.submission-failed";
        var said = submission.IsAccepted
            ? $"Deluno submitted {title} to {connection.Name}."
            : $"Deluno could not submit {title} to {connection.Name}. {submission.Message}";
        int? attempt = null;
        long? keyRevisionMs = null;
        if (ProcessorUnreachable.IsUnreachable(submission))
        {
            attempt = tries + 1;
            keyRevisionMs = outboundHandoff.UpdatedUtc.ToUnixTimeMilliseconds();
            if (tries < ProcessorUnreachable.RetryAfter.Count)
            {
                status = ProcessorHandoffStatuses.Waiting;
                failure = ProcessorUnreachable.WaitingMessage(connection.Name);
                category = ProcessorUnreachable.TriedCategory;
                said = $"{connection.Name} did not answer when Deluno sent {title}; Deluno will send it again in {ProcessorUnreachable.RetryAfter[tries].TotalMinutes:0} min.";
            }
            else
            {
                failure = ProcessorUnreachable.GaveUpMessage(connection.Name);
                said = $"Deluno could not submit {title} to {connection.Name}. {failure}";
            }
        }

        var current = await processorRepository.UpdateProcessorHandoffAsync(
            handoff.Id,
            status,
            null,
            null,
            failure,
            cancellationToken) ?? handoff;
        if (ProcessorHealthRules.HealthAfterSubmission(submission) is { } submittedHealth)
        {
            await processorRepository.RecordProcessorConnectionHealthAsync(
                connection.Id,
                submittedHealth,
                submission.Message,
                cancellationToken);
        }

        await activityFeedRepository.RecordActivityAsync(
            category,
            said,
            JsonSerializer.Serialize(new
            {
                DispatchId = dispatchId,
                handoff.ReleaseName,
                handoff.SourcePath,
                HandoffId = handoff.Id,
                ConnectionId = connection.Id,
                connection.Name,
                connection.Provider,
                Status = status,
                submission.StatusCode,
                Attempt = attempt,
                KeyRevisionMs = keyRevisionMs
            }, PayloadJsonOptions),
            null,
            "processor-handoff",
            handoff.Id,
            cancellationToken);
        return (current, submission);
    }
}
