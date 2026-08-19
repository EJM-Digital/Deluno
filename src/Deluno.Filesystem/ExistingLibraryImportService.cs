using System.Diagnostics;
using System.Text.RegularExpressions;
using Deluno.Contracts;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;

namespace Deluno.Filesystem;

/// <summary>
/// Brings files that are already on disk into the catalogue.
///
/// The shape here is deliberate. Import used to be one HTTP request that walked
/// the whole tree, materialised every discovered item, then inserted them one
/// at a time and returned when it had finished. At 20,000 items that request
/// runs for hours, times out, leaves a partial database, and has no way to
/// resume or to say how far it got.
///
/// Instead:
///
/// <list type="bullet">
/// <item>the request only creates a run; a worker advances it in slices;</item>
/// <item>discovery streams — the top level is listed and sorted once, and each
/// entry is parsed as it is reached, rather than the whole scan being built up
/// before anything is written;</item>
/// <item>writes go in batched transactions, so the cost is one flush per batch
/// rather than one per title;</item>
/// <item>the position marker is the last path completed, so a restart resumes
/// from there;</item>
/// <item>anything unreadable or ambiguous is recorded against the run and the
/// run carries on.</item>
/// </list>
///
/// One thing the original design got right and which is preserved: import never
/// calls the metadata provider. It writes what the filename says and lets the
/// metadata backfill fill in the rest, which keeps import bound by disk and
/// SQLite rather than by a remote API.
/// </summary>
public sealed class ExistingLibraryImportService(
    ILibrariesRepository librariesRepository,
    ILibraryImportRunsRepository importRunsRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IMediaDecisionService mediaDecisionService,
    TimeProvider timeProvider,
    LibraryImportSliceOptions? sliceOptions = null)
    : IExistingLibraryImportService
{
    private readonly LibraryImportSliceOptions _slice = sliceOptions ?? LibraryImportSliceOptions.Default;

    private static readonly string[] VideoExtensions =
    [
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".ts"
    ];

    private static readonly Regex YearPattern = new(@"\b(19|20)\d{2}\b", RegexOptions.Compiled);
    private static readonly Regex EpisodePattern = new(@"^(?<title>.+?)[\s._-]+S\d{1,2}E\d{1,2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EpisodeNumberPattern = new(@"S(?<season>\d{1,2})(?<episodes>(?:E\d{1,2})+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MultiEpisodeSegmentPattern = new(@"E(?<episode>\d{1,2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CleanupTokensPattern = new(
        @"\b(remux|bluray|blu-ray|bdrip|web[-\s]?dl|webrip|web|hdtv|sdtv|dvd|x264|x265|hevc|av1|720p|1080p|2160p)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<LibraryImportRunProgress?> StartAsync(string libraryId, CancellationToken cancellationToken)
    {
        var library = await FindLibraryAsync(libraryId, cancellationToken);
        if (library is null || string.IsNullOrWhiteSpace(library.RootPath) || !Directory.Exists(library.RootPath))
        {
            return null;
        }

        var run = await importRunsRepository.CreateOrGetActiveAsync(
            library.Id,
            library.Name,
            library.MediaType,
            library.RootPath,
            cancellationToken);

        return LibraryImportRunProgress.From(run, timeProvider.GetUtcNow());
    }

    public async Task<LibraryImportRunProgress?> GetProgressAsync(string libraryId, CancellationToken cancellationToken)
    {
        var run = await importRunsRepository.GetActiveForLibraryAsync(libraryId, cancellationToken)
            ?? await importRunsRepository.GetLatestForLibraryAsync(libraryId, cancellationToken);

        return run is null ? null : LibraryImportRunProgress.From(run, timeProvider.GetUtcNow());
    }

    public async Task<LibraryImportRunProgress?> SetStateAsync(
        string libraryId,
        string desiredStatus,
        CancellationToken cancellationToken)
    {
        var run = await importRunsRepository.GetActiveForLibraryAsync(libraryId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        // Pausing a queued run is as valid as pausing a running one, and
        // resuming is only meaningful from paused. Cancelling works from
        // anywhere the run is still alive.
        IReadOnlyList<string> allowed = desiredStatus switch
        {
            LibraryImportRunStatuses.Paused => [LibraryImportRunStatuses.Queued, LibraryImportRunStatuses.Running],
            LibraryImportRunStatuses.Running => [LibraryImportRunStatuses.Paused],
            LibraryImportRunStatuses.Cancelled => LibraryImportRunStatuses.Active,
            _ => []
        };

        if (allowed.Count == 0)
        {
            return null;
        }

        var changed = await importRunsRepository.TrySetStatusAsync(
            run.Id,
            desiredStatus,
            allowed,
            lastError: null,
            cancellationToken);

        if (!changed)
        {
            return null;
        }

        var updated = await importRunsRepository.GetAsync(run.Id, cancellationToken);
        return updated is null ? null : LibraryImportRunProgress.From(updated, timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<LibraryImportIssueItem>> ListIssuesAsync(
        string libraryId,
        int take,
        CancellationToken cancellationToken)
    {
        var run = await importRunsRepository.GetActiveForLibraryAsync(libraryId, cancellationToken)
            ?? await importRunsRepository.GetLatestForLibraryAsync(libraryId, cancellationToken);

        return run is null
            ? []
            : await importRunsRepository.ListIssuesAsync(run.Id, take, cancellationToken);
    }

    public Task<IReadOnlyList<LibraryImportResumeCandidate>> ListResumableRunsAsync(
        DateTimeOffset idleBeforeUtc,
        int take,
        CancellationToken cancellationToken)
        => importRunsRepository.ListResumableRunsAsync(idleBeforeUtc, take, cancellationToken);

    public async Task<LibraryImportSliceOutcome> RunSliceAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            return await RunSliceCoreAsync(runId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The run keeps its status and its position. The job system retries,
            // and the resume sweep picks it up if those retries run out — but
            // whoever is watching the import needs to see why it stopped moving
            // rather than a progress bar that has quietly frozen.
            await importRunsRepository.RecordErrorAsync(runId, ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task<LibraryImportSliceOutcome> RunSliceCoreAsync(string runId, CancellationToken cancellationToken)
    {
        var run = await importRunsRepository.GetAsync(runId, cancellationToken);
        if (run is null)
        {
            return new LibraryImportSliceOutcome("missing", 0, 0, false, "Import run no longer exists.");
        }

        if (!LibraryImportRunStatuses.IsActive(run.Status))
        {
            return new LibraryImportSliceOutcome(run.Status, 0, run.ProcessedCount, false, $"Import run is {run.Status}.");
        }

        // A pause takes effect at the next slice boundary rather than mid-batch,
        // so a paused run always stops on a committed position.
        if (string.Equals(run.Status, LibraryImportRunStatuses.Paused, StringComparison.OrdinalIgnoreCase))
        {
            return new LibraryImportSliceOutcome(run.Status, 0, run.ProcessedCount, false, "Import run is paused.");
        }

        var library = await FindLibraryAsync(run.LibraryId, cancellationToken);
        if (library is null || string.IsNullOrWhiteSpace(library.RootPath) || !Directory.Exists(library.RootPath))
        {
            await importRunsRepository.TrySetStatusAsync(
                run.Id,
                LibraryImportRunStatuses.Failed,
                LibraryImportRunStatuses.Active,
                "The library folder is no longer readable.",
                cancellationToken);

            return new LibraryImportSliceOutcome(
                LibraryImportRunStatuses.Failed,
                0,
                run.ProcessedCount,
                false,
                "The library folder is no longer readable.");
        }

        // The top level is listed and sorted every slice. That is what makes
        // the cursor meaningful — directory enumeration order is not something
        // the filesystem promises to keep stable — and it also keeps the
        // estimate honest as files are added while the import runs.
        var entries = ListTopLevelEntries(library.RootPath);

        if (!await importRunsRepository.MarkRunningAsync(run.Id, entries.Count, cancellationToken))
        {
            var latest = await importRunsRepository.GetAsync(run.Id, cancellationToken);
            return new LibraryImportSliceOutcome(
                latest?.Status ?? LibraryImportRunStatuses.Cancelled,
                0,
                run.ProcessedCount,
                false,
                "Import run is no longer active.");
        }

        var isMovies = string.Equals(library.MediaType, "movies", StringComparison.OrdinalIgnoreCase);
        var batchSize = isMovies ? _slice.MovieBatchSize : _slice.SeriesBatchSize;

        var stopwatch = Stopwatch.StartNew();
        var processedInSlice = 0;
        var moreWorkRemains = false;

        var pending = new List<PendingImport>(batchSize);
        var progress = new SliceProgress();
        string? lastExamined = null;

        foreach (var entry in entries)
        {
            // Ordinal, because the cursor is compared the same way it was
            // sorted; a culture-aware comparison here would silently reorder
            // paths between slices.
            if (run.Cursor is { } cursor && string.CompareOrdinal(entry, cursor) <= 0)
            {
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                moreWorkRemains = true;
                break;
            }

            if (processedInSlice >= _slice.MaxItemsPerSlice || stopwatch.Elapsed >= _slice.MaxSliceDuration)
            {
                moreWorkRemains = true;
                break;
            }

            var detected = isMovies
                ? DetectMovie(entry)
                : DetectSeries(entry);

            lastExamined = entry;
            processedInSlice++;
            progress.Processed++;

            if (detected.Issue is { } issue)
            {
                await importRunsRepository.RecordIssueAsync(
                    run.Id,
                    run.LibraryId,
                    entry,
                    issue.Kind,
                    issue.Detail,
                    cancellationToken);
                progress.Deferred++;
            }

            if (detected.Item is null)
            {
                // Nothing importable here — an extras folder, a subtitle-only
                // directory. It still counts as processed, and the cursor still
                // has to move past it or the run would never finish. It may only
                // move when nothing is buffered, though: advancing over an
                // unwritten batch is how a crash would silently skip titles.
                if (pending.Count == 0)
                {
                    progress.Cursor = entry;
                }

                continue;
            }

            pending.Add(new PendingImport(detected.Item));

            if (pending.Count >= batchSize)
            {
                await FlushAsync(run, library, isMovies, pending, lastExamined, progress, cancellationToken);
            }
        }

        if (pending.Count > 0)
        {
            await FlushAsync(run, library, isMovies, pending, lastExamined, progress, cancellationToken);
        }
        else if (progress.HasUnrecordedProgress)
        {
            await RecordProgressAsync(run.Id, progress, cancellationToken);
        }

        var processedTotal = run.ProcessedCount + progress.RecordedProcessed;

        if (moreWorkRemains)
        {
            return new LibraryImportSliceOutcome(
                LibraryImportRunStatuses.Running,
                processedInSlice,
                processedTotal,
                true,
                $"Imported {processedTotal} of about {entries.Count}.");
        }

        await importRunsRepository.TrySetStatusAsync(
            run.Id,
            LibraryImportRunStatuses.Completed,
            [LibraryImportRunStatuses.Queued, LibraryImportRunStatuses.Running],
            lastError: null,
            cancellationToken);

        return new LibraryImportSliceOutcome(
            LibraryImportRunStatuses.Completed,
            processedInSlice,
            processedTotal,
            false,
            $"Imported {processedTotal} item{(processedTotal == 1 ? string.Empty : "s")} from {library.Name}.");
    }

    private async Task FlushAsync(
        LibraryImportRunItem run,
        LibraryItem library,
        bool isMovies,
        List<PendingImport> pending,
        string? cursor,
        SliceProgress progress,
        CancellationToken cancellationToken)
    {
        var created = 0;

        try
        {
            created = isMovies
                ? await movieCatalogRepository.ImportExistingBatchAsync(
                    library.Id,
                    pending.Select(item => ToMovieRequest(library, item.Item)).ToArray(),
                    cancellationToken)
                : await seriesCatalogRepository.ImportExistingBatchAsync(
                    library.Id,
                    pending.Select(item => ToSeriesRequest(library, item.Item)).ToArray(),
                    cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One bad title must not cost the other 249 in the batch, and must
            // not stop the run. Retry the batch one at a time so the failure is
            // attributed to the title that actually caused it.
            created = await FlushIndividuallyAsync(run, library, isMovies, pending, progress, cancellationToken);
        }

        progress.Imported += created;
        progress.Skipped += pending.Count - created;
        progress.Cursor = cursor ?? progress.Cursor;
        progress.Samples.AddRange(pending.Select(item => item.Item.Title));
        pending.Clear();

        await RecordProgressAsync(run.Id, progress, cancellationToken);
    }

    private async Task<int> FlushIndividuallyAsync(
        LibraryImportRunItem run,
        LibraryItem library,
        bool isMovies,
        List<PendingImport> pending,
        SliceProgress progress,
        CancellationToken cancellationToken)
    {
        var created = 0;

        foreach (var item in pending)
        {
            try
            {
                created += isMovies
                    ? await movieCatalogRepository.ImportExistingBatchAsync(
                        library.Id,
                        [ToMovieRequest(library, item.Item)],
                        cancellationToken)
                    : await seriesCatalogRepository.ImportExistingBatchAsync(
                        library.Id,
                        [ToSeriesRequest(library, item.Item)],
                        cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await importRunsRepository.RecordIssueAsync(
                    run.Id,
                    run.LibraryId,
                    item.Item.FilePath ?? item.Item.Title,
                    "writeFailed",
                    ex.Message,
                    cancellationToken);
                progress.Deferred++;
            }
        }

        return created;
    }

    private async Task RecordProgressAsync(string runId, SliceProgress progress, CancellationToken cancellationToken)
    {
        await importRunsRepository.RecordSliceAsync(
            runId,
            progress.Cursor,
            progress.Processed - progress.RecordedProcessed,
            progress.Imported - progress.RecordedImported,
            progress.Skipped - progress.RecordedSkipped,
            progress.Deferred - progress.RecordedDeferred,
            progress.Samples,
            cancellationToken);

        progress.MarkRecorded();
    }

    private async Task<LibraryItem?> FindLibraryAsync(string libraryId, CancellationToken cancellationToken)
        => (await librariesRepository.ListLibrariesAsync(cancellationToken))
            .FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.OrdinalIgnoreCase));

    private ExistingMovieImportRequest ToMovieRequest(LibraryItem library, DetectedLibraryItem item)
    {
        var decision = Decide(library, item);

        return new ExistingMovieImportRequest(
            Title: item.Title,
            ReleaseYear: item.Year,
            WantedStatus: decision.WantedStatus,
            WantedReason: decision.WantedReason,
            CurrentQuality: decision.CurrentQuality,
            TargetQuality: decision.TargetQuality,
            QualityCutoffMet: decision.QualityCutoffMet,
            UnmonitorWhenCutoffMet: false,
            FilePath: item.FilePath,
            FileSizeBytes: item.FileSizeBytes);
    }

    private ExistingSeriesImportRequest ToSeriesRequest(LibraryItem library, DetectedLibraryItem item)
    {
        var decision = Decide(library, item);

        return new ExistingSeriesImportRequest(
            Title: item.Title,
            StartYear: item.Year,
            WantedStatus: decision.WantedStatus,
            WantedReason: decision.WantedReason,
            CurrentQuality: decision.CurrentQuality,
            TargetQuality: decision.TargetQuality,
            QualityCutoffMet: decision.QualityCutoffMet,
            UnmonitorWhenCutoffMet: false,
            FilePath: item.FilePath,
            FileSizeBytes: item.FileSizeBytes,
            Episodes: item.Episodes);
    }

    private LibraryQualityDecision Decide(LibraryItem library, DetectedLibraryItem item)
        => mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
            MediaType: library.MediaType,
            HasFile: true,
            CurrentQuality: item.DetectedQuality,
            CutoffQuality: library.CutoffQuality,
            UpgradeUntilCutoff: library.UpgradeUntilCutoff,
            UpgradeUnknownItems: library.UpgradeUnknownItems));

    /// <summary>
    /// The entries the run walks, in a stable order.
    ///
    /// This is the one list the import holds that grows with the library, and
    /// it is unavoidable: you cannot import a folder without listing it. It
    /// holds paths, not catalogue rows, and nothing below it is materialised —
    /// each entry is parsed, written and released as it is reached.
    /// </summary>
    private static List<string> ListTopLevelEntries(string rootPath)
    {
        var entries = new List<string>();

        try
        {
            entries.AddRange(Directory.EnumerateDirectories(rootPath));
            entries.AddRange(Directory.EnumerateFiles(rootPath).Where(IsVideoFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return entries;
        }

        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private static DetectionResult DetectMovie(string entry)
    {
        if (File.Exists(entry))
        {
            var rawFileName = Path.GetFileNameWithoutExtension(entry);
            return new DetectionResult(
                ParseTitle(rawFileName) with
                {
                    DetectedQuality = LibraryQualityDecider.DetectQuality(rawFileName),
                    FilePath = entry,
                    FileSizeBytes = GetFileSize(entry)
                },
                null);
        }

        string? videoFile;
        try
        {
            videoFile = EnumerateVideoFiles(entry).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DetectionResult(null, new DetectionIssue("unreadable", ex.Message));
        }

        if (videoFile is null)
        {
            return new DetectionResult(null, null);
        }

        var rawName = Path.GetFileName(entry);
        return new DetectionResult(
            ParseTitle(rawName) with
            {
                DetectedQuality = LibraryQualityDecider.DetectQuality(rawName),
                FilePath = videoFile,
                FileSizeBytes = GetFileSize(videoFile)
            },
            null);
    }

    private static DetectionResult DetectSeries(string entry)
    {
        if (File.Exists(entry))
        {
            var fileName = Path.GetFileNameWithoutExtension(entry);
            var match = EpisodePattern.Match(fileName);
            if (!match.Success)
            {
                // A loose video file at the root of a TV library that carries no
                // season/episode marker could be anything. Guessing at thousands
                // of these is how a library ends up quietly wrong, so it is set
                // aside instead.
                return new DetectionResult(null, new DetectionIssue(
                    "ambiguousEpisode",
                    "The file name does not say which season and episode this is."));
            }

            return new DetectionResult(
                ParseTitle(match.Groups["title"].Value) with
                {
                    DetectedQuality = LibraryQualityDecider.DetectQuality(fileName),
                    FilePath = entry,
                    FileSizeBytes = GetFileSize(entry),
                    Episodes = DetectEpisodes([entry])
                },
                null);
        }

        string[] videoFiles;
        try
        {
            videoFiles = EnumerateVideoFiles(entry).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DetectionResult(null, new DetectionIssue("unreadable", ex.Message));
        }

        if (videoFiles.Length == 0)
        {
            return new DetectionResult(null, null);
        }

        var rawName = Path.GetFileName(entry);
        var episodes = DetectEpisodes(videoFiles);
        var item = ParseTitle(rawName) with
        {
            DetectedQuality = videoFiles
                .Select(file => LibraryQualityDecider.DetectQuality(Path.GetFileNameWithoutExtension(file)))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            FilePath = videoFiles[0],
            FileSizeBytes = GetFileSize(videoFiles[0]),
            Episodes = episodes
        };

        // The show is still imported — it exists, and the user can see it. What
        // is set aside is the claim to know which episodes are present.
        var issue = episodes.Count == 0
            ? new DetectionIssue(
                "ambiguousEpisode",
                $"Found {videoFiles.Length} video file{(videoFiles.Length == 1 ? string.Empty : "s")} but no season or episode numbers in their names.")
            : null;

        return new DetectionResult(item, issue);
    }

    private static bool IsVideoFile(string path)
        => VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateVideoFiles(string path)
        => Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Where(IsVideoFile);

    private static long? GetFileSize(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DetectedLibraryItem ParseTitle(string raw)
    {
        var normalized = raw
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Trim();

        int? year = null;
        var yearMatches = YearPattern.Matches(normalized);
        var yearMatch = yearMatches.Count > 0 ? yearMatches[^1] : Match.Empty;
        if (yearMatch.Success && int.TryParse(yearMatch.Value, out var parsedYear))
        {
            year = parsedYear;
            normalized = normalized.Remove(yearMatch.Index, yearMatch.Length).Trim();
        }

        normalized = CleanupTokensPattern.Replace(normalized, " ").Trim();
        normalized = Regex.Replace(normalized, @"\[[^\]]+\]|\([^\)]+\)", string.Empty).Trim();
        normalized = Regex.Replace(normalized, @"\(\s*\)", string.Empty).Trim();
        normalized = Regex.Replace(normalized, @"\s{2,}", " ").Trim('-', ' ');

        return new DetectedLibraryItem(string.IsNullOrWhiteSpace(normalized) ? raw.Trim() : normalized, year);
    }

    private static IReadOnlyList<ImportedEpisodeItem> DetectEpisodes(IEnumerable<string> files)
        => files
            .SelectMany(ExtractEpisodes)
            .Distinct()
            .OrderBy(item => item.SeasonNumber)
            .ThenBy(item => item.EpisodeNumber)
            .ToArray();

    private static IEnumerable<ImportedEpisodeItem> ExtractEpisodes(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var match = EpisodeNumberPattern.Match(fileName);
        if (!match.Success)
        {
            yield break;
        }

        var seasonNumber = int.Parse(match.Groups["season"].Value);
        foreach (Match episodeMatch in MultiEpisodeSegmentPattern.Matches(match.Groups["episodes"].Value))
        {
            yield return new ImportedEpisodeItem(
                SeasonNumber: seasonNumber,
                EpisodeNumber: int.Parse(episodeMatch.Groups["episode"].Value),
                HasFile: true,
                FilePath: filePath,
                FileSizeBytes: GetFileSize(filePath));
        }
    }

    private sealed record DetectedLibraryItem(
        string Title,
        int? Year,
        string? DetectedQuality = null,
        string? FilePath = null,
        long? FileSizeBytes = null,
        IReadOnlyList<ImportedEpisodeItem>? Episodes = null);

    private sealed record DetectionResult(DetectedLibraryItem? Item, DetectionIssue? Issue);

    private sealed record DetectionIssue(string Kind, string Detail);

    private sealed record PendingImport(DetectedLibraryItem Item);

    /// <summary>
    /// Counters for the slice in progress, and how much of them has already
    /// been written to the run row. Deltas are what gets persisted, so a
    /// mid-slice flush and the final flush cannot double-count.
    /// </summary>
    private sealed class SliceProgress
    {
        public int Processed { get; set; }

        public int Imported { get; set; }

        public int Skipped { get; set; }

        public int Deferred { get; set; }

        public string? Cursor { get; set; }

        public List<string> Samples { get; } = [];

        public int RecordedProcessed { get; private set; }

        public int RecordedImported { get; private set; }

        public int RecordedSkipped { get; private set; }

        public int RecordedDeferred { get; private set; }

        public bool HasUnrecordedProgress => Processed > RecordedProcessed || Deferred > RecordedDeferred;

        public void MarkRecorded()
        {
            RecordedProcessed = Processed;
            RecordedImported = Imported;
            RecordedSkipped = Skipped;
            RecordedDeferred = Deferred;
            Samples.Clear();
        }
    }
}
