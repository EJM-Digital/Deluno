using System.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;

namespace Deluno.Filesystem;

public static class FilesystemEndpointRouteBuilderExtensions
{
    /// <summary>From this many missing files, "These files really are gone" runs in the background (#1070).</summary>
    private const int FilesGoneBackgroundThreshold = 1000;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> FilesGoneRunning = new(StringComparer.OrdinalIgnoreCase);

    // #1170: how long a Windows folder window on the server may sit unanswered
    // before the request gives up, and the one window allowed at a time.
    internal static readonly TimeSpan NativePickerTimeout = TimeSpan.FromSeconds(90);
    private static int nativePickerOpen;

    /// <summary>
    /// Whether the browser asking is on the server's own machine (#1170): the
    /// connection comes from the loopback address or from the machine's own
    /// address, and was not forwarded by a proxy. A Windows folder window opens
    /// on the server's screen, so it is of use to nobody else.
    /// </summary>
    public static bool IsFromThisMachine(System.Net.IPAddress? remote, System.Net.IPAddress? local, IHeaderDictionary headers)
    {
        if (headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("Forwarded") || headers.ContainsKey("X-Real-IP"))
        {
            return false;
        }

        // No address at all is an in-process caller (a test host), never a network one.
        return remote is null
            || System.Net.IPAddress.IsLoopback(remote)
            || (local is not null && remote.Equals(local));
    }

