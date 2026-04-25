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
    IActivityFeedRepository activityFeedRepository)
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
        return ResolveImportPreview(request, settings, rules);
    }

    public async Task<ImportPipelineResult> ExecuteAsync(ImportExecuteRequest request, CancellationToken cancellationToken)
    {
        var settings = await platformRepository.GetAsync(cancellationToken);
        var rules = await platformRepository.ListDestinationRulesAsync(cancellationToken);
        var preview = ResolveImportPreview(request.Preview, settings, rules);
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

        var requestedMode = NormalizeTransferMode(request.TransferMode);
        var mode = requestedMode == "auto" ? preview.PreferredTransferMode : requestedMode;
        var usedFallback = false;
        Directory.CreateDirectory(preview.DestinationFolder);
        var destinationPreExisted = File.Exists(preview.DestinationPath);
        var backupPath = destinationPreExisted && request.Overwrite
            ? BuildTemporaryPath(preview.DestinationPath, ".deluno-backup")
            : null;

        try
        {
            if (backupPath is not null)
            {
                File.Move(preview.DestinationPath, backupPath, overwrite: true);
            }

            if (mode == "hardlink")
            {
                if (!preview.HardlinkAvailable)
                {
                    if (!request.AllowCopyFallback)
                    {
                        return Failed(
                            StatusCodes.Status400BadRequest,
                            "Hardlinking is not available for these paths. Use copy fallback or choose paths on the same filesystem.");
                    }

                    AtomicCopy(preview.SourcePath, preview.DestinationPath, request.Overwrite);
                    usedFallback = true;
                    mode = "copy";
                }
                else if (!TryCreateHardlink(preview.SourcePath, preview.DestinationPath, out var hardlinkError))
                {
                    if (!request.AllowCopyFallback)
                    {
                        return Failed(StatusCodes.Status400BadRequest, hardlinkError);
                    }

                    AtomicCopy(preview.SourcePath, preview.DestinationPath, request.Overwrite);
                    usedFallback = true;
                    mode = "copy";
                }
            }
            else if (mode == "move")
            {
                File.Move(preview.SourcePath, preview.DestinationPath, overwrite: request.Overwrite);
            }
            else
            {
                AtomicCopy(preview.SourcePath, preview.DestinationPath, request.Overwrite);
                mode = "copy";
            }

            if (backupPath is not null && File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            var libraries = await platformRepository.ListLibrariesAsync(cancellationToken);
            var catalogUpdated = await MarkCatalogImportedAsync(
                request.Preview,
                preview,
                mediaType,
                libraries,
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
                    preview.MatchedRuleName
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

            return new ImportPipelineResult(true, StatusCodes.Status200OK, response, response.Message);
        }
        catch (UnauthorizedAccessException)
        {
            RollBackPartialImport(preview.DestinationPath, backupPath);
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
            RollBackPartialImport(preview.DestinationPath, backupPath);
            await RecordImportFailureAsync(
                request,
                request.Preview,
                "io",
                ioException.Message,
                "Check whether the file is still downloading, locked by another process, or on an unavailable network path.",
                cancellationToken);
            return Failed(StatusCodes.Status400BadRequest, ioException.Message);
        }
        catch
        {
            RollBackPartialImport(preview.DestinationPath, backupPath);
            throw;
        }
    }

    private static ImportPipelineResult Failed(int statusCode, string message)
        => new(false, statusCode, null, message);

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

        if (NormalizeMediaType(request.MediaType) == "tv")
        {
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(title, failureKind, summary, recommendedAction, SerializeRecoveryDetails(executeRequest)),
                cancellationToken);
            return;
        }

        await movieCatalogRepository.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(title, failureKind, summary, recommendedAction, SerializeRecoveryDetails(executeRequest)),
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
        CancellationToken cancellationToken)
    {
        var library = ResolveLibraryForImport(preview.DestinationPath, mediaType, libraries);
        if (library is null)
        {
            return false;
        }

        var quality = LibraryQualityDecider.DetectQuality($"{preview.SourcePath} {preview.DestinationPath}");
        var decision = LibraryQualityDecider.Decide(
            mediaType == "tv" ? "TV show" : "movie",
            hasFile: true,
            currentQuality: quality,
            cutoffQuality: library.CutoffQuality,
            upgradeUntilCutoff: library.UpgradeUntilCutoff,
            upgradeUnknownItems: library.UpgradeUnknownItems);
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
            cancellationToken);
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
        if (destinationExists) warnings.Add("Destination already exists. Import will be blocked unless overwrite is enabled.");
        if (!hardlinkAvailable) warnings.Add("Hardlink is unlikely because source and destination appear to be on different filesystems. Copy fallback may be required.");

        if (Path.GetPathRoot(sourcePath) is { } sourceRoot &&
            Path.GetPathRoot(destinationPath) is { } destinationRoot &&
            !string.Equals(sourceRoot, destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Source root {sourceRoot} differs from destination root {destinationRoot}.");
        }

        return warnings;
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

    private static void RollBackPartialImport(string destinationPath, string? backupPath)
    {
        if (backupPath is null)
        {
            TryDelete(destinationPath);
            return;
        }

        TryDelete(destinationPath);
        if (File.Exists(backupPath))
        {
            File.Move(backupPath, destinationPath, overwrite: true);
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
