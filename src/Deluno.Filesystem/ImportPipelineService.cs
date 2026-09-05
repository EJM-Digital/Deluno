using System.ComponentModel;
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
    IBlockedReleaseRepository? blockedReleases = null)
    : IImportPipelineService
{
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    public async Task<ImportPreviewResponse> PreviewAsync(ImportPreviewRequest request, CancellationToken cancellationToken)
    {
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
        var resolvedPreview = await ResolveImportPreviewAsync(request.Preview, settings, rules, libraries, cancellationToken);

        if (resolvedPreview.SourceExists && !ImportFileReadiness.IsReady(resolvedPreview.SourcePath))
        {
            // Do not probe or copy a file while a download client or processor
            // may still be writing it. Worker retries can safely try again on
            // the next attempt without creating a false recovery failure.
            return Failed(
                ImportFileReadiness.RetryableStatusCode,
                "The source file is still being written or is locked by another process. Deluno will retry when it is stable.");
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

        if (!SupportedVideoExtensions.Contains(extension))
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
            return Failed(StatusCodes.Status409Conflict, replacementRisk);
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

            var catalogImportResult = await MarkCatalogImportedAsync(
                request.Preview,
                preview,
                mediaType,
                libraries,
                settings.UnmonitorWhenCutoffMet,
                cancellationToken);

            var matchedLibrary = ResolveLibraryForImport(preview.DestinationPath, mediaType, libraries);

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
            }

            var cleanup = ApplyWorkflowCleanup(
                preview.SourcePath,
                matchedLibrary,
                await IsStillSharedByClientAsync(request.DispatchId, cancellationToken));

            await activityFeedRepository.RecordActivityAsync(
                "filesystem.import.completed",
                $"{TitleForActivity(request.Preview)} was imported using {mode}.",
                JsonSerializer.Serialize(new
                {
                    preview.SourcePath,
                    preview.DestinationPath,
                    preview.PreferredTransferMode,
                    TransferModeUsed = mode,
                    usedFallback,
                    catalogUpdated = catalogImportResult.CatalogUpdated,
                    workflowCleanup = cleanup,
                    preview.MatchedRuleId,
                    preview.MatchedRuleName,
                    MediaProbe = preview.MediaProbe
                }),
                null,
                mediaType == "tv" ? "series" : "movie",
                null,
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
                        ["workflowCleanup"] = cleanup.Summary
                    },
                    Outcome: $"{TitleForActivity(request.Preview)} imported to {preview.DestinationPath}. {cleanup.Summary}",
                    Alternatives: []),
                null,
                mediaType == "tv" ? "series" : "movie",
                null,
                cancellationToken);

            var response = new ImportExecuteResponse(
                Preview: preview,
                Executed: true,
                TransferModeUsed: mode,
                UsedFallback: usedFallback,
                CatalogUpdated: catalogImportResult.CatalogUpdated,
                Message: usedFallback
                    ? $"Import completed with copy fallback because hardlink creation was not possible. {cleanup.Summary}"
                    : $"Import completed using {mode}. {cleanup.Summary}");

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
                "Import completed for {MediaType} title {Title} using {TransferMode}. Destination={DestinationPath} Fallback={UsedFallback}",
                mediaType,
                TitleForActivity(request.Preview),
                mode,
                preview.DestinationPath,
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
            return Failed(StatusCodes.Status400BadRequest, ioException.Message);
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

    private static ImportPipelineResult Failed(int statusCode, string message)
        => new(false, statusCode, null, message);

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

            var warnings = new List<string>();
            if (!preview.SourceExists)
            {
                warnings.Add("Source file is no longer present.");
            }
            else if (!ImportFileReadiness.IsReady(preview.SourcePath))
            {
                warnings.Add("Source file is still being written or locked.");
            }
            if (!Path.IsPathRooted(preview.DestinationPath))
            {
                warnings.Add("No rooted TV library destination is configured.");
            }
            if (!preview.IsSupportedMediaFile)
            {
                warnings.Add("The destination extension is not an importable video type.");
            }
            if (IsSamePath(preview.SourcePath, preview.DestinationPath))
            {
                warnings.Add("Source and destination resolve to the same file.");
            }
            if (preview.MediaProbe is { Status: "failed" })
            {
                warnings.Add(preview.MediaProbe.Message ?? "Media probing failed.");
            }
            if (preview.MediaProbe is { Status: "succeeded", VideoStreams.Count: 0 })
            {
                warnings.Add("No video stream was detected.");
            }
            if (preview.MediaProbe?.DurationSeconds is > 0 and < 120)
            {
                warnings.Add("The runtime is under two minutes and looks like a sample.");
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
                warnings.Add("The filename does not resolve to one or more canonical episodes in this show.");
            }
            else if (declaredSeasons.Length == 1 && fileNumbers.Episodes.Any(episode => episode.SeasonNumber != declaredSeasons[0]))
            {
                warnings.Add($"The file resolves outside season {declaredSeasons[0]:00} named by the download folder.");
            }

            if (warnings.Count > 0)
            {
                blockReasons.Add($"{Path.GetFileName(sourcePath)}: {string.Join(" ", warnings)}");
            }
            files.Add(new TvPackFilePlan(fileRequest, preview, fileNumbers, warnings));
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
        var pack = new ImportPackPreview(
            CanExecute: distinctReasons.Length == 0,
            AlreadyCommitted: alreadyCommitted,
            SourceFileCount: files.Count,
            EpisodeCount: episodes.Length,
            Files: publicFiles,
            BlockReasons: distinctReasons);
        var first = files[0].Preview;
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
                ? [alreadyCommitted
                    ? "This exact season pack is already placed and catalogued. Retrying is safe and will not duplicate it."
                    : $"All {files.Count} files resolve to {episodes.Length} canonical episodes and can be committed together."]
                : distinctReasons,
            Explanation = distinctReasons.Length == 0
                ? "Every file has a unique destination and canonical episode identity. Placement and catalogue updates will be committed as one pack operation."
                : "Deluno will not import any part of this pack until every file has one safe destination and canonical episode mapping.",
            DecisionSteps =
            [
                $"Pack: found {files.Count} importable video files.",
                $"Identity: resolved {episodes.Length} unique canonical episodes.",
                distinctReasons.Length == 0
                    ? "Safety: no duplicate episode claims, destination collisions, or unmatched files were found."
                    : $"Safety: blocked by {distinctReasons.Length} issue(s); no file will be placed."
            ],
            Pack = pack
        };
        return new TvPackPlan(
            summary,
            files,
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
                "This exact season pack is already placed and catalogued. Nothing was duplicated.");
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
                placements[index] = placement with { Finalized = true };
            }

            var catalog = await MarkCatalogImportedAsync(
                request.Preview,
                plan.Files[0].Preview,
                "tv",
                libraries,
                settings.UnmonitorWhenCutoffMet,
                cancellationToken,
                plan.Numbers,
                plan.Files.Select(file => file.Preview).ToArray());
            if (!catalog.CatalogUpdated)
            {
                throw new InvalidOperationException("Every pack file was staged, but the TV catalogue did not accept the atomic episode manifest.");
            }
            catalogCommitted = true;
            foreach (var backup in backups)
            {
                TryDelete(backup.BackupPath);
            }

            var usedFallback = placements.Any(placement => placement.UsedFallback);
            var modes = placements.Select(placement => placement.Mode).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var modeUsed = modes.Length == 1 ? modes[0] : "mixed";
            var message = $"Imported {placements.Count} season-pack files covering {plan.Numbers.Episodes.Count} episodes as one catalogue transaction.";
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
            var response = BuildTvPackResponse(plan, "committed", usedFallback: false, message);
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
            }).ToArray());

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

            var sharedByClient = await IsStillSharedByClientAsync(request.DispatchId, cancellationToken);
            var cleanup = placements.Select(placement => ApplyWorkflowCleanup(
                placement.File.Preview.SourcePath,
                ResolveLibraryForImport(placement.File.Preview.DestinationPath, "tv", libraries),
                sharedByClient)).ToArray();
            await activityFeedRepository.RecordActivityAsync(
                "filesystem.import.season-pack.completed",
                $"{TitleForActivity(request.Preview)} season pack imported {placements.Count} files covering {plan.Numbers.Episodes.Count} episodes.",
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
                    Cleanup = cleanup.Select(item => item.Summary)
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
                        ["usedFallback"] = usedFallback.ToString()
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
            await using (var source = File.OpenRead(file.Preview.SourcePath))
            await using (var destination = File.OpenRead(file.Preview.DestinationPath))
            {
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

    private async Task<string?> ValidateReplacementAsync(
        ImportExecuteRequest request,
        ImportPreviewResponse preview,
        CancellationToken cancellationToken)
    {
        if (!request.Overwrite || request.ForceReplacement || !File.Exists(preview.DestinationPath))
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
                .Where(path => SupportedVideoExtensions.Contains(Path.GetExtension(path)))
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
                .Where(path => SupportedVideoExtensions.Contains(Path.GetExtension(path)))
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
    private static LibraryItem? ResolveLibrary(IReadOnlyList<LibraryItem> libraries, string mediaType)
        => libraries
            .Where(library => NormalizeMediaType(library.MediaType) == mediaType)
            .FirstOrDefault(library => !string.IsNullOrWhiteSpace(library.RootPath));

    private static string? ResolveLibraryRoot(IReadOnlyList<LibraryItem> libraries, string mediaType)
        => ResolveLibrary(libraries, mediaType)?.RootPath;

    private async Task<ImportPreviewResponse> ResolveImportPreviewAsync(
        ImportPreviewRequest request,
        PlatformSettingsSnapshot settings,
        IReadOnlyList<DestinationRuleItem> rules,
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
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
        var rootPath = rule?.RootPath ??
                       ResolveLibraryRoot(libraries, mediaType) ??
                       (mediaType == "tv" ? settings.SeriesRootPath : settings.MovieRootPath) ??
                       string.Empty;
        var template = rule?.FolderTemplate ??
                       (mediaType == "tv" ? settings.SeriesFolderFormat : settings.MovieFolderFormat);
        var library = ResolveLibrary(libraries, mediaType);
        var folder = ApplyTemplate(
            template,
            title,
            request.Year,
            request.ImdbId,
            request.TvDbId,
            request.QualityProfile ?? library?.QualityProfileName,
            request.Genres?.FirstOrDefault(),
            request.Tags?.FirstOrDefault(),
            request.Network);
        var destinationFolder = string.IsNullOrWhiteSpace(rootPath) ? folder : Path.Combine(rootPath, folder);
        var resolvedName = ResolveDestinationFileName(request, settings, mediaType, title, seriesNumbering);
        var fileName = resolvedName.FileName;
        var destinationPath = Path.Combine(destinationFolder, SanitizeFileName(fileName));
        var canHardlink = CanLikelyHardlink(request.SourcePath, destinationPath);
        var sourceExists = File.Exists(request.SourcePath);
        var destinationExists = File.Exists(destinationPath);
        var sourceSize = sourceExists ? new FileInfo(request.SourcePath).Length : 0;
        var destinationSize = destinationExists ? new FileInfo(destinationPath).Length : 0;
        var isSupportedMediaFile = SupportedVideoExtensions.Contains(Path.GetExtension(destinationPath));
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
            string.IsNullOrEmpty(executeRequest.DispatchId) ||
            ImportFailurePolicy.BlockFor(failureKind) == BlockDecision.Never)
        {
            return;
        }

        try
        {
            var dispatch = await downloadDispatchesRepository.GetDispatchAsync(executeRequest.DispatchId, cancellationToken);
            if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.ReleaseName))
            {
                return;
            }

            // Counts attempts at this same release that have already failed, so
            // "one retry" means one retry rather than one per dispatch record.
            var priorFailures = await CountPriorFailuresAsync(dispatch, cancellationToken);
            if (!ImportFailurePolicy.ShouldBlock(failureKind, priorFailures))
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
                    DateTimeOffset.MinValue),
                cancellationToken);

            // Said out loud, because a refusal nobody is told about is the
            // behaviour this whole feature replaces.
            await activityFeedRepository.RecordActivityAsync(
                "release.blocked",
                $"Deluno will not use {dispatch.ReleaseName} again. {summary} Future searches skip it and say so, and you can undo this on the blocklist.",
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
        if (outboundNotificationService is not null)
        try
        {
            await outboundNotificationService.DispatchAsync(
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
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Outbound notification dispatch failed for import failure — notifications may be misconfigured.");
        }
    }

    private async Task RecordImportStartedAsync(
        ImportExecuteRequest request,
        ImportPreviewResponse preview,
        string mediaType,
        CancellationToken cancellationToken)
    {
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
            null,
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
            null,
            cancellationToken);
    }

    private static string SerializeRecoveryDetails(ImportExecuteRequest request)
        => JsonSerializer.Serialize(new
        {
            RetryRequest = request,
            request.Preview.SourcePath,
            request.Preview.FileName,
            request.Preview.MediaType,
            request.Preview.Title,
            request.Preview.Year,
            request.TransferMode,
            request.Overwrite,
            request.AllowCopyFallback
        });

    private sealed class CatalogImportResult
    {
        public required bool CatalogUpdated { get; init; }
        public required string? CatalogId { get; init; }
    }

    private async Task<CatalogImportResult> MarkCatalogImportedAsync(
        ImportPreviewRequest request,
        ImportPreviewResponse preview,
        string mediaType,
        IReadOnlyList<LibraryItem> libraries,
        bool unmonitorWhenCutoffMet,
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
                    preferencePlan: preferencePlan))
                .OfType<PreferenceEvaluationSnapshot>()
                .ToArray();
        var preferenceEvaluation = preferenceEvaluations.FirstOrDefault();

        if (mediaType == "tv")
        {
            // Parse the source name, not the renamed destination. A Daily or
            // Absolute release may have been renamed to canonical SxxEyy
            // tokens already; parsing that output would lose the source
            // numbering model and could attach the file to the wrong episode.
            var episodeNumbers = tvNumbersOverride;
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
                unmonitorWhenCutoffMet,
                preview.DestinationPath,
                fileSizeBytes,
                episodeNumbers.Episodes,
                cancellationToken,
                preferenceEvaluation,
                episodeNumbers.AlternateEpisodes,
                episodeNumbers.SeasonPacks,
                preferenceEvaluations.Skip(1).ToArray());
            // The repository's legacy single-item boolean means "a new series
            // row was created", not "the import was recorded". A download
            // tied to an existing series therefore used to report a successful
            // file import with CatalogUpdated=false and skipped workflow
            // verification. An explicit series id is proof that this was an
            // existing, addressable catalogue item; the write above still
            // updates its wanted state and episode file.
            var catalogUpdated = result || !string.IsNullOrWhiteSpace(request.SeriesId);
            return new CatalogImportResult { CatalogUpdated = catalogUpdated, CatalogId = catalogUpdated ? catalogId : null };
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

            return new CatalogImportResult { CatalogUpdated = false, CatalogId = null };
        }

        var movieResult = await movieCatalogRepository.ImportExistingAsync(
            library.Id,
            title,
            request.Year,
            decision.WantedStatus,
            decision.WantedReason,
            decision.CurrentQuality,
            decision.TargetQuality,
            decision.QualityCutoffMet,
            unmonitorWhenCutoffMet,
            preview.DestinationPath,
            fileSizeBytes,
            cancellationToken,
            preferenceEvaluation);
        return new CatalogImportResult { CatalogUpdated = movieResult, CatalogId = movieResult ? catalogId : null };
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

    private sealed record TvPackPlan(
        ImportPreviewResponse Summary,
        IReadOnlyList<TvPackFilePlan> Files,
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
    }

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

    private WorkflowCleanupResult ApplyWorkflowCleanup(string sourcePath, LibraryItem? library, bool stillSharedByClient)
    {
        if (library is null || !string.Equals(library.CleanupMode, "remove-source-after-import", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowCleanupResult("keep-source", false, 0, "Source kept because this workflow is configured to retain completed downloads.");
        }

        // The sharing rule owns this file now. It knows how long the site the
        // release came from expects it to be shared, and it removes it through
        // the client rather than behind its back (#288). Two settings deleting
        // the same file on different schedules is how the seeding bug happened.
        if (stillSharedByClient)
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

            if (library.RemoveEmptySourceFolders)
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
                    ? "The completed source file was removed after import."
                    : $"The completed source file was removed and {emptyFoldersRemoved} empty source folder(s) cleaned up."
                : "The source file was already gone, so there was nothing left to remove."
            : $"Import succeeded, but source cleanup was not completed: {warning}";

        return new WorkflowCleanupResult("remove-source-after-import", sourceRemoved, emptyFoldersRemoved, summary, warning);
    }

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
        string? network = null)
        => NamingTemplateRenderer.RenderFolder(
            template,
            title,
            year,
            imdbId,
            tvDbId,
            qualityProfile,
            genre,
            tag,
            network);

    private static ResolvedDestinationFileName ResolveDestinationFileName(
        ImportPreviewRequest request,
        PlatformSettingsSnapshot settings,
        string mediaType,
        string title,
        SeriesNumberingDetail? numbering = null)
    {
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
            var movieName = ApplyTemplate("{Movie Title} ({Release Year})", title, request.Year);
            return new ResolvedDestinationFileName($"{movieName}{extension}", null);
        }

        var scheme = SeriesNumberingSchemes.Resolve(
            request.SeriesType ?? numbering?.SeriesType,
            request.NumberingScheme ?? numbering?.NumberingScheme);
        var parsedEpisodes = SeriesNumberingResolver.ParseFileName(incomingName, scheme);
        if (parsedEpisodes.Matches.Count != 1)
        {
            return new ResolvedDestinationFileName(
                incomingName,
                "Rename on import preserved this TV filename because Deluno could not safely determine its season and episode number.");
        }

        var parsed = parsedEpisodes.Matches[0];
        int seasonNumber;
        int episodeNumber;
        SeriesEpisodeNumbering? cataloguedEpisode;
        if (parsed.SeasonNumber is int parsedSeason && parsed.EpisodeNumber is int parsedEpisode)
        {
            seasonNumber = parsedSeason;
            episodeNumber = parsedEpisode;
            cataloguedEpisode = numbering?.Episodes.SingleOrDefault(item =>
                item.SeasonNumber == seasonNumber && item.EpisodeNumber == episodeNumber);
        }
        else if (numbering is not null &&
                 SeriesNumberingResolver.TryResolve(parsed, numbering.Episodes, out var match, out _))
        {
            seasonNumber = match!.SeasonNumber;
            episodeNumber = match.EpisodeNumber;
            cataloguedEpisode = match;
        }
        else
        {
            return new ResolvedDestinationFileName(
                incomingName,
                $"Rename on import preserved this TV filename because its {scheme} number could not be matched to one catalogued episode.");
        }

        var episodeTitle = string.IsNullOrWhiteSpace(cataloguedEpisode?.Title)
            ? $"Episode {episodeNumber:D2}"
            : cataloguedEpisode.Title.Trim();
        var quality = LibraryQualityDecider.DetectQuality($"{incomingName} {request.SourcePath}") ?? string.Empty;
        var formatted = settings.EpisodeFileFormat
            .Replace("{Series Title}", SanitizeFileName(title), StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", SanitizeFileName(title), StringComparison.OrdinalIgnoreCase)
            .Replace("{season:00}", seasonNumber.ToString("D2"), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", seasonNumber.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{episode:00}", episodeNumber.ToString("D2"), StringComparison.OrdinalIgnoreCase)
            .Replace("{episode}", episodeNumber.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{Episode Title}", episodeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Quality}", quality, StringComparison.OrdinalIgnoreCase);
        return new ResolvedDestinationFileName(
            $"{NamingTemplateRenderer.RenderSegment(formatted, title: null, year: null)}{extension}",
            null);
    }

    [GeneratedRegex(@"(^|[.\-_\s])sample([.\-_\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SampleTokenPattern();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned.Trim();
    }

    private sealed record ResolvedDestinationFileName(string FileName, string? Warning);

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
