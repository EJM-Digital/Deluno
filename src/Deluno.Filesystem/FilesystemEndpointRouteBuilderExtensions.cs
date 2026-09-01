using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;

namespace Deluno.Filesystem;

public static class FilesystemEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoFilesystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var filesystem = endpoints.MapGroup("/api/filesystem");

        filesystem.MapPost("/native-folder-picker", async ([FromBody] NativeFolderPickerRequest request) =>
        {
            var result = await WindowsFolderPicker.PickAsync(request.InitialPath);
            if (!result.Available)
            {
                return Results.Conflict(new
                {
                    message = result.Message ?? "The native folder picker is unavailable.",
                    nativePickerUnavailable = true
                });
            }

            return Results.Ok(new NativeFolderPickerResponse(result.Path, result.Cancelled));
        });

        filesystem.MapGet("/directories", (string? path) =>
        {
            try
            {
                var normalizedPath = string.IsNullOrWhiteSpace(path)
                    ? null
                    : NormalizePath(path);

                if (normalizedPath is null)
                {
                    return Results.Ok(new DirectoryBrowseResponse(
                        CurrentPath: null,
                        ParentPath: null,
                        Entries: ListRootEntries()));
                }

                if (!Directory.Exists(normalizedPath))
                {
                    return Results.NotFound(new
                    {
                        message = "The requested directory does not exist."
                    });
                }

                var parentPath = Directory.GetParent(normalizedPath)?.FullName;
                var entries = Directory
                    .EnumerateDirectories(normalizedPath)
                    .Select(directory => new DirectoryBrowseEntry(
                        Name: Path.GetFileName(directory),
                        Path: directory,
                        Kind: "directory",
                        Description: null))
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return Results.Ok(new DirectoryBrowseResponse(
                    CurrentPath: normalizedPath,
                    ParentPath: parentPath,
                    Entries: entries));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (IOException ioException)
            {
                return Results.BadRequest(new
                {
                    message = ioException.Message
                });
            }
        });

        filesystem.MapPost("/import/preview", async (
            [FromBody] ImportPreviewRequest request,
            IImportPipelineService importPipeline,
            CancellationToken cancellationToken) =>
        {
            var preview = await importPipeline.PreviewAsync(request, cancellationToken);
            return Results.Ok(preview);
        });

        filesystem.MapPost("/import/execute", async (
            [FromBody] ImportExecuteRequest request,
            IImportPipelineService importPipeline,
            CancellationToken cancellationToken) =>
        {
            var result = await importPipeline.ExecuteAsync(request, cancellationToken);
            if (result.Succeeded && result.Response is not null)
            {
                return Results.Ok(result.Response);
            }

            return Results.Json(
                new { message = result.Message },
                statusCode: result.StatusCode);
        });

        filesystem.MapPost("/import/jobs", async (
            [FromBody] ImportExecuteRequest request,
            IImportPipelineService importPipeline,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var preview = await importPipeline.PreviewAsync(request.Preview, cancellationToken);
            if (preview.SourceExists && !ImportFileReadiness.IsPreviewReady(preview))
            {
                return Results.Json(
                    new
                    {
                        message = "The source file is still being written or is locked by another process. Deluno will wait until it is stable before queuing the import.",
                        preview
                    },
                    statusCode: ImportFileReadiness.RetryableStatusCode);
            }

            if (!preview.SourceExists ||
                !preview.IsSupportedMediaFile ||
                preview.Pack is { CanExecute: false } ||
                IsSamePath(preview.SourcePath, preview.DestinationPath) ||
                preview.MediaProbe is { Status: "failed" } ||
                preview.MediaProbe is { Status: "succeeded", VideoStreams.Count: 0 } ||
                preview.MediaProbe?.DurationSeconds is > 0 and < 120 ||
                preview.DestinationExists && !request.Overwrite)
            {
                return Results.BadRequest(new
                {
                    message = "Import cannot be queued until the preview is valid.",
                    preview
                });
            }

            var job = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "filesystem",
                    PayloadJson: System.Text.Json.JsonSerializer.Serialize(request),
                    RelatedEntityType: NormalizeMediaType(request.Preview.MediaType) == "tv" ? "series" : "movie",
                    RelatedEntityId: null),
                cancellationToken);

            return Results.Ok(new ImportJobResponse(job.Id, preview, job));
        });

        endpoints.MapPost("/api/integrations/external/import-preview", async (
            [FromBody] ImportPreviewRequest request,
            IImportPipelineService importPipeline,
            CancellationToken cancellationToken) =>
        {
            var preview = await importPipeline.PreviewAsync(request, cancellationToken);
            return Results.Ok(preview);
        });

        filesystem.MapPost("/path-diagnostics", (PathDiagnosticRequest request) =>
        {
            var path = request.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest(new { message = "Path is required." });
            }

            return Results.Ok(BuildPathDiagnostic(path));
        });

        filesystem.MapGet("/reconciliation", async (
            IFilesystemReconciliationService reconciliationService,
            CancellationToken cancellationToken) =>
        {
            var report = await reconciliationService.ScanAsync(cancellationToken);
            return Results.Ok(report);
        });

        filesystem.MapPost("/reconciliation/repair", async (
            [FromBody] FilesystemReconciliationRepairRequest request,
            IFilesystemReconciliationService reconciliationService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.IssueId) || string.IsNullOrWhiteSpace(request.Action))
            {
                return Results.BadRequest(new { message = "Issue id and repair action are required." });
            }

            var result = await reconciliationService.RepairAsync(request, cancellationToken);
            return result.Repaired ? Results.Ok(result) : Results.Conflict(result);
        });

        return endpoints;
    }

    private static PathDiagnosticResponse BuildPathDiagnostic(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var isDirectory = Directory.Exists(fullPath);
            var isFile = File.Exists(fullPath);
            var parent = isDirectory ? fullPath : Path.GetDirectoryName(fullPath);
            var parentExists = !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent);
            var root = Path.GetPathRoot(fullPath);
            var exists = isDirectory || isFile;
            var isUncPath = OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal);
            var isLikelyDockerPath = IsLikelyDockerPath(fullPath);
            var warnings = new List<string>();
            var canRead = false;
            var canWrite = false;

            // Only reach for the exotic explanations when something exotic is
            // actually in play. A hand-typed local folder that is simply not
            // there was being answered with "check Docker volumes, UNC
            // permissions, mapped drives", which sends the reader looking in
            // four places that have nothing to do with it.
            if (!exists && !parentExists)
            {
                warnings.Add("Neither this folder nor the one above it is visible to the Deluno process. Check Docker volumes, UNC permissions, mapped drives, or service account access.");
            }

            if (isUncPath)
            {
                warnings.Add("This is a UNC path. Ensure the Deluno service account has network-share permissions, not just your interactive Windows user.");
            }

            if (isLikelyDockerPath)
            {
                warnings.Add("This looks like a container path. Make sure the host path is mounted into the Deluno container with the same internal path.");
            }

            try
            {
                if (isFile)
                {
                    using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    canRead = stream.CanRead;
                }
                else if (isDirectory)
                {
                    Directory.EnumerateFileSystemEntries(fullPath).Take(1).ToArray();
                    canRead = true;
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                warnings.Add($"Read check failed: {exception.Message}");
            }

            if (parentExists)
            {
                var probePath = Path.Combine(parent!, $".deluno-write-test-{Guid.CreateVersion7():N}.tmp");
                try
                {
                    File.WriteAllText(probePath, "deluno");
                    File.Delete(probePath);
                    canWrite = true;
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    warnings.Add($"Write check failed: {exception.Message}");
                }
            }

            return new PathDiagnosticResponse(
                Path: path,
                NormalizedPath: fullPath,
                Root: root,
                Exists: exists,
                IsDirectory: isDirectory,
                IsFile: isFile,
                ParentExists: parentExists,
                Readable: canRead,
                Writable: canWrite,
                IsUncPath: isUncPath,
                IsLikelyDockerPath: isLikelyDockerPath,
                Message: DescribePath(exists, isDirectory, isFile, parentExists, canRead, canWrite),
                Warnings: warnings);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PathDiagnosticResponse(path, path, null, false, false, false, false, false, false, false, false, "That path could not be read.", [exception.Message]);
        }
    }

    /// <summary>One sentence telling the reader where they stand.</summary>
    private static string DescribePath(bool exists, bool isDirectory, bool isFile, bool parentExists, bool readable, bool writable)
    {
        if (!exists)
        {
            return parentExists
                ? "That folder does not exist yet."
                : "That path does not exist, and neither does the folder above it.";
        }

        if (isFile)
        {
            return "That is a file, not a folder.";
        }

        if (!isDirectory)
        {
            return "That path exists but is not a folder.";
        }

        if (!readable)
        {
            return "Deluno can see this folder but cannot read it.";
        }

        return writable
            ? "Deluno can read and write this folder."
            : "Deluno can read this folder but cannot write to it, so imports would fail.";
    }

    private static bool IsLikelyDockerPath(string path)
        => path.StartsWith("/downloads", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/media", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/data", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/mnt", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/config", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);

        if (OperatingSystem.IsWindows())
        {
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        return fullPath;
    }

    private static IReadOnlyList<DirectoryBrowseEntry> ListRootEntries()
    {
        if (OperatingSystem.IsWindows())
        {
            var drives = DriveInfo
                .GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive =>
                {
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? drive.Name
                        : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";

                    var description = drive.DriveType switch
                    {
                        DriveType.Fixed => "Local drive",
                        DriveType.Removable => "External drive",
                        DriveType.Network => "Network drive",
                        DriveType.CDRom => "Optical drive",
                        _ => drive.DriveType.ToString()
                    };

                    return new DirectoryBrowseEntry(
                        Name: label,
                        Path: drive.RootDirectory.FullName,
                        Kind: "root",
                        Description: description);
                })
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return drives
                .Concat(ListSuggestedEntries(windows: true))
                .ToArray();
        }

        return
        [
            new DirectoryBrowseEntry(
                Name: "/",
                Path: "/",
                Kind: "root",
                Description: "Filesystem root"),
            .. ListSuggestedEntries(windows: false)
        ];
    }

    private static IReadOnlyList<DirectoryBrowseEntry> ListSuggestedEntries(bool windows)
    {
        if (windows)
        {
            return
            [
                new DirectoryBrowseEntry(
                    Name: "UNC network share",
                    Path: @"\\server\share\media",
                    Kind: "preset",
                    Description: "Template for NAS or SMB shares visible to the Deluno service account"),
                new DirectoryBrowseEntry(
                    Name: "Mapped media drive",
                    Path: @"Z:\",
                    Kind: "preset",
                    Description: "Common mapped-drive location for media libraries"),
                new DirectoryBrowseEntry(
                    Name: "Downloads drive",
                    Path: @"D:\Downloads",
                    Kind: "preset",
                    Description: "Common Windows download staging location")
            ];
        }

        return
        [
            new DirectoryBrowseEntry(
                Name: "Docker downloads",
                Path: "/downloads",
                Kind: "preset",
                Description: "Common container path for completed downloads"),
            new DirectoryBrowseEntry(
                Name: "Docker media",
                Path: "/media",
                Kind: "preset",
                Description: "Common container path for mounted libraries"),
            new DirectoryBrowseEntry(
                Name: "Data volume",
                Path: "/data",
                Kind: "preset",
                Description: "Common Docker volume root for media stacks"),
            new DirectoryBrowseEntry(
                Name: "Mounted storage",
                Path: "/mnt",
                Kind: "preset",
                Description: "Linux mount point for local, NAS, or external storage")
        ];
    }

    private static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows" ? "tv" : "movies";

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
}

