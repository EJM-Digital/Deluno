using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Media;

/// <summary>
/// Clears the records standing between a title and its next download, and says
/// exactly which ones it cleared.
///
/// <para><b>The point is the saying.</b> Radarr's blocklist is the right
/// mechanism — without something like it a failed import becomes an endless
/// re-download loop — and its cost is that a title simply stops arriving with
/// no account of why. Deluno keeps the mechanism, explains it through
/// <see cref="AcquisitionBlockerReader"/>, and makes the override deliberate:
/// one action, and a list of what it actually did.</para>
///
/// <para><b>It reaches into things Deluno does not own.</b> Removing a download
/// from a client and restarting a processor hand-off are not Deluno's data, and
/// they are not reversible by pressing the button again. So nothing here is
/// silent: every step lands in the activity feed, a step that fails is reported
/// rather than swallowed, and the response names both halves — what was
/// cleared, and what could not be.</para>
///
/// <para>A partial result is still a result. If the client refuses, the
/// exclusion is still worth removing, and the person is better served by "three
/// of four, and here is the one that did not" than by a rollback that leaves
/// them exactly where they started with no information.</para>
/// </summary>
public sealed class AcquisitionOverrideService(
    IProcessorRepository processorRepository,
    IUnifiedExclusionRepository exclusions,
    IDownloadClientTelemetryService downloadClients,
    ILogger<AcquisitionOverrideService> logger)
{
    public async Task<AcquisitionOverrideResponse> ForceAsync(
        AcquisitionOverrideRequest request,
        CancellationToken cancellationToken)
    {
        var cleared = new List<string>();
        var couldNotClear = new List<string>();

        await ClearHandoffAsync(request, cleared, couldNotClear, cancellationToken);
        await ClearDownloadAsync(request, cleared, couldNotClear, cancellationToken);
        await ClearExclusionsAsync(request, cleared, couldNotClear, cancellationToken);

        return new AcquisitionOverrideResponse(
            request.MediaId,
            cleared,
            couldNotClear,
            SearchStarted: false,
            Summary: Describe(request.Title, cleared, couldNotClear));
    }

    /// <summary>
    /// Puts the hand-off back to waiting rather than deleting it, so the
    /// processor is asked again for the same file. Deleting the row would lose
    /// the record that this source path was ever handled, which is the thing
    /// that stops one download being submitted twice.
    /// </summary>
    private async Task ClearHandoffAsync(
        AcquisitionOverrideRequest request,
        List<string> cleared,
        List<string> couldNotClear,
        CancellationToken cancellationToken)
    {
        if (request.HandoffId is not { Length: > 0 } handoffId)
        {
            return;
        }

        try
        {
            var reset = await processorRepository.UpdateProcessorHandoffAsync(
                handoffId,
                "waiting",
                null,
                null,
                null,
                cancellationToken);

            if (reset is null)
            {
                couldNotClear.Add("The processor hand-off could not be found, so it was left alone.");
                return;
            }

            cleared.Add($"Reset the {reset.ProcessorName ?? "processor"} hand-off, so the file will be sent for processing again.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not reset processor hand-off {HandoffId} during an acquisition override.", handoffId);
            couldNotClear.Add("The processor hand-off could not be reset.");
        }
    }

    /// <summary>
    /// Removes the download the client is still holding, with its data.
    ///
    /// <para>With its data on purpose: the reason to force is that what is
    /// there is not wanted, and leaving the files behind would have the client
    /// refuse the same release again for the same reason.</para>
    /// </summary>
    private async Task ClearDownloadAsync(
        AcquisitionOverrideRequest request,
        List<string> cleared,
        List<string> couldNotClear,
        CancellationToken cancellationToken)
    {
        if (request.DownloadClientId is not { Length: > 0 } clientId ||
            request.QueueItemId is not { Length: > 0 } queueItemId)
        {
            return;
        }

        try
        {
            var result = await downloadClients.ExecuteActionAsync(
                clientId,
                new DownloadClientActionRequest("delete-with-data", queueItemId),
                cancellationToken);

            if (result.Succeeded)
            {
                cleared.Add($"Removed the download from {request.DownloadClientName ?? "the download client"}, along with its files.");
                return;
            }

            couldNotClear.Add(
                $"{request.DownloadClientName ?? "The download client"} would not remove the download: {result.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not remove queue item {QueueItemId} during an acquisition override.", queueItemId);
            couldNotClear.Add($"{request.DownloadClientName ?? "The download client"} could not be reached to remove the download.");
        }
    }

    private async Task ClearExclusionsAsync(
        AcquisitionOverrideRequest request,
        List<string> cleared,
        List<string> couldNotClear,
        CancellationToken cancellationToken)
    {
        if (request.ExclusionIds.Count == 0)
        {
            return;
        }

        var removed = 0;
        foreach (var exclusionId in request.ExclusionIds)
        {
            try
            {
                if (await exclusions.DeleteAsync(exclusionId, cancellationToken))
                {
                    removed++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not remove exclusion {ExclusionId} during an acquisition override.", exclusionId);
            }
        }

        if (removed > 0)
        {
            cleared.Add(removed == 1
                ? "Removed the exclusion, so lists and collections may add this again."
                : $"Removed {removed} exclusions, so lists and collections may add this again.");
        }

        if (removed < request.ExclusionIds.Count)
        {
            var left = request.ExclusionIds.Count - removed;
            couldNotClear.Add(left == 1
                ? "One exclusion could not be removed."
                : $"{left} exclusions could not be removed.");
        }
    }

    /// <summary>
    /// What happened, in the order a person would want it: what changed first,
    /// then what did not.
    /// </summary>
    private static string Describe(string title, IReadOnlyList<string> cleared, IReadOnlyList<string> couldNotClear)
    {
        if (cleared.Count == 0 && couldNotClear.Count == 0)
        {
            return $"There was nothing to clear for {title}.";
        }

        if (couldNotClear.Count == 0)
        {
            return cleared.Count == 1
                ? $"Cleared one thing that was holding {title} back."
                : $"Cleared {cleared.Count} things that were holding {title} back.";
        }

        if (cleared.Count == 0)
        {
            return $"Nothing could be cleared for {title}. {couldNotClear[0]}";
        }

        return $"Cleared {cleared.Count} of {cleared.Count + couldNotClear.Count} things holding {title} back. {couldNotClear[0]}";
    }
}

/// <summary>
/// What to clear, decided by the caller that could see it.
///
/// <para>Everything is optional because a force is asked for against whatever
/// blockers were actually found. A request that names nothing clears nothing
/// and says so, rather than guessing at what the caller might have meant.</para>
/// </summary>
public sealed record AcquisitionOverrideRequest(
    string MediaId,
    string Title,
    string? HandoffId = null,
    string? DownloadClientId = null,
    string? DownloadClientName = null,
    string? QueueItemId = null,
    IReadOnlyList<string>? ExclusionIdsOrNull = null)
{
    public IReadOnlyList<string> ExclusionIds => ExclusionIdsOrNull ?? [];
}
