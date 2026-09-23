using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// What Deluno does when a processor gives up on a file (#497).
///
/// <para><b>Rejected</b> means the processor has deleted the download and is
/// waiting for Deluno's answer before it counts the deletion as safe. So the
/// answer is yes only once the release is refused: from then on no search takes
/// that copy again. Having the client forget it and searching for another copy
/// follow, and neither decides the answer — the client is told again by the
/// scheduled clean-up if it is off, and the next scheduled search finds the
/// title still wanted if the immediate one cannot run.</para>
///
/// <para><b>Held</b> means the processor kept the original. Nothing is refused:
/// the file may be fine and the processor wrong. It becomes a recovery case so
/// a person sees it.</para>
/// </summary>
public sealed class ProcessorFailureReportHandler(
    IJobQueueRepository jobQueueRepository,
    IDownloadDispatchesRepository dispatches,
    IBlockedReleaseRepository blockedReleases,
    IRefusedDownloadCleanupService refusedDownloadCleanup,
    IJobScheduler jobScheduler,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    ILogger<ProcessorFailureReportHandler> logger) : IProcessorFailureReportHandler
{
    public async Task<ProcessorRejectionOutcome> HandleRejectedAsync(
        ProcessorFailureReport report,
        CancellationToken cancellationToken)
    {
        var handoff = report.Handoff;
        var link = await jobQueueRepository.FindRecentDispatchLinkAsync(handoff.ClientId, handoff.ReleaseName, cancellationToken);
        var dispatch = link is null ? null : await dispatches.GetDispatchAsync(link.DispatchId, cancellationToken);
        if (dispatch is null)
        {
            return new ProcessorRejectionOutcome(
                false,
                $"Deluno could not find the download {handoff.ReleaseName} came from, so it could not refuse that release. Keep the original and hand it back.");
        }

        var failureClass = string.IsNullOrWhiteSpace(report.FailureClass) ? null : report.FailureClass.Trim();
        var reason = failureClass is null
            ? $"{report.ProcessorName} could not process it and removed it: {report.Message}"
            : $"{report.ProcessorName} could not process it ({failureClass}) and removed it: {report.Message}";

        BlockedRelease blocked;
        try
        {
            blocked = await blockedReleases.BlockAsync(
                new BlockedRelease(
                    Guid.NewGuid().ToString("n"),
                    BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                    dispatch.ReleaseName,
                    dispatch.IndexerName,
                    dispatch.MediaType,
                    dispatch.EntityId,
                    handoff.ReleaseName,
                    ImportFailurePolicy.ProcessorRejected,
                    reason,
                    dispatch.TorrentHashOrItemId,
                    dispatch.DownloadClientId,
                    dispatch.DownloadClientName,
                    DateTimeOffset.MinValue),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not refuse {ReleaseName} after {Processor} rejected it.", dispatch.ReleaseName, report.ProcessorName);
            return new ProcessorRejectionOutcome(
                false,
                $"Deluno could not record {dispatch.ReleaseName} as refused. Keep the original and hand it back.");
        }

        try
        {
            await refusedDownloadCleanup.CleanUpOneAsync(blocked.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The scheduled clean-up tries every uncleared refusal again.
            logger.LogInformation(exception, "{Client} will be told to forget {ReleaseName} on the next clean-up.", dispatch.DownloadClientName, dispatch.ReleaseName);
        }

        // The grab is over: refused, and the client told to forget it. Left open
        // it read as "qBittorrent already has a download for The Terror" after
        // qBittorrent had dropped it (seen on the rig, #497). The refusal carries
        // the client and item, so the scheduled clean-up can still reach it.
        try
        {
            await dispatches.ArchiveDispatchAsync(dispatch.Id, $"Rejected by {report.ProcessorName}; the release was refused.", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not close the download of {ReleaseName} after {Processor} rejected it.", dispatch.ReleaseName, report.ProcessorName);
        }

        // A processor that refuses everything walks the whole result list
        // (#576). The scheduled search asks the same question, so stopping here
        // and saying so is true (#583).
        var refusedBefore = ProcessorRefusals.Count(
            await blockedReleases.ListForAsync(dispatch.MediaType, dispatch.EntityId, cancellationToken));
        if (ProcessorRefusals.StopSearching(refusedBefore))
        {
            var enough = $"{report.ProcessorName} has refused {refusedBefore} copies of this title. Deluno stopped looking for another.";
            await RaiseRecoveryCaseAsync(report, "processor-refuses-every-copy", enough,
                $"Check {report.ProcessorName}. You can import a refused copy as it is from the blocklist, or unblock one to try again.",
                cancellationToken);
            return new ProcessorRejectionOutcome(true, enough, blocked.Id, null);
        }

        var search = await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                LibrarySearchJobTypes.For(dispatch.MediaType),
                "processor-rejected",
                JsonSerializer.Serialize(new
                {
                    libraryId = dispatch.LibraryId,
                    libraryName = report.Library.Name,
                    mediaType = dispatch.MediaType,
                    checkMissing = true,
                    checkUpgrades = true,
                    maxItems = 1,
                    retryDelayHours = 24,
                    triggeredBy = "processor-rejected",
                    targetEntityId = dispatch.EntityId
                }),
                "download_dispatch",
                dispatch.Id,
                IdempotencyKey: $"processor-rejected:{handoff.Id}",
                DedupeKey: $"processor-rejected:{handoff.Id}"),
            cancellationToken);

        return new ProcessorRejectionOutcome(
            true,
            $"{report.ProcessorName} could not process {dispatch.ReleaseName} and removed it. Deluno refused that release and is searching for another copy.",
            blocked.Id,
            search.Id);
    }

    private async Task RaiseRecoveryCaseAsync(
        ProcessorFailureReport report,
        string kind,
        string summary,
        string recommended,
        CancellationToken cancellationToken)
    {
        var handoff = report.Handoff;
        var details = JsonSerializer.Serialize(new
        {
            libraryId = handoff.LibraryId,
            handoffId = handoff.Id,
            releaseName = handoff.ReleaseName,
            sourcePath = handoff.SourcePath,
            failureClass = report.FailureClass
        });

        if (DelunoValueNormalizers.NormalizeMediaType(report.Library.MediaType) == "tv")
        {
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(handoff.ReleaseName, kind, summary, recommended, details),
                cancellationToken);
        }
        else
        {
            await movieCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(handoff.ReleaseName, kind, summary, recommended, details),
                cancellationToken);
        }
    }

    public async Task HandleHeldAsync(
        ProcessorFailureReport report,
        CancellationToken cancellationToken)
    {
        await RaiseRecoveryCaseAsync(
            report,
            "processor-held",
            $"{report.ProcessorName} could not process {report.Handoff.ReleaseName} and is keeping the original: {report.Message}",
            "Retry or release the file in the processor, or import the original from the download folder if it is acceptable.",
            cancellationToken);
    }
}
