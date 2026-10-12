using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// Try again on a failed hand-off sends it at once (#1253), through the planner's own send and with the checks the planner
/// makes before it: the download's files must still be there, its file must not already be imported, its library must
/// still hand to a processor, the processor connection must be on, and the processor must have room. A refusal, Deluno's
/// or the processor's, is said as a refusal; it is never sent twice (<see cref="ProcessorHandoffSends"/>).
/// </summary>
public sealed class ProcessorHandoffSender(
    IProcessorRepository processorRepository,
    ILibrariesRepository librariesRepository,
    IJobQueueRepository jobQueueRepository,
    IProcessorConnectionService processorConnectionService,
    IActivityFeedRepository activityFeedRepository) : IProcessorHandoffSender
{
    public async Task<ProcessorHandoffSendResult> SendNowAsync(string handoffId, CancellationToken cancellationToken)
    {
        using var lease = ProcessorHandoffSends.TryBegin(handoffId);
        if (lease is null)
        {
            return new(ProcessorHandoffSendKind.AlreadySending, "This hand-off is already being sent.");
        }

        // Read it again with the lease held: the planner may have sent it between the press and now.
        var handoff = await processorRepository.GetProcessorHandoffAsync(handoffId, cancellationToken);
        if (handoff is null)
        {
            return new(ProcessorHandoffSendKind.Refused, "Deluno no longer has this hand-off.");
        }

        if (!string.Equals(handoff.Status, ProcessorHandoffStatuses.Waiting, StringComparison.OrdinalIgnoreCase))
        {
            return new(ProcessorHandoffSendKind.AlreadySending, "This hand-off has already been sent.");
        }

        var library = (await librariesRepository.ListLibrariesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase));
        if (library is null || !ImportWorkflows.HandsToProcessor(library.ImportWorkflow))
        {
            // The planner sends nothing for a library that does not hand to a processor, and neither does this.
            return new(ProcessorHandoffSendKind.Waiting, $"{handoff.ReleaseName}'s library does not hand its downloads to a processor, so Deluno leaves the hand-off waiting.");
        }

        var connection = await processorRepository.FindProcessorConnectionAsync(library.ProcessorConnectionId, library.ProcessorName, cancellationToken);
        if (connection is not { IsEnabled: true })
        {
            return await RefuseAsync(handoff, "The processor connection for this hand-off is unavailable. Restore and test the connection before trying the hand-off again.", cancellationToken);
        }

        if (await ImportedDownloads.WasImportedAsync(jobQueueRepository, handoff.ClientId, handoff.ReleaseName, cancellationToken))
        {
            return await RefuseAsync(handoff, $"{handoff.ReleaseName} has already been imported, so it is not sent to the processor again.", cancellationToken);
        }

        if (DownloadFilesGone.AreGoneOnDisk(handoff.SourcePath))
        {
            return await RefuseAsync(handoff, $"The files for {handoff.ReleaseName} are gone from where the download client left them, so there is nothing to send.", cancellationToken);
        }

        var holding = (await processorRepository.ListProcessorHandoffsWithStatusAsync(ProcessorHandoffStatuses.WithProcessor, cancellationToken))
            .Count(other => !string.Equals(other.Id, handoff.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    other.ProcessorConnectionId ?? other.ProcessorName ?? string.Empty,
                    library.ProcessorConnectionId ?? library.ProcessorName ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase));
        if (holding >= WorkPlanner.MostFilesWithOneProcessor)
        {
            return new(ProcessorHandoffSendKind.Waiting, $"{connection.Name} is holding as many files as it can; Deluno sends this one as soon as there is room.");
        }

        var dispatch = await jobQueueRepository.FindDispatchLinkForDownloadAsync(handoff.ClientId, handoff.QueueItemId, handoff.ReleaseName, cancellationToken);
        var (sent, submission) = await ProcessorHandoffSubmission.SubmitAsync(
            handoff, library, connection, handoff.ReleaseName, dispatch?.DispatchId,
            processorRepository, processorConnectionService, activityFeedRepository, cancellationToken);
        return submission.IsAccepted
            ? new(ProcessorHandoffSendKind.Sent, $"Sent to {connection.Name} again.")
            : new(ProcessorHandoffSendKind.Refused, sent.FailureMessage ?? submission.Message);
    }

    /// <summary>Deluno's own check said no. Nothing was sent and the hand-off is left as it is; the caller puts back what it changed.</summary>
    private static Task<ProcessorHandoffSendResult> RefuseAsync(ProcessorHandoffItem handoff, string message, CancellationToken cancellationToken)
        => Task.FromResult(new ProcessorHandoffSendResult(ProcessorHandoffSendKind.Refused, message));
}
