using System.Text;
using System.Text.Json;
using Deluno.Jobs.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.WebUtilities;

namespace Deluno.Filesystem;

public sealed class FilesystemReconciliationService(
    IPlatformSettingsRepository platformRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider)
    : IFilesystemReconciliationService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts"
    };

    private static readonly string[] ArtifactMarkers =
    [
        ".deluno-stage-",
        ".deluno-copy-",
        ".deluno-backup-"
    ];

    public async Task<FilesystemReconciliationReport> ScanAsync(CancellationToken cancellationToken)
    {
        var libraries = await platformRepository.ListLibrariesAsync(cancellationToken);
        var issues = new List<FilesystemReconciliationIssue>();

        foreach (var library in libraries.Where(item => !string.IsNullOrWhiteSpace(item.RootPath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootPath = Path.GetFullPath(library.RootPath);
            var tracked = await ListTrackedFilesAsync(library, cancellationToken);
            issues.AddRange(FindMissingTrackedFiles(library, tracked));
            issues.AddRange(FindOrphanFiles(library, rootPath, tracked));
            issues.AddRange(FindPartialImportArtifacts(library, rootPath));
        }

        return new FilesystemReconciliationReport(
            ScannedUtc: timeProvider.GetUtcNow(),
            LibraryCount: libraries.Count,
            IssueCount: issues.Count,
            Issues: issues
                .OrderByDescending(item => item.Severity == "critical")
                .ThenBy(item => item.LibraryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public async Task<FilesystemReconciliationRepairResult> RepairAsync(
        FilesystemReconciliationRepairRequest request,
        CancellationToken cancellationToken)
    {
        var token = DecodeIssueToken(request.IssueId);
        var action = NormalizeAction(request.Action);
        var libraries = await platformRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => string.Equals(item.Id, token.LibraryId, StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            return new FilesystemReconciliationRepairResult(false, action, "The library referenced by this reconciliation issue no longer exists.");
        }

        var fullPath = Path.GetFullPath(token.Path);
        if (!IsInsideRoot(fullPath, library.RootPath))
        {
            return new FilesystemReconciliationRepairResult(false, action, "Repair refused because the path is outside the library root.");
        }

        if (token.Kind == "missingTrackedFile" && action == "mark-missing")
        {
            var repaired = token.MediaType == "tv"
                ? await seriesCatalogRepository.MarkTrackedFileMissingAsync(
                    token.EntityId,
                    token.EpisodeId,
                    token.LibraryId,
                    token.Path,
                    cancellationToken)
                : await movieCatalogRepository.MarkTrackedFileMissingAsync(
                    token.EntityId,
                    token.LibraryId,
                    token.Path,
                    cancellationToken);

            await RecordRepairAsync(token, action, repaired, cancellationToken);
            return new FilesystemReconciliationRepairResult(
                repaired,
                action,
                repaired
                    ? "The tracked item was safely marked missing. Deluno did not delete any files."
                    : "No matching tracked item was updated.");
        }

        if (token.Kind == "partialImportArtifact" && action == "cleanup-artifact")
        {
            if (!IsDelunoArtifact(fullPath))
            {
                return new FilesystemReconciliationRepairResult(false, action, "Cleanup refused because the file is not a Deluno staging artifact.");
            }

            var deleted = TryDelete(fullPath);
            await RecordRepairAsync(token, action, deleted, cancellationToken);
            return new FilesystemReconciliationRepairResult(
                deleted,
                action,
                deleted
                    ? "The Deluno staging artifact was deleted."
                    : "The staging artifact could not be deleted. Check permissions or file locks.");
        }

        if (token.Kind == "orphanFile" && action == "queue-import-review")
        {
            await AddImportReviewCaseAsync(token, fullPath, cancellationToken);
            await RecordRepairAsync(token, action, repaired: true, cancellationToken);
            return new FilesystemReconciliationRepairResult(
                true,
                action,
                "A recovery case was created so the file can be reviewed and imported intentionally.");
        }

        return new FilesystemReconciliationRepairResult(false, action, "This repair action is not supported for the selected reconciliation issue.");
    }

    private async Task<IReadOnlyList<TrackedFile>> ListTrackedFilesAsync(
        LibraryItem library,
        CancellationToken cancellationToken)
    {
        if (NormalizeMediaType(library.MediaType) == "tv")
        {
            var tracked = await seriesCatalogRepository.ListTrackedFilesAsync(library.Id, cancellationToken);
            return tracked.Select(item => new TrackedFile(
                MediaType: "tv",
                LibraryId: item.LibraryId,
                EntityId: item.SeriesId,
                EpisodeId: item.EpisodeId,
                Title: item.EpisodeId is null
                    ? item.Title
                    : $"{item.Title} S{item.SeasonNumber:00}E{item.EpisodeNumber:00}",
                Path: item.FilePath,
                FileSizeBytes: item.FileSizeBytes)).ToArray();
        }

        var movies = await movieCatalogRepository.ListTrackedFilesAsync(library.Id, cancellationToken);
        return movies.Select(item => new TrackedFile(
            MediaType: "movies",
            LibraryId: item.LibraryId,
            EntityId: item.MovieId,
            EpisodeId: null,
            Title: item.ReleaseYear is null ? item.Title : $"{item.Title} ({item.ReleaseYear})",
            Path: item.FilePath,
            FileSizeBytes: item.FileSizeBytes)).ToArray();
    }

    private static IEnumerable<FilesystemReconciliationIssue> FindMissingTrackedFiles(
        LibraryItem library,
        IReadOnlyList<TrackedFile> tracked)
    {
        foreach (var item in tracked)
        {
            if (File.Exists(item.Path))
            {
                continue;
            }

            yield return new FilesystemReconciliationIssue(
                Id: EncodeIssueToken("missingTrackedFile", item.MediaType, library.Id, item.EntityId, item.EpisodeId, item.Path),
                Kind: "missingTrackedFile",
                Severity: "critical",
                MediaType: item.MediaType,
                LibraryId: library.Id,
                LibraryName: library.Name,
                Path: item.Path,
                Title: item.Title,
                Summary: "The database says this item has a file, but the tracked path is missing from disk.",
                RecommendedAction: "Mark it missing so search/import can recover, or restore the file manually before running reconciliation again.",
                RepairActions: ["mark-missing"],
                EntityId: item.EntityId,
                EpisodeId: item.EpisodeId,
                ExpectedSizeBytes: item.FileSizeBytes);
        }
    }

    private static IEnumerable<FilesystemReconciliationIssue> FindOrphanFiles(
        LibraryItem library,
        string rootPath,
        IReadOnlyList<TrackedFile> tracked)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        var known = tracked
            .Select(item => Path.GetFullPath(item.Path))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var path in EnumerateFilesSafe(rootPath).Where(IsVideoFile))
        {
            var fullPath = Path.GetFullPath(path);
            if (known.Contains(fullPath) || IsDelunoArtifact(fullPath))
            {
                continue;
            }

            yield return new FilesystemReconciliationIssue(
                Id: EncodeIssueToken("orphanFile", NormalizeMediaType(library.MediaType), library.Id, Path.GetFileNameWithoutExtension(fullPath), null, fullPath),
                Kind: "orphanFile",
                Severity: "warning",
                MediaType: NormalizeMediaType(library.MediaType),
                LibraryId: library.Id,
                LibraryName: library.Name,
                Path: fullPath,
                Title: Path.GetFileNameWithoutExtension(fullPath),
                Summary: "This video file exists under a library root but is not tracked by Deluno.",
                RecommendedAction: "Review and import it intentionally. Deluno will not delete orphan media automatically.",
                RepairActions: ["queue-import-review"],
                EntityId: Path.GetFileNameWithoutExtension(fullPath),
                ActualSizeBytes: GetFileSize(fullPath));
        }
    }

    private static IEnumerable<FilesystemReconciliationIssue> FindPartialImportArtifacts(
        LibraryItem library,
        string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        foreach (var path in EnumerateFilesSafe(rootPath).Where(IsDelunoArtifact))
        {
            var fullPath = Path.GetFullPath(path);
            yield return new FilesystemReconciliationIssue(
                Id: EncodeIssueToken("partialImportArtifact", NormalizeMediaType(library.MediaType), library.Id, Path.GetFileName(fullPath), null, fullPath),
                Kind: "partialImportArtifact",
                Severity: "warning",
                MediaType: NormalizeMediaType(library.MediaType),
                LibraryId: library.Id,
                LibraryName: library.Name,
                Path: fullPath,
                Title: Path.GetFileName(fullPath),
                Summary: "A Deluno staging/backup artifact was left on disk after an interrupted import.",
                RecommendedAction: "Clean it up only after confirming no active import job is using this path.",
                RepairActions: ["cleanup-artifact"],
                EntityId: Path.GetFileName(fullPath),
                ActualSizeBytes: GetFileSize(fullPath));
        }
    }

    private async Task AddImportReviewCaseAsync(
        IssueToken token,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var title = Path.GetFileNameWithoutExtension(fullPath);
        var details = JsonSerializer.Serialize(new
        {
            SourcePath = fullPath,
            token.LibraryId,
            token.MediaType,
            SuggestedAction = "Open import preview and decide destination before importing."
        });

        if (token.MediaType == "tv")
        {
            await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                new CreateSeriesImportRecoveryCaseRequest(
                    title,
                    "unmatched",
                    "Reconciliation found an untracked video file in this library.",
                    "Review this file and import it intentionally if it belongs in the library.",
                    details),
                cancellationToken);
            return;
        }

        await movieCatalogRepository.AddImportRecoveryCaseAsync(
            new CreateMovieImportRecoveryCaseRequest(
                title,
                "unmatched",
                "Reconciliation found an untracked video file in this library.",
                "Review this file and import it intentionally if it belongs in the library.",
                details),
            cancellationToken);
    }

    private async Task RecordRepairAsync(
        IssueToken token,
        string action,
        bool repaired,
        CancellationToken cancellationToken)
    {
        await activityFeedRepository.RecordActivityAsync(
            repaired ? "filesystem.reconciliation.repaired" : "filesystem.reconciliation.repair-failed",
            repaired
                ? $"Reconciliation repair '{action}' completed for {Path.GetFileName(token.Path)}."
                : $"Reconciliation repair '{action}' failed for {Path.GetFileName(token.Path)}.",
            JsonSerializer.Serialize(new
            {
                token.Kind,
                token.MediaType,
                token.LibraryId,
                token.EntityId,
                token.EpisodeId,
                token.Path,
                Action = action,
                Repaired = repaired
            }),
            null,
            token.MediaType == "tv" ? "series" : "movie",
            token.EntityId,
            cancellationToken);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string rootPath)
    {
        try
        {
            return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsVideoFile(string path)
        => VideoExtensions.Contains(Path.GetExtension(path));

    private static bool IsDelunoArtifact(string path)
        => ArtifactMarkers.Any(marker => Path.GetFileName(path).Contains(marker, StringComparison.OrdinalIgnoreCase));

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

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInsideRoot(string path, string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(
            root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            string.Equals(fullPath, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows" ? "tv" : "movies";

    private static string NormalizeAction(string? action)
        => action?.Trim().ToLowerInvariant() switch
        {
            "markmissing" or "mark-missing" => "mark-missing",
            "cleanup" or "cleanup-artifact" => "cleanup-artifact",
            "reimport" or "queue-import-review" => "queue-import-review",
            _ => string.Empty
        };

    private static string EncodeIssueToken(
        string kind,
        string mediaType,
        string libraryId,
        string entityId,
        string? episodeId,
        string path)
        => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new IssueToken(
            kind,
            mediaType,
            libraryId,
            entityId,
            episodeId,
            path))));

    private static IssueToken DecodeIssueToken(string issueId)
        => JsonSerializer.Deserialize<IssueToken>(Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(issueId)))
           ?? throw new InvalidOperationException("Invalid reconciliation issue id.");

    private sealed record TrackedFile(
        string MediaType,
        string LibraryId,
        string EntityId,
        string? EpisodeId,
        string Title,
        string Path,
        long? FileSizeBytes);

    private sealed record IssueToken(
        string Kind,
        string MediaType,
        string LibraryId,
        string EntityId,
        string? EpisodeId,
        string Path);
}