public sealed record DirectoryBrowseResponse(
    string? CurrentPath,
    string? ParentPath,
    IReadOnlyList<DirectoryBrowseEntry> Entries);

public sealed record DirectoryBrowseEntry(
    string Name,
    string Path,
    string Kind,
    string? Description);

public sealed record PathDiagnosticRequest(string? Path);

public sealed record NativeFolderPickerRequest(string? InitialPath);

public sealed record NativeFolderPickerResponse(string? Path, bool Cancelled);

/// <summary>
/// What Deluno can actually do with a path.
///
/// The names here are the ones the folder check has always rendered. The server
/// used to answer with <c>canRead</c>, <c>canWriteToParent</c> and <c>fullPath</c>
/// and no message at all, so Readable and Writable were never lit for any path,
/// good or bad, and a healthy folder reported a warning with nothing written
/// under it. Nothing failed loudly because the two halves of the contract were
/// only ever compared by hand.
/// </summary>
public sealed record PathDiagnosticResponse(
    string Path,
    string NormalizedPath,
    string? Root,
    bool Exists,
    bool IsDirectory,
    bool IsFile,
    bool ParentExists,
    bool Readable,
    bool Writable,
    bool IsUncPath,
    bool IsLikelyDockerPath,
    string Message,
    IReadOnlyList<string> Warnings);

