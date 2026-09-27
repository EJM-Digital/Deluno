using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Observability;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform;
using Deluno.Notifications;
using Deluno.Notifications.Contracts;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Deluno.Filesystem;

public sealed partial class ImportPipelineService(
    IPlatformSettingsRepository platformRepository,
    ILibrariesRepository librariesRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    IMediaProbeService mediaProbeService,
    IMediaDecisionService mediaDecisionService,
    IOutboundNotificationService? outboundNotificationService,
    IImportResolutionsRepository? importResolutionsRepository,
    IDownloadDispatchesRepository? downloadDispatchesRepository,
    IConnectionsRepository? connectionsRepository,
    ILogger<ImportPipelineService> logger,
    IRealtimeEventPublisher? realtimeEventPublisher,
    IRecycleBinService? recycleBinService = null,
    IQualityRepository? qualityRepository = null,
    IGuidePackageStore? guidePackageStore = null,
    IReleasePreferencePlanRepository? releasePreferencePlanRepository = null,
    IBlockedReleaseRepository? blockedReleases = null,
    IImportFailureRuleRepository? failureRules = null,
    IDispatchAlertRepository? dispatchAlerts = null,
    // #662: queued, never awaited inline - a slow or down media server must
    // never hold up or fail an import that already landed.
    IJobScheduler? mediaServerJobScheduler = null,
    // Only a test passes this, to say "do not wait" without waiting for it.
    TimeSpan? settleWindow = null,
    INotificationService? notificationService = null,
    // #806: the same "ask the client to let go" gateway the sharing rule
    // uses (#288), reused here for Remove completed rather than duplicating
    // the client-action path.
    Deluno.Recovery.Services.IDownloadClientActionGateway? downloadClientActions = null,
    // #765: only a test passes this, to fake a near-full or unreadable
    // volume without needing one. Returns available free bytes for the
    // folder's volume, or null when the filesystem could not report it.
    Func<string, long?>? freeSpaceBytesProbe = null)
    : IImportPipelineService
{
    private readonly TimeSpan _settleWindow = settleWindow ?? ImportFileReadiness.SettleWindow;
    private readonly Func<string, long?> _freeSpaceBytesProbe = freeSpaceBytesProbe ?? DefaultFreeSpaceBytesProbe;

    public async Task<ImportPreviewResponse> PreviewAsync(ImportPreviewRequest request, CancellationToken cancellationToken)
    {
        // Named as the import will be named, from its grab (#617).
        if (await GrabbedTitleAsync(await ResolveDispatchAsync(request.DispatchId, cancellationToken), NormalizeMediaType(request.MediaType), cancellationToken) is { } grabbed)
        {
            request = request with { Title = grabbed.Title, Year = grabbed.Year ?? request.Year };
        }

        var tvDirectoryFiles = NormalizeMediaType(request.MediaType) == "tv"
            ? TryListImportableVideoFiles(request.SourcePath)
            : null;
        var settings = await platformRepository.GetAsync(cancellationToken);
        var rules = await librariesRepository.ListDestinationRulesAsync(cancellationToken);
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        if (tvDirectoryFiles is { Count: > 1 })
        {
            var pack = await BuildTvPackPlanAsync(
                request,
                tvDirectoryFiles,
                settings,
                rules,
                libraries,
                cancellationToken);
            return pack.Summary;
        }

        var preview = await ResolveImportPreviewAsync(request, settings, rules, libraries, cancellationToken);
        if (preview.SourceExists && !ImportFileReadiness.IsReady(preview.SourcePath))
        {
            return AddReadinessWarning(preview);
        }

        return await EnrichPreviewWithMediaProbeAsync(preview, cancellationToken);
    }

    public async Task<ImportPipelineResult> ExecuteAsync(ImportExecuteRequest request, CancellationToken cancellationToken)
    {
        var mediaType = NormalizeMediaType(request.Preview.MediaType);

        // Named from its grab before anything else, so the folder, the file
        // and the catalogue entry agree (#617). An import queued from Transfers
        // arrived titled with the release's name.
        if (await GrabbedTitleAsync(await ResolveDispatchAsync(request.DispatchId, cancellationToken), mediaType, cancellationToken) is { } grabbed)
        {
            request = request with { Preview = request.Preview with { Title = grabbed.Title, Year = grabbed.Year ?? request.Preview.Year } };
        }
        var tvDirectoryFiles = mediaType == "tv"
            ? TryListImportableVideoFiles(request.Preview.SourcePath)
            : null;
        if (tvDirectoryFiles is { Count: > 1 })
        {
            return await ExecuteTvPackAsync(request, tvDirectoryFiles, cancellationToken);
        }

        var settings = await platformRepository.GetAsync(cancellationToken);
        var rules = await librariesRepository.ListDestinationRulesAsync(cancellationToken);
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        // The release Deluno chose is what the file is named from, so the
        // decision is resolved before the destination, not after it.
        var decision = await ResolveNamingDecisionAsync(
            request.Preview,
            libraries,
            (await ResolveDispatchAsync(request.DispatchId, cancellationToken))?.ReleaseName,
            cancellationToken);
        var resolvedPreview = await ResolveImportPreviewAsync(request.Preview, settings, rules, libraries, cancellationToken, decision);

        if (resolvedPreview.SourceExists &&
            !await ImportFileReadiness.WaitUntilReadyAsync(resolvedPreview.SourcePath, _settleWindow, cancellationToken))
        {
            // Do not probe or copy a file while a download client or processor
            // may still be writing it. A processor finishing with its own output
            // takes a moment, which the wait above covers (#562); anything
            // longer is somebody else's business, and the worker's retries can
            // safely try again without creating a false recovery failure.
            return Failed(
                ImportFileReadiness.RetryableStatusCode,
                $"The source file was still being written, or locked by another process, {_settleWindow.TotalSeconds:0} seconds after Deluno started importing it. Deluno will retry when it is stable.");
        }

        var preview = await EnrichPreviewWithMediaProbeAsync(resolvedPreview, cancellationToken);

        if (realtimeEventPublisher is not null &&
            downloadDispatchesRepository is not null &&
            !string.IsNullOrWhiteSpace(request.DispatchId))
        {
            var dispatch = await downloadDispatchesRepository.GetDispatchAsync(request.DispatchId, cancellationToken);
            if (dispatch is not null)
            {
                await realtimeEventPublisher.PublishDispatchImportStartedAsync(
                    dispatch.Id,
                    dispatch.ReleaseName,
                    mediaType,
                    cancellationToken);
            }
        }

        // Never write media to a relative path. If no rule, library or platform
        // setting produced a root, the destination resolves against whatever the
        // host's working directory happens to be, which is how imports silently
        // ended up outside the library while reporting success. A missing root is
        // a configuration failure the user has to see, not a path to guess at.
        if (!Path.IsPathRooted(preview.DestinationPath))
        {
            var message = $"No library root is configured for {mediaType}, so Deluno has nowhere to import '{preview.SourcePath}'.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "missingLibraryRoot",
                message,
                "Set the root folder for this library in Media Management, then retry the import.",
                cancellationToken);
            return Failed(StatusCodes.Status409Conflict, message);
        }

        var extension = Path.GetExtension(preview.DestinationPath);

        if (!SupportedVideoExtensions.Extensions.Contains(extension))
        {
            var message = $"The file extension '{extension}' is not configured as an importable video file.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "unsupportedFile",
                message,
                "Choose a video file such as MKV, MP4, M4V, AVI, MOV, WMV, TS, or M2TS.",
                cancellationToken);
            return Failed(StatusCodes.Status400BadRequest, message);
        }

        if (!File.Exists(preview.SourcePath))
        {
            var message = Directory.Exists(preview.SourcePath)
                ? $"No importable video file was found inside '{preview.SourcePath}'."
                : $"The source file was not found at '{preview.SourcePath}'. Deluno checked from its own process.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "missingSource",
                message,
                "Check the download client's completed path, Docker volume mappings, or Windows service account permissions.",
                cancellationToken);
            return Failed(StatusCodes.Status404NotFound, message);
        }

        if (IsSamePath(preview.SourcePath, preview.DestinationPath))
        {
            const string message = "The source and destination resolve to the same file. Deluno will not import a file onto itself.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "samePath",
                message,
                "Choose a destination root that is separate from the completed download path, or adjust the file name/routing rule.",
                cancellationToken);
            return Failed(StatusCodes.Status409Conflict, message);
        }

        if (File.Exists(preview.DestinationPath) && !request.Overwrite)
        {
            const string message = "The destination file already exists. Enable overwrite or choose a different naming/routing rule.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "conflict",
                "The destination file already exists.",
                "Preview the route, confirm the existing file, then enable overwrite only if replacement is intentional.",
                cancellationToken);
            return Failed(StatusCodes.Status409Conflict, message);
        }

        if (File.Exists(preview.DestinationPath) &&
            request.Overwrite &&
            !string.IsNullOrWhiteSpace(request.DispatchId) &&
            (string.IsNullOrWhiteSpace(request.ExpectedExistingPath) ||
             !IsSamePath(request.ExpectedExistingPath, preview.DestinationPath)))
        {
            const string message = "Replacement blocked: the resolved destination is not the file this title owned when the release was dispatched.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "replacementOwnershipMismatch",
                message,
                "Review the destination naming rule and the title's tracked file. Deluno did not overwrite the unrelated path.",
                cancellationToken);
            return Failed(StatusCodes.Status409Conflict, message);
        }

        if (preview.MediaProbe is { Status: "unreadable" })
        {
            var message = preview.MediaProbe.Message ?? "Deluno could not read this file to check it.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                ImportFailurePolicy.MediaProbeUnreadable,
                message,
                "Check the file is reachable — a share that dropped, a lock, or a disk error. The release itself is not suspected.",
                cancellationToken);
            return Failed(StatusCodes.Status400BadRequest, message);
        }

        if (preview.MediaProbe is { Status: "failed" })
        {
            var message = preview.MediaProbe.Message ?? "Media probing failed. Deluno cannot confirm this file is playable.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                ImportFailurePolicy.MediaProbeRejected,
                message,
                "Check whether the file is complete, playable, and readable by ffprobe before importing.",
                cancellationToken);
            return Failed(StatusCodes.Status400BadRequest, message);
        }

        if (preview.MediaProbe is { Status: "succeeded", VideoStreams.Count: 0 })
        {
            const string message = "No video stream was detected in this file.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "noVideoStream",
                message,
                "Choose a valid video file. Subtitle, sample, archive, or metadata-only files should not be imported.",
                cancellationToken);
            return Failed(StatusCodes.Status400BadRequest, message);
        }

        // Every guard above is absolute, and a processor's failures are
        // relative: a botched remux yields a valid file that is simply less
        // than what went in (#574).
        var lost = await WhatTheProcessorLostAsync(request, preview, cancellationToken);
        if (lost is not null)
        {
            // The broken processed copy is deleted; the original -- already
            // shown good at Checkpoint A -- is kept until a person chooses
            // (#919). Only the processed file, never the download it came
            // from (request.ProcessedFromPath).
            DeleteBrokenProcessedCopy(preview.SourcePath);
            await RecordImportFailureAsync(
                request,
                request.Preview,
                ImportFailurePolicy.ProcessedFileIsLess,
                lost,
                "Import the original, run the processor again, or search for another release.",
                cancellationToken);
            return Failed(StatusCodes.Status400BadRequest, lost);
        }

        if (preview.MediaProbe?.DurationSeconds is > 0 and < 120)
        {
            const string message = "The detected runtime is under two minutes, so Deluno is treating this as a likely sample.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "likelySample",
                message,
                "Import the full release file instead of a sample or trailer.",
                cancellationToken);
            return Failed(StatusCodes.Status400BadRequest, message);
        }

        if (mediaType == "tv" && !string.IsNullOrWhiteSpace(request.Preview.SeriesId))
        {
            var numbering = await ResolveSeriesNumberingAsync(request.Preview, cancellationToken);
            var resolution = ParseTvImportNumbers(
                preview.SourcePath,
                preview.DestinationPath,
                preview.SourceSizeBytes,
                SeriesNumberingSchemes.Resolve(
                    request.Preview.SeriesType ?? numbering?.SeriesType,
                    request.Preview.NumberingScheme ?? numbering?.NumberingScheme),
                numbering);
            if (resolution.Episodes.Count == 0)
            {
                var seasonPackLabel = resolution.SeasonPacks.Count > 0;
                var message = seasonPackLabel
                    ? "The filename identifies a TV season pack but does not prove which catalogued episode files are present. Deluno left it unmatched instead of marking the whole season covered."
                    : "The TV filename could not be matched to a catalogued episode. Deluno left it unmatched instead of inventing an episode identity.";
                await RecordImportFailureAsync(
                    request,
                    request.Preview,
                    "unmatched",
                    message,
                    seasonPackLabel
                        ? "Review the pack contents and map the actual episode files before retrying the import."
                        : "Review the series numbering and map this file to one catalogued episode before retrying the import.",
                    cancellationToken);
                return Failed(StatusCodes.Status409Conflict, message);
            }
        }

        var replacementRisk = await ValidateReplacementAsync(request, preview, cancellationToken);
        if (replacementRisk is not null)
        {
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "replacementRejected",
                replacementRisk,
                "Use force replacement only after confirming the incoming file is intentionally better.",
                cancellationToken);
            return Failed(StatusCodes.Status409Conflict, replacementRisk, ImportFailurePolicy.ReplacementRejected);
        }

        // #765: a destination volume that would drop below the configured
        // floor once this import lands fails at the worst possible moment --
        // mid-copy -- with nothing saying why beforehand. Checked here,
        // after every other guard, so a release that was always going to be
        // refused for another reason is never blamed for the disk instead.
        if (!settings.SkipFreeSpaceCheck)
        {
            var freeSpaceProblem = DescribeInsufficientFreeSpace(preview.DestinationFolder, settings.MinimumFreeSpaceMb);
            if (freeSpaceProblem is not null)
            {
                await RecordImportFailureAsync(
                    request,
                    request.Preview,
                    ImportFailurePolicy.InsufficientFreeSpace,
                    freeSpaceProblem,
                    "Free up space on the destination volume, lower the minimum free space setting, or turn on 'Skip free space check' if this filesystem cannot report free space.",
                    cancellationToken);
                return Failed(StatusCodes.Status507InsufficientStorage, freeSpaceProblem);
            }
        }

        var requestedMode = NormalizeTransferMode(request.TransferMode);
        var mode = requestedMode == "auto" ? preview.PreferredTransferMode : requestedMode;
        var usedFallback = false;
        Directory.CreateDirectory(preview.DestinationFolder);
        var destinationPreExisted = File.Exists(preview.DestinationPath);
        var backupPath = destinationPreExisted && request.Overwrite
            ? BuildTemporaryPath(preview.DestinationPath, ".deluno-backup")
            : null;
        var stagingPath = BuildTemporaryPath(preview.DestinationPath, ".deluno-stage");
        var restoreSourceOnFailure = false;

        // A restart between here and the final catalogue write (below) leaves
        // this staging file on disk with nothing to say what it was for. The
        // marker records enough to finish or undo the import automatically on
        // the next startup (#913) - it is deleted the moment the file is
        // safely placed or the attempt is rolled back.
        WriteRestartMarker(stagingPath, preview.SourcePath, preview.DestinationPath, mediaType, TitleForActivity(request.Preview));

        try
        {
            await RecordImportStartedAsync(request, preview, mediaType, cancellationToken);

            if (backupPath is not null)
            {
                File.Move(preview.DestinationPath, backupPath, overwrite: true);
            }

            if (mode == "hardlink")
            {
                if (!preview.HardlinkAvailable)
                {
                    const string message = "Hardlinking is not available for these paths. Use copy fallback or choose paths on the same filesystem.";
                    if (!request.AllowCopyFallback)
                    {
                        RollBackPartialImport(preview.SourcePath, preview.DestinationPath, stagingPath, backupPath, restoreSourceOnFailure);
                        await RecordImportFailureAsync(
                            request,
                            request.Preview,
                            "hardlinkUnavailable",
                            message,
                            "Enable copy fallback or place downloads and the library on the same filesystem so hardlinks can be created.",
                            cancellationToken);
                        return Failed(StatusCodes.Status400BadRequest, message);
                    }

                    AtomicCopy(preview.SourcePath, stagingPath, overwrite: false);
                    usedFallback = true;
                    mode = "copy";
                }
                else if (!TryCreateHardlink(preview.SourcePath, stagingPath, out var hardlinkError))
                {
                    if (!request.AllowCopyFallback)
                    {
                        RollBackPartialImport(preview.SourcePath, preview.DestinationPath, stagingPath, backupPath, restoreSourceOnFailure);
                        await RecordImportFailureAsync(
                            request,
                            request.Preview,
                            "hardlinkFailed",
                            hardlinkError,
                            "Enable copy fallback, check filesystem permissions, or import from a path where the OS allows hardlinks.",
                            cancellationToken);
                        return Failed(StatusCodes.Status400BadRequest, hardlinkError);
                    }

                    TryDelete(stagingPath);
                    AtomicCopy(preview.SourcePath, stagingPath, overwrite: false);
                    usedFallback = true;
                    mode = "copy";
                }
            }
            else if (mode == "move")
            {
                File.Move(preview.SourcePath, stagingPath, overwrite: false);
                restoreSourceOnFailure = true;
            }
            else
            {
                AtomicCopy(preview.SourcePath, stagingPath, overwrite: false);
                mode = "copy";
            }

            var stagedSize = VerifyStagedImport(stagingPath);
            File.Move(stagingPath, preview.DestinationPath, overwrite: request.Overwrite);
            VerifyFinalImport(preview.DestinationPath, stagedSize);
            DeleteRestartMarker(stagingPath);
            await ApplyImportPermissionsAsync(preview.DestinationPath, settings, cancellationToken);

            var catalogImportResult = await MarkCatalogImportedAsync(
                request.Preview,
                preview,
                mediaType,
                libraries,
                request.DispatchId,
                cancellationToken);

            var matchedLibrary = ResolveLibraryForImport(preview.DestinationPath, mediaType, libraries);

            // The catalogue write above already moved the title/episode onto
            // the new file. Whatever file it tracked before that is the file
            // this upgrade replaces, and by this point the new file is placed
            // and verified - so it is safe to recycle now (#656).
            var replacementCleanup = await RecycleReplacedFilesAsync(
                catalogImportResult.ReplacedFilePaths,
                preview.DestinationPath,
                mediaType,
                libraries,
                cancellationToken);
            foreach (var warning in replacementCleanup.Warnings)
            {
                logger.LogWarning("{Warning}", warning);
            }

            // #662: the new file, plus whichever old file this upgrade
            // recycled (#656) - a fresh import has none of those, an upgrade
            // has exactly the one it just replaced.
            await EnqueueMediaServerRefreshAsync(
                mediaType,
                [
                    new MediaServerPathChange(preview.DestinationPath, MediaServerChangeKind.Created),
                    .. catalogImportResult.ReplacedFilePaths.Select(replaced => new MediaServerPathChange(replaced, MediaServerChangeKind.Deleted))
                ],
                cancellationToken);

            if (catalogImportResult.CatalogUpdated && !string.IsNullOrWhiteSpace(request.DispatchId))
            {
                await platformRepository.MarkWorkflowVerifiedAsync(cancellationToken);
            }

            if (importResolutionsRepository is not null && !string.IsNullOrEmpty(request.DispatchId) && catalogImportResult.CatalogUpdated && catalogImportResult.CatalogId is not null)
            {
                await importResolutionsRepository.RecordSuccessAsync(
                    request.DispatchId,
                    mediaType,
                    catalogImportResult.CatalogId,
                    mediaType == "tv" ? "series" : "movie",
                    cancellationToken);
            }

            if (downloadDispatchesRepository is not null && !string.IsNullOrEmpty(request.DispatchId))
            {
                await downloadDispatchesRepository.RecordImportOutcomeAsync(
                    request.DispatchId,
                    "imported",
                    preview.DestinationPath,
                    null,
                    null,
                    cancellationToken);

                // Announce it now, from where the outcome is known. The only
                // other publisher of this event is the dispatch poller, which
                // runs hourly — so "your download is in your library" could
                // arrive up to an hour after it was true (#264).
                if (realtimeEventPublisher is not null)
                {
                    await realtimeEventPublisher.PublishDispatchImportCompletedAsync(
                        request.DispatchId,
                        TitleForActivity(request.Preview),
                        succeeded: true,
                        importedPath: preview.DestinationPath,
                        failureReason: null,
                        cancellationToken);
                }

                await RemoveCompletedDownloadIfConfiguredAsync(request.DispatchId, cancellationToken);
            }

            var cleanup = ApplyWorkflowCleanup(
                preview.SourcePath,
                matchedLibrary,
                await IsStillSharedByClientAsync(request.DispatchId, cancellationToken),
                mode);
            var provenance = BuildImportProvenance(
                request,
                preview.SourcePath,
                preview.DestinationPath,
                preview.PreferredTransferMode,
                mode,
                usedFallback,
                catalogImportResult,
                matchedLibrary,
                cleanup.Summary,
                preview.MatchedRuleId,
                preview.MatchedRuleName,
                preview.MediaProbe);
            if (downloadDispatchesRepository is not null && !string.IsNullOrWhiteSpace(request.DispatchId))
            {
                await downloadDispatchesRepository.RecordTimelineEventAsync(
                    request.DispatchId,
                    "file_provenance_recorded",
                    JsonSerializer.Serialize(provenance),
                    cancellationToken);
            }

            await activityFeedRepository.RecordActivityAsync(
                "filesystem.import.completed",
                ImportCompletedMessage(TitleForActivity(request.Preview), provenance, mode) + replacementCleanup.Summary,
                JsonSerializer.Serialize(provenance),
                null,
                mediaType == "tv" ? "series" : "movie",
                catalogImportResult.EntityId,
                cancellationToken);

            await DispatchImportOutcomeNotificationAsync(
                "import.completed",
                $"Import completed: {TitleForActivity(request.Preview)}",
                ImportCompletedMessage(TitleForActivity(request.Preview), provenance, mode) + replacementCleanup.Summary,
                JsonSerializer.Serialize(new
                {
                    title = TitleForActivity(request.Preview),
                    mediaType,
                    transferMode = provenance.TransferModeUsed
                }),
                prefs => prefs.ImportCompletedEnabled,
                "completion",
                cancellationToken);

            if (catalogImportResult.ReplacedFilePaths.Count > 0)
            {
                await DispatchImportOutcomeNotificationAsync(
                    "upgrade",
                    $"Upgraded: {TitleForActivity(request.Preview)}",
                    $"{TitleForActivity(request.Preview)} was upgraded by this import.",
                    JsonSerializer.Serialize(new { title = TitleForActivity(request.Preview), mediaType }),
                    prefs => prefs.UpgradeEnabled,
                    "upgrade",
                    cancellationToken);
            }

            await RecordRenameAsync(
                provenance,
                mediaType,
                catalogImportResult.EntityId,
                cancellationToken);

            await activityFeedRepository.RecordDecisionAsync(
                new DecisionExplanationPayload(
                    Scope: "filesystem.import",
                    Status: "completed",
                    Reason: usedFallback
                        ? "Deluno imported with copy fallback because the preferred hardlink path was unavailable."
                        : $"Deluno imported using {mode} because that transfer mode was selected by preview and user settings.",
                    Inputs: new Dictionary<string, string?>
                    {
                        ["sourcePath"] = preview.SourcePath,
                        ["destinationPath"] = preview.DestinationPath,
                        ["preferredTransferMode"] = preview.PreferredTransferMode,
                        ["requestedTransferMode"] = request.TransferMode,
                        ["transferModeUsed"] = mode,
                        ["matchedRuleId"] = preview.MatchedRuleId,
                        ["matchedRuleName"] = preview.MatchedRuleName,
                        ["catalogUpdated"] = catalogImportResult.CatalogUpdated.ToString(),
                        ["workflowCleanup"] = cleanup.Summary,
                        ["replacedFileCleanup"] = replacementCleanup.Messages.Count == 0
                            ? null
                            : string.Join(" ", replacementCleanup.Messages),
                        ["dispatchId"] = provenance.DispatchId,
                        ["releaseNameEvidence"] = provenance.ReleaseNameEvidence,
                        ["indexer"] = provenance.IndexerName,
                        ["downloadClient"] = provenance.DownloadClientName,
                        ["preferencePlan"] = provenance.PreferencePlanId,
                        ["preferencePlanHash"] = provenance.PreferencePlanHash,
                        ["matchedCustomRules"] = provenance.MatchedRuleNames
                    },
                    Outcome: $"{TitleForActivity(request.Preview)} imported to {preview.DestinationPath}. {cleanup.Summary}{replacementCleanup.Summary}",
                    Alternatives: []),
                null,
                mediaType == "tv" ? "series" : "movie",
                catalogImportResult.EntityId,
                cancellationToken);

            var response = new ImportExecuteResponse(
                Preview: preview,
                Executed: true,
                TransferModeUsed: mode,
                UsedFallback: usedFallback,
                CatalogUpdated: catalogImportResult.CatalogUpdated,
                Message: (usedFallback
                    ? $"Import completed with copy fallback because hardlink creation was not possible. {cleanup.Summary}"
                    : $"Import completed using {mode}. {cleanup.Summary}") + replacementCleanup.Summary);

            restoreSourceOnFailure = false;
            if (backupPath is not null)
            {
                if (recycleBinService is null)
                {
                    // Keep hand-constructed test/utility instances backwards
                    // compatible; the host always supplies the recoverable
                    // service through DI.
                    TryDelete(backupPath);
                }
                else if (matchedLibrary is null)
                {
                    logger.LogWarning(
                        "The replaced file was left at {BackupPath} because no library matched {DestinationPath}.",
                        backupPath,
                        preview.DestinationPath);
                }
                else if (await recycleBinService.StoreReplacementAsync(
                    matchedLibrary,
                    preview.DestinationPath,
                    backupPath,
                    cancellationToken) is null && File.Exists(backupPath))
                {
                    logger.LogWarning(
                        "The replaced file was left at {BackupPath} because it could not be moved into the recycle bin.",
                        backupPath);
                }
            }

            DelunoObservability.ImportCompleted.Add(1, new("media.type", mediaType), new("transfer.mode", mode));
            logger.LogInformation(
                "Import completed for {MediaType} title {Title} using {TransferMode}. Dispatch={DispatchId} ReleaseNameEvidence={ReleaseNameEvidence} Indexer={IndexerName} DownloadClient={DownloadClientName} Source={SourcePath} Destination={DestinationPath} Rename={Rename} MatchedRules={MatchedRules} PreferencePlan={PreferencePlanId} PreferencePlanHash={PreferencePlanHash} Fallback={UsedFallback}",
                mediaType,
                TitleForActivity(request.Preview),
                mode,
                provenance.DispatchId,
                provenance.ReleaseNameEvidence,
                provenance.IndexerName,
                provenance.DownloadClientName,
                provenance.SourcePath,
                preview.DestinationPath,
                provenance.Rename,
                provenance.MatchedRuleNames,
                provenance.PreferencePlanId,
                provenance.PreferencePlanHash,
                usedFallback);

            return new ImportPipelineResult(true, StatusCodes.Status200OK, response, response.Message);
        }
        catch (UnauthorizedAccessException)
        {
            RollBackPartialImport(preview.SourcePath, preview.DestinationPath, stagingPath, backupPath, restoreSourceOnFailure);
            const string message = "Deluno does not have permission to import this file.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "permission",
                message,
                "Grant the Deluno service account read access to downloads and write access to the destination library.",
                cancellationToken);
            return Failed(StatusCodes.Status403Forbidden, message);
        }
        catch (IOException ioException)
        {
            RollBackPartialImport(preview.SourcePath, preview.DestinationPath, stagingPath, backupPath, restoreSourceOnFailure);
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "io",
                ioException.Message,
                "Check whether the file is still downloading, locked by another process, or on an unavailable network path.",
                cancellationToken);
            // Not a refusal (#603). A lock, a file still arriving or a path that went
            // away can clear by itself, so this is retried, and the processor is not
            // told "not imported" - which it would take as leave to remove its copy.
            return Failed(StatusCodes.Status503ServiceUnavailable, ioException.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RollBackPartialImport(preview.SourcePath, preview.DestinationPath, stagingPath, backupPath, restoreSourceOnFailure);
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "importFailed",
                exception.Message,
                "Review the recovery case, confirm whether the source and destination files are intact, then retry the import.",
                cancellationToken);
            return Failed(StatusCodes.Status500InternalServerError, exception.Message);
        }
    }

    private static ImportPipelineResult Failed(int statusCode, string message, string? failureKind = null)
        => new(false, statusCode, null, message, failureKind);

    private static ImportPreviewResponse AddReadinessWarning(ImportPreviewResponse preview)
        => preview with
        {
            Warnings = [.. preview.Warnings, "The source file is still being written or locked. Deluno will wait before probing or importing it."],
            DecisionSteps = [.. preview.DecisionSteps, "Source: the file is visible, but it is not stable enough to read safely yet."]
        };

    private static ImportPreviewResponse AddWarning(ImportPreviewResponse preview, string warning)
        => preview with
        {
            Warnings = [.. preview.Warnings, warning],
            DecisionSteps = [.. preview.DecisionSteps, $"Attention: {warning}"]
        };

    private async Task<TvPackPlan> BuildTvPackPlanAsync(
        ImportPreviewRequest request,
        IReadOnlyList<string> sourceFiles,
        PlatformSettingsSnapshot settings,
        IReadOnlyList<DestinationRuleItem> rules,
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken,
        IReadOnlyList<DispatchReplacementTarget>? replacementTargets = null,
        bool replacementAuthorized = false)
    {
        var blockReasons = new List<string>();
        var normalizedReplacementTargets = (replacementTargets ?? [])
            .Where(target => !string.IsNullOrWhiteSpace(target.EntityId) &&
                             !string.IsNullOrWhiteSpace(target.ExpectedPath))
            .Select(target => new DispatchReplacementTarget(
                target.EntityId.Trim(),
                target.ExpectedPath.Trim()))
            .ToArray();
        var duplicateReplacementTargets = normalizedReplacementTargets
            .GroupBy(target => target.EntityId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(target => Path.GetFullPath(target.ExpectedPath))
                .Distinct(GetPathComparer()).Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateReplacementTargets.Length > 0)
        {
            blockReasons.Add($"Replacement ownership names conflicting paths for episode(s): {string.Join(", ", duplicateReplacementTargets)}.");
        }
        var replacementTargetsByEpisode = normalizedReplacementTargets
            .GroupBy(target => target.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var replacements = new List<TvPackReplacement>();
        var numbering = await ResolveSeriesNumberingAsync(request, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.SeriesId) || numbering is null)
        {
            blockReasons.Add("Choose the existing TV show before importing a season pack so every file can be matched to its canonical episode.");
        }

        var declaredSeasons = SeriesNumberingResolver.ParseSeasonPackNumbers(request.SourcePath)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        if (declaredSeasons.Length != 1)
        {
            blockReasons.Add("The download folder must identify exactly one season before Deluno can treat it as a season-pack operation.");
        }

        var files = new List<TvPackFilePlan>();
        var skippedFiles = new List<TvPackSkippedFilePlan>();
        foreach (var sourcePath in sourceFiles)
        {
            var fileRequest = request with
            {
                SourcePath = sourcePath,
                FileName = Path.GetFileName(sourcePath)
            };
            var preview = await ResolveImportPreviewAsync(fileRequest, settings, rules, libraries, cancellationToken);
            if (preview.SourceExists && ImportFileReadiness.IsReady(preview.SourcePath))
            {
                preview = await EnrichPreviewWithMediaProbeAsync(preview, cancellationToken);
            }

            // #889: a problem that belongs to the whole pack (a setup gap or
            // an outage, never this file's fault) still blocks every file, the
            // same as before - a local problem does not deserve the same
            // reach the setup-or-outage rule protects everything else from.
            var packWideWarnings = new List<string>();
            if (!preview.SourceExists)
            {
                packWideWarnings.Add("Source file is no longer present.");
            }
            else if (!ImportFileReadiness.IsReady(preview.SourcePath))
            {
                // Execution already waited out the pack's shared settle window
                // (see ExecuteTvPackAsync) before this method is ever called, so
                // this only fires from a preview - it is informational there,
                // not something a still-arriving file should be skipped over.
                packWideWarnings.Add("Source file is still being written or locked.");
            }
            if (!Path.IsPathRooted(preview.DestinationPath))
            {
                packWideWarnings.Add("No rooted TV library destination is configured.");
            }

            // #889 (cleanup sheet E9): a problem that belongs to this one
            // file - it is corrupt, too short, the wrong type, or would land
            // on its own source - no longer stops the rest of the pack. The
            // file is treated the same as an unmatched extra: left alone,
            // named, and searched for again on its own, while every other
            // episode in the pack still imports.
            var fileOwnWarnings = new List<string>();
            if (!preview.IsSupportedMediaFile)
            {
                fileOwnWarnings.Add("The destination extension is not an importable video type.");
            }
            if (IsSamePath(preview.SourcePath, preview.DestinationPath))
            {
                fileOwnWarnings.Add("Source and destination resolve to the same file.");
            }
            if (preview.MediaProbe is { Status: "failed" })
            {
                fileOwnWarnings.Add(preview.MediaProbe.Message ?? "Media probing failed.");
            }
            if (preview.MediaProbe is { Status: "succeeded", VideoStreams.Count: 0 })
            {
                fileOwnWarnings.Add("No video stream was detected.");
            }
            if (preview.MediaProbe?.DurationSeconds is > 0 and < 120)
            {
                fileOwnWarnings.Add("The runtime is under two minutes and looks like a sample.");
            }

            var fileNumbers = ParseTvImportNumbers(
                preview.SourcePath,
                preview.DestinationPath,
                preview.SourceSizeBytes,
                SeriesNumberingSchemes.Resolve(
                    request.SeriesType ?? numbering?.SeriesType,
                    request.NumberingScheme ?? numbering?.NumberingScheme),
                numbering);
            if (fileNumbers.Episodes.Count == 0)
            {
                // Not a reason to refuse the rest of the pack (#652): an extra,
                // featurette or sample the catalogue never listed as an episode
                // does not belong in the library, so it is left where it is and
                // named rather than treated as a blocking problem. A season-0
                // special the catalogue DOES list resolves to an episode above
                // and is imported like any other file. Whatever else is wrong
                // with a file that is being skipped anyway (a failed probe, a
                // short runtime) does not matter, so it is not carried into
                // blockReasons.
                skippedFiles.Add(new TvPackSkippedFilePlan(
                    preview,
                    "The filename does not resolve to one or more canonical episodes in this show."));
                continue;
            }
            if (declaredSeasons.Length == 1 && fileNumbers.Episodes.Any(episode => episode.SeasonNumber != declaredSeasons[0]))
            {
                packWideWarnings.Add($"The file resolves outside season {declaredSeasons[0]:00} named by the download folder.");
            }

            if (packWideWarnings.Count > 0)
            {
                blockReasons.Add($"{Path.GetFileName(sourcePath)}: {string.Join(" ", packWideWarnings)}");
                continue;
            }

            if (fileOwnWarnings.Count > 0)
            {
                var episodeKeys = string.Join(", ", fileNumbers.Episodes
                    .Select(episode => EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal));
                skippedFiles.Add(new TvPackSkippedFilePlan(
                    preview,
                    $"Matched to episode(s) {episodeKeys} but not imported: {string.Join(" ", fileOwnWarnings)}"));
                continue;
            }

            files.Add(new TvPackFilePlan(fileRequest, preview, fileNumbers, []));
        }

        if (files.Count == 0)
        {
            blockReasons.Add("No file in this pack resolves to one or more canonical episodes in this show.");
        }

        foreach (var duplicate in files
                     .SelectMany(file => file.Numbers.Episodes.Select(episode => new
                     {
                         Key = EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber),
                         file.Preview.SourcePath
                     }))
                     .GroupBy(item => item.Key, StringComparer.Ordinal)
                     .Where(group => group.Select(item => item.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            blockReasons.Add($"Episode {duplicate.Key} is claimed by more than one file in the pack.");
        }

        foreach (var duplicate in files
                     .GroupBy(file => Path.GetFullPath(file.Preview.DestinationPath), GetPathComparer())
                     .Where(group => group.Count() > 1))
        {
            blockReasons.Add($"More than one source resolves to destination '{duplicate.Key}'.");
        }

        var alreadyPlacedAndOwned = numbering is not null &&
                                    await IsTvPackAlreadyCommittedAsync(request, files, numbering, cancellationToken);
        if (numbering is not null && !alreadyPlacedAndOwned)
        {
            var episodeIds = numbering.Episodes.ToDictionary(
                episode => (episode.SeasonNumber, episode.EpisodeNumber),
                episode => episode.EpisodeId);
            var packEpisodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                foreach (var episode in file.Numbers.Episodes)
                {
                    if (!episodeIds.TryGetValue((episode.SeasonNumber, episode.EpisodeNumber), out var episodeId))
                    {
                        continue;
                    }
                    packEpisodeIds.Add(episodeId);
                    var trackedPath = await seriesCatalogRepository.GetEpisodeFilePathAsync(episodeId, cancellationToken);
                    replacementTargetsByEpisode.TryGetValue(episodeId, out var replacementTarget);
                    if (string.IsNullOrWhiteSpace(trackedPath))
                    {
                        if (replacementTarget is not null)
                        {
                            blockReasons.Add($"Episode {EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber)} no longer owns the file named by the replacement dispatch.");
                        }
                        continue;
                    }

                    if (!replacementAuthorized)
                    {
                        if (!IsSamePath(trackedPath, file.Preview.DestinationPath))
                        {
                            blockReasons.Add($"Episode {EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber)} already owns a different file. Replace it through an episode-scoped review instead of a season-pack import.");
                        }
                        continue;
                    }

                    if (replacementTarget is null)
                    {
                        blockReasons.Add($"Episode {EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber)} has an installed file but no dispatch-time replacement target.");
                        continue;
                    }
                    if (!IsSamePath(replacementTarget.ExpectedPath, trackedPath))
                    {
                        blockReasons.Add($"Episode {EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber)} no longer owns the exact file authorized when the pack was dispatched.");
                        continue;
                    }

                    replacements.Add(new TvPackReplacement(
                        episodeId,
                        trackedPath,
                        file.Preview.DestinationPath));
                }
            }

            foreach (var target in normalizedReplacementTargets.Where(target => !packEpisodeIds.Contains(target.EntityId)))
            {
                blockReasons.Add($"Replacement target '{target.EntityId}' is not one of the episodes resolved from this season pack.");
            }
        }

        if (replacementAuthorized && !alreadyPlacedAndOwned && normalizedReplacementTargets.Length == 0)
        {
            blockReasons.Add("Season-pack replacement authority has no episode-scoped ownership manifest.");
        }
        if (!replacementAuthorized && normalizedReplacementTargets.Length > 0)
        {
            blockReasons.Add("An episode replacement manifest was supplied without replacement authority.");
        }

        var episodes = files
            .SelectMany(file => file.Numbers.Episodes)
            .GroupBy(episode => (episode.SeasonNumber, episode.EpisodeNumber))
            .Select(group => group.First())
            .OrderBy(episode => episode.SeasonNumber)
            .ThenBy(episode => episode.EpisodeNumber)
            .ToArray();
        var alternateEpisodes = files
            .SelectMany(file => file.Numbers.AlternateEpisodes)
            .Distinct()
            .ToArray();
        var seasonPacks = episodes
            .GroupBy(episode => episode.SeasonNumber)
            .Select(group => new ImportedSeasonPackItem(
                group.Key,
                Episodes: group.OrderBy(episode => episode.EpisodeNumber).ToArray()))
            .ToArray();
        var numbers = new TvImportNumbers(episodes, alternateEpisodes, seasonPacks);

        var alreadyCommitted = blockReasons.Count == 0 && alreadyPlacedAndOwned;
        if (!alreadyCommitted)
        {
            var ownedExistingPaths = replacements
                .Select(replacement => replacement.ExistingPath)
                .Distinct(GetPathComparer())
                .ToArray();
            foreach (var file in files.Where(file => file.Preview.DestinationExists))
            {
                if (!replacementAuthorized || !ownedExistingPaths.Any(path => IsSamePath(path, file.Preview.DestinationPath)))
                {
                    blockReasons.Add($"{Path.GetFileName(file.Preview.DestinationPath)} already exists but is not the fully committed pack owned by these episodes.");
                }
            }
        }

        var distinctReasons = blockReasons.Distinct(StringComparer.Ordinal).ToArray();
        var commonFolders = files.Select(file => file.Preview.DestinationFolder)
            .Distinct(GetPathComparer())
            .ToArray();
        var commonFolder = commonFolders.Length == 1 ? commonFolders[0] : string.Empty;
        var publicFiles = files.Select(file => new ImportPackFilePreview(
            file.Preview.SourcePath,
            file.Preview.DestinationPath,
            file.Preview.SourceSizeBytes,
            file.Numbers.Episodes
                .Select(episode => EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray(),
            file.Warnings)).ToArray();
        var publicSkippedFiles = skippedFiles
            .Select(file => new ImportPackSkippedFile(file.Preview.SourcePath, file.Reason))
            .ToArray();
        var pack = new ImportPackPreview(
            CanExecute: distinctReasons.Length == 0,
            AlreadyCommitted: alreadyCommitted,
            SourceFileCount: sourceFiles.Count,
            EpisodeCount: episodes.Length,
            Files: publicFiles,
            BlockReasons: distinctReasons,
            SkippedFiles: publicSkippedFiles);
        var first = files.Count > 0 ? files[0].Preview : skippedFiles[0].Preview;
        var skippedNote = skippedFiles.Count > 0
            ? $" {skippedFiles.Count} file(s) do not resolve to a canonical episode and will be skipped: {string.Join(", ", skippedFiles.Select(file => Path.GetFileName(file.Preview.SourcePath)))}."
            : string.Empty;
        var summary = first with
        {
            SourcePath = request.SourcePath,
            DestinationFolder = commonFolder,
            DestinationPath = commonFolder,
            HardlinkAvailable = files.All(file => file.Preview.HardlinkAvailable),
            SourceExists = files.All(file => file.Preview.SourceExists),
            DestinationExists = files.Any(file => file.Preview.DestinationExists),
            SourceSizeBytes = files.Sum(file => file.Preview.SourceSizeBytes),
            DestinationSizeBytes = files.Sum(file => file.Preview.DestinationSizeBytes),
            IsSupportedMediaFile = files.All(file => file.Preview.IsSupportedMediaFile),
            MediaProbe = null,
            TransferExplanation = files.All(file => file.Preview.PreferredTransferMode == "hardlink")
                ? "Every pack file can use a hardlink. Deluno stages all links before committing the episode catalogue."
                : "Deluno will stage every pack file before committing the episode catalogue; copy fallback is used where hardlinks are unavailable.",
            Warnings = distinctReasons.Length == 0
                ? [(alreadyCommitted
                    ? "This exact season pack is already placed and catalogued. Retrying is safe and will not duplicate it."
                    : $"All {files.Count} files resolve to {episodes.Length} canonical episodes and can be committed together.") + skippedNote]
                : distinctReasons,
            Explanation = distinctReasons.Length == 0
                ? "Every file has a unique destination and canonical episode identity. Placement and catalogue updates will be committed as one pack operation."
                : "Deluno will not import any part of this pack until every file has one safe destination and canonical episode mapping.",
            DecisionSteps =
            [
                $"Pack: found {sourceFiles.Count} video file(s) in the folder.",
                $"Identity: resolved {episodes.Length} unique canonical episode(s) across {files.Count} file(s).",
                .. skippedFiles.Count > 0
                    ? [$"Skip:{skippedNote}"]
                    : Array.Empty<string>(),
                distinctReasons.Length == 0
                    ? "Safety: no duplicate episode claims, destination collisions, or unmatched files were found."
                    : $"Safety: blocked by {distinctReasons.Length} issue(s); no file will be placed."
            ],
            Pack = pack
        };
        return new TvPackPlan(
            summary,
            files,
            skippedFiles,
            numbers,
            numbering,
            distinctReasons,
            alreadyCommitted,
            replacements.Distinct().ToArray());
    }

    private async Task<ImportPipelineResult> ExecuteTvPackAsync(
        ImportExecuteRequest request,
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken)
    {
        // A source file that exists but has not settled yet is transient, not a
        // bad file (#652): a processor or download client can still be
        // finishing with it. The whole pack shares one settle window - the
        // same one a single file gets - rather than each file getting its own,
        // so a ten-file pack cannot wait ten times as long. Checked here,
        // before any plan is built or anything staged, so a pack that is not
        // ready yet is never partly imported: nothing has touched disk when
        // this returns. The caller (the job handler) turns this retryable
        // status into a deferred retry that does not spend one of the pack's
        // dead-letter attempts, exactly like a single locked file does.
        var readinessDeadline = DateTimeOffset.UtcNow + _settleWindow;
        foreach (var sourcePath in sourceFiles)
        {
            if (!File.Exists(sourcePath) || ImportFileReadiness.IsReady(sourcePath))
            {
                continue;
            }

            var remaining = readinessDeadline - DateTimeOffset.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }
            if (!await ImportFileReadiness.WaitUntilReadyAsync(sourcePath, remaining, cancellationToken))
            {
                return Failed(
                    ImportFileReadiness.RetryableStatusCode,
                    $"The source file '{Path.GetFileName(sourcePath)}' was still being written, or locked by another process, {_settleWindow.TotalSeconds:0} seconds after Deluno started importing this season pack. Deluno will retry when it is stable.");
            }
        }

        var settings = await platformRepository.GetAsync(cancellationToken);
        var rules = await librariesRepository.ListDestinationRulesAsync(cancellationToken);
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var plan = await BuildTvPackPlanAsync(
            request.Preview,
            sourceFiles,
            settings,
            rules,
            libraries,
            cancellationToken,
            request.ReplacementTargets,
            request.Overwrite);

        if (plan.AlreadyCommitted)
        {
            var response = BuildTvPackResponse(
                plan,
                "already-committed",
                usedFallback: false,
                $"This exact season pack is already placed and catalogued. Nothing was duplicated.{DescribeSkippedFiles(plan.SkippedFiles)}");
            return new ImportPipelineResult(true, StatusCodes.Status200OK, response, response.Message);
        }

        if (plan.BlockReasons.Count > 0)
        {
            var message = $"Season-pack import is blocked: {string.Join(" ", plan.BlockReasons)}";
            await TryRecordPackFailureAsync(
                request,
                "unmatched",
                message,
                "Review the file-to-episode plan. Deluno did not place any part of the pack.",
                cancellationToken);
            return Failed(StatusCodes.Status409Conflict, message);
        }

        if ((request.Overwrite || request.ForceReplacement) && plan.Replacements.Count == 0)
        {
            const string message = "Season-pack replacement requires an exact episode-to-file ownership manifest from the dispatch decision.";
            await TryRecordPackFailureAsync(
                request,
                "replacementRejected",
                message,
                "Search the installed episodes through a reviewed season-pack decision so Deluno can bind every replacement to its current file.",
                cancellationToken);
            return Failed(StatusCodes.Status409Conflict, message);
        }

        // #765: checked once for the pack's destination folder rather than
        // per file -- every file in one season pack lands on the same
        // library root, and a pack can be dozens of episodes.
        if (!settings.SkipFreeSpaceCheck && plan.Files.Count > 0)
        {
            var freeSpaceProblem = DescribeInsufficientFreeSpace(plan.Files[0].Preview.DestinationFolder, settings.MinimumFreeSpaceMb);
            if (freeSpaceProblem is not null)
            {
                await TryRecordPackFailureAsync(
                    request,
                    ImportFailurePolicy.InsufficientFreeSpace,
                    freeSpaceProblem,
                    "Free up space on the destination volume, lower the minimum free space setting, or turn on 'Skip free space check' if this filesystem cannot report free space.",
                    cancellationToken);
                return Failed(StatusCodes.Status507InsufficientStorage, freeSpaceProblem);
            }
        }

        var placements = new List<TvPackPlacement>();
        var backups = new List<TvPackBackup>();
        var catalogCommitted = false;
        try
        {
            await RecordImportStartedAsync(request, plan.Summary, "tv", cancellationToken);

            foreach (var file in plan.Files)
            {
                Directory.CreateDirectory(file.Preview.DestinationFolder);
                var requestedMode = NormalizeTransferMode(request.TransferMode);
                var mode = requestedMode == "auto" ? file.Preview.PreferredTransferMode : requestedMode;
                var stagingPath = BuildTemporaryPath(file.Preview.DestinationPath, ".deluno-pack-stage");
                var placement = new TvPackPlacement(
                    file,
                    stagingPath,
                    mode,
                    UsedFallback: false,
                    SourceMoved: false,
                    Finalized: false);
                var placementIndex = placements.Count;
                placements.Add(placement);

                if (mode == "hardlink")
                {
                    var hardlinkError = "Hardlink creation failed for this season-pack file.";
                    if (!file.Preview.HardlinkAvailable || !TryCreateHardlink(file.Preview.SourcePath, stagingPath, out hardlinkError))
                    {
                        TryDelete(stagingPath);
                        if (!request.AllowCopyFallback)
                        {
                            throw new IOException(file.Preview.HardlinkAvailable
                                ? hardlinkError
                                : "Hardlinking is not available for every file in this season pack.");
                        }
                        AtomicCopy(file.Preview.SourcePath, stagingPath, overwrite: false);
                        placement = placement with { Mode = "copy", UsedFallback = true };
                    }
                }
                else if (mode == "move")
                {
                    File.Move(file.Preview.SourcePath, stagingPath, overwrite: false);
                    placement = placement with { SourceMoved = true };
                }
                else
                {
                    AtomicCopy(file.Preview.SourcePath, stagingPath, overwrite: false);
                    placement = placement with { Mode = "copy" };
                }

                placements[placementIndex] = placement;
                VerifyStagedImport(stagingPath);
            }

            foreach (var existingPath in plan.Replacements
                         .Select(replacement => replacement.ExistingPath)
                         .Distinct(GetPathComparer()))
            {
                if (!File.Exists(existingPath))
                {
                    throw new IOException($"The replacement target '{existingPath}' changed or disappeared after validation.");
                }
                var backupPath = BuildTemporaryPath(existingPath, ".deluno-pack-backup");
                File.Move(existingPath, backupPath, overwrite: false);
                backups.Add(new TvPackBackup(existingPath, backupPath));
            }

            for (var index = 0; index < placements.Count; index++)
            {
                var placement = placements[index];
                var stagedSize = VerifyStagedImport(placement.StagingPath);
                File.Move(placement.StagingPath, placement.File.Preview.DestinationPath, overwrite: false);
                VerifyFinalImport(placement.File.Preview.DestinationPath, stagedSize);
                await ApplyImportPermissionsAsync(placement.File.Preview.DestinationPath, settings, cancellationToken);
                placements[index] = placement with { Finalized = true };
            }

            var catalog = await MarkCatalogImportedAsync(
                request.Preview,
                plan.Files[0].Preview,
                "tv",
                libraries,
                request.DispatchId,
                cancellationToken,
                plan.Numbers,
                plan.Files.Select(file => file.Preview).ToArray());
            if (!catalog.CatalogUpdated)
            {
                throw new InvalidOperationException("Every pack file was staged, but the TV catalogue did not accept the atomic episode manifest.");
            }
            catalogCommitted = true;

            // Each backup is the episode file this pack replaced, moved aside
            // before its slot was reused. The new files are placed and
            // verified and the catalogue transaction has committed, so it is
            // safe to recycle them now instead of deleting them outright -
            // the same rule a single-file upgrade follows (#656).
            var replacementMessages = new List<string>();
            foreach (var backup in backups)
            {
                var fileName = Path.GetFileName(backup.ExistingPath);
                var library = ResolveLibraryForImport(backup.ExistingPath, "tv", libraries);
                if (library is null || recycleBinService is null)
                {
                    TryDelete(backup.BackupPath);
                    if (library is null)
                    {
                        logger.LogWarning(
                            "The replaced file was left at {BackupPath} because no library matched {ExistingPath}.",
                            backup.BackupPath,
                            backup.ExistingPath);
                    }
                    continue;
                }

                if (await recycleBinService.StoreReplacementAsync(
                    library,
                    backup.ExistingPath,
                    backup.BackupPath,
                    cancellationToken) is null && File.Exists(backup.BackupPath))
                {
                    logger.LogWarning(
                        "The replaced file was left at {BackupPath} because it could not be moved into the recycle bin.",
                        backup.BackupPath);
                    continue;
                }

                replacementMessages.Add($"Moved the previous {fileName} file to the recycle bin.");
            }

            // #662: one refresh call per distinct destination folder for the
            // whole pack, not one per episode - the batching the audit calls
            // for, carried by the placed files' own destination folders.
            await EnqueueMediaServerRefreshAsync(
                "tv",
                [
                    .. placements.Select(placement => new MediaServerPathChange(placement.File.Preview.DestinationPath, MediaServerChangeKind.Created)),
                    .. backups.Select(backup => new MediaServerPathChange(backup.ExistingPath, MediaServerChangeKind.Deleted))
                ],
                cancellationToken);

            var usedFallback = placements.Any(placement => placement.UsedFallback);
            var modes = placements.Select(placement => placement.Mode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var modeUsed = modes.Length == 1 ? modes[0] : "mixed";
            var replacementSummary = replacementMessages.Count == 0 ? string.Empty : " " + string.Join(" ", replacementMessages);
            var message = $"Imported {placements.Count} season-pack files covering {plan.Numbers.Episodes.Count} episodes as one catalogue transaction.{DescribeSkippedFiles(plan.SkippedFiles)}{replacementSummary}";
            var response = BuildTvPackResponse(plan, modeUsed, usedFallback, message, placements);
            await RecordTvPackCompletionBestEffortAsync(
                request,
                plan,
                placements,
                catalog,
                libraries,
                modeUsed,
                usedFallback,
                cancellationToken);

            DelunoObservability.ImportCompleted.Add(placements.Count, new("media.type", "tv"), new("transfer.mode", modeUsed));
            logger.LogInformation(
                "Season-pack import completed for {Title}. Files={FileCount} Episodes={EpisodeCount} TransferMode={TransferMode}",
                TitleForActivity(request.Preview),
                placements.Count,
                plan.Numbers.Episodes.Count,
                modeUsed);
            return new ImportPipelineResult(true, StatusCodes.Status200OK, response, response.Message);
        }
        catch (OperationCanceledException) when (!catalogCommitted)
        {
            RollBackTvPackPlacements(placements);
            RestoreTvPackBackups(backups);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || catalogCommitted)
        {
            IReadOnlyList<string> rollbackFailures = [];
            if (!catalogCommitted)
            {
                rollbackFailures = RollBackTvPackPlacements(placements);
                rollbackFailures = [.. rollbackFailures, .. RestoreTvPackBackups(backups)];
            }
            var message = catalogCommitted
                ? $"The season pack was imported, but a follow-up record failed: {exception.Message}"
                : rollbackFailures.Count == 0
                    ? $"Season-pack import was rolled back: {exception.Message}"
                    : $"Season-pack import failed and needs manual recovery: {exception.Message} Rollback could not complete: {string.Join(" ", rollbackFailures)}";
            if (!catalogCommitted)
            {
                await TryRecordPackFailureAsync(
                    request,
                    exception is UnauthorizedAccessException ? "permission" : exception is IOException ? "io" : "importFailed",
                    message,
                    rollbackFailures.Count == 0
                        ? "The source files were retained. Review the recovery case and retry the complete pack."
                        : "Do not retry yet. Restore the named files from their staging or destination paths, then review the complete pack.",
                    CancellationToken.None);
                return Failed(exception is UnauthorizedAccessException
                    ? StatusCodes.Status403Forbidden
                    : exception is IOException
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status500InternalServerError, message);
            }

            logger.LogWarning(exception, "Season-pack follow-up recording failed after the catalogue transaction committed.");
            var response = BuildTvPackResponse(plan, "committed", usedFallback: false, message + DescribeSkippedFiles(plan.SkippedFiles));
            return new ImportPipelineResult(true, StatusCodes.Status200OK, response, response.Message);
        }
    }

    private static ImportExecuteResponse BuildTvPackResponse(
        TvPackPlan plan,
        string transferMode,
        bool usedFallback,
        string message,
        IReadOnlyList<TvPackPlacement>? placements = null)
        => new(
            Preview: plan.Summary,
            Executed: true,
            TransferModeUsed: transferMode,
            UsedFallback: usedFallback,
            CatalogUpdated: true,
            Message: message,
            PackFiles: plan.Files.Select(file =>
            {
                var placement = placements?.FirstOrDefault(item => IsSamePath(
                    item.File.Preview.SourcePath,
                    file.Preview.SourcePath));
                return new ImportPackFileResult(
                    file.Preview.SourcePath,
                    file.Preview.DestinationPath,
                    file.Numbers.Episodes
                    .Select(episode => EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray(),
                    placement?.Mode ?? transferMode);
            }).ToArray(),
            SkippedFiles: plan.SkippedFiles
                .Select(file => new ImportPackSkippedFile(file.Preview.SourcePath, file.Reason))
                .ToArray());

    /// <summary>Plain words for what did not go in, appended to a result message (#652).</summary>
    private static string DescribeSkippedFiles(IReadOnlyList<TvPackSkippedFilePlan> skippedFiles)
        => skippedFiles.Count == 0
            ? string.Empty
            : $" Skipped {skippedFiles.Count} file(s) that do not resolve to a canonical episode: {string.Join(", ", skippedFiles.Select(file => Path.GetFileName(file.Preview.SourcePath)))}.";

    private async Task TryRecordPackFailureAsync(
        ImportExecuteRequest request,
        string kind,
        string summary,
        string action,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecordImportFailureAsync(request, request.Preview, kind, summary, action, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not record the season-pack recovery case after {FailureKind}.", kind);
        }
    }

    private async Task RecordTvPackCompletionBestEffortAsync(
        ImportExecuteRequest request,
        TvPackPlan plan,
        IReadOnlyList<TvPackPlacement> placements,
        CatalogImportResult catalog,
        IReadOnlyList<LibraryItem> libraries,
        string modeUsed,
        bool usedFallback,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(request.DispatchId))
            {
                await platformRepository.MarkWorkflowVerifiedAsync(cancellationToken);
                if (importResolutionsRepository is not null && catalog.CatalogId is not null)
                {
                    await importResolutionsRepository.RecordSuccessAsync(
                        request.DispatchId,
                        "tv",
                        catalog.CatalogId,
                        "series",
                        cancellationToken);
                }
                if (downloadDispatchesRepository is not null)
                {
                    await downloadDispatchesRepository.RecordImportOutcomeAsync(
                        request.DispatchId,
                        "imported",
                        plan.Summary.DestinationFolder,
                        null,
                        null,
                        cancellationToken);
                }
                if (realtimeEventPublisher is not null)
                {
                    await realtimeEventPublisher.PublishDispatchImportCompletedAsync(
                        request.DispatchId,
                        TitleForActivity(request.Preview),
                        succeeded: true,
                        importedPath: plan.Summary.DestinationFolder,
                        failureReason: null,
                        cancellationToken);
                }
            }

            // #892: a copy import's source is checked and cleaned up once the
            // pack is committed, the same as a single file - IsTvPackAlreadyCommittedAsync
            // no longer needs the source to still be there to recognise a
            // retry of this same pack, so packs get the same cleanup a single
            // file already did rather than being left out of it.
            var sharedByClient = await IsStillSharedByClientAsync(request.DispatchId, cancellationToken);
            var cleanup = placements.Select(placement => ApplyWorkflowCleanup(
                placement.File.Preview.SourcePath,
                ResolveLibraryForImport(placement.File.Preview.DestinationPath, "tv", libraries),
                sharedByClient,
                modeUsed)).ToArray();
            var matchedLibrary = ResolveLibraryForImport(plan.Summary.DestinationPath, "tv", libraries);
            var provenance = BuildImportProvenance(
                request,
                request.Preview.SourcePath,
                plan.Summary.DestinationFolder,
                plan.Summary.PreferredTransferMode,
                modeUsed,
                usedFallback,
                catalog,
                matchedLibrary,
                string.Join(" ", cleanup.Select(item => item.Summary)),
                plan.Summary.MatchedRuleId,
                plan.Summary.MatchedRuleName,
                plan.Summary.MediaProbe);
            if (downloadDispatchesRepository is not null && !string.IsNullOrWhiteSpace(request.DispatchId))
            {
                await downloadDispatchesRepository.RecordTimelineEventAsync(
                    request.DispatchId,
                    "file_provenance_recorded",
                    JsonSerializer.Serialize(provenance),
                    cancellationToken);
            }
            await activityFeedRepository.RecordActivityAsync(
                "filesystem.import.season-pack.completed",
                $"{TitleForActivity(request.Preview)} season pack imported {placements.Count} files covering {plan.Numbers.Episodes.Count} episodes.{DescribeSkippedFiles(plan.SkippedFiles)}",
                JsonSerializer.Serialize(new
                {
                    Files = placements.Select(placement => new
                    {
                        placement.File.Preview.SourcePath,
                        placement.File.Preview.DestinationPath,
                        placement.Mode,
                        placement.UsedFallback,
                        Episodes = placement.File.Numbers.Episodes.Select(episode => EpisodeKey(episode.SeasonNumber, episode.EpisodeNumber))
                    }),
                    Skipped = plan.SkippedFiles.Select(file => new
                    {
                        file.Preview.SourcePath,
                        file.Reason
                    }),
                    Cleanup = cleanup.Select(item => item.Summary),
                    Provenance = provenance
                }),
                null,
                "series",
                request.Preview.SeriesId,
                cancellationToken);
            await activityFeedRepository.RecordDecisionAsync(
                new DecisionExplanationPayload(
                    Scope: "filesystem.import.season-pack",
                    Status: "completed",
                    Reason: "Every file had a unique canonical episode mapping and destination before Deluno placed any part of the pack.",
                    Inputs: new Dictionary<string, string?>
                    {
                        ["sourcePath"] = request.Preview.SourcePath,
                        ["fileCount"] = placements.Count.ToString(),
                        ["episodeCount"] = plan.Numbers.Episodes.Count.ToString(),
                        ["transferModeUsed"] = modeUsed,
                        ["usedFallback"] = usedFallback.ToString(),
                        ["dispatchId"] = provenance.DispatchId,
                        ["releaseNameEvidence"] = provenance.ReleaseNameEvidence,
                        ["indexer"] = provenance.IndexerName,
                        ["downloadClient"] = provenance.DownloadClientName,
                        ["preferencePlan"] = provenance.PreferencePlanId,
                        ["preferencePlanHash"] = provenance.PreferencePlanHash,
                        ["matchedCustomRules"] = provenance.MatchedRuleNames
                    },
                    Outcome: $"All {placements.Count} files were placed before one transaction marked {plan.Numbers.Episodes.Count} episodes imported.",
                    Alternatives:
                    [
                        new DecisionAlternativeExplanation(
                            "Recovery review",
                            "not-needed",
                            "If any file or episode is ambiguous, Deluno leaves the whole download in recovery review.")
                    ]),
                null,
                "series",
                request.Preview.SeriesId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Season-pack completion side effects failed after files and catalogue committed.");
        }
    }

    private IReadOnlyList<string> RollBackTvPackPlacements(IReadOnlyList<TvPackPlacement> placements)
    {
        var failures = new List<string>();
        foreach (var placement in placements.Reverse())
        {
            try
            {
                if (placement.SourceMoved && !File.Exists(placement.File.Preview.SourcePath))
                {
                    if (File.Exists(placement.File.Preview.DestinationPath))
                    {
                        File.Move(placement.File.Preview.DestinationPath, placement.File.Preview.SourcePath, overwrite: false);
                    }
                    else if (File.Exists(placement.StagingPath))
                    {
                        File.Move(placement.StagingPath, placement.File.Preview.SourcePath, overwrite: false);
                    }
                }
                else
                {
                    if (File.Exists(placement.File.Preview.DestinationPath))
                    {
                        File.Delete(placement.File.Preview.DestinationPath);
                    }
                    if (File.Exists(placement.StagingPath))
                    {
                        File.Delete(placement.StagingPath);
                    }
                }

                if (placement.SourceMoved && !File.Exists(placement.File.Preview.SourcePath))
                {
                    throw new IOException("The moved source could not be restored.");
                }
                if (!placement.SourceMoved &&
                    (File.Exists(placement.File.Preview.DestinationPath) || File.Exists(placement.StagingPath)))
                {
                    throw new IOException("A staged or destination file could not be removed.");
                }
            }
            catch (Exception exception)
            {
                var failure = $"{Path.GetFileName(placement.File.Preview.SourcePath)}: {exception.Message}";
                failures.Add(failure);
                logger.LogError(
                    exception,
                    "Season-pack rollback could not restore {SourcePath} from {DestinationPath} or {StagingPath}.",
                    placement.File.Preview.SourcePath,
                    placement.File.Preview.DestinationPath,
                    placement.StagingPath);
            }
        }
        return failures;
    }

    private IReadOnlyList<string> RestoreTvPackBackups(IReadOnlyList<TvPackBackup> backups)
    {
        var failures = new List<string>();
        foreach (var backup in backups.AsEnumerable().Reverse())
        {
            try
            {
                if (File.Exists(backup.ExistingPath))
                {
                    File.Delete(backup.ExistingPath);
                }
                if (File.Exists(backup.BackupPath))
                {
                    File.Move(backup.BackupPath, backup.ExistingPath, overwrite: false);
                }
                if (!File.Exists(backup.ExistingPath))
                {
                    throw new IOException("The original replacement target could not be restored.");
                }
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(backup.ExistingPath)}: {exception.Message}");
                logger.LogError(exception, "Season-pack rollback could not restore replacement target {ExistingPath}.", backup.ExistingPath);
            }
        }
        return failures;
    }

    private async Task<bool> IsTvPackAlreadyCommittedAsync(
        ImportPreviewRequest request,
        IReadOnlyList<TvPackFilePlan> files,
        SeriesNumberingDetail? numbering,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SeriesId) || numbering is null ||
            files.Any(file => !File.Exists(file.Preview.DestinationPath) ||
                              new FileInfo(file.Preview.DestinationPath).Length != file.Preview.SourceSizeBytes))
        {
            return false;
        }

        var episodeIds = numbering.Episodes.ToDictionary(
            episode => (episode.SeasonNumber, episode.EpisodeNumber),
            episode => episode.EpisodeId);
        foreach (var file in files)
        {
            // #892: a copy import's own source is cleaned up (checked, then
            // deleted) once the pack is committed, same as a single file. A
            // retry of an already-committed pack most often finds that source
            // already gone - which is not evidence of anything wrong, so it
            // is not re-hashed against a destination it can no longer reach.
            // The destination's already-verified size, plus the catalogue
            // still tracking this exact path below, is what "already
            // committed" is resting on for that file instead.
            if (File.Exists(file.Preview.SourcePath))
            {
                await using var source = File.OpenRead(file.Preview.SourcePath);
                await using var destination = File.OpenRead(file.Preview.DestinationPath);
                var sourceHash = await SHA256.HashDataAsync(source, cancellationToken);
                var destinationHash = await SHA256.HashDataAsync(destination, cancellationToken);
                if (!sourceHash.AsSpan().SequenceEqual(destinationHash))
                {
                    return false;
                }
            }

            foreach (var episode in file.Numbers.Episodes)
            {
                if (!episodeIds.TryGetValue((episode.SeasonNumber, episode.EpisodeNumber), out var episodeId))
                {
                    return false;
                }
                var trackedPath = await seriesCatalogRepository.GetEpisodeFilePathAsync(episodeId, cancellationToken);
                if (string.IsNullOrWhiteSpace(trackedPath) || !IsSamePath(trackedPath, file.Preview.DestinationPath))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static string EpisodeKey(int seasonNumber, int episodeNumber)
        => $"S{seasonNumber:D2}E{episodeNumber:D2}";

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task<ImportPreviewResponse> EnrichPreviewWithMediaProbeAsync(
        ImportPreviewResponse preview,
        CancellationToken cancellationToken)
    {
        if (!preview.SourceExists || !preview.IsSupportedMediaFile)
        {
            return preview;
        }

        var probe = await mediaProbeService.ProbeAsync(preview.SourcePath, cancellationToken);
        var warnings = preview.Warnings.ToList();
        var decisionSteps = preview.DecisionSteps.ToList();

        if (probe.Status == "succeeded")
        {
            decisionSteps.Add(BuildProbeDecisionStep(probe));
            if (probe.VideoStreams.Count == 0)
            {
                warnings.Add("ffprobe did not find a video stream in this file.");
            }

            if (probe.DurationSeconds is > 0 and < 120)
            {
                warnings.Add("Detected runtime is under two minutes. This is likely a sample.");
            }
        }
        else if (probe.Status == "unavailable")
        {
            warnings.Add(probe.Message ?? "ffprobe is unavailable, so Deluno cannot validate streams before import.");
            decisionSteps.Add("Probe: ffprobe is unavailable, so stream validation was skipped.");
        }
        else
        {
            warnings.Add(probe.Message ?? "ffprobe could not parse this file.");
            decisionSteps.Add("Probe: ffprobe failed to parse the file, so import should be blocked until the file is verified.");
        }

        return preview with
        {
            MediaProbe = probe,
            Warnings = warnings,
            DecisionSteps = decisionSteps
        };
    }

    private static string BuildProbeDecisionStep(MediaProbeInfo probe)
    {
        var duration = probe.DurationSeconds is > 0
            ? TimeSpan.FromSeconds(probe.DurationSeconds.Value).ToString(@"hh\:mm\:ss")
            : "unknown runtime";
        var video = probe.VideoStreams.FirstOrDefault();
        var videoSummary = video is null
            ? "no video stream"
            : $"{video.Codec ?? "unknown codec"} {video.Width?.ToString() ?? "?"}x{video.Height?.ToString() ?? "?"}";
        return $"Probe: ffprobe detected {duration}, {videoSummary}, {probe.AudioStreams.Count} audio stream(s), and {probe.SubtitleStreams.Count} subtitle stream(s).";
    }

    /// <summary>
    /// What a processor gave back, measured against what it was given, or null
    /// when there is nothing to complain about.
    ///
    /// <para>Only losses that cannot be intended are refused. A processor may
    /// legitimately transcode a file far smaller, or keep one audio track out
    /// of five, so neither size nor a reduced track count is evidence of
    /// anything. A file that is materially shorter than the one that went in
    /// is: that is a remux that stopped early, and it is the failure that
    /// otherwise reaches the library looking perfectly valid (#574).</para>
    /// </summary>
    private async Task<string?> WhatTheProcessorLostAsync(
        ImportExecuteRequest request,
        ImportPreviewResponse preview,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProcessedFromPath)) return null;

        var source = ResolveProcessedSourceFile(request.ProcessedFromPath);
        if (source is null) return null;

        var before = await mediaProbeService.ProbeAsync(source, cancellationToken);
        var after = preview.MediaProbe;
        // Nothing to compare the original with is not evidence of a problem.
        // It is recorded on the import's decision trail either way, so an
        // install that cannot probe can still see that nothing was checked.
        if (before.Status != "succeeded") return null;

        // Checkpoint A already showed the original opens and plays; a
        // processed copy that does not is the processor's fault, not the
        // release's (#919).
        if (after?.Status == "failed")
        {
            return after.Message is { Length: > 0 } message
                ? $"The processed file will not open or play: {message}"
                : "The processed file will not open or play.";
        }

        if (after?.Status != "succeeded") return null;

        if (before.DurationSeconds is > 0 and var wasSeconds &&
            after.DurationSeconds is > 0 and var nowSeconds &&
            nowSeconds < wasSeconds * 0.9)
        {
            return $"The processed file runs {Describe(nowSeconds)}, and the download it was made from runs {Describe(wasSeconds)}. Deluno did not import it.";
        }

        if (before.AudioStreams.Count > 0 && after.AudioStreams.Count == 0)
        {
            return "The processed file has no audio, and the download it was made from does. Deluno did not import it.";
        }

        if (before.VideoStreams.Count > 0 && after.VideoStreams.Count == 0)
        {
            return "The processed file has no video, and the download it was made from does. Deluno did not import it.";
        }

        // "Lost every track in a wanted language" (#919, once #769's language
        // preference exists). Only a library whose profile *requires* a
        // language, rather than merely ranking by it, can fail a processed
        // file over this -- a ranking-only preference never refuses anything.
        if (before.AudioStreams.Count > 0 &&
            after.AudioStreams.Count > 0 &&
            !string.IsNullOrWhiteSpace(request.Preview.LibraryId))
        {
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(candidate => string.Equals(candidate.Id, request.Preview.LibraryId, StringComparison.OrdinalIgnoreCase));
            var preference = library is null
                ? null
                : (await ResolvePreferenceProfileAsync(library, NormalizeMediaType(request.Preview.MediaType) ?? library.MediaType, cancellationToken))?.LanguagePreference;

            if (preference is { RequireLanguage: true, PreferredLanguages.Count: > 0 })
            {
                var wanted = preference.PreferredLanguages.Where(AudioLanguageMatch.Recognizes).ToArray();
                var hadWantedLanguage = wanted.Any(code => AudioLanguageMatch.AnyMatches(before.AudioStreams.Select(stream => stream.Language), code));
                var stillHasWantedLanguage = wanted.Any(code => AudioLanguageMatch.AnyMatches(after.AudioStreams.Select(stream => stream.Language), code));
                if (hadWantedLanguage && !stillHasWantedLanguage)
                {
                    var wantedLabel = string.Join(" or ", wanted.Select(ProfileLanguageCodes.Label));
                    return $"The processed file has no audio track in {wantedLabel}, and the download it was made from does. Deluno did not import it.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The processed copy is the processor's own work, and it is broken; the
    /// download it was made from is kept regardless, because Checkpoint A
    /// already showed that one is fine (#919). Best-effort: a delete that
    /// fails is logged, not thrown, and does not stop the fault being held
    /// and shown.
    /// </summary>
    private void DeleteBrokenProcessedCopy(string processedFilePath)
    {
        try
        {
            if (File.Exists(processedFilePath))
            {
                File.Delete(processedFilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the broken processed copy at {Path}.", processedFilePath);
        }
    }

    /// <summary>
    /// The file a processor was handed. A download is usually a folder, so the
    /// biggest video file inside it is the one that was the media.
    /// </summary>
    private static string? ResolveProcessedSourceFile(string processedFrom)
    {
        try
        {
            if (File.Exists(processedFrom)) return processedFrom;
            if (!Directory.Exists(processedFrom)) return null;

            return Directory.EnumerateFiles(processedFrom, "*.*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4" or ".avi" or ".mov" or ".m4v" or ".ts")
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Describe(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m {span.Seconds}s";
    }

    private async Task<string?> ValidateReplacementAsync(
        ImportExecuteRequest request,
        ImportPreviewResponse preview,
        CancellationToken cancellationToken)
    {
        // Force was authorised against the release Deluno chose to grab, before
        // a processor touched it. It does not carry over to whatever the
        // processor handed back, which nobody has looked at (#574).
        var forced = request.ForceReplacement && string.IsNullOrWhiteSpace(request.ProcessedFromPath);
        if (!request.Overwrite || forced || !File.Exists(preview.DestinationPath))
        {
            return null;
        }

        var incomingProbe = preview.MediaProbe;
        if (incomingProbe?.Status != "succeeded")
        {
            return "Deluno will not replace an existing file until the incoming file is successfully probed.";
        }

        var existingProbe = await mediaProbeService.ProbeAsync(preview.DestinationPath, cancellationToken);
        if (existingProbe.Status != "succeeded")
        {
            return null;
        }

        var incomingVideo = incomingProbe.VideoStreams.FirstOrDefault();
        var existingVideo = existingProbe.VideoStreams.FirstOrDefault();
        if (incomingVideo is null || existingVideo is null)
        {
            return "Deluno will not replace an existing file when either file is missing a video stream.";
        }

        if ((incomingVideo.Width ?? 0) < (existingVideo.Width ?? 0) ||
            (incomingVideo.Height ?? 0) < (existingVideo.Height ?? 0))
        {
            return $"Replacement blocked: incoming video is {incomingVideo.Width ?? 0}x{incomingVideo.Height ?? 0}, existing video is {existingVideo.Width ?? 0}x{existingVideo.Height ?? 0}.";
        }

        if (incomingProbe.DurationSeconds is > 0 &&
            existingProbe.DurationSeconds is > 0 &&
            incomingProbe.DurationSeconds < existingProbe.DurationSeconds * 0.92)
        {
            return "Replacement blocked: incoming runtime is significantly shorter than the existing file.";
        }

        if (incomingProbe.Bitrate is > 0 &&
            existingProbe.Bitrate is > 0 &&
            incomingProbe.Bitrate < existingProbe.Bitrate * 0.65)
        {
            return "Replacement blocked: incoming bitrate is substantially lower than the existing file.";
        }

        return null;
    }

    /// <summary>
    /// Download clients report a folder, not a file, for multi-file torrents.
    /// Resolve the media file inside so the rest of the pipeline always works
    /// on a file path. The largest non-sample video wins; the caller's
    /// FileName keeps its base but follows the resolved file's extension.
    /// </summary>
    private static ImportPreviewRequest ResolveDirectorySource(ImportPreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath) || !Directory.Exists(request.SourcePath))
        {
            return request;
        }

        string? resolved;
        try
        {
            resolved = Directory
                .EnumerateFiles(request.SourcePath, "*.*", SearchOption.AllDirectories)
                .Where(path => SupportedVideoExtensions.Extensions.Contains(Path.GetExtension(path)))
                .Where(path => !SampleTokenPattern().IsMatch(Path.GetFileNameWithoutExtension(path)))
                .OrderByDescending(path => new FileInfo(path).Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return request;
        }

        if (resolved is null)
        {
            return request;
        }

        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? null
            : Path.ChangeExtension(request.FileName.Trim(), Path.GetExtension(resolved));
        return request with { SourcePath = resolved, FileName = fileName };
    }

    /// <summary>
    /// The videos in a folder an import would take: supported extensions, no
    /// samples. One definition, so a processor's hand-back folder is read the way
    /// a download folder is (#619).
    /// </summary>
    public static IReadOnlyList<string> ListImportableVideoFiles(string folder) => TryListImportableVideoFiles(folder) ?? [];

    private static IReadOnlyList<string>? TryListImportableVideoFiles(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Directory.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                .Where(path => SupportedVideoExtensions.Extensions.Contains(Path.GetExtension(path)))
                .Where(path => !SampleTokenPattern().IsMatch(Path.GetFileNameWithoutExtension(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The root of the library that owns this media type, or null when no
    /// library of that type has one. First match wins; a library without a root
    /// is skipped rather than treated as an empty answer.
    /// </summary>
    private static LibraryItem? ResolveLibrary(IReadOnlyList<LibraryItem> libraries, string mediaType, string? libraryId = null)
    {
        var usable = libraries
            .Where(library => NormalizeMediaType(library.MediaType) == mediaType && !string.IsNullOrWhiteSpace(library.RootPath))
            .ToArray();
        return (libraryId is null ? null : usable.FirstOrDefault(library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase)))
               ?? usable.FirstOrDefault();
    }

    private async Task<ImportPreviewResponse> ResolveImportPreviewAsync(
        ImportPreviewRequest request,
        PlatformSettingsSnapshot settings,
        IReadOnlyList<DestinationRuleItem> rules,
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken,
        NamingDecision? decision = null)
    {
        // A preview asked for on its own has no dispatch to read, so the
        // incoming name is all there is; an import resolves the decision first
        // and passes it in, and both then name the file the same way.
        decision ??= await ResolveNamingDecisionAsync(request, libraries, releaseName: null, cancellationToken);
        // #764: the release folder a download client (or a processor) handed
        // Deluno, captured before ResolveDirectorySource resolves the source
        // down to the video file inside it. This is the only "existing
        // folder name" Deluno has -- there is no persisted movie/series
        // folder path to preserve -- so it is what RenameFoldersOnImport off
        // falls back to, the same way RenameOnImport off falls back to the
        // incoming file name.
        var incomingFolderName = string.IsNullOrWhiteSpace(request.SourcePath) || !Directory.Exists(request.SourcePath)
            ? null
            : Path.GetFileName(request.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        request = ResolveDirectorySource(request);
        var mediaType = NormalizeMediaType(request.MediaType);
        var seriesNumbering = mediaType == "tv"
            ? await ResolveSeriesNumberingAsync(request, cancellationToken)
            : null;
        var title = TitleForActivity(request);
        var rule = rules
            .Where(item => item.IsEnabled && NormalizeMediaType(item.MediaType) == mediaType)
            .OrderBy(item => item.Priority)
            .FirstOrDefault(item => MatchesRule(item, request));
        // Root precedence, most specific first. The library that owns the media
        // sits above the platform default deliberately: a destination rule is an
        // explicit override, a library's own root is where the user pointed that
        // library in Media Management, and the platform setting is only a
        // fallback for an install that has no library to speak of. Two movie
        // libraries with different roots must not both land on one global path.
        //
        // Before this the library was never consulted at all, so an install with
        // libraries configured and no platform default fell through to
        // string.Empty and imported to a *relative* path — which .NET resolves
        // against the host's working directory. Media landed wherever the
        // service happened to be started from while Deluno reported success.
        //
        // And the library is the one the release was grabbed for, when the
        // request says so (#526). "The first library of this media type" put a
        // film grabbed for a second movie library into the first one.
        var library = ResolveLibrary(libraries, mediaType, request.LibraryId);
        var rootPath = rule?.RootPath ??
                       library?.RootPath ??
                       (mediaType == "tv" ? settings.SeriesRootPath : settings.MovieRootPath) ??
                       string.Empty;
        var template = rule?.FolderTemplate ??
                       (mediaType == "tv" ? settings.SeriesFolderFormat : settings.MovieFolderFormat);
        var templatedFolder = ApplyTemplate(
            template,
            title,
            request.Year,
            request.ImdbId,
            request.TvDbId,
            request.QualityProfile ?? library?.QualityProfileName,
            request.Genres?.FirstOrDefault(),
            request.Tags?.FirstOrDefault(),
            request.Network,
            settings.ReplaceIllegalCharacters,
            settings.ColonReplacementMode);
        // #764: independent of RenameOnImport, which only ever governs the
        // file name below. Off keeps the release folder Deluno was handed
        // (when there was one to keep); on, or when the source was a single
        // file with no folder of its own, the templated movie/series name is
        // still the only name there is.
        var folder = !settings.RenameFoldersOnImport && incomingFolderName is not null
            ? SanitizeFileName(incomingFolderName, settings.ReplaceIllegalCharacters, settings.ColonReplacementMode)
            : templatedFolder;
        var resolvedName = ResolveDestinationFileName(request, settings, mediaType, title, seriesNumbering, decision);
        // #762: a season (or specials, for season 0) subfolder under the
        // series folder, only for TV and only when the matching format is
        // configured. Resolved after the file name so the subfolder uses the
        // exact same season this import already matched against the
        // catalogue, rather than re-parsing the incoming name a second time.
        var seasonSubfolder = mediaType == "tv" ? BuildSeasonSubfolder(settings, resolvedName.SeasonNumber, seriesNumbering?.SeasonFolder) : null;
        var destinationFolder = string.IsNullOrWhiteSpace(rootPath)
            ? (seasonSubfolder is null ? folder : Path.Combine(folder, seasonSubfolder))
            : (seasonSubfolder is null ? Path.Combine(rootPath, folder) : Path.Combine(rootPath, folder, seasonSubfolder));
        var fileName = resolvedName.FileName;
        var destinationPath = Path.Combine(destinationFolder, SanitizeFileName(fileName, settings.ReplaceIllegalCharacters, settings.ColonReplacementMode));
        var canHardlink = CanLikelyHardlink(request.SourcePath, destinationPath);
        var sourceExists = File.Exists(request.SourcePath);
        var destinationExists = File.Exists(destinationPath);
        var sourceSize = sourceExists ? new FileInfo(request.SourcePath).Length : 0;
        var destinationSize = destinationExists ? new FileInfo(destinationPath).Length : 0;
        var isSupportedMediaFile = SupportedVideoExtensions.Extensions.Contains(Path.GetExtension(destinationPath));
        var warnings = BuildImportWarnings(request.SourcePath, destinationPath, sourceExists, destinationExists, canHardlink, isSupportedMediaFile).ToList();
        if (!string.IsNullOrWhiteSpace(resolvedName.Warning))
        {
            warnings.Add(resolvedName.Warning);
        }
        var preferredMode = settings.UseHardlinks && canHardlink ? "hardlink" : "copy";
        var explanation = rule is null
            ? "No destination rule matched, so Deluno used the default root folder."
            : $"Matched {rule.MatchKind} destination rule '{rule.Name}'.";

        return new ImportPreviewResponse(
            SourcePath: request.SourcePath,
            DestinationFolder: destinationFolder,
            DestinationPath: destinationPath,
            PreferredTransferMode: preferredMode,
            HardlinkAvailable: canHardlink,
            MatchedRuleId: rule?.Id,
            MatchedRuleName: rule?.Name,
            SourceExists: sourceExists,
            DestinationExists: destinationExists,
            SourceSizeBytes: sourceSize,
            DestinationSizeBytes: destinationSize,
            IsSupportedMediaFile: isSupportedMediaFile,
            MediaProbe: null,
            TransferExplanation: BuildTransferExplanation(preferredMode, canHardlink, settings.UseHardlinks),
            Warnings: warnings,
            Explanation: explanation,
            DecisionSteps: BuildImportDecisionSteps(rule, rootPath, template, folder, preferredMode, sourceExists, destinationExists, isSupportedMediaFile, canHardlink, warnings));
    }

    private static IReadOnlyList<string> BuildImportDecisionSteps(
        DestinationRuleItem? rule,
        string rootPath,
        string template,
        string folder,
        string preferredMode,
        bool sourceExists,
        bool destinationExists,
        bool isSupportedMediaFile,
        bool canHardlink,
        IReadOnlyList<string> warnings)
    {
        var steps = new List<string>
        {
            rule is null
                ? $"Root: using the default library root '{rootPath}'."
                : $"Root: matched rule '{rule.Name}' and selected '{rootPath}'.",
            $"Folder: applied '{template}' and resolved '{folder}'.",
            sourceExists
                ? "Source: file is visible from the Deluno server process."
                : "Source: file is not visible from the Deluno server process.",
            destinationExists
                ? "Destination: target file already exists and needs overwrite approval."
                : "Destination: target path is clear.",
            isSupportedMediaFile
                ? "File type: extension is configured as an importable video file."
                : "File type: extension is not currently importable.",
            preferredMode == "hardlink"
                ? "Transfer: hardlink is preferred and appears available for these paths."
                : canHardlink
                    ? "Transfer: copy is preferred by settings even though hardlink appears possible."
                    : "Transfer: copy is preferred because hardlink does not appear available."
        };

        foreach (var warning in warnings)
        {
            steps.Add($"Attention: {warning}");
        }

        return steps;
    }

    /// <summary>
    /// Refuses the release, if the failure was the release's fault.
    ///
    /// <para>Every import failure passes through <see cref="RecordImportFailureAsync"/>,
    /// so this is the one place the decision is made — and the decision itself
    /// lives in <see cref="ImportFailurePolicy"/> rather than here, so it can be
    /// read and asserted without an import running.</para>
    ///
    /// <para><b>It does not touch the download client.</b> Removing a client's
    /// copy belongs to the sharing rule, which knows how long the site the
    /// release came from expects you to keep seeding, and which this pipeline
    /// already defers to elsewhere. This records what was decided; the worker
    /// acts on it when the rule allows. DESIGN-007 decision 17.</para>
    /// </summary>
    private async Task BlockTheReleaseIfTheTableSaysSoAsync(
        ImportExecuteRequest executeRequest,
        string failureKind,
        string summary,
        CancellationToken cancellationToken)
    {
        if (blockedReleases is null ||
            downloadDispatchesRepository is null ||
            string.IsNullOrEmpty(executeRequest.DispatchId))
        {
            return;
        }

        try
        {
            // The user's answers first, because they can turn a "never" into a
            // "refuse immediately" and the shipped table cannot be trusted to
            // rule this out on its own any more.
            var overrides = failureRules is null
                ? new Dictionary<string, BlockDecision>()
                : new Dictionary<string, BlockDecision>(await failureRules.GetOverridesAsync(cancellationToken));

            // #888: "unmatched" has no shipped answer in the table at all
            // (Deluno cannot say whose fault an unidentified file is), so
            // James's dedicated setting fills it in instead of the generic
            // per-reason table -- unless the user has already set an explicit
            // override for it there, which always wins.
            if (failureKind == ImportFailurePolicy.Unmatched && !overrides.ContainsKey(failureKind))
            {
                var unmatchedPolicy = (await platformRepository.GetAsync(cancellationToken)).UnmatchedImportPolicy;
                if (unmatchedPolicy == UnmatchedImportPolicies.RetryOnceThenBad)
                {
                    overrides[failureKind] = BlockDecision.AfterOneRetry;
                }
            }

            var decision = ImportFailurePolicy.BlockFor(failureKind, overrides);
            if (decision == BlockDecision.Never)
            {
                return;
            }

            var dispatch = await downloadDispatchesRepository.GetDispatchAsync(executeRequest.DispatchId, cancellationToken);
            if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.ReleaseName))
            {
                return;
            }

            // Counts attempts at this same release that have already failed, so
            // "one retry" means one retry rather than one per dispatch record.
            var priorFailures = await CountPriorFailuresAsync(dispatch, cancellationToken);

            // "Ask me" asks on the first failure. Waiting for a retry first
            // would be Deluno deciding how patient the person wants to be,
            // which is the decision they took off it.
            var proposing = decision == BlockDecision.AskMe;
            if (!proposing && !ImportFailurePolicy.ShouldBlock(decision, priorFailures))
            {
                return;
            }

            await blockedReleases.BlockAsync(
                new BlockedRelease(
                    Guid.NewGuid().ToString("n"),
                    BlockedReleaseKeys.For(dispatch.ReleaseName, dispatch.IndexerName),
                    dispatch.ReleaseName,
                    dispatch.IndexerName,
                    dispatch.MediaType,
                    dispatch.EntityId,
                    TitleForActivity(executeRequest.Preview),
                    failureKind,
                    summary,
                    dispatch.TorrentHashOrItemId,
                    dispatch.DownloadClientId,
                    dispatch.DownloadClientName,
                    DateTimeOffset.MinValue,
                    proposing ? BlockedReleaseStates.Proposed : BlockedReleaseStates.Refused),
                cancellationToken);

            // Said out loud, because a refusal nobody is told about is the
            // behaviour this whole feature replaces. A proposal is said out
            // loud for the opposite reason: nothing has happened yet, and
            // nothing will until somebody answers.
            await activityFeedRepository.RecordActivityAsync(
                proposing ? "release.block.proposed" : "release.blocked",
                proposing
                    ? $"{dispatch.ReleaseName} failed and you asked to be consulted. {summary} Nothing has changed — decide on the blocklist."
                    : $"Deluno will not use {dispatch.ReleaseName} again. {summary} Future searches skip it and say so, and you can undo this on the blocklist.",
                null,
                null,
                dispatch.MediaType == "tv" ? "series" : "movie",
                dispatch.EntityId,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed import that also fails to record its own refusal is
            // still a failed import, and the import's own reporting matters
            // more than this does.
            logger.LogWarning(exception, "Could not block the release for dispatch {DispatchId}.", executeRequest.DispatchId);
        }
    }

    private async Task<int> CountPriorFailuresAsync(DownloadDispatchItem dispatch, CancellationToken cancellationToken)
    {
        if (downloadDispatchesRepository is null)
        {
            return 0;
        }

        var recent = await downloadDispatchesRepository.QueryDispatchesAsync(
            new DispatchQueryFilter { MediaType = dispatch.MediaType, EntityId = dispatch.EntityId },
            new DispatchPaginationOptions { PageSize = 25 },
            cancellationToken);

        return recent.Items.Count(item =>
            !string.Equals(item.Id, dispatch.Id, StringComparison.Ordinal) &&
            string.Equals(item.ReleaseName, dispatch.ReleaseName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.ImportStatus, "failed", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RecordImportFailureAsync(
        ImportExecuteRequest executeRequest,
        ImportPreviewRequest request,
        string failureKind,
        string summary,
        string recommendedAction,
        CancellationToken cancellationToken)
    {
        var title = TitleForActivity(request);
        var mediaType = NormalizeMediaType(request.MediaType);
        DelunoObservability.ImportFailed.Add(1, new("media.type", mediaType), new("failure.kind", failureKind));
        await BlockTheReleaseIfTheTableSaysSoAsync(executeRequest, failureKind, summary, cancellationToken);
        logger.LogWarning(
            "Import failed for {MediaType} title {Title}. FailureKind={FailureKind} Source={SourcePath} Message={Summary}",
            mediaType,
            title,
            failureKind,
            request.SourcePath,
            summary);

        if (importResolutionsRepository is not null && !string.IsNullOrEmpty(executeRequest.DispatchId))
        {
            var catalogId = $"{title.ToLowerInvariant().Replace(" ", "-")}-{request.Year ?? 0}".Substring(0, Math.Min(64, $"{title.ToLowerInvariant().Replace(" ", "-")}-{request.Year ?? 0}".Length));
            await importResolutionsRepository.RecordFailureAsync(
                executeRequest.DispatchId,
                mediaType,
                catalogId,
                mediaType == "tv" ? "series" : "movie",
                failureKind,
                summary,
                cancellationToken);
        }

        if (downloadDispatchesRepository is not null && !string.IsNullOrEmpty(executeRequest.DispatchId))
        {
            await downloadDispatchesRepository.RecordImportOutcomeAsync(
                executeRequest.DispatchId,
                "failed",
                null,
                failureKind,
                summary,
                cancellationToken);

            // Same reasoning as the success path: a failed import is exactly
            // what the user needs told promptly, not on the next hourly poll.
            if (realtimeEventPublisher is not null)
            {
                await realtimeEventPublisher.PublishDispatchImportCompletedAsync(
                    executeRequest.DispatchId,
                    title,
                    succeeded: false,
                    importedPath: null,
                    failureReason: summary,
                    cancellationToken);
            }
        }

        // The download is marked failed above, and the poller opens a case for
        // every failed download it has not been told about (#602). This case is
        // that telling: the alert beside it is what the poller checks, and
        // dismissing the case settles both.
        if (dispatchAlerts is not null
            && !string.IsNullOrEmpty(executeRequest.DispatchId)
            && !await dispatchAlerts.HasUnresolvedAlertAsync(executeRequest.DispatchId, "import-failed", cancellationToken))
        {
            await dispatchAlerts.CreateAlertAsync(
                executeRequest.DispatchId,
                title,
                $"Import failed: {summary}",
                "import-failed",
                "error",
                new Dictionary<string, string> { ["failureCode"] = failureKind, ["failureMessage"] = summary },
                cancellationToken);
        }

        if (mediaType == "tv")
        {
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(title, failureKind, summary, recommendedAction, SerializeRecoveryDetails(executeRequest)),
                cancellationToken);
        }
        else
        {
            await movieCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(title, failureKind, summary, recommendedAction, SerializeRecoveryDetails(executeRequest)),
                cancellationToken);
        }

        await activityFeedRepository.RecordActivityAsync(
            "filesystem.import.failed",
            $"{title} import failed: {summary}",
            JsonSerializer.Serialize(new
            {
                FailureKind = failureKind,
                Summary = summary,
                RecommendedAction = recommendedAction,
                executeRequest.Preview.SourcePath,
                executeRequest.Preview.FileName,
                executeRequest.Preview.MediaType,
                executeRequest.Preview.Title,
                executeRequest.Preview.Year,
                executeRequest.TransferMode,
                executeRequest.Overwrite,
                executeRequest.AllowCopyFallback,
                executeRequest.ForceReplacement
            }),
            null,
            mediaType == "tv" ? "series" : "movie",
            null,
            cancellationToken);

        await activityFeedRepository.RecordDecisionAsync(
            new DecisionExplanationPayload(
                Scope: "filesystem.import",
                Status: "failed",
                Reason: summary,
                Inputs: new Dictionary<string, string?>
                {
                    ["failureKind"] = failureKind,
                    ["sourcePath"] = executeRequest.Preview.SourcePath,
                    ["fileName"] = executeRequest.Preview.FileName,
                    ["mediaType"] = executeRequest.Preview.MediaType,
                    ["title"] = executeRequest.Preview.Title,
                    ["transferMode"] = executeRequest.TransferMode,
                    ["overwrite"] = executeRequest.Overwrite.ToString(),
                    ["allowCopyFallback"] = executeRequest.AllowCopyFallback.ToString(),
                    ["forceReplacement"] = executeRequest.ForceReplacement.ToString()
                },
                Outcome: recommendedAction,
                Alternatives: []),
            null,
            mediaType == "tv" ? "series" : "movie",
            null,
            cancellationToken);

        // Dispatch outbound webhook notification for critical failures so external
        // monitoring systems (Discord, Slack, custom webhooks) can react promptly.
        await DispatchImportOutcomeNotificationAsync(
            "import.failed",
            $"Import failed: {title}",
            $"{summary} ({failureKind})",
            JsonSerializer.Serialize(new
            {
                title,
                failureKind,
                summary,
                recommendedAction,
                sourcePath = executeRequest.Preview.SourcePath,
                mediaType
            }),
            prefs => prefs.ImportFailedEnabled,
            "failure",
            cancellationToken);

        // Every failure recorded here also opened a recovery case above: it is
        // exactly Radarr/Sonarr's "On Manual Interaction Required" — someone
        // has to look at this before it can finish (#856).
        await DispatchImportOutcomeNotificationAsync(
            "manual_interaction_required",
            $"Needs your attention: {title}",
            $"{summary} ({failureKind}) — {recommendedAction}",
            JsonSerializer.Serialize(new { title, failureKind, recommendedAction, mediaType }),
            prefs => prefs.ManualInteractionRequiredEnabled,
            "manual-interaction-required",
            cancellationToken);
    }

    /// <summary>
    /// Shared by the import success and failure paths: gated by the matching
    /// <see cref="NotificationPreferences"/> toggle when a notification
    /// service is wired up, and never lets a misconfigured webhook fail the
    /// import itself.
    /// </summary>
    private async Task DispatchImportOutcomeNotificationAsync(
        string eventCategory,
        string title,
        string message,
        string detailsJson,
        Func<NotificationPreferences, bool> isEnabled,
        string outcomeLabel,
        CancellationToken cancellationToken)
    {
        if (outboundNotificationService is null)
        {
            return;
        }

        try
        {
            if (notificationService is not null)
            {
                var preferences = await notificationService.GetPreferencesAsync(cancellationToken);
                if (!isEnabled(preferences))
                {
                    return;
                }
            }

            await outboundNotificationService.DispatchAsync(eventCategory, title, message, detailsJson, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbound notification dispatch failed for import {Outcome} — notifications may be misconfigured.", outcomeLabel);
        }
    }

    /// <summary>
    /// A replaced file's own delete/recycle is the same "file delete" concept
    /// Radarr/Sonarr fire "On Movie File Delete For Upgrade" for (#856).
    /// </summary>
    private async Task DispatchFileDeletedNotificationAsync(string fileName, bool forUpgrade, CancellationToken cancellationToken)
        => await DispatchImportOutcomeNotificationAsync(
            "file.deleted",
            $"File removed: {fileName}",
            forUpgrade ? $"{fileName} was removed because an upgrade replaced it." : $"{fileName} was removed.",
            JsonSerializer.Serialize(new { fileName, forUpgrade }),
            prefs => prefs.FileDeletedEnabled,
            "file-deleted",
            cancellationToken);

    private async Task RecordImportStartedAsync(
        ImportExecuteRequest request,
        ImportPreviewResponse preview,
        string mediaType,
        CancellationToken cancellationToken)
    {
        // Attributed to the title, so the file's own journey is readable from
        // the title's History rather than only from the whole-system feed.
        var entityId = request.Preview.SeriesId
            ?? (await ResolveDispatchAsync(request.DispatchId, cancellationToken))?.EntityId;

        await activityFeedRepository.RecordActivityAsync(
            "filesystem.import.started",
            $"{TitleForActivity(request.Preview)} import started.",
            JsonSerializer.Serialize(new
            {
                preview.SourcePath,
                preview.DestinationPath,
                preview.PreferredTransferMode,
                RequestedTransferMode = request.TransferMode,
                request.Overwrite,
                request.AllowCopyFallback,
                request.ForceReplacement,
                preview.MatchedRuleId,
                preview.MatchedRuleName,
                MediaProbe = preview.MediaProbe
            }),
            null,
            mediaType == "tv" ? "series" : "movie",
            entityId,
            cancellationToken);

        await activityFeedRepository.RecordDecisionAsync(
            new DecisionExplanationPayload(
                Scope: "filesystem.import.transfer",
                Status: "started",
                Reason: $"Deluno chose {preview.PreferredTransferMode} during preview and will honor the requested transfer mode {request.TransferMode}.",
                Inputs: new Dictionary<string, string?>
                {
                    ["sourcePath"] = preview.SourcePath,
                    ["destinationPath"] = preview.DestinationPath,
                    ["preferredTransferMode"] = preview.PreferredTransferMode,
                    ["requestedTransferMode"] = request.TransferMode,
                    ["hardlinkAvailable"] = preview.HardlinkAvailable.ToString(),
                    ["matchedRuleId"] = preview.MatchedRuleId,
                    ["matchedRuleName"] = preview.MatchedRuleName
                },
                Outcome: "Import execution started with explicit staging and rollback protection.",
                Alternatives: []),
            null,
            mediaType == "tv" ? "series" : "movie",
            entityId,
            cancellationToken);
    }

    private static string SerializeRecoveryDetails(ImportExecuteRequest request)
        => JsonSerializer.Serialize(new
        {
            RetryRequest = request,
            // At the top as well (#602): dismissing a case settles the download it
            // names by this, and nested it named none, so a dismissed failure came back.
            request.DispatchId,
            request.Preview.SourcePath,
            request.Preview.FileName,
            request.Preview.MediaType,
            request.Preview.Title,
            request.Preview.Year,
            request.TransferMode,
            request.Overwrite,
            request.AllowCopyFallback
        });

    private sealed record ImportProvenanceDetails(
        string? DispatchId,
        string? ReleaseNameEvidence,
        string? IndexerName,
        string? DownloadClientName,
        string? DownloadClientItemId,
        string SourcePath,
        string DestinationPath,
        string Rename,
        string PreferredTransferMode,
        string TransferModeUsed,
        bool UsedFallback,
        bool CatalogUpdated,
        string? DestinationRuleId,
        string? DestinationRuleName,
        string? ImportWorkflow,
        string? ProcessorName,
        string? PreferencePlanId,
        string? PreferencePlanVersion,
        string? PreferencePlanHash,
        string? PreferenceStatus,
        string MatchedRuleIds,
        string MatchedRuleNames,
        string WorkflowCleanup,
        MediaProbeInfo? MediaProbe);

    /// <summary>
    /// Names the release the copy actually came from. Without it the feed says
    /// only that something was imported, and the release that was chosen -
    /// the one fact a person is looking for - is nowhere on the title.
    /// </summary>
    private static string ImportCompletedMessage(
        string title,
        ImportProvenanceDetails provenance,
        string mode)
    {
        var release = provenance.ReleaseNameEvidence;
        if (string.IsNullOrWhiteSpace(release) || release.Contains(Path.DirectorySeparatorChar) || release.Contains('/'))
        {
            // A path is the source file, not a release title; saying "imported
            // from C:\..." tells a person nothing they cannot see already.
            return $"{title} was imported using {mode}.";
        }

        return $"{title} was imported from {release.Trim()} using {mode}.";
    }

    /// <summary>
    /// Queues Plex/Jellyfin/Emby to be told about changed files (#662),
    /// without ever waiting on them: the job runs on its own lane, so a slow
    /// or unreachable server never delays or fails an import that has already
    /// landed and been catalogued. A failed notify surfaces later as a
    /// clearable finding on that server's own health, not as an import error.
    /// </summary>
    private Task EnqueueMediaServerRefreshAsync(string mediaType, IReadOnlyList<MediaServerPathChange> changes, CancellationToken cancellationToken)
    {
        if (mediaServerJobScheduler is null || changes.Count == 0)
        {
            return Task.CompletedTask;
        }

        var payload = new MediaServerRefreshJobPayload(mediaType, changes);
        return mediaServerJobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                MediaServerJobTypes.Refresh,
                Source: "filesystem.import",
                PayloadJson: JsonSerializer.Serialize(payload),
                RelatedEntityType: null,
                RelatedEntityId: null),
            cancellationToken);
    }

    /// <summary>
    /// The rename is its own step. It is the moment a release title becomes a
    /// library filename, and it is the step people most often need to check.
    /// A file that kept its name has nothing to report.
    /// </summary>
    private async Task RecordRenameAsync(
        ImportProvenanceDetails provenance,
        string mediaType,
        string? entityId,
        CancellationToken cancellationToken)
    {
        var sourceName = Path.GetFileName(provenance.SourcePath);
        var destinationName = Path.GetFileName(provenance.DestinationPath);
        if (string.IsNullOrWhiteSpace(destinationName)
            || string.Equals(sourceName, destinationName, StringComparison.Ordinal))
        {
            return;
        }

        await activityFeedRepository.RecordActivityAsync(
            "filesystem.import.named",
            string.IsNullOrWhiteSpace(provenance.MatchedRuleNames)
                ? $"{sourceName} was named {destinationName}."
                : $"{sourceName} was named {destinationName}, keeping {provenance.MatchedRuleNames}.",
            JsonSerializer.Serialize(new
            {
                provenance.SourcePath,
                provenance.DestinationPath,
                provenance.ReleaseNameEvidence,
                provenance.MatchedRuleNames,
                provenance.DestinationRuleName
            }),
            null,
            mediaType == "tv" ? "series" : "movie",
            entityId,
            cancellationToken);

        await DispatchImportOutcomeNotificationAsync(
            "rename",
            $"Renamed: {destinationName}",
            string.IsNullOrWhiteSpace(provenance.MatchedRuleNames)
                ? $"{sourceName} was named {destinationName}."
                : $"{sourceName} was named {destinationName}, keeping {provenance.MatchedRuleNames}.",
            JsonSerializer.Serialize(new { provenance.SourcePath, provenance.DestinationPath }),
            prefs => prefs.RenameEnabled,
            "rename",
            cancellationToken);
    }

    private static ImportProvenanceDetails BuildImportProvenance(
        ImportExecuteRequest request,
        string sourcePath,
        string destinationPath,
        string preferredTransferMode,
        string transferModeUsed,
        bool usedFallback,
        CatalogImportResult catalog,
        LibraryItem? library,
        string workflowCleanup,
        string? destinationRuleId,
        string? destinationRuleName,
        MediaProbeInfo? mediaProbe)
    {
        var evaluation = catalog.PreferenceEvaluations.FirstOrDefault();
        var matchedRuleIds = catalog.PreferenceEvaluations
            .SelectMany(item => item.MatchedRuleIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceName = Path.GetFileName(sourcePath);
        var destinationName = Path.GetFileName(destinationPath);

        return new ImportProvenanceDetails(
            DispatchId: request.DispatchId?.Trim(),
            ReleaseNameEvidence: catalog.ReleaseNameEvidence,
            IndexerName: catalog.Dispatch?.IndexerName,
            DownloadClientName: catalog.Dispatch?.DownloadClientName,
            DownloadClientItemId: catalog.Dispatch?.TorrentHashOrItemId,
            SourcePath: sourcePath,
            DestinationPath: destinationPath,
            Rename: string.Equals(sourceName, destinationName, StringComparison.Ordinal)
                ? "Filename preserved"
                : $"{sourceName} -> {destinationName}",
            PreferredTransferMode: preferredTransferMode,
            TransferModeUsed: transferModeUsed,
            UsedFallback: usedFallback,
            CatalogUpdated: catalog.CatalogUpdated,
            DestinationRuleId: destinationRuleId,
            DestinationRuleName: destinationRuleName,
            ImportWorkflow: library?.ImportWorkflow,
            ProcessorName: ImportWorkflows.HandsToProcessor(library?.ImportWorkflow)
                ? library?.ProcessorName
                : null,
            PreferencePlanId: evaluation?.PlanId,
            PreferencePlanVersion: evaluation?.PlanVersion,
            PreferencePlanHash: evaluation?.PlanHash,
            PreferenceStatus: evaluation?.Evaluation.Status.ToString(),
            MatchedRuleIds: string.Join(", ", matchedRuleIds),
            MatchedRuleNames: string.Join(", ", catalog.MatchedRuleNames),
            WorkflowCleanup: workflowCleanup,
            MediaProbe: mediaProbe);
    }

    private sealed class CatalogImportResult
    {
        public required bool CatalogUpdated { get; init; }
        public required string? CatalogId { get; init; }

        /// <summary>
        /// The catalogue entity this import belongs to, as the activity feed
        /// addresses it. <see cref="CatalogId"/> is a title-and-year slug and
        /// matches nothing the title page asks for, so import events recorded
        /// against it never reached the title's own History.
        /// </summary>
        public string? EntityId { get; init; }
        public DownloadDispatchItem? Dispatch { get; init; }
        public string? ReleaseNameEvidence { get; init; }
        public IReadOnlyList<PreferenceEvaluationSnapshot> PreferenceEvaluations { get; init; } = [];
        public IReadOnlyList<string> MatchedRuleNames { get; init; } = [];

        /// <summary>
        /// The file(s) this import's catalogue write just superseded, found
        /// before the write overwrote their tracked path with the new one.
        /// Never includes a path equal to the destination - that upgrade
        /// replaces a file in place and is handled by ExecuteAsync's own
        /// backup-then-recycle path instead (#656).
        /// </summary>
        public IReadOnlyList<string> ReplacedFilePaths { get; init; } = [];
    }

    private async Task<CatalogImportResult> MarkCatalogImportedAsync(
        ImportPreviewRequest request,
        ImportPreviewResponse preview,
        string mediaType,
        IReadOnlyList<LibraryItem> libraries,
        string? dispatchId,
        CancellationToken cancellationToken,
        TvImportNumbers? tvNumbersOverride = null,
        IReadOnlyList<ImportPreviewResponse>? tvFilePreviews = null)
    {
        var library = ResolveLibraryForImport(preview.DestinationPath, mediaType, libraries);
        if (library is null)
        {
            return new CatalogImportResult { CatalogUpdated = false, CatalogId = null };
        }

        var quality = mediaDecisionService.DetectQuality($"{preview.SourcePath} {preview.DestinationPath}");
        var fileSizeBytes = GetFileSize(preview.DestinationPath);
        var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: mediaType,
            HasFile: true,
            CurrentQuality: quality,
            CutoffQuality: library.CutoffQuality,
            UpgradeUntilCutoff: library.UpgradeUntilCutoff,
            UpgradeUnknownItems: library.UpgradeUnknownItems));
        var dispatch = await ResolveDispatchAsync(dispatchId, cancellationToken);

        // A grab says which title this file is for, and the file goes there,
        // whatever name it arrived under (#617): matched by the release's own
        // name, a real film landed as a new title called "The Kid 1921 CC 1080p
        // BluRay x264-nikt0" while the film grabbed stayed wanted.
        if (await GrabbedTitleAsync(dispatch, mediaType, cancellationToken) is { } grabbed)
        {
            request = request with { Title = grabbed.Title, Year = grabbed.Year ?? request.Year };
        }

        var title = TitleForActivity(request);
        var catalogId = $"{title.ToLowerInvariant().Replace(" ", "-")}-{request.Year ?? 0}".Substring(0, Math.Min(64, $"{title.ToLowerInvariant().Replace(" ", "-")}-{request.Year ?? 0}".Length));
        var preferenceProfile = await ResolvePreferenceProfileAsync(library, mediaType, cancellationToken);
        var customFormats = preferenceProfile is null || qualityRepository is null
            ? null
            : await qualityRepository.ListCustomFormatsAsync(cancellationToken);
        var preferencePlan = preferenceProfile is null || qualityRepository is null
            ? null
            : await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
                qualityRepository,
                releasePreferencePlanRepository,
                preferenceProfile.Id,
                cancellationToken,
                customFormats);
        var guidePackage = preferenceProfile is null || preferencePlan is not null || guidePackageStore is null
            ? null
            : (await guidePackageStore.GetCurrentAsync(cancellationToken)).Package;
        var dispatchReleaseName = string.IsNullOrWhiteSpace(dispatch?.ReleaseName)
            ? null
            : dispatch.ReleaseName.Trim();
        // Kept with the file, not with the dispatch: a successful import
        // archives its dispatch row immediately, so a title asking later where
        // its copy came from would otherwise get nothing.
        var acquisition = dispatch is null
            ? null
            : new ReleaseAcquisition(
                IndexerName: dispatch.IndexerName,
                DownloadClientName: dispatch.DownloadClientName,
                DownloadClientItemId: dispatch.TorrentHashOrItemId,
                GrabbedUtc: dispatch.GrabAttemptedUtc ?? dispatch.CreatedUtc,
                DispatchId: dispatch.Id);
        var evidencePreviews = (tvFilePreviews is { Count: > 0 } ? tvFilePreviews : [preview])
            .GroupBy(item => item.DestinationPath, GetPathComparer())
            .Select(group => group.First())
            .ToArray();
        var evaluatedUtc = DateTimeOffset.UtcNow;
        var preferenceEvaluations = preferenceProfile is null
            ? []
            : evidencePreviews
                .Select(item => InstalledPreferenceEvaluationFactory.Create(
                    preferenceProfile,
                    mediaId: string.Empty,
                    libraryId: library.Id,
                    filePath: item.DestinationPath,
                    fileSizeBytes: GetFileSize(item.DestinationPath),
                    currentQuality: mediaDecisionService.DetectQuality($"{item.SourcePath} {item.DestinationPath}"),
                    evaluatedUtc: evaluatedUtc,
                    source: "filesystem.import",
                    customFormats: customFormats,
                    guidePackage: guidePackage,
                    preferencePlan: preferencePlan,
                    releaseNameEvidence: dispatchReleaseName ?? item.SourcePath,
                    acquisition: acquisition))
                .OfType<PreferenceEvaluationSnapshot>()
                .ToArray();
        var preferenceEvaluation = preferenceEvaluations.FirstOrDefault();

        // The title this file is already known to be: the show the request
        // names, or the show or film its grab was for -- and only when that
        // entry still exists (#643). The catalogue write goes straight to it.
        // Every Say Yes to the Dress episode Deluno grabbed for the real show
        // carried the real show's id, yet the write matched on title and year
        // instead; the real show had no start year, the file name said 2007,
        // and 120 episodes landed on a phantom made from the file name. An id
        // that no longer exists is no identity at all, so it does not excuse
        // an import from the unconfirmed-title guards below either.
        var knownSeriesId = mediaType == "tv" ? await KnownSeriesIdAsync(request, dispatch, cancellationToken) : null;
        var knownMovieId = mediaType == "movies" ? await KnownMovieIdAsync(dispatch, cancellationToken) : null;

        if (mediaType == "tv"
            && string.IsNullOrWhiteSpace(request.Title)
            && knownSeriesId is null
            && request.TvDbId is null)
        {
            // The TV side of #417, and #617: with no grab found and no title,
            // the only name left is the file's, and a show made from it holds
            // the episode under something that is not a show at all.
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(
                    title,
                    "unmatched",
                    $"Deluno imported the file but could not tell which show it belongs to, so it has not been added to the library. The only name available was the file's own: {title}",
                    "Open this case and choose the show, or add the show first and import again.",
                    null),
                cancellationToken);

            return new CatalogImportResult
            {
                CatalogUpdated = false,
                CatalogId = null,
                Dispatch = dispatch,
                EntityId = dispatch?.EntityId,
                ReleaseNameEvidence = dispatchReleaseName ?? preview.SourcePath,
                PreferenceEvaluations = preferenceEvaluations,
                MatchedRuleNames = ResolveMatchedRuleNames(preferenceEvaluations, customFormats, preferencePlan)
            };
        }

        if (mediaType == "tv"
            && knownSeriesId is null
            && request.TvDbId is null
            && string.IsNullOrWhiteSpace(request.ImdbId)
            && await seriesCatalogRepository.FindExistingIdAsync(title, request.Year, request.ImdbId, null, null, cancellationToken) is null)
        {
            // #643, live on the rig: two separate "Say Yes to the Dress" shows
            // existed in one library. Text-matching (CatalogueImportNaming or
            // similar) had found the TEXT "Say Yes to the Dress" -- plausibly
            // from a release filename -- so Title was non-empty and the guard
            // above did not fire, but nothing had confirmed that text against
            // TMDB: no SeriesId, no TvDbId, no ImdbId. ImportExistingAsync
            // below does its own title+year lookup and, on a miss, creates a
            // new show from whatever Title it was given -- and a guessed
            // title is a word, a punctuation mark, or a year away from the
            // real, already-catalogued show's own title, so that lookup is a
            // coincidence away from missing. It missed here: the real show
            // (tmdbId "31306") stayed untouched while a phantom with the same
            // guessed name grew 97 episodes across 8 seasons, every episode
            // title null, because there was no real metadata behind any of
            // it.
            //
            // FindExistingIdAsync just above runs the same match ImportExisting
            // would, ahead of time: if it finds nothing, this Title does not
            // yet name anything Deluno has, and inventing a show from the
            // guess is exactly how the phantom happened. Route it to recovery
            // instead. A Title that DOES land on an existing show by name is
            // unaffected -- FindExistingIdAsync finds it, this guard does not
            // fire, and ImportExistingAsync matches onto it exactly as today.
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(
                    title,
                    "unmatched",
                    $"Deluno matched the file to \"{title}\" but could not confirm that against a metadata provider, and no show already in the library has that name, so it has not been added. Adding it now would risk a second, unconfirmed entry for a show Deluno cannot tell apart from a real one.",
                    "Open this case and choose the show, or add the show first and import again.",
                    null),
                cancellationToken);

            return new CatalogImportResult
            {
                CatalogUpdated = false,
                CatalogId = null,
                Dispatch = dispatch,
                EntityId = dispatch?.EntityId,
                ReleaseNameEvidence = dispatchReleaseName ?? preview.SourcePath,
                PreferenceEvaluations = preferenceEvaluations,
                MatchedRuleNames = ResolveMatchedRuleNames(preferenceEvaluations, customFormats, preferencePlan)
            };
        }

        if (mediaType == "tv")
        {
            // Parse the source name, not the renamed destination. A Daily or
            // Absolute release may have been renamed to canonical SxxEyy
            // tokens already; parsing that output would lose the source
            // numbering model and could attach the file to the wrong episode.
            var episodeNumbers = tvNumbersOverride;
            IReadOnlyList<string> replacedEpisodeFilePaths = [];
            if (episodeNumbers is null)
            {
                var seriesNumbering = await ResolveSeriesNumberingAsync(request, cancellationToken);
                episodeNumbers = ParseTvImportNumbers(
                    preview.SourcePath,
                    preview.DestinationPath,
                    fileSizeBytes,
                    SeriesNumberingSchemes.Resolve(
                        request.SeriesType ?? seriesNumbering?.SeriesType,
                        request.NumberingScheme ?? seriesNumbering?.NumberingScheme),
                    seriesNumbering);

                // A season pack (tvNumbersOverride is not null) resolves and
                // discards its own replaced files before this method is ever
                // called (see ExecuteTvPackAsync), so this only runs for a
                // single-file import: the shape that left The Wild Robot's
                // superseded WEB 720p copy sitting beside its Bluray 1080p
                // replacement (#656). The file the catalogue tracked before
                // this write overwrites it is the file this import replaces -
                // whatever it was named.
                if (seriesNumbering is not null)
                {
                    var episodeIdsByNumber = seriesNumbering.Episodes.ToDictionary(
                        episode => (episode.SeasonNumber, episode.EpisodeNumber),
                        episode => episode.EpisodeId);
                    var replaced = new List<string>();
                    foreach (var episode in episodeNumbers.Episodes)
                    {
                        if (!episodeIdsByNumber.TryGetValue((episode.SeasonNumber, episode.EpisodeNumber), out var episodeId))
                        {
                            continue;
                        }
                        var trackedPath = await seriesCatalogRepository.GetEpisodeFilePathAsync(episodeId, cancellationToken);
                        if (!string.IsNullOrWhiteSpace(trackedPath))
                        {
                            replaced.Add(trackedPath);
                        }
                    }
                    replacedEpisodeFilePaths = replaced.Distinct(GetPathComparer()).ToArray();
                }
            }
            var result = await seriesCatalogRepository.ImportExistingAsync(
                library.Id,
                title,
                request.Year,
                decision.WantedStatus,
                decision.WantedReason,
                decision.CurrentQuality,
                decision.TargetQuality,
                decision.QualityCutoffMet,
                preview.DestinationPath,
                fileSizeBytes,
                episodeNumbers.Episodes,
                cancellationToken,
                preferenceEvaluation,
                episodeNumbers.AlternateEpisodes,
                episodeNumbers.SeasonPacks,
                preferenceEvaluations.Skip(1).ToArray(),
                seriesId: knownSeriesId,
                upgradeUntilCutoff: library.UpgradeUntilCutoff,
                upgradeUnknownItems: library.UpgradeUnknownItems);
            // The repository's legacy single-item boolean means "a new series
            // row was created", not "the import was recorded". A download
            // tied to an existing series therefore used to report a successful
            // file import with CatalogUpdated=false and skipped workflow
            // verification. An explicit series id is proof that this was an
            // existing, addressable catalogue item; the write above still
            // updates its wanted state and episode file.
            var catalogUpdated = result || knownSeriesId is not null;
            return new CatalogImportResult
            {
                CatalogUpdated = catalogUpdated,
                CatalogId = catalogUpdated ? catalogId : null,
                Dispatch = dispatch,
                // The show, not the grab: an episode search's grab names the episode,
                // and the show's History is where this import belongs (#595).
                EntityId = knownSeriesId ?? request.SeriesId ?? dispatch?.EntityId,
                ReleaseNameEvidence = dispatchReleaseName ?? preview.SourcePath,
                PreferenceEvaluations = preferenceEvaluations,
                MatchedRuleNames = ResolveMatchedRuleNames(preferenceEvaluations, customFormats, preferencePlan),
                ReplacedFilePaths = catalogUpdated
                    ? replacedEpisodeFilePaths.Where(path => !IsSamePath(path, preview.DestinationPath)).ToArray()
                    : []
            };
        }

        // A film Deluno cannot name is not a film Deluno should invent.
        //
        // TitleForActivity falls back to the source filename when the request
        // carries no title, which is right for an activity line and wrong for a
        // catalogue entry: an unmatched import used to create a movie called
        // "Sintel.2010.2160p.WEB-DL.x265-DELUNO" with no metadata provider and
        // no ids, marked as meeting its target quality - so nothing would ever
        // search for it or correct it, and it sat in the library looking like a
        // film for ever (#417).
        //
        // Import recovery is where an unidentified file belongs. It already
        // exists, it already explains itself, and it can be resolved by hand.
        if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.ImdbId))
        {
            await movieCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(
                    title,
                    "unmatched",
                    $"Deluno imported the file but could not tell which film it is, so it has not been added to the library. The only name available was the file's own: {title}",
                    "Open this case and choose the film, or add the film first and import again.",
                    null),
                cancellationToken);

            return new CatalogImportResult
            {
                CatalogUpdated = false,
                CatalogId = null,
                Dispatch = dispatch,
                EntityId = dispatch?.EntityId ?? request.SeriesId,
                ReleaseNameEvidence = dispatchReleaseName ?? preview.SourcePath,
                PreferenceEvaluations = preferenceEvaluations,
                MatchedRuleNames = ResolveMatchedRuleNames(preferenceEvaluations, customFormats, preferencePlan)
            };
        }

        // #643's movie mirror: the guard above only catches a Title that is
        // completely empty. A present-but-unconfirmed Title -- text-matched
        // from a release filename, never checked against a metadata provider
        // -- sailed through it, and ImportExistingAsync below would do its
        // own title+year lookup and, on a miss, insert a brand-new film from
        // whatever Title it was given: the exact shape that gave "Say Yes to
        // the Dress" a phantom duplicate on the TV side. FindExistingIdAsync
        // runs that same lookup ahead of time; if it finds nothing, this
        // Title does not yet name a film Deluno has and is not confirmed
        // against a provider either, so it is not safe to invent one. A
        // Title that lands on an existing film by name is unaffected --
        // FindExistingIdAsync finds it, this guard does not fire, and
        // ImportExistingAsync matches onto it exactly as before.
        if (string.IsNullOrWhiteSpace(request.ImdbId)
            && knownMovieId is null
            && await movieCatalogRepository.FindExistingIdAsync(title, request.Year, request.ImdbId, null, null, cancellationToken) is null)
        {
            await movieCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateMovieImportRecoveryCaseRequest(
                    title,
                    "unmatched",
                    $"Deluno matched the file to \"{title}\" but could not confirm that against a metadata provider, and no film already in the library has that name, so it has not been added. Adding it now would risk a second, unconfirmed entry for a film Deluno cannot tell apart from a real one.",
                    "Open this case and choose the film, or add the film first and import again.",
                    null),
                cancellationToken);

            return new CatalogImportResult
            {
                CatalogUpdated = false,
                CatalogId = null,
                Dispatch = dispatch,
                EntityId = dispatch?.EntityId ?? request.SeriesId,
                ReleaseNameEvidence = dispatchReleaseName ?? preview.SourcePath,
                PreferenceEvaluations = preferenceEvaluations,
                MatchedRuleNames = ResolveMatchedRuleNames(preferenceEvaluations, customFormats, preferencePlan)
            };
        }

        // The file the catalogue tracked for this film before this write
        // overwrites it is the file this import replaces - whatever name it
        // was under. A same-name upgrade is caught separately by the
        // destination-already-exists backup path in ExecuteAsync; this is
        // what a differently-named upgrade (a Bluray remux replacing a WEB
        // rip, say) needs to find its own predecessor at all (#656).
        var replacedMovieFilePath = knownMovieId is null
            ? null
            : (await movieCatalogRepository.GetByIdAsync(knownMovieId, cancellationToken))?.FilePath;

        var movieResult = await movieCatalogRepository.ImportExistingAsync(
            library.Id,
            title,
            request.Year,
            decision.WantedStatus,
            decision.WantedReason,
            decision.CurrentQuality,
            decision.TargetQuality,
            decision.QualityCutoffMet,
            preview.DestinationPath,
            fileSizeBytes,
            cancellationToken,
            preferenceEvaluation,
            movieId: knownMovieId);
        return new CatalogImportResult
        {
            CatalogUpdated = movieResult,
            CatalogId = movieResult ? catalogId : null,
            Dispatch = dispatch,
            EntityId = dispatch?.EntityId ?? request.SeriesId,
            ReleaseNameEvidence = dispatchReleaseName ?? preview.SourcePath,
            PreferenceEvaluations = preferenceEvaluations,
            MatchedRuleNames = ResolveMatchedRuleNames(preferenceEvaluations, customFormats, preferencePlan),
            // The wanted-state write above is an upsert that always runs -
            // movieResult only reports whether a *new* movie row was needed,
            // not whether the file was recorded (mirrored in the tv branch's
            // own catalogUpdated compensation above). An existing film's file
            // is replaced whether or not this import happened to create it.
            ReplacedFilePaths = !string.IsNullOrWhiteSpace(replacedMovieFilePath)
                && !IsSamePath(replacedMovieFilePath, preview.DestinationPath)
                    ? [replacedMovieFilePath]
                    : []
        };
    }

    private async Task<string?> KnownSeriesIdAsync(ImportPreviewRequest request, DownloadDispatchItem? dispatch, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SeriesId)
            && await seriesCatalogRepository.GetByIdAsync(request.SeriesId.Trim(), cancellationToken) is { } named)
        {
            return named.Id;
        }

        return dispatch is not null
            && !string.IsNullOrWhiteSpace(dispatch.EntityId)
            && string.Equals(dispatch.EntityType, "series", StringComparison.OrdinalIgnoreCase)
            && await seriesCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken) is { } grabbed
                ? grabbed.Id
                : null;
    }

    private async Task<string?> KnownMovieIdAsync(DownloadDispatchItem? dispatch, CancellationToken cancellationToken)
    {
        return dispatch is not null
            && !string.IsNullOrWhiteSpace(dispatch.EntityId)
            && string.Equals(dispatch.EntityType, "movie", StringComparison.OrdinalIgnoreCase)
            && await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken) is { } grabbed
                ? grabbed.Id
                : null;
    }

    /// <summary>The title and year of what a grab was for: its film, or its show.</summary>
    private async Task<(string Title, int? Year)?> GrabbedTitleAsync(DownloadDispatchItem? dispatch, string mediaType, CancellationToken cancellationToken)
    {
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return null;
        }

        if (mediaType == "movies" && string.Equals(dispatch.EntityType, "movie", StringComparison.OrdinalIgnoreCase)
            && await movieCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken) is { } movie)
        {
            return (movie.Title, movie.ReleaseYear);
        }

        if (mediaType == "tv" && string.Equals(dispatch.EntityType, "series", StringComparison.OrdinalIgnoreCase)
            && await seriesCatalogRepository.GetByIdAsync(dispatch.EntityId, cancellationToken) is { } series)
        {
            return (series.Title, series.StartYear);
        }

        return null;
    }

    private async Task<DownloadDispatchItem?> ResolveDispatchAsync(
        string? dispatchId,
        CancellationToken cancellationToken)
    {
        if (downloadDispatchesRepository is null || string.IsNullOrWhiteSpace(dispatchId))
        {
            return null;
        }

        return await downloadDispatchesRepository.GetDispatchAsync(dispatchId, cancellationToken);
    }

    /// <summary>
    /// Remove completed (#806): a per-client choice, off unless the person
    /// turned it on for the specific client this download came from. Nothing
    /// here changes behaviour for a client left at its default.
    /// </summary>
    private async Task RemoveCompletedDownloadIfConfiguredAsync(string? dispatchId, CancellationToken cancellationToken)
    {
        if (downloadClientActions is null || connectionsRepository is null)
        {
            return;
        }

        var dispatch = await ResolveDispatchAsync(dispatchId, cancellationToken);
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.TorrentHashOrItemId) || string.IsNullOrWhiteSpace(dispatch.DownloadClientId))
        {
            return;
        }

        var client = (await connectionsRepository.ListDownloadClientsAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, dispatch.DownloadClientId, StringComparison.OrdinalIgnoreCase));
        if (client is not { RemoveCompleted: true })
        {
            return;
        }

        await downloadClientActions.RemoveWithDataAsync(client.Id, dispatch.TorrentHashOrItemId, cancellationToken);
    }

    private static IReadOnlyList<string> ResolveMatchedRuleNames(
        IReadOnlyList<PreferenceEvaluationSnapshot> evaluations,
        IReadOnlyList<CustomFormatItem>? customFormats,
        ReleasePreferencePlan? preferencePlan)
    {
        var matchedIds = evaluations
            .SelectMany(item => item.MatchedRuleIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var immutableNames = (preferencePlan?.Sources ?? [])
            .Where(source => matchedIds.Contains(source.RuleId ?? source.SourceId))
            .Select(source => source.RuleName);
        var currentNames = (customFormats ?? [])
            .Where(format => matchedIds.Contains(format.Id))
            .Select(format => format.Name);
        return immutableNames
            .Concat(currentNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<QualityProfileItem?> ResolvePreferenceProfileAsync(
        LibraryItem library,
        string mediaType,
        CancellationToken cancellationToken)
    {
        if (qualityRepository is not null && !string.IsNullOrWhiteSpace(library.QualityProfileId))
        {
            var profile = (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, library.QualityProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile is not null)
            {
                return profile;
            }
        }

        // Older libraries can predate a persisted quality-profile id. Keep the
        // import evidence useful by compiling the library's effective cutoff
        // into the same typed contract, while clearly leaving custom formats
        // out of the legacy translation.
        if (string.IsNullOrWhiteSpace(library.CutoffQuality))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        return new QualityProfileItem(
            Id: $"library/{library.Id}",
            Name: library.QualityProfileName ?? library.Name,
            MediaType: mediaType,
            CutoffQuality: library.CutoffQuality,
            AllowedQualities: library.CutoffQuality,
            CustomFormatIds: string.Empty,
            UpgradeUntilCutoff: library.UpgradeUntilCutoff,
            UpgradeUnknownItems: library.UpgradeUnknownItems,
            AllowLowerQualityReplacements: false,
            PresetId: null,
            PresetVersion: null,
            PresetDrifted: false,
            CreatedUtc: library.CreatedUtc == default ? now : library.CreatedUtc,
            UpdatedUtc: library.UpdatedUtc == default ? now : library.UpdatedUtc);
    }

    private static long? GetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<SeriesNumberingDetail?> ResolveSeriesNumberingAsync(
        ImportPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SeriesId))
        {
            return null;
        }

        return await seriesCatalogRepository.GetNumberingAsync(
            request.SeriesId.Trim(),
            cancellationToken);
    }

    private static TvImportNumbers ParseTvImportNumbers(
        string sourcePath,
        string destinationPath,
        long? fileSizeBytes,
        string? requestedScheme = null,
        SeriesNumberingDetail? numbering = null)
    {
        var selectedScheme = SeriesNumberingSchemes.Normalize(requestedScheme);
        // Numbering belongs to the release name, while file ownership belongs
        // to the placed library file. Keeping those paths separate is
        // essential: download-client cleanup may remove sourcePath after a
        // successful import, and episode rows must not then point at a file
        // Deluno deliberately stopped owning.
        var selected = SeriesNumberingResolver.ParseFileName(sourcePath, selectedScheme).Matches;
        var episodes = new List<ImportedEpisodeItem>();
        foreach (var item in selected)
        {
            if (item.SeasonNumber is int seasonNumber && item.EpisodeNumber is int episodeNumber)
            {
                if (numbering is not null)
                {
                    var catalogued = numbering.Episodes.Count(episode =>
                        episode.SeasonNumber == seasonNumber && episode.EpisodeNumber == episodeNumber);
                    if (catalogued != 1)
                    {
                        // A syntactically valid S99E99 token is not permission
                        // to manufacture that episode in a known series. Zero
                        // and duplicate catalogue matches both require review.
                        continue;
                    }
                }

                episodes.Add(new ImportedEpisodeItem(
                    seasonNumber,
                    episodeNumber,
                    HasFile: true,
                    FilePath: destinationPath,
                    FileSizeBytes: fileSizeBytes));
                continue;
            }

            // Alternate numbering is only safe when the persisted catalogue
            // maps the token to exactly one canonical episode. If the series
            // is not known yet, retain the alternate fact for the repository's
            // later resolution path instead of guessing a season/episode.
            if (numbering is null ||
                !SeriesNumberingResolver.TryResolve(item, numbering.Episodes, out var match, out _))
            {
                continue;
            }

            episodes.Add(new ImportedEpisodeItem(
                match!.SeasonNumber,
                match.EpisodeNumber,
                HasFile: true,
                FilePath: destinationPath,
                FileSizeBytes: fileSizeBytes,
                AbsoluteNumber: item.AbsoluteNumber,
                AirDate: item.AirDate,
                SceneSeasonNumber: item.SceneSeasonNumber,
                SceneEpisodeNumber: item.SceneEpisodeNumber,
                NumberingSource: match.NumberingSource));
        }

        var alternateEpisodes = new List<ImportedEpisodeNumberingItem>();
        foreach (var scheme in new[]
        {
            SeriesNumberingSchemes.AirDate,
            SeriesNumberingSchemes.Absolute,
            SeriesNumberingSchemes.Scene
        })
        {
            var parsed = SeriesNumberingResolver.ParseFileName(sourcePath, scheme);
            alternateEpisodes.AddRange(parsed.Matches.Select(item => new ImportedEpisodeNumberingItem(
                item.NumberingScheme,
                item.SeasonNumber,
                item.EpisodeNumber,
                item.AbsoluteNumber,
                item.SceneSeasonNumber,
                item.SceneEpisodeNumber,
                item.AirDate,
                HasFile: true,
                FilePath: destinationPath,
                FileSizeBytes: fileSizeBytes)));
        }

        var seasonPacks = SeriesNumberingResolver
            .ParseSeasonPackNumbers(sourcePath)
            .Select(seasonNumber => new ImportedSeasonPackItem(
                seasonNumber,
                destinationPath,
                fileSizeBytes))
            .ToArray();

        return new TvImportNumbers(episodes.Distinct().ToArray(), alternateEpisodes.Distinct().ToArray(), seasonPacks);
    }

    private sealed record TvImportNumbers(
        IReadOnlyList<ImportedEpisodeItem> Episodes,
        IReadOnlyList<ImportedEpisodeNumberingItem> AlternateEpisodes,
        IReadOnlyList<ImportedSeasonPackItem> SeasonPacks);

    private sealed record TvPackFilePlan(
        ImportPreviewRequest Request,
        ImportPreviewResponse Preview,
        TvImportNumbers Numbers,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// A pack file that does not resolve to any canonical episode - an extra,
    /// featurette, sample, or a special the catalogue does not list (#652). It
    /// is left in place and named rather than blocking the rest of the pack.
    /// </summary>
    private sealed record TvPackSkippedFilePlan(
        ImportPreviewResponse Preview,
        string Reason);

    private sealed record TvPackPlan(
        ImportPreviewResponse Summary,
        IReadOnlyList<TvPackFilePlan> Files,
        IReadOnlyList<TvPackSkippedFilePlan> SkippedFiles,
        TvImportNumbers Numbers,
        SeriesNumberingDetail? Numbering,
        IReadOnlyList<string> BlockReasons,
        bool AlreadyCommitted,
        IReadOnlyList<TvPackReplacement> Replacements);

    private sealed record TvPackReplacement(
        string EpisodeId,
        string ExistingPath,
        string DestinationPath);

    private sealed record TvPackBackup(
        string ExistingPath,
        string BackupPath);

    private sealed record TvPackPlacement(
        TvPackFilePlan File,
        string StagingPath,
        string Mode,
        bool UsedFallback,
        bool SourceMoved,
        bool Finalized);

    private static IReadOnlyList<string> BuildImportWarnings(
        string sourcePath,
        string destinationPath,
        bool sourceExists,
        bool destinationExists,
        bool hardlinkAvailable,
        bool isSupportedMediaFile)
    {
        var warnings = new List<string>();
        if (!isSupportedMediaFile) warnings.Add("This file extension is not configured as an importable video file.");
        if (!sourceExists) warnings.Add("Source file is not visible to Deluno. Check Docker mounts, UNC access, mapped drives, or service account permissions.");
        if (sourceExists && IsSamePath(sourcePath, destinationPath)) warnings.Add("Source and destination resolve to the same file. Deluno will block this import.");
        if (destinationExists) warnings.Add("Destination already exists. Import will be blocked unless overwrite is enabled.");
        if (!hardlinkAvailable) warnings.Add("Hardlink is unlikely because source and destination appear to be on different filesystems. Copy fallback may be required.");
        if (sourceExists && IsRecentlyWritten(sourcePath)) warnings.Add("Source was modified recently. If the download client is still writing, import may fail or be incomplete.");

        if (Path.GetPathRoot(sourcePath) is { } sourceRoot &&
            Path.GetPathRoot(destinationPath) is { } destinationRoot &&
            !string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Source root {sourceRoot} differs from destination root {destinationRoot}.");
        }

        return warnings;
    }

    private static bool IsRecentlyWritten(string sourcePath)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(sourcePath) < TimeSpan.FromSeconds(30);
        }
        catch
        {
            return false;
        }
    }

    private static LibraryItem? ResolveLibraryForImport(
        string destinationPath,
        string mediaType,
        IReadOnlyList<LibraryItem> libraries)
    {
        var normalizedDestination = Path.GetFullPath(destinationPath);
        return libraries
            .Where(library => NormalizeMediaType(library.MediaType) == mediaType && !string.IsNullOrWhiteSpace(library.RootPath))
            .Select(library => new { Library = library, Root = Path.GetFullPath(library.RootPath) })
            .Where(item => normalizedDestination.StartsWith(item.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .OrderByDescending(item => item.Root.Length)
            .Select(item => item.Library)
            .FirstOrDefault();
    }

    /// <summary>
    /// What an automatic upgrade does with the file(s) it just superseded:
    /// recycle each one through Deluno's recycle bin, once the new file is
    /// already placed and verified (#656). Never touches a path outside a
    /// configured library root, and never a path equal to the destination -
    /// that is not a replaced file, it is the file this import just wrote.
    /// </summary>
    private async Task<ReplacedFileCleanupResult> RecycleReplacedFilesAsync(
        IReadOnlyList<string> replacedPaths,
        string destinationPath,
        string mediaType,
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();
        var warnings = new List<string>();

        foreach (var replacedPath in replacedPaths.Distinct(GetPathComparer()))
        {
            if (string.IsNullOrWhiteSpace(replacedPath) ||
                IsSamePath(replacedPath, destinationPath) ||
                !File.Exists(replacedPath))
            {
                continue;
            }

            var fileName = Path.GetFileName(replacedPath);
            var library = ResolveLibraryForImport(replacedPath, mediaType, libraries);
            if (library is null)
            {
                // Never outside the library root: Deluno will not delete or
                // move a file it cannot place under a configured root.
                warnings.Add($"The previous file {fileName} was left in place because it is outside a configured library root.");
                continue;
            }

            // #893: subtitles beside the replaced video belong to it, not to
            // whatever the upgrade wrote - left behind, nothing ever reads
            // them or finds them missing again.
            var oldSubtitlePaths = SubtitleInventoryService.FindSidecarSubtitlePaths(replacedPath);

            if (recycleBinService is null)
            {
                // Keep hand-constructed test/utility instances backwards
                // compatible; the host always supplies the recoverable
                // service through DI.
                TryDelete(replacedPath);
                foreach (var subtitlePath in oldSubtitlePaths)
                {
                    TryDelete(subtitlePath);
                }

                messages.Add(oldSubtitlePaths.Count > 0
                    ? $"Removed the previous {fileName} file it replaced, and its subtitles."
                    : $"Removed the previous {fileName} file it replaced.");
                await DispatchFileDeletedNotificationAsync(fileName, forUpgrade: true, cancellationToken);
                continue;
            }

            var recycled = await recycleBinService.StoreReplacementAsync(
                library,
                replacedPath,
                replacedPath,
                cancellationToken);

            var subtitlesRecycled = 0;
            foreach (var subtitlePath in oldSubtitlePaths)
            {
                if (!File.Exists(subtitlePath))
                {
                    continue;
                }

                var subtitleRecycled = await recycleBinService.StoreReplacementAsync(
                    library,
                    subtitlePath,
                    subtitlePath,
                    cancellationToken);
                if (subtitleRecycled is not null)
                {
                    subtitlesRecycled++;
                }
            }

            if (recycled is null && File.Exists(replacedPath))
            {
                warnings.Add($"The previous file {fileName} could not be moved to the recycle bin and was left in place.");
            }
            else
            {
                messages.Add(subtitlesRecycled > 0
                    ? $"Moved the previous {fileName} file and its {subtitlesRecycled} subtitle file(s) to the recycle bin."
                    : $"Moved the previous {fileName} file to the recycle bin.");
                await DispatchFileDeletedNotificationAsync(fileName, forUpgrade: true, cancellationToken);
            }
        }

        return new ReplacedFileCleanupResult(messages, warnings);
    }

    private sealed record ReplacedFileCleanupResult(
        IReadOnlyList<string> Messages,
        IReadOnlyList<string> Warnings)
    {
        public string Summary => Messages.Count == 0 ? string.Empty : " " + string.Join(" ", Messages);
    }

    private static void AtomicCopy(string sourcePath, string destinationPath, bool overwrite)
    {
        var temporaryPath = BuildTemporaryPath(destinationPath, ".deluno-copy");
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, destinationPath, overwrite);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static long VerifyStagedImport(string stagingPath)
    {
        if (!File.Exists(stagingPath))
        {
            throw new IOException("The import staging file was not created.");
        }

        var length = new FileInfo(stagingPath).Length;
        if (length <= 0)
        {
            throw new IOException("The import staging file is empty.");
        }

        return length;
    }

    private static void VerifyFinalImport(string destinationPath, long expectedSize)
    {
        if (!File.Exists(destinationPath))
        {
            throw new IOException("The imported file was not placed at its final destination.");
        }

        var length = new FileInfo(destinationPath).Length;
        if (length != expectedSize)
        {
            throw new IOException($"The final imported file size ({length}) does not match the staged file size ({expectedSize}).");
        }
    }

    /// <summary>
    /// #766: mirrors Radarr/Sonarr's Permissions section. Only takes effect on
    /// non-Windows hosts, only when enabled, and only actually changes anything
    /// when Deluno's own process owns the file — the same caveat Radarr/Sonarr
    /// document for their own chmod/chown.
    /// </summary>
    private Task ApplyImportPermissionsAsync(string destinationPath, PlatformSettingsSnapshot settings, CancellationToken cancellationToken)
        => FilePermissions.ApplyAsync(destinationPath, settings, logger, cancellationToken);

    private static void RollBackPartialImport(
        string sourcePath,
        string destinationPath,
        string stagingPath,
        string? backupPath,
        bool restoreSourceOnFailure)
    {
        RestoreMovedSourceIfNeeded(sourcePath, destinationPath, stagingPath, restoreSourceOnFailure);
        TryDelete(stagingPath);
        TryDelete(destinationPath);
        if (File.Exists(backupPath))
        {
            File.Move(backupPath, destinationPath, overwrite: true);
        }

        // This attempt is over one way or another - nothing is left for a
        // restart to recover (#913).
        DeleteRestartMarker(stagingPath);
    }

    /// <summary>
    /// Records what an in-flight import was doing, next to its staging file,
    /// so a restart can tell "finish this" from "this is nobody's business"
    /// (#913). Best-effort: a marker that fails to write only means a crash
    /// during this particular import will surface as a plain missing-source
    /// case on the next scan rather than an automatic restart recovery.
    /// </summary>
    private static void WriteRestartMarker(
        string stagingPath,
        string sourcePath,
        string destinationPath,
        string mediaType,
        string title)
    {
        try
        {
            var marker = new ImportRestartMarker(sourcePath, destinationPath, stagingPath, mediaType, title);
            File.WriteAllText(RestartMarkerPath(stagingPath), JsonSerializer.Serialize(marker));
        }
        catch
        {
            // Best-effort only - see summary above.
        }
    }

    private static void DeleteRestartMarker(string stagingPath) => TryDelete(RestartMarkerPath(stagingPath));

    internal static string RestartMarkerPath(string stagingPath) => stagingPath + ".deluno-marker.json";

    private static void RestoreMovedSourceIfNeeded(
        string sourcePath,
        string destinationPath,
        string stagingPath,
        bool restoreSourceOnFailure)
    {
        if (!restoreSourceOnFailure || File.Exists(sourcePath))
        {
            return;
        }

        if (File.Exists(stagingPath))
        {
            File.Move(stagingPath, sourcePath, overwrite: false);
            return;
        }

        if (File.Exists(destinationPath))
        {
            File.Move(destinationPath, sourcePath, overwrite: false);
        }
    }

    private static string BuildTemporaryPath(string destinationPath, string suffix)
        => $"{destinationPath}{suffix}-{Guid.CreateVersion7():N}.tmp";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only. Recovery cases carry the actionable error.
        }
    }

    /// <summary>
    /// Whether the download client that fetched this still believes it owns the
    /// file, because it is still sharing it (#287).
    ///
    /// Deleting such a file is not cleanup, it is sabotage: the torrent stays
    /// registered against data that has vanished, the client errors it, sharing
    /// stops, and on a private tracker that is the user's ratio or their
    /// account. Usenet has no such phase, so the ordinary delete is correct
    /// there and stays.
    ///
    /// An unknown answer counts as still shared. Leaving a file behind wastes
    /// disk and says so on the dashboard; deleting one wrongly cannot be undone.
    /// </summary>
    private async Task<bool> IsStillSharedByClientAsync(string? dispatchId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dispatchId) || downloadDispatchesRepository is null)
        {
            // No dispatch behind it: a manual import, a watched folder or an
            // existing-library scan. No client owns it, so nobody else is going
            // to tidy it up.
            return false;
        }

        try
        {
            var dispatch = await downloadDispatchesRepository.GetDispatchAsync(dispatchId, cancellationToken);
            if (dispatch is null || connectionsRepository is null)
            {
                return true;
            }

            var clients = await connectionsRepository.ListDownloadClientsAsync(cancellationToken);
            var client = clients.FirstOrDefault(item =>
                string.Equals(item.Id, dispatch.DownloadClientId, StringComparison.OrdinalIgnoreCase));

            return client is null || DownloadProtocols.HasSharingPhase(client.Protocol);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not tell whether a download client still owns {DispatchId}; leaving the source file alone.",
                dispatchId);
            return true;
        }
    }

    /// <summary>
    /// Whether this file is a processor's output for the library: under its
    /// processor output folder, and not under the folder its downloads land in.
    /// The second half is what keeps a library whose two folders overlap from
    /// having a client's file deleted from underneath it.
    /// </summary>
    public static bool IsProcessorOutput(string? sourcePath, LibraryItem? library)
        => library is not null
            && ImportWorkflows.HandsToProcessor(library.ImportWorkflow)
            && IsUnder(sourcePath, library.ProcessorOutputPath)
            && !IsUnder(sourcePath, library.DownloadsPath);

    private static bool IsUnder(string? path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        // Compared as text so a Windows path means the same thing on the Linux
        // build agents as it does where it is used.
        static string Normalize(string value) => value.Trim().Replace('\\', '/').TrimEnd('/');
        return Normalize(path).StartsWith(Normalize(folder) + "/", StringComparison.OrdinalIgnoreCase);
    }

    private WorkflowCleanupResult ApplyWorkflowCleanup(
        string sourcePath,
        LibraryItem? library,
        bool stillSharedByClient,
        string transferModeUsed)
    {
        // #892: a copy import leaves a second, full copy of the data on disk -
        // the source and the library file no longer share anything. A hardlink
        // import's "source" is the same bytes under a second name, so removing
        // it is the opt-in housekeeping setting below and nothing changes for
        // an install that never turned it on. A copy import gets no such
        // choice, single file or season pack alike: once the copy is verified
        // (it is, or this method would not have been reached) the original is
        // checked and cleared, subject to the same sharing rule as everything
        // else.
        // #915 (cleanup sheet row H4): a processor's copy is only ever a
        // working copy. It is always removed after a successful import,
        // together with everything else the processor left for that
        // hand-off, regardless of the library's own Cleanup mode -- that
        // setting answers a different question, about the original download.
        var isProcessorOutput = IsProcessorOutput(sourcePath, library);
        var cleanupEnabled = isProcessorOutput
            || string.Equals(transferModeUsed, "copy", StringComparison.OrdinalIgnoreCase)
            || (library is not null && string.Equals(library.CleanupMode, "remove-source-after-import", StringComparison.OrdinalIgnoreCase));

        if (library is null || !cleanupEnabled)
        {
            return new WorkflowCleanupResult("keep-source", false, 0, "Source kept because this workflow is configured to retain completed downloads.");
        }

        // The sharing rule owns this file now. It knows how long the site the
        // release came from expects it to be shared, and it removes it through
        // the client rather than behind its back (#288). Two settings deleting
        // the same file on different schedules is how the seeding bug happened.
        //
        // That is about the client's own file. A processor's output is a second
        // copy the client has never heard of: it shares the download, not this.
        // Treating it as shared left every processed torrent's output, at full
        // size, in the processor's folder for good, because the sharing rule
        // removes through the client and the client cannot remove what it does
        // not hold.
        if (stillSharedByClient && !isProcessorOutput)
        {
            return new WorkflowCleanupResult(
                "share-then-remove",
                false,
                0,
                "The download client is still sharing this, so Deluno left its copy alone. It will ask the client to remove it once your sharing rule is met.");
        }

        var sourceRemoved = false;
        var emptyFoldersRemoved = 0;
        string? warning = null;

        try
        {
            if (File.Exists(sourcePath))
            {
                File.Delete(sourcePath);
                sourceRemoved = true;
            }

            if (isProcessorOutput)
            {
                // #915: everything else the processor left for this hand-off
                // -- other output, temporary/partial files, empty folders --
                // scoped to this hand-off's own place under the processor
                // output folder. A hand-off in its own subfolder (the
                // documented folder-hand-off shape) owns that subfolder
                // outright; a loose file sharing the processor output root
                // with other hand-offs only ever loses its own leftovers, by
                // name, never a sibling hand-off's files.
                emptyFoldersRemoved = CleanupProcessorHandoffLeftovers(sourcePath, library.ProcessorOutputPath);
            }
            else if (library.RemoveEmptySourceFolders)
            {
                emptyFoldersRemoved = RemoveEmptySourceFolders(sourcePath, library.DownloadsPath);
            }
        }
        catch (IOException exception)
        {
            warning = exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            warning = exception.Message;
        }

        var summary = warning is null
            ? sourceRemoved
                ? emptyFoldersRemoved == 0
                    ? isProcessorOutput
                        ? "The processor's copy was removed after import."
                        : "The completed source file was removed after import."
                    : isProcessorOutput
                        ? $"The processor's copy was removed, along with {emptyFoldersRemoved} leftover item(s) from that hand-off."
                        : $"The completed source file was removed and {emptyFoldersRemoved} empty source folder(s) cleaned up."
                : "The source file was already gone, so there was nothing left to remove."
            : $"Import succeeded, but source cleanup was not completed: {warning}";

        return new WorkflowCleanupResult("remove-source-after-import", sourceRemoved, emptyFoldersRemoved, summary, warning);
    }

    /// <summary>
    /// Removes everything the processor left for one hand-off under its
    /// output folder, and reports how many extra items were cleared (#915).
    /// The matched file itself is already gone by the time this runs.
    /// </summary>
    private static int CleanupProcessorHandoffLeftovers(string matchedFilePath, string? processorOutputRoot)
    {
        if (string.IsNullOrWhiteSpace(processorOutputRoot) || !Directory.Exists(processorOutputRoot))
        {
            return 0;
        }

        var root = Path.GetFullPath(processorOutputRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var handoffFolder = Path.GetDirectoryName(Path.GetFullPath(matchedFilePath));
        if (string.IsNullOrWhiteSpace(handoffFolder))
        {
            return 0;
        }

        var removed = 0;

        if (!string.Equals(handoffFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, GetPathComparison())
            && IsWithinPath(root, handoffFolder)
            && Directory.Exists(handoffFolder))
        {
            // The folder-hand-off shape: the whole subfolder was named for
            // this one completed download and belongs to this hand-off
            // alone, so whatever the processor left in it -- scraps,
            // temporary files, anything -- goes with it.
            removed += Directory.EnumerateFileSystemEntries(handoffFolder, "*", SearchOption.AllDirectories).Count();
            Directory.Delete(handoffFolder, recursive: true);

            var parent = Path.GetDirectoryName(handoffFolder);
            while (!string.IsNullOrWhiteSpace(parent)
                   && !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, GetPathComparison())
                   && IsWithinPath(root, parent))
            {
                if (Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    break;
                }

                Directory.Delete(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        else if (Directory.Exists(handoffFolder))
        {
            // A loose-file hand-off shares the processor output root with
            // other hand-offs' files. Only remove this hand-off's own
            // leftovers: files with the same name stem and a temporary
            // suffix the processor never finished renaming away.
            var stem = Path.GetFileNameWithoutExtension(matchedFilePath);
            foreach (var candidate in Directory.EnumerateFiles(handoffFolder, stem + ".*"))
            {
                if (string.Equals(candidate, matchedFilePath, GetPathComparison())
                    || !IsTemporaryProcessorFileName(candidate))
                {
                    continue;
                }

                File.Delete(candidate);
                removed++;
            }
        }

        return removed;
    }

    // #915: the processor contract requires a drop-folder processor to write
    // under one of these temporary names and rename when finished. Deluno's
    // own import extension allow-list already ignores any of them for
    // matching purposes (their extension is never a supported video
    // extension); this is only consulted for cleaning up a leftover that a
    // rename never claimed.
    private static readonly string[] TemporaryProcessorFileSuffixes = [".partial", ".tmp", ".temp", ".part", ".crdownload"];

    private static bool IsTemporaryProcessorFileName(string path)
        => TemporaryProcessorFileSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static int RemoveEmptySourceFolders(string sourcePath, string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return 0;
        }

        var root = Path.GetFullPath(configuredRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        var removed = 0;

        while (!string.IsNullOrWhiteSpace(current) &&
               !string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, GetPathComparison()) &&
               IsWithinPath(root, current))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            Directory.Delete(current);
            removed++;
            current = Path.GetDirectoryName(current);
        }

        return removed;
    }

    private static bool IsWithinPath(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedRoot, GetPathComparison());
    }

    private static StringComparison GetPathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record WorkflowCleanupResult(
        string Mode,
        bool SourceRemoved,
        int EmptyFoldersRemoved,
        string Summary,
        string? Warning = null);

    private static string BuildTransferExplanation(string preferredMode, bool hardlinkAvailable, bool useHardlinks)
    {
        if (preferredMode == "hardlink") return "Hardlink is preferred because it keeps one physical copy on disk while making the file appear in the library.";
        return useHardlinks && !hardlinkAvailable
            ? "Copy is preferred because hardlinking does not appear possible for these source and destination paths."
            : "Copy is preferred because hardlinks are disabled in media management settings.";
    }

    private static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows" ? "tv" : "movies";

    private static bool MatchesRule(DestinationRuleItem rule, ImportPreviewRequest request)
    {
        var expected = rule.MatchValue.Trim();
        return rule.MatchKind.Trim().ToLowerInvariant() switch
        {
            "genre" => request.Genres?.Any(value => value.Contains(expected, StringComparison.OrdinalIgnoreCase)) == true,
            "tag" => request.Tags?.Any(value => value.Contains(expected, StringComparison.OrdinalIgnoreCase)) == true,
            "studio" => request.Studio?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true,
            "language" or "originallanguage" => request.OriginalLanguage?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true,
            "title" => request.Title?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true,
            _ => false
        };
    }

    private static string ApplyTemplate(
        string? template,
        string title,
        int? year,
        string? imdbId = null,
        string? tvDbId = null,
        string? qualityProfile = null,
        string? genre = null,
        string? tag = null,
        string? network = null,
        bool replaceIllegalCharacters = true,
        string colonReplacementMode = ColonReplacementModes.SmartReplace)
        => NamingTemplateRenderer.RenderFolder(
            template,
            title,
            year,
            imdbId,
            tvDbId,
            qualityProfile,
            genre,
            tag,
            network,
            replaceIllegalCharacters: replaceIllegalCharacters,
            colonReplacementMode: colonReplacementMode);

    /// <summary>
    /// #762: the per-season subfolder a series folder splits into, or null
    /// for no subfolder at all -- the fallback both when the season could not
    /// be resolved and when its format is blank, which is today's behaviour
    /// for every install that has never opened this setting. Season 0 (a
    /// specials episode) uses SpecialsFolderFormat instead of
    /// SeasonFolderFormat, the way Sonarr splits the two.
    ///
    /// <para>
    /// #850: a series can override this globally-configured behaviour.
    /// <paramref name="perSeriesSeasonFolder"/> null (every series that has
    /// never opted in) changes nothing. false forces episodes flat into the
    /// series folder even when the global format is set. true forces a
    /// season subfolder even when the global format is blank, falling back
    /// to Sonarr's own default pattern so "on" always does something.
    /// </para>
    /// </summary>
    private static string? BuildSeasonSubfolder(PlatformSettingsSnapshot settings, int? seasonNumber, bool? perSeriesSeasonFolder = null)
    {
        if (seasonNumber is not int season)
        {
            return null;
        }

        if (perSeriesSeasonFolder is false)
        {
            return null;
        }

        var template = season == 0 ? settings.SpecialsFolderFormat : settings.SeasonFolderFormat;
        if (string.IsNullOrWhiteSpace(template))
        {
            if (perSeriesSeasonFolder is not true)
            {
                return null;
            }

            template = season == 0 ? "Specials" : "Season {season:00}";
        }

        var withSeason = template
            .Replace("{season:00}", season.ToString("D2"), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", season.ToString(), StringComparison.OrdinalIgnoreCase);
        var rendered = NamingTemplateRenderer.RenderSegment(
            withSeason,
            title: null,
            year: null,
            replaceIllegalCharacters: settings.ReplaceIllegalCharacters,
            colonReplacementMode: settings.ColonReplacementMode);
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }

    /// <summary>
    /// What Deluno decided about the release being imported: the quality it
    /// counts as, the group that made it, and the custom formats it matched.
    ///
    /// <para>The formats a name keeps are the ones TRaSH marks
    /// <c>includeCustomFormatWhenRenaming</c>, so a rename carries exactly what
    /// the guide says belongs in a file name - no Deluno list to drift, and
    /// nothing added because a parser happened to spot a word.</para>
    /// </summary>
    private async Task<NamingDecision> ResolveNamingDecisionAsync(
        ImportPreviewRequest request,
        IReadOnlyList<LibraryItem> libraries,
        string? releaseName,
        CancellationToken cancellationToken)
    {
        var mediaType = NormalizeMediaType(request.MediaType);
        // The incoming file's own name, independent of releaseName: a dispatch
        // resolves the decision from the release Deluno grabbed, but the
        // {Original Filename} token means the file that actually arrived.
        var originalFileName = Path.GetFileNameWithoutExtension(
            string.IsNullOrWhiteSpace(request.FileName) ? request.SourcePath : request.FileName);
        var evidence = string.IsNullOrWhiteSpace(releaseName) ? originalFileName : releaseName.Trim();
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return NamingDecision.None;
        }

        var quality = mediaDecisionService.DetectQuality($"{evidence} {request.SourcePath}");
        var releaseGroup = ReleaseGroupPattern().Match(evidence) is { Success: true } group
            ? group.Groups["group"].Value
            : null;
        // #760: MediaInfo tokens read the same name evidence Quality and the
        // release group already do -- a probe pipeline is a larger feature
        // than a naming token, and the name carries this by convention the
        // same way it carries codec and audio for the library list.
        var mediaFacts = MediaFileNameFacts.Parse(evidence);
        // A short, stable identifier for the release, so two files that
        // otherwise render the same name can still be told apart.
        var releaseHash = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(evidence)))[..8].ToLowerInvariant();
        if (qualityRepository is null)
        {
            return new NamingDecision(
                quality,
                releaseGroup,
                OriginalFileName: originalFileName,
                ReleaseHash: releaseHash,
                VideoCodec: mediaFacts.VideoCodec,
                AudioCodec: mediaFacts.AudioCodec,
                AudioChannels: mediaFacts.AudioChannels,
                VideoDynamicRangeType: mediaFacts.DynamicRange,
                VideoBitDepth: mediaFacts.BitDepth);
        }

        var profileId = ResolveLibrary(libraries, mediaType, request.LibraryId)?.QualityProfileId;
        var profile = string.IsNullOrWhiteSpace(profileId)
            ? null
            : (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, profileId, StringComparison.OrdinalIgnoreCase));
        var rules = (await qualityRepository.ListCustomFormatsAsync(cancellationToken))
            .Where(rule => string.Equals(rule.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
            .Where(rule => profile is null || SplitIds(profile.CustomFormatIds).Contains(rule.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (rules.Length == 0)
        {
            return new NamingDecision(
                quality,
                releaseGroup,
                OriginalFileName: originalFileName,
                ReleaseHash: releaseHash,
                VideoCodec: mediaFacts.VideoCodec,
                AudioCodec: mediaFacts.AudioCodec,
                AudioChannels: mediaFacts.AudioChannels,
                VideoDynamicRangeType: mediaFacts.DynamicRange,
                VideoBitDepth: mediaFacts.BitDepth);
        }

        var guide = guidePackageStore is null
            ? GuidePackageCatalog.Current
            : (await guidePackageStore.GetCurrentAsync(cancellationToken)).Package;
        var guideById = guide.CustomFormats.ToDictionary(format => format.TrashId, StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        var editions = new List<string>();
        foreach (var rule in rules)
        {
            // The same matcher acquisition and the installed snapshot use, so a
            // name can only say what the decision said.
            if (!CustomFormatDefinitionMatcher.Evaluate(
                    evidence,
                    rule.Conditions,
                    anyCondition: rule.TrashId is not null,
                    mediaType).IsMatch)
            {
                continue;
            }

            var guideFormat = string.IsNullOrWhiteSpace(rule.TrashId) ? null : guideById.GetValueOrDefault(rule.TrashId);
            if (guideFormat?.IncludeWhenRenaming == true)
            {
                names.Add(guideFormat.Name);
            }

            if (string.Equals(guideFormat?.Category, "edition", StringComparison.OrdinalIgnoreCase))
            {
                editions.Add(guideFormat!.Name);
            }
        }

        return new NamingDecision(
            quality,
            releaseGroup,
            names.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            editions.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            OriginalFileName: originalFileName,
            ReleaseHash: releaseHash,
            VideoCodec: mediaFacts.VideoCodec,
            AudioCodec: mediaFacts.AudioCodec,
            AudioChannels: mediaFacts.AudioChannels,
            VideoDynamicRangeType: mediaFacts.DynamicRange,
            VideoBitDepth: mediaFacts.BitDepth);
    }

    private static IEnumerable<string> SplitIds(string? csv)
        => (csv ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"-(?<group>[A-Za-z0-9]{2,20})$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseGroupPattern();

    private static ResolvedDestinationFileName ResolveDestinationFileName(
        ImportPreviewRequest request,
        PlatformSettingsSnapshot settings,
        string mediaType,
        string title,
        SeriesNumberingDetail? numbering = null,
        NamingDecision? decision = null)
    {
        decision ??= NamingDecision.None;
        var incomingName = string.IsNullOrWhiteSpace(request.FileName)
            ? Path.GetFileName(request.SourcePath)
            : request.FileName.Trim();
        if (!settings.RenameOnImport)
        {
            return new ResolvedDestinationFileName(incomingName, null);
        }

        var extension = Path.GetExtension(incomingName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return new ResolvedDestinationFileName(incomingName, "Rename on import could not determine the file extension, so Deluno preserved the incoming filename.");
        }

        if (mediaType != "tv")
        {
            // A movie had no file-name template at all, so every import became
            // "Title (Year)" and the quality, the release group and every
            // custom format Deluno matched were thrown away.
            var movieName = NamingTemplateRenderer.RenderSegment(
                settings.MovieFileFormat,
                title,
                request.Year,
                request.ImdbId,
                request.TvDbId,
                request.QualityProfile,
                request.Genres?.FirstOrDefault(),
                request.Tags?.FirstOrDefault(),
                request.Network,
                decision,
                settings.ReplaceIllegalCharacters,
                settings.ColonReplacementMode);
            return new ResolvedDestinationFileName($"{movieName}{extension}", null);
        }

        var scheme = SeriesNumberingSchemes.Resolve(
            request.SeriesType ?? numbering?.SeriesType,
            request.NumberingScheme ?? numbering?.NumberingScheme);
        var parsedEpisodes = SeriesNumberingResolver.ParseFileName(incomingName, scheme);
        if (parsedEpisodes.Matches.Count == 0)
        {
            return new ResolvedDestinationFileName(
                incomingName,
                "Rename on import preserved this TV filename because Deluno could not safely determine its season and episode number.");
        }

        // #763: a file naming more than one episode (S01E01E02, S01E01-E03,
        // ...) resolves every one of them the same way a single match always
        // has, so a multi-episode file gets exactly the same catalogue
        // matching guarantees a single-episode one does.
        var resolved = new List<(int Season, int Episode, SeriesEpisodeNumbering? Catalogued)>();
        foreach (var parsed in parsedEpisodes.Matches)
        {
            if (parsed.SeasonNumber is int parsedSeason && parsed.EpisodeNumber is int parsedEpisode)
            {
                resolved.Add((
                    parsedSeason,
                    parsedEpisode,
                    numbering?.Episodes.SingleOrDefault(item =>
                        item.SeasonNumber == parsedSeason && item.EpisodeNumber == parsedEpisode)));
                continue;
            }

            if (numbering is not null && SeriesNumberingResolver.TryResolve(parsed, numbering.Episodes, out var match, out _))
            {
                resolved.Add((match!.SeasonNumber, match.EpisodeNumber, match));
                continue;
            }

            return new ResolvedDestinationFileName(
                incomingName,
                $"Rename on import preserved this TV filename because its {scheme} number could not be matched to one catalogued episode.");
        }

        if (resolved.Select(item => item.Season).Distinct().Count() > 1)
        {
            return new ResolvedDestinationFileName(
                incomingName,
                "Rename on import preserved this TV filename because its episodes span more than one season.");
        }

        var seasonNumber = resolved[0].Season;
        var episodeNumbers = resolved.Select(item => item.Episode).Distinct().OrderBy(number => number).ToArray();
        var episodeNumber = episodeNumbers[0];
        var cataloguedEpisode = resolved[0].Catalogued;
        // Every episode's own title, in episode order, joined the way Sonarr
        // joins a multi-episode file's combined title -- the multi-episode
        // style below only ever changes the season/episode token, never this.
        var episodeTitle = string.Join(
            " + ",
            resolved
                .OrderBy(item => item.Episode)
                .Select(item => string.IsNullOrWhiteSpace(item.Catalogued?.Title) ? $"Episode {item.Episode:D2}" : item.Catalogued!.Title.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));
        // The decision's quality when there is one: reading it back out of the
        // incoming name is how an episode Deluno took as WEB-DL could be filed
        // as something else entirely.
        var quality = decision.Quality
            ?? LibraryQualityDecider.DetectQuality($"{incomingName} {request.SourcePath}")
            ?? string.Empty;
        // #760/#761: {Air-Date} and {absolute:000} come from this exact
        // episode's own catalogued numbering, never re-parsed from the
        // incoming name -- the same reasoning NamingDecision already applies
        // to Quality and the release group.
        var episodeDecision = decision with
        {
            AirDate = cataloguedEpisode?.AirDate,
            AbsoluteEpisodeNumber = cataloguedEpisode?.AbsoluteNumber
        };
        // #761: a dated or absolute-numbered show gets its own template;
        // everything else keeps the standard one. Keyed off the same scheme
        // that resolved this exact episode, not the raw series type, so a
        // show whose numbering scheme was overridden to standard still gets
        // the standard template.
        var episodeFileFormat = scheme switch
        {
            SeriesNumberingSchemes.AirDate => settings.DailyEpisodeFileFormat,
            SeriesNumberingSchemes.Absolute => settings.AnimeEpisodeFileFormat,
            _ => settings.EpisodeFileFormat
        };
        // #763: a multi-episode file replaces the template's whole
        // "S{Season:00}...E{Episode:00}" group with the chosen style's
        // combined token before the usual token-by-token substitution below
        // runs -- which is why the plain {season:00}/{episode:00} replacements
        // further down are left in place as the fallback for a custom
        // template that does not put the two tokens next to each other.
        var multiEpisodeFormat = episodeNumbers.Length > 1
            ? MultiEpisodeTokenPattern().Replace(
                episodeFileFormat,
                _ => MultiEpisodeStyles.FormatToken(settings.MultiEpisodeStyle, seasonNumber, episodeNumbers),
                1)
            : episodeFileFormat;
        var formatted = multiEpisodeFormat
            .Replace("{Series Title}", SanitizeFileName(title, settings.ReplaceIllegalCharacters, settings.ColonReplacementMode), StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", SanitizeFileName(title, settings.ReplaceIllegalCharacters, settings.ColonReplacementMode), StringComparison.OrdinalIgnoreCase)
            .Replace("{season:00}", seasonNumber.ToString("D2"), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", seasonNumber.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{episode:00}", episodeNumber.ToString("D2"), StringComparison.OrdinalIgnoreCase)
            .Replace("{episode}", episodeNumber.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Episode Title}", episodeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Quality}", quality, StringComparison.OrdinalIgnoreCase);
        return new ResolvedDestinationFileName(
            $"{NamingTemplateRenderer.RenderSegment(formatted, title: null, year: null, decision: episodeDecision, replaceIllegalCharacters: settings.ReplaceIllegalCharacters, colonReplacementMode: settings.ColonReplacementMode)}{extension}",
            null,
            seasonNumber);
    }

    // #763: the template's own "S{Season:00}...E{Episode:00}" group -- an
    // optional leading literal S (case-insensitive, matching a custom
    // template that spells it lower-case), the season token, up to a
    // handful of connecting characters (typically a literal "E"), then the
    // episode token. Matched once, against the format string itself, before
    // the tokens are substituted for real values.
    [GeneratedRegex(@"S?\{season:00\}[^{}]{0,4}\{episode:00\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MultiEpisodeTokenPattern();

    [GeneratedRegex(@"(^|[.\-_\s])sample([.\-_\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SampleTokenPattern();

    /// <summary>
    /// The import-path counterpart to <c>NamingTemplateRenderer</c>'s own
    /// sanitizing: both call <see cref="FileNameSanitizer.Sanitize"/>, so a
    /// name assembled here and one assembled by the renderer never disagree
    /// about what #757's switch or #759's colon mode does to a character.
    /// </summary>
    private static string SanitizeFileName(
        string value,
        bool replaceIllegalCharacters = true,
        string colonReplacementMode = ColonReplacementModes.SmartReplace)
    {
        var cleaned = FileNameSanitizer.Sanitize(value, replaceIllegalCharacters, colonReplacementMode);
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned;
    }

    // #762: the resolved episode's season, so the caller can build a season
    // (or specials) subfolder without re-parsing the incoming file name.
    // Null for a movie, or a TV file whose season could not be determined --
    // both cases fall back to no season subfolder, same as an unconfigured
    // SeasonFolderFormat/SpecialsFolderFormat.
    private sealed record ResolvedDestinationFileName(string FileName, string? Warning, int? SeasonNumber = null);

    private static string TitleForActivity(ImportPreviewRequest request)
        => string.IsNullOrWhiteSpace(request.Title)
            ? Path.GetFileNameWithoutExtension(request.SourcePath)
            : request.Title.Trim();

    private static bool CanLikelyHardlink(string sourcePath, string destinationPath)
    {
        try
        {
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
            var destinationRoot = Path.GetPathRoot(Path.GetFullPath(destinationPath));
            return !string.IsNullOrWhiteSpace(sourceRoot) &&
                   string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSamePath(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Null when the destination volume has enough room, or a plain-words
    /// reason otherwise (#765). A volume the probe cannot read is never
    /// blocked here -- that is what SkipFreeSpaceCheck exists for the person
    /// to decide, and guessing wrong on an unreadable volume would fail an
    /// import Radarr/Sonarr would have allowed.
    /// </summary>
    private string? DescribeInsufficientFreeSpace(string destinationFolder, int minimumFreeSpaceMb)
    {
        if (minimumFreeSpaceMb <= 0)
        {
            return null;
        }

        var freeBytes = _freeSpaceBytesProbe(destinationFolder);
        if (freeBytes is null)
        {
            return null;
        }

        var minimumBytes = (long)minimumFreeSpaceMb * 1024 * 1024;
        if (freeBytes.Value >= minimumBytes)
        {
            return null;
        }

        var freeMb = freeBytes.Value / (1024 * 1024);
        return $"Importing this file would leave the destination volume with {freeMb:N0} MB free, below the configured minimum of {minimumFreeSpaceMb:N0} MB.";
    }

    private static long? DefaultFreeSpaceBytesProbe(string destinationFolder)
    {
        try
        {
            var probePath = Directory.Exists(destinationFolder) ? destinationFolder : Path.GetPathRoot(Path.GetFullPath(destinationFolder));
            if (string.IsNullOrEmpty(probePath))
            {
                return null;
            }

            return new DriveInfo(probePath).AvailableFreeSpace;
        }
        catch
        {
            // Some network mounts and FUSE layers cannot report free space at
            // all. Nothing here can tell "cannot report" apart from "briefly
            // unavailable", so it is never treated as a block -- only
            // SkipFreeSpaceCheck exists to say that on purpose.
            return null;
        }
    }

    private static string NormalizeTransferMode(string? transferMode)
        => transferMode?.Trim().ToLowerInvariant() switch
        {
            "hardlink" => "hardlink",
            "move" => "move",
            "copy" => "copy",
            _ => "auto"
        };

    private static bool TryCreateHardlink(string sourcePath, string destinationPath, out string error)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (CreateHardLink(destinationPath, sourcePath, IntPtr.Zero))
                {
                    error = string.Empty;
                    return true;
                }

                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            if (link(sourcePath, destinationPath) == 0)
            {
                error = string.Empty;
                return true;
            }

            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            error = exception.Message;
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int link(string oldpath, string newpath);
}