    public static async Task<IResult> PickNativeFolderAsync(HttpContext httpContext, NativeFolderPickerRequest request, CancellationToken cancellationToken)
    {
        if (!IsFromThisMachine(httpContext.Connection.RemoteIpAddress, httpContext.Connection.LocalIpAddress, httpContext.Request.Headers))
        {
            return Results.Json(
                new
                {
                    message = "The Windows folder window opens on the computer Deluno runs on. Use the folder browser instead.",
                    nativePickerUnavailable = true
                },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (Interlocked.CompareExchange(ref nativePickerOpen, 1, 0) != 0)
        {
            return Results.Conflict(new
            {
                message = "A folder window is already open on the server. Answer it there, or use the folder browser.",
                nativePickerUnavailable = true
            });
        }

        var pick = WindowsFolderPicker.PickAsync(request.InitialPath);
        _ = pick.ContinueWith(_ => Interlocked.Exchange(ref nativePickerOpen, 0), TaskScheduler.Default);

        WindowsFolderPickerResult result;
        try
        {
            result = await pick.WaitAsync(NativePickerTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return Results.Json(
                new
                {
                    message = "Nobody answered the folder window on the server in time. Use the folder browser instead.",
                    nativePickerUnavailable = true
                },
                statusCode: StatusCodes.Status408RequestTimeout);
        }

        if (!result.Available)
        {
            return Results.Conflict(new
            {
                message = result.Message ?? "The native folder picker is unavailable.",
                nativePickerUnavailable = true
            });
        }

        return Results.Ok(new NativeFolderPickerResponse(result.Path, result.Cancelled));
    }

    public static IEndpointRouteBuilder MapDelunoFilesystemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var filesystem = endpoints.MapGroup("/api/filesystem");

        filesystem.MapPost("/native-folder-picker", PickNativeFolderAsync);

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

                var parentPath = DirectoryNavigation.ParentOf(normalizedPath);
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
            IClientHeldFileProbe? clientHeldFiles,
            CancellationToken cancellationToken) =>
        {
            var preview = await importPipeline.PreviewAsync(request, cancellationToken);
            // #1171: a file with no download behind it may still be one a
            // client is seeding. Said here so the person sees it before
            // choosing "Move the file".
            if (clientHeldFiles is not null
                && string.IsNullOrWhiteSpace(request.DispatchId)
                && preview.SourceExists
                && await clientHeldFiles.IsHeldByAnyClientAsync(preview.SourcePath, cancellationToken))
            {
                preview = preview with { ClientHoldsSource = true };
            }

            return Results.Ok(preview);
        });

        // #889 (cleanup sheet E9): a season pack's own leftovers - an extra,
        // sample, or unlisted special that never resolved to a canonical
        // episode - are never deleted on their own. This is the screen
        // action that lets a person confirm the deletion once they have seen
        // what did not match, one file (or several) at a time.
        filesystem.MapPost("/import/season-pack/leftovers/delete", async (
            [FromBody] DeleteSeasonPackLeftoversRequest request,
            ILibrariesRepository librariesRepository,
            IActivityFeedRepository activityFeedRepository,
            Deluno.Platform.IRecycleBinService recycleBinService,
            CancellationToken cancellationToken) =>
        {
            if (request?.Paths is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["paths"] = ["Choose at least one leftover file to delete."]
                });
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            // #1318/#1325: while any library folder overlaps another, nothing is cleaned up at all.
            if (Deluno.Libraries.LibraryFolderLayout.CleanupBlockedReason(libraries) is { } blocked)
            {
                return Results.Conflict(new { message = blocked });
            }

            var downloadRoots = libraries
                .Select(library => library.DownloadsPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path!))
                .ToArray();

            var deleted = new List<string>();
            var refused = new List<string>();
            foreach (var rawPath in request.Paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(rawPath);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    refused.Add(rawPath);
                    continue;
                }

                // Only ever a file already sitting under a configured
                // download path - never an arbitrary path a request happens
                // to name. The same trust boundary the import pipeline
                // itself reads from.
                var withinDownloads = downloadRoots.Any(root =>
                    string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (!withinDownloads)
                {
                    refused.Add(rawPath);
                    continue;
                }

                // #1318: a path that is, holds or sits inside any library root is a person's media.
                if (Deluno.Libraries.LibraryFolderLayout.ReachesALibrary(libraries, fullPath))
                {
                    refused.Add(rawPath);
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    // Already gone - nothing to confirm, and not a refusal.
                    continue;
                }

                // #1318: a leftover leaves through the recycle bin, never off the disk.
                var owner = libraries.FirstOrDefault(library =>
                    !string.IsNullOrWhiteSpace(library.DownloadsPath) &&
                    Deluno.Contracts.SafePath.IsInside(Path.GetFullPath(library.DownloadsPath), fullPath));
                var stored = owner is null
                    ? null
                    : await recycleBinService.StoreDownloadFileAsync(owner, fullPath, cancellationToken);
                if (stored is null)
                {
                    refused.Add(rawPath);
                    continue;
                }

                deleted.Add(fullPath);
            }

            if (deleted.Count > 0)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "filesystem.import.season-pack.leftovers-deleted",
                    $"Moved {deleted.Count} leftover file(s) to the recycle bin from a season pack that never matched a canonical episode: {string.Join(", ", deleted.Select(Path.GetFileName))}.",
                    System.Text.Json.JsonSerializer.Serialize(new { Deleted = deleted, Refused = refused }),
                    null,
                    null,
                    null,
                    cancellationToken);
            }

            return Results.Ok(new { deleted = deleted.Count, refused });
        });

        filesystem.MapPost("/import/scan-folder", async (
            [FromBody] ImportFolderScanRequest request,
            IImportPipelineService importPipeline,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.FolderPath))
            {
                return Results.BadRequest(new { message = "A folder path is required." });
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(request.FolderPath.Trim());
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
            {
                return Results.BadRequest(new { message = "That folder path is not valid." });
            }

            if (!Directory.Exists(fullPath))
            {
                return Results.NotFound(new { message = "That folder does not exist." });
            }

            var files = ManualImportFolderScanner.FindVideoFiles(fullPath);
            var items = new List<ImportFolderScanItem>(files.Count);
            var issues = new List<string>();

            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                var (guessedTitle, guessedYear) = ManualImportFolderScanner.GuessTitleAndYear(fileName);

                try
                {
                    var preview = await importPipeline.PreviewAsync(
                        new ImportPreviewRequest(
                            SourcePath: file,
                            FileName: null,
                            MediaType: request.MediaType,
                            Title: guessedTitle,
                            Year: guessedYear,
                            Genres: null,
                            Tags: null,
                            Studio: null,
                            OriginalLanguage: null,
                            SeriesId: request.SeriesId,
                            LibraryId: request.LibraryId),
                        cancellationToken);

                    items.Add(new ImportFolderScanItem(file, fileName, guessedTitle, guessedYear, guessedTitle is not null, preview));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    issues.Add($"{fileName}: {ex.Message}");
                }
            }

            if (files.Count == 0)
            {
                issues.Add("No supported video files were found in this folder.");
            }

            return Results.Ok(new ImportFolderScanResponse(fullPath, items, issues));
        });

        filesystem.MapPost("/import/execute-batch", async (
            [FromBody] ImportBatchExecuteRequest request,
            IImportPipelineService importPipeline,
            CancellationToken cancellationToken) =>
        {
            if (request?.Items is null)
            {
                return Results.BadRequest(new { message = "Give the list of files to import." });
            }

            var results = new List<ImportBatchExecuteResult>(request.Items.Count);
            var imported = 0;

            foreach (var item in request.Items)
            {
                if (item?.Preview is null)
                {
                    results.Add(new ImportBatchExecuteResult(item?.Preview?.SourcePath ?? "unknown", false, "This row has no preview to import.", null));
                    continue;
                }

                var outcome = await importPipeline.ExecuteAsync(item, cancellationToken);
                if (outcome.Succeeded && outcome.Response is not null)
                {
                    imported++;
                    results.Add(new ImportBatchExecuteResult(item.Preview.SourcePath, true, outcome.Message, outcome.Response));
                }
                else
                {
                    results.Add(new ImportBatchExecuteResult(item.Preview.SourcePath, false, outcome.Message, outcome.Response));
                }
            }

            return Results.Ok(new ImportBatchExecuteResponse(request.Items.Count, imported, results));
        });

        filesystem.MapPost("/import/jobs", async (
            [FromBody] ImportExecuteRequest request,
            IImportPipelineService importPipeline,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            // A body without a preview is a bad request, not a broken Deluno.
            //
            // `Preview` is non-nullable on the record and the JSON binder does
            // not enforce that, so a caller that omits it — or sends the
            // preview's fields flattened onto the top level, which is the
            // natural mistake — bound null and the pipeline dereferenced it.
            // What came back was 500 "An unexpected error occurred", which
            // tells the person Deluno broke when in fact their request did.
            if (request?.Preview is null)
            {
                return Results.Json(
                    new { message = "An import needs a preview. Send the preview object returned by /api/filesystem/import/preview under a \"preview\" property." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

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

            if (preview.NeedsTitle)
            {
                return Results.BadRequest(new
                {
                    message = "Deluno could not tell which show in your library this is. Enter the show's title under \"Help Deluno match it\", then queue the import again.",
                    preview
                });
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
            IClientHeldFileProbe? clientHeldFiles,
            CancellationToken cancellationToken) =>
        {
            var preview = await importPipeline.PreviewAsync(request, cancellationToken);
            // #1171: a file with no download behind it may still be one a
            // client is seeding. Said here so the person sees it before
            // choosing "Move the file".
            if (clientHeldFiles is not null
                && string.IsNullOrWhiteSpace(request.DispatchId)
                && preview.SourceExists
                && await clientHeldFiles.IsHeldByAnyClientAsync(preview.SourcePath, cancellationToken))
            {
                preview = preview with { ClientHoldsSource = true };
            }

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

        // The answer to the "library looks unreachable" note when the files
        // really were deleted (#1070).
        //
        // A big answer (thousands of files) can outlast an HTTP request, so it
        // runs as a background job and answers 202; the note goes away, and
        // Activity gets its one line, when the job is done.
        filesystem.MapPost("/libraries/{libraryId}/files-gone", async (
            string libraryId,
            IFilesystemReconciliationService reconciliationService,
            IServiceScopeFactory scopes,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            var count = await reconciliationService.GetUnreachableNoticeMissingCountAsync(libraryId, cancellationToken);
            if (count is null)
            {
                return Results.NotFound();
            }

            if (count < FilesGoneBackgroundThreshold)
            {
                var result = await reconciliationService.ConfirmFilesGoneAsync(libraryId, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }

            if (!FilesGoneRunning.TryAdd(libraryId, 0))
            {
                return Results.Accepted(null, new LibraryFilesGoneResult(0, 0, 0, false,
                    "Deluno is already marking these files as gone. This note goes away when it is done.", InBackground: true));
            }

            var logger = loggers.CreateLogger("Deluno.Filesystem.FilesGone");
            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<IFilesystemReconciliationService>();
                    try
                    {
                        await service.ConfirmFilesGoneAsync(libraryId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Marking the missing files of library {LibraryId} as gone failed.", libraryId);
                        await scope.ServiceProvider.GetRequiredService<IActivityFeedRepository>().RecordActivityAsync(
                            "library.files-gone-failed",
                            "Marking the missing files as gone stopped because of an error, so some may not have been changed. Choose These files really are gone again to finish.",
                            null, null, "library", libraryId, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Marking the missing files of library {LibraryId} as gone failed.", libraryId);
                }
                finally
                {
                    FilesGoneRunning.TryRemove(libraryId, out _);
                }
            }, CancellationToken.None);
            return Results.Accepted(null, new LibraryFilesGoneResult(0, 0, 0, false,
                $"Deluno is marking about {count} files as gone in the background. This note goes away when it is done, and Activity gets one line.",
                InBackground: true));
        });

        // The manual half of the scheduled file check. DESIGN-007: "everything
        // we have decided as far as a lot of the behaviour can have manual
        // overrides like if a file is missing and the schedule hasn't run, a
        // user can manually trigger a refresh of the library and it should come
        // up as missing and then the user can manually trigger a search".
        //
        // It calls the same service the schedule calls, so the answer cannot
        // depend on which one ran.
        filesystem.MapPost("/file-check", async (
            ILibraryFileCheckService fileCheck,
            CancellationToken cancellationToken) =>
        {
            var result = await fileCheck.RunAsync(cancellationToken);
            return Results.Ok(result);
        });

        // One title's own rescan (#997): a separate, labelled button on the
        // title page, never folded into "Refresh metadata".
        filesystem.MapPost("/rescan-title", async (
            [FromBody] TitleRescanRequest request,
            IFilesystemReconciliationService reconciliationService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.TitleId))
            {
                return Results.BadRequest(new { message = "Choose a title to rescan." });
            }

            var result = await reconciliationService.RescanTitleAsync(request.MediaType ?? "movies", request.TitleId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
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
        var canCreateHere = false;

            // Only reach for the exotic explanations when something exotic is
            // actually in play. A hand-typed local folder that is simply not
            // there was being answered with "check Docker volumes, UNC
            // permissions, mapped drives", which sends the reader looking in
            // four places that have nothing to do with it.
            if (!exists && !parentExists)
            {
                warnings.Add($"Neither this folder nor the one above it is visible to the Deluno process. Check Docker volumes, UNC permissions, mapped drives, or access for {RunAccount.Phrase()}.");
            }

            if (isUncPath)
            {
                warnings.Add($"This is a UNC path. Give {RunAccount.Phrase()} network-share permissions; being able to open the share yourself is not enough.");
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

            // Two different questions, which used to share one answer.
            //
            // `Writable` is about this path: can Deluno put a file in this
            // folder. `ParentWritable` is about the folder above it: could
            // Deluno create this one. The probe only ever ran against the
            // parent, so a folder that did not exist came back writable - a
            // green chip directly under "That folder does not exist yet".
            if (isDirectory)
            {
                canWrite = TryProbeWrite(fullPath, "Write check failed", warnings);
            }

            if (parentExists)
            {
                canCreateHere = TryProbeWrite(parent!, "Parent write check failed", warnings);
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
                ParentWritable: canCreateHere,
                IsUncPath: isUncPath,
                IsLikelyDockerPath: isLikelyDockerPath,
                Message: DescribePath(exists, isDirectory, isFile, parentExists, canRead, canWrite, canCreateHere),
                Warnings: warnings);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PathDiagnosticResponse(path, path, null, false, false, false, false, false, false, false, false, false, "That path could not be read.", [exception.Message]);
        }
    }

    /// <summary>
    /// Whether Deluno can actually write into a directory, proven by writing.
    /// </summary>
    private static bool TryProbeWrite(string directory, string warningPrefix, List<string> warnings)
    {
        var probePath = Path.Combine(directory, $".deluno-write-test-{Guid.CreateVersion7():N}.tmp");
        try
        {
            File.WriteAllText(probePath, "deluno");
            File.Delete(probePath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            warnings.Add($"{warningPrefix}: {exception.Message}");
            return false;
        }
    }

    /// <summary>One sentence telling the reader where they stand.</summary>
    private static string DescribePath(
        bool exists,
        bool isDirectory,
        bool isFile,
        bool parentExists,
        bool readable,
        bool writable,
        bool parentWritable)
    {
        if (!exists)
        {
            if (!parentExists)
            {
                return "That path does not exist, and neither does the folder above it.";
            }

            return parentWritable
                ? "That folder does not exist yet, but Deluno could create it here."
                : "That folder does not exist yet, and Deluno cannot write to the folder above it.";
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
                    Description: $"Template for NAS or SMB shares visible to {RunAccount.Phrase()}"),
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
    IReadOnlyList<DirectoryBrowseEntry> Entries,
    bool Windows = false)
{
    /// <summary>Whether the server is Windows, so the folder browser offers that platform's example paths only.</summary>
    public bool Windows { get; init; } = Windows || OperatingSystem.IsWindows();
}

public sealed record DirectoryBrowseEntry(
    string Name,
    string Path,
    string Kind,
    string? Description);

public sealed record PathDiagnosticRequest(string? Path);

public sealed record NativeFolderPickerRequest(string? InitialPath);

public sealed record NativeFolderPickerResponse(string? Path, bool Cancelled);

/// <summary>
/// The exact leftover paths a person confirmed for deletion, read straight
/// back from an <see cref="ImportPackSkippedFile"/> the pack's own import
/// response already named (#889).
/// </summary>
public sealed record DeleteSeasonPackLeftoversRequest(IReadOnlyList<string> Paths);

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
    /// <summary>
    /// Whether Deluno could create this path, which is a question about the
    /// folder above it rather than about this one. Kept separate from
    /// <see cref="Writable"/> because reporting both under one name is what
    /// made a missing folder look writable.
    /// </summary>
    bool ParentWritable,
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
    string? NumberingScheme = null,
    /// <summary>
    /// The library the release was grabbed for (#526). An automated import goes
    /// there, not into the first library of its media type. A destination rule
    /// still wins; a manual import without it keeps choosing as before.
    /// </summary>
    string? LibraryId = null,
    /// <summary>
    /// The grab behind the file, so a preview names it as the import will: as
    /// the title grabbed, not the release (#617).
    /// </summary>
    string? DispatchId = null,
    /// <summary>
    /// The title's own original-language title, from the catalogue (#760),
    /// for the {Original Title} naming token — never re-derived from the
    /// release name, which usually only carries the library's own title.
    /// </summary>
    string? OriginalTitle = null);

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
    ImportPackPreview? Pack = null,
    /// <summary>
    /// The title in the library this file was matched to (#1171), so the
    /// preview can say whose folder and episode name it is using.
    /// </summary>
    string? MatchedTitle = null,
    /// <summary>
    /// True when a TV file matched nothing in the library and nobody gave a
    /// title either, so there is no honest destination to show: the person
    /// is asked to pick the title instead (#1171).
    /// </summary>
    bool NeedsTitle = false,
    /// <summary>
    /// True when a connected download client still holds this file (#1171).
    /// Importing it copies; the original stays where the client expects it.
    /// </summary>
    bool ClientHoldsSource = false);

public sealed record ImportPackPreview(
    bool CanExecute,
    bool AlreadyCommitted,
    int SourceFileCount,
    int EpisodeCount,
    IReadOnlyList<ImportPackFilePreview> Files,
    IReadOnlyList<string> BlockReasons,
    /// <summary>
    /// Files in the pack folder that do not resolve to a canonical episode -
    /// extras, featurettes, samples, or specials the catalogue does not list
    /// (#652). They are not imported and do not block the rest of the pack.
    /// </summary>
    IReadOnlyList<ImportPackSkippedFile>? SkippedFiles = null);

public sealed record ImportPackFilePreview(
    string SourcePath,
    string DestinationPath,
    long SourceSizeBytes,
    IReadOnlyList<string> EpisodeKeys,
    IReadOnlyList<string> Warnings);

/// <summary>A pack file Deluno left alone, and why (#652).</summary>
public sealed record ImportPackSkippedFile(
    string SourcePath,
    string Reason);

public sealed record ImportExecuteRequest(
    ImportPreviewRequest Preview,
    string? TransferMode,
    bool Overwrite,
    bool AllowCopyFallback,
    bool ForceReplacement = false,
    string? DispatchId = null,
    string? ExpectedExistingPath = null,
    IReadOnlyList<DispatchReplacementTarget>? ReplacementTargets = null,
    /// <summary>
    /// The download a processor was given, when this file is what it handed
    /// back. Deluno compares the two before importing, because every other
    /// check it makes is absolute — a failed remux that yields a valid
    /// three-minute stub of a two-hour film passes all of them (#574).
    /// </summary>
    string? ProcessedFromPath = null,
    /// <summary>
    /// The hand-off this file came back from, so the processor can be told what
    /// became of it and let go of its own copy (#578).
    /// </summary>
    string? HandoffId = null,
    /// <summary>
    /// Set when Deluno is trying a processed file's import again after the first one gave up (#1155): why it is
    /// trying. The retry is one story, so it is told in the one line the import writes at the end, with the steps in
    /// that line's details, and the job's own lines for it stay quiet. It also says the retry has been made, so it
    /// is made only once.
    /// </summary>
    string? RetriedBecause = null);

public sealed record ImportExecuteResponse(
    ImportPreviewResponse Preview,
    bool Executed,
    string TransferModeUsed,
    bool UsedFallback,
    bool CatalogUpdated,
    string Message,
    IReadOnlyList<ImportPackFileResult>? PackFiles = null,
    /// <summary>Pack files left alone because they did not resolve to a canonical episode (#652).</summary>
    IReadOnlyList<ImportPackSkippedFile>? SkippedFiles = null);

public sealed record ImportPackFileResult(
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<string> EpisodeKeys,
    string TransferModeUsed);

public sealed record ImportJobResponse(
    string JobId,
    ImportPreviewResponse Preview,
    Deluno.Jobs.Contracts.JobQueueItem Job);

public sealed record TitleRescanRequest(string? MediaType, string? TitleId);