public sealed record ImportPreviewRequest(
    string SourcePath,
    string? FileName,
    string? MediaType,
    string? Title,
    int? Year,
    IReadOnlyList<string>? Genres,
    IReadOnlyList<string>? Tags,
    string? Studio,
    string? OriginalLanguage,
    string? ImdbId = null,
    string? TvDbId = null,
    string? Network = null,
    string? QualityProfile = null,
    /// <summary>
    /// The catalogue id that owns this TV import. When present, Deluno uses
    /// the persisted series numbering map to turn AirDate, Absolute, and Scene
    /// numbers into canonical episode identities before renaming or importing.
    /// An omitted id keeps older/manual requests backward compatible and makes
    /// alternate-number imports review-only rather than guessed.
    /// </summary>
    string? SeriesId = null,
    string? SeriesType = null,
    string? NumberingScheme = null);

public sealed record ImportPreviewResponse(
    string SourcePath,
    string DestinationFolder,
    string DestinationPath,
    string PreferredTransferMode,
    bool HardlinkAvailable,
    string? MatchedRuleId,
    string? MatchedRuleName,
    bool SourceExists,
    bool DestinationExists,
    long SourceSizeBytes,
    long DestinationSizeBytes,
    bool IsSupportedMediaFile,
    MediaProbeInfo? MediaProbe,
    string TransferExplanation,
    IReadOnlyList<string> Warnings,
    string Explanation,
    IReadOnlyList<string> DecisionSteps,
    ImportPackPreview? Pack = null);

