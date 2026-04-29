using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Http;

namespace Deluno.Filesystem;

public sealed class ImportPipelineService(
    IPlatformSettingsRepository platformRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    IMediaProbeService mediaProbeService,
    IMediaDecisionService mediaDecisionService)
    : IImportPipelineService
{
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    public async Task<ImportPreviewResponse> PreviewAsync(ImportPreviewRequest request, CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        var rules = await platformRepository.ListDestinationRulesAsync(cancellationToken);
        var preview = ResolveImportPreview(request, settings, rules);
        return await EnrichPreviewWithMediaProbeAsync(preview, cancellationToken);
    }

    public async Task<ImportPipelineResult> ExecuteAsync(ImportExecuteRequest request, CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        var rules = await platformRepository.ListDestinationRulesAsync(cancellationToken);
        var preview = await EnrichPreviewWithMediaProbeAsync(ResolveImportPreview(request.Preview, settings, rules), cancellationToken);
        var mediaType = NormalizeMediaType(request.Preview.MediaType);
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
            const string message = "The source file does not exist from the Deluno service account's filesystem view.";
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

        if (preview.MediaProbe is { Status: "failed" })
        {
            var message = preview.MediaProbe.Message ?? "Media probing failed. Deluno cannot confirm this file is playable.";
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "mediaProbeFailed",
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

            var libraries = await platformRepository.ListLibrariesAsync(cancellationToken);
            var catalogUpdated = await MarkCatalogImportedAsync(
                request.Preview,
                preview,
                mediaType,
                libraries,
                settings.UnmonitorWhenCutoffMet,
                cancellationToken);

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
                    catalogUpdated,
                    preview.MatchedRuleId,
                    preview.MatchedRuleName,
                    MediaProbe = preview.MediaProbe
                }),
                null,
                mediaType == "tv" ? "series" : "movie",
                null,
                cancellationToken);

            var response = new ImportExecuteResponse(
                Preview: preview,
                Executed: true,
                TransferModeUsed: mode,
                UsedFallback: usedFallback,
                CatalogUpdated: catalogUpdated,
                Message: usedFallback
                    ? "Import completed with copy fallback because hardlink creation was not possible."
                    : $"Import completed using {mode}.");

            restoreSourceOnFailure = false;
            if (backupPath is not null)
            {
                TryDelete(backupPath);
            }

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

    private static ImportPreviewResponse ResolveImportPreview(
        ImportPreviewRequest request,
        PlatformSettingsSnapshot settings,
        IReadOnlyList<DestinationRuleItem> rules)
    {
        var mediaType = NormalizeMediaType(request.MediaType);
        var title = TitleForActivity(request);
        var rule = rules
            .Where(item => item.IsEnabled && NormalizeMediaType(item.MediaType) == mediaType)
            .OrderBy(item => item.Priority)
            .FirstOrDefault(item => MatchesRule(item, request));
        var rootPath = rule?.RootPath ??
                       (mediaType == "tv" ? settings.SeriesRootPath : settings.MovieRootPath) ??
                       string.Empty;
        var template = rule?.FolderTemplate ??
                       (mediaType == "tv" ? settings.SeriesFolderFormat : settings.MovieFolderFormat);
        var folder = ApplyTemplate(template, title, request.Year);
        var destinationFolder = string.IsNullOrWhiteSpace(rootPath) ? folder : Path.Combine(rootPath, folder);
        var fileName = string.IsNullOrWhiteSpace(request.FileName)
            ? Path.GetFileName(request.SourcePath)
            : request.FileName.Trim();
        var destinationPath = Path.Combine(destinationFolder, SanitizeFileName(fileName));
        var canHardlink = CanLikelyHardlink(request.SourcePath, destinationPath);
        var sourceExists = File.Exists(request.SourcePath);
        var destinationExists = File.Exists(destinationPath);
        var sourceSize = sourceExists ? new FileInfo(request.SourcePath).Length : 0;
        var destinationSize = destinationExists ? new FileInfo(destinationPath).Length : 0;
        var isSupportedMediaFile = SupportedVideoExtensions.Contains(Path.GetExtension(destinationPath));
        var warnings = BuildImportWarnings(request.SourcePath, destinationPath, sourceExists, destinationExists, canHardlink, isSupportedMediaFile);
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

    private async Task<bool> MarkCatalogImportedAsync(
        ImportPreviewRequest request,
        ImportPreviewResponse preview,
        string mediaType,
        IReadOnlyList<LibraryItem> libraries,
        bool unmonitorWhenCutoffMet,
        CancellationToken cancellationToken)
    {
        var library = ResolveLibraryForImport(preview.DestinationPath, mediaType, libraries);
        if (library is null)
        {
            return false;
        }

        var quality = mediaDecisionService.DetectQuality($"{preview.SourcePath} {preview.DestinationPath}");
        var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: mediaType,
            HasFile: true,
            CurrentQuality: quality,
            CutoffQuality: library.CutoffQuality,
            UpgradeUntilCutoff: library.UpgradeUntilCutoff,
            UpgradeUnknownItems: library.UpgradeUnknownItems));
        var title = TitleForActivity(request);

        if (mediaType == "tv")
        {
            return await seriesCatalogRepository.ImportExistingAsync(
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
                GetFileSize(preview.DestinationPath),
                null,
                cancellationToken);
        }

        return await movieCatalogRepository.ImportExistingAsync(
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
            GetFileSize(preview.DestinationPath),
            cancellationToken);
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

    private static string ApplyTemplate(string? template, string title, int? year)
    {
        var safeTitle = SanitizeFileName(title);
        var safeYear = year?.ToString() ?? "Unknown Year";
        return SanitizeFileName((string.IsNullOrWhiteSpace(template) ? "{Title} ({Year})" : template)
            .Replace("{Movie Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Series Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Release Year}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{Series Year}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", safeYear, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned.Trim();
    }

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
