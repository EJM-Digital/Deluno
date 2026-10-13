using System.Text;
using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.WebUtilities;

namespace Deluno.Filesystem;

public sealed class FilesystemReconciliationService(
    ILibrariesRepository librariesRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider,
    IHealthCheckRepository? healthChecks = null,
    Deluno.Platform.IRecycleBinService? recycleBinService = null)
    : IFilesystemReconciliationService
{
    private static readonly string[] ArtifactMarkers =
    [
        ".deluno-stage-",
        ".deluno-copy-",
        ".deluno-backup-"
    ];

    public async Task<FilesystemReconciliationReport> ScanAsync(CancellationToken cancellationToken)
    {
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var issues = new List<FilesystemReconciliationIssue>();
        var recycled = await RecycledOriginalPathsAsync(cancellationToken);

        foreach (var library in libraries.Where(item => !string.IsNullOrWhiteSpace(item.RootPath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootPath = Path.GetFullPath(library.RootPath);

            // An unreachable library is not a library full of deleted files.
            //
            // The two scans below this one already stopped here; the scan that
            // marks files *missing* did not, and it is the one that matters.
            // It asks File.Exists of every tracked path in turn, so a drive
            // that has not mounted answers false to all of them and every
            // title in the library becomes a critical missing-file issue. Once
            // those corrections are applied on a schedule rather than by hand,
            // that is a whole library marked missing and re-downloaded because
            // a NAS was asleep.
            //
            // So the root is checked once, and its absence is reported as the
            // single fact it is. DESIGN-007 decision 12.
            if (!Directory.Exists(rootPath))
            {
                issues.Add(CreateUnreachableLibraryIssue(library, rootPath));
                continue;
            }

            var knownTrackedPaths = new HashSet<string>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            // A mount point that exists but holds nothing (a Docker volume or
            // share that did not mount) passes the check above and then makes
            // every tracked file look deleted. A root with no entries at all
            // while the library tracks files is that, not a wiped library.
            var rootIsEmpty = IsRootEmptyOrUnreadable(rootPath);
            var trackedList = new List<TrackedFile>();
            await foreach (var tracked in StreamTrackedFilesAsync(library, cancellationToken))
            {
                trackedList.Add(tracked);
            }

            if (rootIsEmpty && trackedList.Count > 0)
            {
                issues.Add(CreateUnreachableLibraryIssue(library, rootPath));
                continue;
            }

            var missingHere = new List<FilesystemReconciliationIssue>();
            foreach (var tracked in trackedList)
            {
                var fullPath = Path.GetFullPath(tracked.Path);
                if (File.Exists(tracked.Path))
                {
                    knownTrackedPaths.Add(fullPath);
                    continue;
                }

                if (IsBeingReplacedByAnImport(tracked.Path))
                {
                    knownTrackedPaths.Add(fullPath);
                    continue;
                }

                // A file Deluno itself moved to its recycle bin was not removed outside Deluno.
                if (recycled.Contains(fullPath))
                {
                    if (tracked.MediaType == "tv")
                    {
                        await seriesCatalogRepository.DetachSeriesFileRecordsAsync(
                            tracked.EntityId, tracked.Path, cancellationToken);
                    }

                    continue;
                }

                missingHere.Add(CreateMissingTrackedFileIssue(library, tracked));
            }

            // A large share missing in one pass is a share that dropped out,
            // not that many people deleting files at once (#1065).
            if (LooksLikeDropout(missingHere.Count, trackedList.Count))
            {
                await RaiseUnreachableNoticeAsync(
                    library, missingHere.Count, trackedList.Count, cancellationToken);
                issues.Add(CreateUnreachableLibraryIssue(library, rootPath));
                continue;
            }

            // The library is healthy again (files present at the normal rate):
            // a note that said it looked unreachable is no longer true.
            await ClearUnreachableNoticeAsync(library, cancellationToken);

            issues.AddRange(missingHere);

            issues.AddRange(FindOrphanFiles(library, rootPath, knownTrackedPaths));
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
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
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
            if (IsBeingReplacedByAnImport(token.Path))
            {
                return new FilesystemReconciliationRepairResult(
                    false,
                    action,
                    "An import is replacing this file right now, so it was left alone. The next check will see the new file.");
            }

            // A file the reconciliation scan finds gone is a file that
            // disappeared outside Deluno. Deluno's own removals never come
            // through here: an upgrade never marks the old file missing, and
            // an external re-download keeps the title monitored. So the title
            // (or episode) stopped being something the user still wants and is
            // unmonitored in the same step, so it is never searched for and
            // downloaded again. Cleanup sheet row K2, #931, #1065. For a movie
            // the file fields, status and monitored flag change in one
            // transaction (MarkTrackedFileGoneAsync).
            // One missed sighting must never unmonitor a title (#1065): look
            // again, immediately before the write. The file must still be
            // gone and the library's root must still be there and not empty,
            // or a drive that dropped out mid-scan would unmonitor whatever
            // was left in the queue.
            if (File.Exists(token.Path))
            {
                return new FilesystemReconciliationRepairResult(
                    false,
                    action,
                    "The file is back on disk, so nothing was changed.");
            }

            if ((await RecycledOriginalPathsAsync(cancellationToken)).Contains(fullPath))
            {
                if (token.MediaType == "tv")
                {
                    await seriesCatalogRepository.DetachSeriesFileRecordsAsync(
                        token.EntityId, token.Path, cancellationToken);
                }

                return new FilesystemReconciliationRepairResult(
                    false,
                    action,
                    "Deluno moved this file to its recycle bin itself, so nothing was changed.");
            }

            var rootFull = string.IsNullOrWhiteSpace(library.RootPath) ? null : Path.GetFullPath(library.RootPath);
            if (rootFull is not null && (!Directory.Exists(rootFull) || IsRootEmptyOrUnreadable(rootFull)))
            {
                await RaiseUnreachableNoticeAsync(library, null, null, cancellationToken);
                return new FilesystemReconciliationRepairResult(
                    false,
                    action,
                    $"{library.Name} looks unreachable, so nothing was changed.",
                    LibraryUnreachable: true);
            }

            bool repaired;
            MovieFileGoneOutcome movieOutcome = default;
            if (token.MediaType == "tv")
            {
                // Clear the file, mark it missing and unmonitor in one transaction.
                repaired = await seriesCatalogRepository.MarkTrackedFileGoneAsync(
                    token.EntityId,
                    token.EpisodeId,
                    token.LibraryId,
                    token.Path,
                    cancellationToken);
            }
            else
            {
                movieOutcome = await movieCatalogRepository.MarkTrackedFileGoneAsync(
                    token.EntityId,
                    token.LibraryId,
                    token.Path,
                    cancellationToken);
                repaired = movieOutcome.Cleared;
            }

            await RecordMarkMissingRepairAsync(token, repaired, movieOutcome, libraries, cancellationToken);
            return new FilesystemReconciliationRepairResult(
                repaired,
                action,
                repaired
                    ? movieOutcome.OnlyThatLibrary
                        ? "The copy in this library was safely marked missing and Deluno stopped wanting it here. The film's other copy is still managed. Deluno did not delete any files."
                        : "The tracked item was safely marked missing and unmonitored. Deluno did not delete any files."
                    : "No matching tracked item was updated.");
        }

        if (token.Kind == "partialImportArtifact" && action == "cleanup-artifact")
        {
            if (!IsDelunoArtifact(fullPath))
            {
                return new FilesystemReconciliationRepairResult(false, action, "Cleanup refused because the file is not a Deluno staging artifact.");
            }

            // A stage file can be the only copy of a move import and a backup file is the previous library
            // file, so neither is ever deleted for good (#1259): the recycle bin, or it stays where it is.
            var recycled = false;
            if (recycleBinService is not null && File.Exists(fullPath))
            {
                try
                {
                    recycled = await recycleBinService.StoreRemovedFileAsync(library, fullPath, null, cancellationToken) is not null
                        && !File.Exists(fullPath);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    recycled = false;
                }
            }

            var message = recycled
                ? "The staging artifact was moved to the recycle bin. Restore it from there if it was needed."
                : "The staging artifact was left where it is: Deluno never deletes it for good, and the recycle bin could not take it.";
            await RecordRepairAsync(token, action, recycled, cancellationToken, message);
            return new FilesystemReconciliationRepairResult(recycled, action, message);
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

    /// <summary>
    /// One title's own rescan (#997): the same question the library-wide file
    /// check asks, put to a single film or show.
    ///
    /// <para>Only one thing is ever changed, and it is the thing the scheduled
    /// check already changes: a tracked file that is no longer on disk is
    /// marked missing (Deluno's own note, never the disk). Video files that sit
    /// in the title's folder without being tracked are only listed, each with
    /// the id of the existing review repair, so a person decides. Nothing is
    /// deleted, moved or imported here.</para>
    /// </summary>
    public async Task<TitleRescanResult?> RescanTitleAsync(
        string mediaType,
        string titleId,
        CancellationToken cancellationToken)
    {
        var isTv = NormalizeMediaType(mediaType) == "tv";
        string title;
        string? libraryId;
        var tracked = new List<(string? EpisodeId, string Path)>();

        if (isTv)
        {
            var item = await seriesCatalogRepository.GetByIdAsync(titleId, cancellationToken);
            if (item is null)
            {
                return null;
            }

            title = item.Title;
            libraryId = item.LibraryId;
            foreach (var (episodeId, filePath) in await seriesCatalogRepository.ListEpisodeFilePathsAsync(titleId, cancellationToken))
            {
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    tracked.Add((episodeId, filePath));
                }
            }
        }
        else
        {
            var item = await movieCatalogRepository.GetByIdAsync(titleId, cancellationToken);
            if (item is null)
            {
                return null;
            }

            title = item.Title;
            libraryId = item.LibraryId;
            if (!string.IsNullOrWhiteSpace(item.FilePath))
            {
                tracked.Add((null, item.FilePath));
            }
        }

        var normalizedType = isTv ? "tv" : "movies";
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item =>
                string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase))
            ?? (tracked.Count == 0
                ? null
                : libraries.FirstOrDefault(item =>
                    NormalizeMediaType(item.MediaType) == normalizedType
                    && !string.IsNullOrWhiteSpace(item.RootPath)
                    && IsInsideRoot(tracked[0].Path, item.RootPath)));

        if (tracked.Count == 0)
        {
            var nothing = new TitleRescanResult(
                normalizedType, titleId, title, "nothing-tracked", 0, 0, [], [], null,
                $"{title} has no file on record, so there was nothing to check.");
            await RecordRescanAsync(nothing, cancellationToken);
            return nothing;
        }

        if (library is null)
        {
            // Nothing says which library holds these files, so nothing can be
            // checked against a root and nothing may be changed.
            var unknown = new TitleRescanResult(
                normalizedType, titleId, title, "library-unknown", tracked.Count, 0, [], [], null,
                $"Deluno could not tell which library holds {title}, so it changed nothing. Nothing was marked missing and it is still monitored.");
            await RecordRescanAsync(unknown, cancellationToken);
            return unknown;
        }

        if (!string.IsNullOrWhiteSpace(library.RootPath)
            && (!Directory.Exists(Path.GetFullPath(library.RootPath))
                // An empty mount point exists but holds nothing; the title's
                // files are tracked, so this is a drive not mounted, not
                // deleted files.
                || IsRootEmptyOrUnreadable(Path.GetFullPath(library.RootPath))))
        {
            // A drive that has not mounted is not a title with no files.
            var unreachable = new TitleRescanResult(
                normalizedType, titleId, title, "library-unreachable", tracked.Count, 0, [], [], null,
                $"{library.Name} is not reachable at {library.RootPath}, so Deluno changed nothing. Check the drive is mounted and try again.");
            await RecordRescanAsync(unreachable, cancellationToken);
            return unreachable;
        }

        var present = 0;
        var trackedFullPaths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var missing = new List<TitleRescanMissingFile>();
        bool? dropout = null;
        foreach (var (episodeId, path) in tracked)
        {
            trackedFullPaths.Add(Path.GetFullPath(path));
            if (File.Exists(path))
            {
                present++;
                continue;
            }

            if (IsBeingReplacedByAnImport(path))
            {
                // An import is swapping this file right now (#1065); not a deletion.
                present++;
                continue;
            }

            var displayTitle = episodeId is null ? title : $"{title} ({Path.GetFileName(path)})";

            // A whole share dropping out looks like this title's files gone
            // too: ask a sample of the library's other files before writing.
            dropout ??= await LibraryOthersLookMissingAsync(library, trackedFullPaths, cancellationToken);
            if (dropout == true)
            {
                return await UnreachableRescanAsync(
                    library, normalizedType, titleId, title, tracked.Count, cancellationToken, describeSet: true);
            }

            var issueId = EncodeIssueToken(
                "missingTrackedFile", normalizedType, library.Id, titleId, episodeId, path, displayTitle);
            var repair = await RepairAsync(
                new FilesystemReconciliationRepairRequest(issueId, "mark-missing"),
                cancellationToken);
            if (repair.LibraryUnreachable)
            {
                return await UnreachableRescanAsync(
                    library, normalizedType, titleId, title, tracked.Count, cancellationToken);
            }

            missing.Add(new TitleRescanMissingFile(Path.GetFileName(path), path, repair.Repaired));
        }

        // Look only in the title's own folder, and never in a library root
        // itself: a flat library would otherwise list every other title.
        var untracked = new List<TitleRescanUntrackedFile>();
        var folder = CommonFolder(tracked.Select(item => item.Path).ToArray());
        if (folder is not null
            && library is not null
            && IsInsideRoot(folder, library.RootPath)
            && !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(library.RootPath)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && Directory.Exists(folder))
        {
            foreach (var path in EnumerateFilesSafe(folder).Where(IsVideoFile))
            {
                var fullPath = Path.GetFullPath(path);
                if (trackedFullPaths.Contains(fullPath) || IsDelunoArtifact(fullPath))
                {
                    continue;
                }

                untracked.Add(new TitleRescanUntrackedFile(
                    Path.GetFileName(fullPath),
                    fullPath,
                    GetFileSize(fullPath),
                    EncodeIssueToken(
                        "orphanFile", normalizedType, library.Id,
                        Path.GetFileNameWithoutExtension(fullPath), null, fullPath)));
            }
        }

        // "changed" only when something really was: a refused repair changed nothing.
        var outcome = missing.Any(item => item.MarkedMissing)
            ? "changed"
            : missing.Count > 0 ? "not-changed" : "ok";
        var result = new TitleRescanResult(
            normalizedType, titleId, title, outcome, tracked.Count, present, missing, untracked, folder,
            DescribeRescan(title, tracked.Count, present, missing, untracked.Count));
        await RecordRescanAsync(result, cancellationToken);
        return result;
    }

    /// <summary>
    /// Asks a sample of the library's other tracked files whether they are
    /// there. True when a large share of them is not: the library dropped out.
    /// </summary>
    private async Task<bool> LibraryOthersLookMissingAsync(
        LibraryItem library,
        HashSet<string> ownPaths,
        CancellationToken cancellationToken)
    {
        const int sampleSize = 200;
        var checkedCount = 0;
        var missingCount = 0;
        await foreach (var other in StreamTrackedFilesAsync(library, cancellationToken))
        {
            if (ownPaths.Contains(Path.GetFullPath(other.Path)))
            {
                continue;
            }

            checkedCount++;
            if (!File.Exists(other.Path) && !IsBeingReplacedByAnImport(other.Path))
            {
                missingCount++;
            }

            if (checkedCount >= sampleSize)
            {
                break;
            }
        }

        return LooksLikeDropout(missingCount, checkedCount);
    }

    private async Task<TitleRescanResult> UnreachableRescanAsync(
        LibraryItem library,
        string mediaType,
        string titleId,
        string title,
        int trackedCount,
        CancellationToken cancellationToken,
        bool describeSet = false)
    {
        if (describeSet)
        {
            // Put the count on the note so "These files really are gone" is
            // offered, even when the scheduled check has not run yet.
            var missingCount = 0;
            var total = 0;
            await foreach (var other in StreamTrackedFilesAsync(library, cancellationToken))
            {
                total++;
                if (!File.Exists(other.Path) && !IsBeingReplacedByAnImport(other.Path))
                {
                    missingCount++;
                }
            }

            await RaiseUnreachableNoticeAsync(library, missingCount, total, cancellationToken);
        }
        else
        {
            await RaiseUnreachableNoticeAsync(library, null, null, cancellationToken);
        }

        var result = new TitleRescanResult(
            mediaType, titleId, title, "library-unreachable", trackedCount, 0, [], [], null,
            $"Deluno changed nothing: most of the {library.Name} library's files could not be found at once, so {title} was not marked missing and is still monitored. "
            + "If the drive or share dropped out, reconnect it and rescan. If you deleted these files, open System, Health and choose These files really are gone.");
        await RecordRescanAsync(result, cancellationToken);
        return result;
    }

    public const string UnreachableNoticePrefix = "notice:library-unreachable:";

    private async Task ClearUnreachableNoticeAsync(LibraryItem library, CancellationToken cancellationToken)
    {
        if (healthChecks is null)
        {
            return;
        }

        var id = UnreachableNoticePrefix + library.Id;
        if (await healthChecks.GetAsync(id, cancellationToken) is null)
        {
            return;
        }

        await healthChecks.RemoveAsync(id, cancellationToken);
        await activityFeedRepository.RecordActivityAsync(
            "library.reachable",
            $"{library.Name} is back: its files were found again, so the unreachable note was cleared.",
            JsonSerializer.Serialize(new { LibraryId = library.Id }),
            null,
            "library",
            library.Id,
            cancellationToken);
    }

    /// <summary>How many files one write covers; each chunk is one transaction (#1070).</summary>
    internal const int FilesGoneChunkSize = 500;

    /// <summary>
    /// How many missing files the note says there are (0 when it has no count),
    /// or null when the library has no note.
    /// </summary>
    public async Task<int?> GetUnreachableNoticeMissingCountAsync(string libraryId, CancellationToken cancellationToken)
    {
        var notice = healthChecks is null
            ? null
            : await healthChecks.GetAsync(UnreachableNoticePrefix + libraryId, cancellationToken);
        return notice is null ? null : ReadNoticeMissingCount(notice.DetailsJson);
    }

    /// <summary>
    /// The user's answer to the unreachable note when they really did delete
    /// the files: apply the missing-file rule to the files that are missing
    /// right now. The note keeps only the library, a count and when, so the set
    /// is worked out here with every re-check the single repair makes: the file
    /// is still gone, the library's root is readable and not empty, the file
    /// lies inside the root, and Deluno tracks it in this library. The writes go
    /// a chunk at a time, one transaction per chunk, with one Activity line for
    /// the whole answer.
    /// </summary>
    public async Task<LibraryFilesGoneResult?> ConfirmFilesGoneAsync(string libraryId, CancellationToken cancellationToken)
    {
        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        var library = libraries.FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        var noticeId = UnreachableNoticePrefix + libraryId;
        var notice = healthChecks is null ? null : await healthChecks.GetAsync(noticeId, cancellationToken);
        if (library is null || notice is null)
        {
            return null;
        }

        var noted = ReadNoticeMissingCount(notice.DetailsJson);

        // The root has to be readable now: a share that is still down proves nothing.
        var root = string.IsNullOrWhiteSpace(library.RootPath) ? null : Path.GetFullPath(library.RootPath);
        if (root is null || !Directory.Exists(root) || IsRootEmptyOrUnreadable(root))
        {
            return new LibraryFilesGoneResult(0, 0, 0, true,
                $"{library.Name} is still not reachable, so Deluno changed nothing.");
        }

        var gone = new List<TrackedFile>();
        var refused = 0;
        await foreach (var tracked in StreamTrackedFilesAsync(library, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(tracked.LibraryId, library.Id, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(tracked.Path))
            {
                refused++;
                continue;
            }

            if (File.Exists(tracked.Path) || IsBeingReplacedByAnImport(tracked.Path))
            {
                continue;
            }

            if (!IsInsideRoot(Path.GetFullPath(tracked.Path), library.RootPath))
            {
                refused++;
                continue;
            }

            gone.Add(tracked);
        }

        if (gone.Count == 0 && refused == 0)
        {
            return new LibraryFilesGoneResult(0, noted, 0, false,
                $"Every file Deluno tracks in {library.Name} is there now, so there is nothing to mark as gone. The note clears at the next file check.");
        }

        var marked = 0;
        var cameBack = Math.Max(0, noted - gone.Count - refused);
        var perTitle = gone.Count <= 10;
        var perTitleOutcomes = new List<(TrackedFile File, bool Repaired, MovieFileGoneOutcome Movie)>();
        var stoppedEarly = false;
        foreach (var chunk in gone.Chunk(FilesGoneChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Look again immediately before each write: a share that dropped
            // out part-way through must not unmonitor what is left.
            if (!Directory.Exists(root) || IsRootEmptyOrUnreadable(root))
            {
                stoppedEarly = true;
                break;
            }

            var still = chunk.Where(item => !File.Exists(item.Path) && !IsBeingReplacedByAnImport(item.Path)).ToArray();
            cameBack += chunk.Length - still.Length;
            if (still.Length == 0)
            {
                continue;
            }

            if (NormalizeMediaType(library.MediaType) == "tv")
            {
                var results = await seriesCatalogRepository.MarkTrackedFilesGoneAsync(
                    still.Select(item => new SeriesFileGoneRequest(item.EntityId, item.EpisodeId, library.Id, item.Path)).ToArray(),
                    cancellationToken);
                for (var i = 0; i < still.Length; i++)
                {
                    if (results[i]) { marked++; } else { refused++; }
                    if (perTitle) { perTitleOutcomes.Add((still[i], results[i], default)); }
                }
            }
            else
            {
                var results = await movieCatalogRepository.MarkTrackedFilesGoneAsync(
                    still.Select(item => new MovieFileGoneRequest(item.EntityId, library.Id, item.Path)).ToArray(),
                    cancellationToken);
                for (var i = 0; i < still.Length; i++)
                {
                    if (results[i].Cleared) { marked++; } else { refused++; }
                    if (perTitle) { perTitleOutcomes.Add((still[i], results[i].Cleared, results[i])); }
                }
            }
        }

        if (stoppedEarly)
        {
            await RaiseUnreachableNoticeAsync(library, null, null, cancellationToken);
            return new LibraryFilesGoneResult(marked, cameBack, refused, true,
                $"{library.Name} stopped being reachable part-way through, so Deluno stopped. {marked} file{(marked == 1 ? "" : "s")} {(marked == 1 ? "was" : "were")} marked as gone before that.");
        }

        var message = $"{library.Name}: {marked} file{(marked == 1 ? "" : "s")} marked as gone"
            + (cameBack > 0 ? $", {cameBack} came back and {(cameBack == 1 ? "was" : "were")} left alone" : "")
            + (refused > 0 ? $", {refused} could not be changed" : "")
            + ". Deluno did not delete any files.";

        // A handful of titles are told by name, as a single repair does (#931);
        // a large answer is one line so Activity is not flooded.
        foreach (var (file, repaired, movie) in perTitleOutcomes)
        {
            await RecordMarkMissingRepairAsync(
                new IssueToken("missingTrackedFile", file.MediaType, library.Id, file.EntityId, file.EpisodeId, file.Path, file.Title),
                repaired, movie, libraries, cancellationToken);
        }

        await activityFeedRepository.RecordActivityAsync(
            "library.files-gone",
            message,
            JsonSerializer.Serialize(new { LibraryId = library.Id, Marked = marked, CameBack = cameBack, Refused = refused }),
            null,
            "library",
            library.Id,
            cancellationToken);
        await healthChecks!.RemoveAsync(noticeId, cancellationToken);
        return new LibraryFilesGoneResult(marked, cameBack, refused, false, message);
    }

    private static int ReadNoticeMissingCount(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            return doc.RootElement.TryGetProperty("missing", out var count) && count.ValueKind == JsonValueKind.Number
                ? count.GetInt32()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>The fewest missing files that can look like a dropout.</summary>
    public const int DropoutMinimumMissing = 20;

    /// <summary>
    /// A large share of a library's tracked files missing in one pass is a
    /// drive or share that dropped out, not a person deleting that many files
    /// at once: at least 20 files and at least a quarter of what was checked.
    /// Consistent with an empty root meaning "not mounted" (#1070).
    /// </summary>
    public static bool LooksLikeDropout(int missing, int total)
        => total > 0 && missing >= DropoutMinimumMissing && missing * 4 >= total;

    /// <summary>
    /// One Health note and one Activity line, in plain words, that the library
    /// looks unreachable and nothing was changed. The note can be cleared from
    /// System, Health. Raised once: while the note is still there, a repeat
    /// pass adds nothing.
    /// </summary>
    private async Task RaiseUnreachableNoticeAsync(
        LibraryItem library,
        int? missing,
        int? total,
        CancellationToken cancellationToken)
    {
        var id = UnreachableNoticePrefix + library.Id;
        var alreadyThere = false;
        if (healthChecks is not null)
        {
            var existing = await healthChecks.GetAsync(id, cancellationToken);
            alreadyThere = existing is not null
                && string.Equals(existing.Status, HealthCheckStatuses.Problem, StringComparison.Ordinal);
            // A note that already says how many files are missing is left as it
            // is (the answer recounts them anyway); one raised without a count
            // (a root that vanished mid-repair) gets it when a pass finds one.
            if (alreadyThere && (missing is not > 0 || ReadNoticeMissingCount(existing!.DetailsJson) > 0))
            {
                return;
            }
        }

        var canConfirm = missing is > 0;
        var message = missing is not null && total is not null
            ? $"{library.Name}: {missing} of {total} files could not be found, so Deluno changed nothing. "
                + "If the drive or share dropped out, reconnect it and this clears by itself."
                + (canConfirm ? " If you deleted these files, choose These files really are gone." : "")
            : $"{library.Name}: its files could not be found while Deluno was checking, so Deluno changed nothing. "
                + "If the drive or share dropped out, reconnect it and this clears by itself.";
        if (!alreadyThere)
        {
            await activityFeedRepository.RecordActivityAsync(
                "library.unreachable",
                message,
                JsonSerializer.Serialize(new { LibraryId = library.Id, Missing = missing, Total = total }),
                null,
                "library",
                library.Id,
                cancellationToken);
        }

        if (healthChecks is not null)
        {
            // Only the library, how many and when: the answer recounts the
            // missing files itself, so a huge dropout never bloats this row.
            var details = JsonSerializer.Serialize(new
            {
                libraryId = library.Id,
                missing = missing ?? 0,
                total = total ?? 0,
                raisedUtc = timeProvider.GetUtcNow()
            });
            await healthChecks.UpsertResultsAsync(
                [HealthCheckOutcome.Problem(
                    id,
                    HealthCheckCategories.Notice,
                    $"{library.Name} looks unreachable",
                    message,
                    cause: null,
                    fix: null,
                    detailsJson: details)],
                cancellationToken);
        }
    }

    /// <summary>
    /// True when the root has no entries at all, or cannot be listed. A library
    /// whose root is a mount point that never mounted looks exactly like this.
    /// </summary>
    private static bool IsRootEmptyOrUnreadable(string rootPath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(rootPath).All(IsSystemFolder);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// A folder the operating system or a volume puts on an otherwise empty mount (an ext4 volume's lost+found,
    /// the Windows recycle bin and volume information, a Linux trash folder). It holds none of the library.
    /// </summary>
    internal static bool IsSystemFolder(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(name, "lost+found", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".Trash-", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeRescan(
        string title,
        int checkedCount,
        int present,
        IReadOnlyList<TitleRescanMissingFile> missing,
        int untrackedCount)
    {
        var parts = new List<string>();
        if (missing.Count == 0)
        {
            parts.Add(checkedCount == 1
                ? "Its file is where Deluno expects it."
                : $"All {checkedCount} of its files are where Deluno expects them.");
        }
        else
        {
            // Say only what happened: a repair that was refused (a path outside
            // the library root, or nothing matching) marked nothing missing.
            var marked = missing.Count(item => item.MarkedMissing);
            if (marked == missing.Count)
            {
                parts.Add(present == 0 && checkedCount == 1
                    ? "Its file is no longer on disk, so Deluno marked it missing and stopped wanting it. No files were touched."
                    : $"{missing.Count} of {checkedCount} files are no longer on disk. Deluno marked them missing and stopped wanting them. No files were touched.");
            }
            else if (marked == 0)
            {
                parts.Add(missing.Count == 1
                    ? "Its file is not on disk, but Deluno could not mark it missing, so nothing was changed and it is still monitored. No files were touched."
                    : $"{missing.Count} of {checkedCount} files are not on disk, but Deluno could not mark them missing, so nothing was changed and the title is still monitored. No files were touched.");
            }
            else
            {
                parts.Add($"{missing.Count} of {checkedCount} files are not on disk. Deluno marked {marked} missing and stopped wanting {(marked == 1 ? "it" : "them")}; it could not mark the other {missing.Count - marked}, which are unchanged. No files were touched.");
            }
        }

        if (untrackedCount > 0)
        {
            parts.Add(untrackedCount == 1
                ? "One video file in its folder is not tracked; nothing was imported or deleted."
                : $"{untrackedCount} video files in its folder are not tracked; nothing was imported or deleted.");
        }

        return $"Rescanned {title}. {string.Join(' ', parts)}";
    }

    private Task RecordRescanAsync(TitleRescanResult result, CancellationToken cancellationToken)
        => activityFeedRepository.RecordActivityAsync(
            "library.title.rescanned",
            result.Summary,
            JsonSerializer.Serialize(new
            {
                result.Outcome,
                result.FilesChecked,
                result.FilesPresent,
                Missing = result.Missing.Select(item => item.Path),
                Untracked = result.Untracked.Select(item => item.Path)
            }),
            null,
            result.MediaType == "tv" ? "series" : "movie",
            result.TitleId,
            cancellationToken);

    private static string? CommonFolder(IReadOnlyList<string> filePaths)
    {
        string? common = null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var filePath in filePaths)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (dir is null)
            {
                return null;
            }

            if (common is null)
            {
                common = dir;
                continue;
            }

            while (common is not null
                   && !(string.Equals(common, dir, comparison)
                        || dir.StartsWith(common.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)))
            {
                common = Path.GetDirectoryName(common);
            }

            if (common is null)
            {
                return null;
            }
        }

        return common;
    }

    private async IAsyncEnumerable<TrackedFile> StreamTrackedFilesAsync(
        LibraryItem library,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (NormalizeMediaType(library.MediaType) == "tv")
        {
            await foreach (var item in seriesCatalogRepository.StreamTrackedFilesAsync(
                               library.Id,
                               cancellationToken))
            {
                yield return new TrackedFile(
                    MediaType: "tv",
                    LibraryId: item.LibraryId,
                    EntityId: item.SeriesId,
                    EpisodeId: item.EpisodeId,
                    Title: item.EpisodeId is null
                        ? item.Title
                        : $"{item.Title} S{item.SeasonNumber:00}E{item.EpisodeNumber:00}",
                    Path: item.FilePath,
                    FileSizeBytes: item.FileSizeBytes);
            }

            yield break;
        }

        await foreach (var item in movieCatalogRepository.StreamTrackedFilesAsync(
                           library.Id,
                           cancellationToken))
        {
            yield return new TrackedFile(
                MediaType: "movies",
                LibraryId: item.LibraryId,
                EntityId: item.MovieId,
                EpisodeId: null,
                Title: item.ReleaseYear is null ? item.Title : $"{item.Title} ({item.ReleaseYear})",
                Path: item.FilePath,
                FileSizeBytes: item.FileSizeBytes);
        }
    }

    /// <summary>
    /// The library's root is not there at all.
    ///
    /// <para>Reported instead of, not alongside, everything inside it. One
    /// sentence a person can act on beats ten thousand saying the same thing,
    /// and the recommended action is deliberately not "mark them missing" —
    /// the overwhelmingly likely cause is a drive that has not mounted.</para>
    /// </summary>
    private static FilesystemReconciliationIssue CreateUnreachableLibraryIssue(LibraryItem library, string rootPath)
        => new(
            Id: EncodeIssueToken("libraryRootUnreachable", NormalizeMediaType(library.MediaType), library.Id, string.Empty, null, rootPath),
            Kind: "libraryRootUnreachable",
            Severity: "critical",
            MediaType: NormalizeMediaType(library.MediaType),
            LibraryId: library.Id,
            LibraryName: library.Name,
            Path: rootPath,
            Title: library.Name,
            Summary: $"{library.Name} is not reachable at {rootPath}, so Deluno cannot tell you anything about the files in it.",
            RecommendedAction: "Check the drive or share is mounted. Deluno has changed nothing and will resume on its own once the path is back.",
            RepairActions: []);

    private static FilesystemReconciliationIssue CreateMissingTrackedFileIssue(
        LibraryItem library,
        TrackedFile item)
        => new(
            Id: EncodeIssueToken("missingTrackedFile", item.MediaType, library.Id, item.EntityId, item.EpisodeId, item.Path, item.Title),
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

    private static IEnumerable<FilesystemReconciliationIssue> FindOrphanFiles(
        LibraryItem library,
        string rootPath,
        IReadOnlySet<string> knownTrackedPaths)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        foreach (var path in EnumerateFilesSafe(rootPath).Where(IsVideoFile))
        {
            var fullPath = Path.GetFullPath(path);
            if (knownTrackedPaths.Contains(fullPath) || IsDelunoArtifact(fullPath))
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
        CancellationToken cancellationToken,
        string? detail = null)
    {
        await activityFeedRepository.RecordActivityAsync(
            repaired ? "filesystem.reconciliation.repaired" : "filesystem.reconciliation.repair-failed",
            (repaired
                ? $"Reconciliation repair '{action}' completed for {Path.GetFileName(token.Path)}."
                : $"Reconciliation repair '{action}' failed for {Path.GetFileName(token.Path)}.")
                + (detail is null ? string.Empty : " " + detail),
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

    /// <summary>
    /// Told in plain words, by name: a title going from held to unmonitored is
    /// something a person will notice on a shelf, and they should learn why
    /// from Deluno rather than guess. #931.
    /// </summary>
    private async Task RecordMarkMissingRepairAsync(
        IssueToken token,
        bool repaired,
        MovieFileGoneOutcome movieOutcome,
        IReadOnlyList<LibraryItem> libraries,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(token.Title) ? Path.GetFileName(token.Path) : token.Title;
        string LibraryName(string? id) => libraries.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? "that library";
        var message = !repaired
            ? $"Reconciliation repair 'mark-missing' failed for {Path.GetFileName(token.Path)}."
            : movieOutcome.OnlyThatLibrary
                // #1065: a film kept in two libraries stops being wanted only where its file went.
                ? $"{name}'s file in {LibraryName(movieOutcome.LibraryId)} was removed outside Deluno, so Deluno stopped wanting it in {LibraryName(movieOutcome.LibraryId)}. The {string.Join(" and ", (movieOutcome.OtherLibraryIds ?? []).Select(LibraryName))} copy is still managed."
                : $"{name}'s file was removed outside Deluno, so Deluno stopped wanting it.";
        await activityFeedRepository.RecordActivityAsync(
            repaired ? "filesystem.reconciliation.unmonitored" : "filesystem.reconciliation.repair-failed",
            message,
            JsonSerializer.Serialize(new
            {
                token.Kind,
                token.MediaType,
                token.LibraryId,
                token.EntityId,
                token.EpisodeId,
                token.Path,
                Action = "mark-missing",
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
        => SupportedVideoExtensions.Contains(path);

    private static bool IsDelunoArtifact(string path)
        => ArtifactMarkers.Any(marker => Path.GetFileName(path).Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where the files Deluno itself moved to its recycle bin used to be. Those are never "removed outside Deluno".
    /// </summary>
    private async Task<HashSet<string>> RecycledOriginalPathsAsync(CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (recycleBinService is null)
        {
            return paths;
        }

        foreach (var item in await recycleBinService.ListAsync(cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(item.OriginalPath))
            {
                paths.Add(Path.GetFullPath(item.OriginalPath));
            }
        }

        return paths;
    }

    /// <summary>
    /// True while an import is replacing this very file: the import moves the old
    /// file aside as <c>name.deluno-backup-*.tmp</c> and stages the new one as
    /// <c>name.deluno-stage-*.tmp</c> before the new file takes its place, so for
    /// that stretch the tracked path is genuinely absent. A check that ran then
    /// would read the upgrade as a deletion and unmonitor the title mid-upgrade
    /// (#1065). The next check, once the import has finished, sees the new file.
    /// </summary>
    internal static bool IsBeingReplacedByAnImport(string trackedPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(trackedPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return false;
            }

            var name = Path.GetFileName(trackedPath);
            return Directory.EnumerateFiles(folder, $"{name}.deluno-*-*.tmp").Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
        string path,
        string? title = null)
        => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new IssueToken(
            kind,
            mediaType,
            libraryId,
            entityId,
            episodeId,
            path,
            title))));

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
        string Path,
        string? Title = null);
}