public sealed record ImportPackPreview(
    bool CanExecute,
    bool AlreadyCommitted,
    int SourceFileCount,
    int EpisodeCount,
    IReadOnlyList<ImportPackFilePreview> Files,
    IReadOnlyList<string> BlockReasons);

public sealed record ImportPackFilePreview(
    string SourcePath,
    string DestinationPath,
    long SourceSizeBytes,
    IReadOnlyList<string> EpisodeKeys,
    IReadOnlyList<string> Warnings);

public sealed record ImportExecuteRequest(
    ImportPreviewRequest Preview,
    string? TransferMode,
    bool Overwrite,
    bool AllowCopyFallback,
    bool ForceReplacement = false,
    string? DispatchId = null,
    string? ExpectedExistingPath = null,
    IReadOnlyList<DispatchReplacementTarget>? ReplacementTargets = null);

public sealed record ImportExecuteResponse(
    ImportPreviewResponse Preview,
    bool Executed,
    string TransferModeUsed,
    bool UsedFallback,
    bool CatalogUpdated,
    string Message,
    IReadOnlyList<ImportPackFileResult>? PackFiles = null);

public sealed record ImportPackFileResult(
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<string> EpisodeKeys,
    string TransferModeUsed);

public sealed record ImportJobResponse(
    string JobId,
    ImportPreviewResponse Preview,
    Deluno.Jobs.Contracts.JobQueueItem Job);
