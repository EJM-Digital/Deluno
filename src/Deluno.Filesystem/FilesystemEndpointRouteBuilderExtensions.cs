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
            ImportPreviewRequest request,
            IImportPipelineService importPipeline,
            CancellationToken cancellationToken) =>
        {
            var preview = await importPipeline.PreviewAsync(request, cancellationToken);
            return Results.Ok(preview);
        });

        filesystem.MapPost("/import/execute", async (
            ImportExecuteRequest request,
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
            ImportExecuteRequest request,
            IImportPipelineService importPipeline,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var preview = await importPipeline.PreviewAsync(request.Preview, cancellationToken);
            if (!preview.SourceExists ||
                !preview.IsSupportedMediaFile ||
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
            ImportPreviewRequest request,
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
            FilesystemReconciliationRepairRequest request,
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
            var warnings = new List<string>();
            var canRead = false;
            var canWrite = false;

            if (!isDirectory && !isFile)
            {
                warnings.Add("Path is not visible to the Deluno process. Check Docker volumes, UNC permissions, mapped drives, or service account access.");
            }

            if (OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                warnings.Add("This is a UNC path. Ensure the Deluno service account has network-share permissions, not just your interactive Windows user.");
            }

            if (IsLikelyDockerPath(fullPath))
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
                FullPath: fullPath,
                Root: root,
                Exists: isDirectory || isFile,
                IsDirectory: isDirectory,
                IsFile: isFile,
                ParentExists: parentExists,
                CanRead: canRead,
                CanWriteToParent: canWrite,
                Warnings: warnings);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PathDiagnosticResponse(path, path, null, false, false, false, false, false, false, [exception.Message]);
        }
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

public sealed record PathDiagnosticResponse(
    string Path,
    string FullPath,
    string? Root,
    bool Exists,
    bool IsDirectory,
    bool IsFile,
    bool ParentExists,
    bool CanRead,
    bool CanWriteToParent,
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
    string? OriginalLanguage);

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
    IReadOnlyList<string> DecisionSteps);

public sealed record ImportExecuteRequest(
    ImportPreviewRequest Preview,
    string? TransferMode,
    bool Overwrite,
    bool AllowCopyFallback,
    bool ForceReplacement = false,
    string? DispatchId = null);

public sealed record ImportExecuteResponse(
    ImportPreviewResponse Preview,
    bool Executed,
    string TransferModeUsed,
    bool UsedFallback,
    bool CatalogUpdated,
    string Message);

public sealed record ImportJobResponse(
    string JobId,
    ImportPreviewResponse Preview,
    Deluno.Jobs.Contracts.JobQueueItem Job);
