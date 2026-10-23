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
        var link = await jobQueueRepository.FindDispatchLinkForDownloadAsync(handoff.ClientId, handoff.QueueItemId, handoff.ReleaseName, cancellationToken);
        var dispatch = link is null ? null : await dispatches.GetDispatchAsync(link.DispatchId, cancellationToken);
        if (dispatch is null)
        {
            return new ProcessorRejectionOutcome(
                false,
                $"Deluno could not find the download {handoff.ReleaseName} came from, so it could not refuse that release. Keep the original and hand it back.");
        }

        var failureClass = string.IsNullOrWhiteSpace(report.FailureClass) ? null : report.FailureClass.Trim();
        var processorRemoved = report.SourceRemoved == true;
        var classNote = failureClass is null ? "" : $" ({failureClass})";
        var reason = processorRemoved
            ? $"{report.ProcessorName} could not process it{classNote} and removed it: {report.Message}"
            : $"{report.ProcessorName} refused it{classNote}: {report.Message.TrimEnd('.', ' ')}. Deluno will remove it from {dispatch.DownloadClientName} once its sharing time is up.";
        var whatHappened = processorRemoved ? "and removed it" : "and left it for Deluno to remove";

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
            logger.LogWarning(exception, "Could not refuse {ReleaseName} after {Processor} rejected it.", dispatch.ReleaseName.ForLog(), report.ProcessorName.ForLog());
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
            logger.LogWarning(exception, "Could not close the download of {ReleaseName} after {Processor} rejected it.", dispatch.ReleaseName.ForLog(), report.ProcessorName.ForLog());
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

        if (!DispatchTargets.HasTitle(dispatch.EntityType))
        {
            // A release chosen on Search has no title to search for (#1062).
            return new ProcessorRejectionOutcome(
                true,
                $"{report.ProcessorName} could not process {dispatch.ReleaseName} {whatHappened}. {DispatchTargets.ChosenOnSearchNote}",
                blocked.Id,
                null);
        }

        var search = await AnotherCopySearch.EnqueueAsync(
            jobScheduler, dispatch, report.Library.Name, "processor-rejected", handoff.Id, cancellationToken);

        return new ProcessorRejectionOutcome(
            true,
            $"{report.ProcessorName} could not process {dispatch.ReleaseName} {whatHappened}. Deluno refused that release and is searching for another copy.",
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
            clientId = handoff.ClientId,
            queueItemId = handoff.QueueItemId,
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

    /// <summary>
    /// Names each missing file where a person can act on it (#930), not only
    /// in Activity. When a missing file's own name resolves to a canonical
    /// episode, the case names that episode and offers to search for it or
    /// import manually; otherwise -- a movie, or a TV file that does not
    /// resolve -- it names what the processor said and what is missing.
    /// </summary>
    public async Task HandleOutputsMissingAsync(
        ProcessorMissingOutputReport report,
        CancellationToken cancellationToken)
    {
        var mediaType = DelunoValueNormalizers.NormalizeMediaType(report.Library.MediaType);
        var imported = report.TotalOutputFiles - report.MissingFiles.Count;

        if (mediaType == "tv" && report.EntityId is { Length: > 0 } seriesId)
        {
            var detail = await seriesCatalogRepository.GetInventoryDetailAsync(seriesId, cancellationToken);
            if (detail is not null)
            {
                var resolvedAny = false;
                foreach (var missingFile in report.MissingFiles)
                {
                    var parsed = SeriesNumberingResolver.ParseFileName(Path.GetFileName(missingFile)).Matches
                        .FirstOrDefault(match => match.SeasonNumber is not null && match.EpisodeNumber is not null);
                    var episode = parsed is null
                        ? null
                        : detail.Episodes.FirstOrDefault(item =>
                            item.SeasonNumber == parsed.SeasonNumber && item.EpisodeNumber == parsed.EpisodeNumber);
                    if (parsed is null || episode is null)
                    {
                        continue;
                    }

                    resolvedAny = true;
                    var summary = $"{detail.Title} S{parsed.SeasonNumber:D2}: {imported} of {report.TotalOutputFiles} imported, E{parsed.EpisodeNumber:D2} missing from {report.ProcessorName}'s output.";
                    var details = JsonSerializer.Serialize(new
                    {
                        libraryId = report.Library.Id,
                        seriesId,
                        episodeId = episode.EpisodeId,
                        seasonNumber = parsed.SeasonNumber,
                        episodeNumber = parsed.EpisodeNumber,
                        outputPath = report.OutputPath,
                        missingFile
                    });
                    await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                        new CreateSeriesImportRecoveryCaseRequest(
                            detail.Title, "processor-output-missing", summary, "Search for this episode, or import it manually.", details),
                        cancellationToken);
                }

                if (resolvedAny)
                {
                    return;
                }
            }
        }

        // A movie, or a TV file whose own name does not resolve to a
        // canonical episode: name what is known rather than nothing.
        var fallbackSummary = $"{report.ProcessorName} named {report.MissingFiles.Count} of its {report.TotalOutputFiles} output files that Deluno cannot find: {string.Join(", ", report.MissingFiles.Select(Path.GetFileName))}.";
        var fallbackDetails = JsonSerializer.Serialize(new
        {
            libraryId = report.Library.Id,
            entityId = report.EntityId,
            outputPath = report.OutputPath,
            missingFiles = report.MissingFiles
        });
        if (mediaType == "tv")
        {
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(
                    report.ReleaseName, "processor-output-missing", fallbackSummary, "Import what is there manually, or search again.", fallbackDetails),
                cancellationToken);
        }
        else
        {
            await movieCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(
                    report.ReleaseName, "processor-output-missing", fallbackSummary, "Import what is there manually, or search again.", fallbackDetails),
                cancellationToken);
        }
    }

    public async Task<ProcessorSourceGoneOutcome> HandleSourceGoneAsync(
        ProcessorFailureReport report,
        CancellationToken cancellationToken)
    {
        var reason = await SourceGoneRule.SettledBecauseAsync(
            report.Handoff, dispatches, movieCatalogRepository, seriesCatalogRepository, cancellationToken);
        return new ProcessorSourceGoneOutcome(reason is not null, reason);
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
