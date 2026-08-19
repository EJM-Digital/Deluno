using System.Text.Json;
using Deluno.Filesystem;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Worker.Intake;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>
/// The recurring maintenance and automation passes that used to live directly
/// on <see cref="DelunoHeartbeatWorker"/>. Each pass claims its own slot in the
/// jobs database via <see cref="IJobQueueRepository.TryClaimScheduledPassAsync"/>
/// rather than gating on an in-memory timestamp, so the schedule survives a
/// restart and two hosts sharing one database cannot both run the same pass in
/// the same window.
/// </summary>
public sealed class WorkPlanner(
    ILogger<WorkPlanner> logger,
    IJobQueueRepository jobQueueRepository)
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task RunDispatchCleanupAsync(
        IDispatchCleanupService cleanupService,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("dispatch.cleanup", TimeSpan.FromHours(6), cancellationToken))
        {
            return;
        }

        try
        {
            await cleanupService.RunCleanupPassAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dispatch cleanup pass failed.");
        }
    }

    public async Task RunDispatchRetryPassAsync(
        IDownloadRetryService downloadRetryService,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("dispatch.retry", TimeSpan.FromMinutes(2), cancellationToken))
        {
            return;
        }

        try
        {
            await downloadRetryService.RunRetryPassAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dispatch retry pass failed.");
        }
    }

    public async Task PlanMetadataRefreshAutomationAsync(
        IJobScheduler jobScheduler,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<JobQueueItem> existingJobs,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("metadata.refresh", TimeSpan.FromHours(6), cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var staleBefore = now.AddDays(-14);

        var queuedMovieIds = existingJobs
            .Where(job => job.JobType == "movies.metadata.refresh")
            .Select(job => job.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queuedSeriesIds = existingJobs
            .Where(job => job.JobType == "series.metadata.refresh")
            .Select(job => job.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var moviesToRefresh = (await movieCatalogRepository.ListAsync(cancellationToken))
            .Where(item => string.IsNullOrWhiteSpace(item.MetadataProviderId) || item.MetadataUpdatedUtc is null || item.MetadataUpdatedUtc < staleBefore)
            .Where(item => !queuedMovieIds.Contains(item.Id))
            .OrderBy(item => item.MetadataUpdatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        foreach (var movie in moviesToRefresh)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "movies.metadata.refresh",
                    Source: "metadata",
                    PayloadJson: JsonSerializer.Serialize(new { movie.Id, movie.Title, movie.ReleaseYear, scheduled = true }),
                    RelatedEntityType: "movie",
                    RelatedEntityId: movie.Id),
                cancellationToken);
        }

        var seriesToRefresh = (await seriesCatalogRepository.ListAsync(cancellationToken))
            .Where(item => string.IsNullOrWhiteSpace(item.MetadataProviderId) || item.MetadataUpdatedUtc is null || item.MetadataUpdatedUtc < staleBefore)
            .Where(item => !queuedSeriesIds.Contains(item.Id))
            .OrderBy(item => item.MetadataUpdatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        foreach (var series in seriesToRefresh)
        {
            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "series.metadata.refresh",
                    Source: "metadata",
                    PayloadJson: JsonSerializer.Serialize(new { series.Id, series.Title, series.StartYear, scheduled = true }),
                    RelatedEntityType: "series",
                    RelatedEntityId: series.Id),
                cancellationToken);
        }
    }

    public async Task PlanIntakeAutomationAsync(
        IIntakeSyncService intakeSyncService,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("intake.automation", TimeSpan.FromMinutes(5), cancellationToken))
        {
            return;
        }

        await intakeSyncService.PlanDueSyncJobsAsync(cancellationToken);
    }

    public async Task PlanImportAutomationAsync(
        IJobScheduler jobScheduler,
        IPlatformSettingsRepository platformSettingsRepository,
        ILibrariesRepository librariesRepository,
        IDownloadClientTelemetryService downloadClientTelemetryService,
        IProcessorConnectionService processorConnectionService,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await jobQueueRepository.TryClaimScheduledPassAsync("import.automation", TimeSpan.FromSeconds(15), cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
        if (libraries.Count == 0)
        {
            return;
        }

        var existingJobs = await jobQueueRepository.ListAsync(300, cancellationToken);
        var knownImportSources = existingJobs
            .Where(job => job.JobType == "filesystem.import.execute")
            .Select(job => TryReadImportSourcePath(job.PayloadJson))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => NormalizeSourceKey(source!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recentWaiting = await activityFeedRepository.ListActivityAsync(150, null, null, cancellationToken);
        await ReconcileMatchedProcessorOutputsAsync(
            jobScheduler,
            platformSettingsRepository,
            activityFeedRepository,
            libraries,
            knownImportSources,
            cancellationToken);
        await RecordUnmatchedProcessorOutputsAsync(
            activityFeedRepository,
            movieCatalogRepository,
            seriesCatalogRepository,
            libraries,
            knownImportSources,
            recentWaiting,
            cancellationToken);
        await RecordProcessorTimeoutsAsync(
            platformSettingsRepository,
            activityFeedRepository,
            movieCatalogRepository,
            seriesCatalogRepository,
            libraries,
            recentWaiting,
            now,
            cancellationToken);

        var recentWaitingKeys = recentWaiting
            .Where(item => item.Category == "processing.waiting" && item.CreatedUtc > now.AddHours(-6))
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var telemetry = await downloadClientTelemetryService.GetOverviewAsync(cancellationToken);
        await downloadClientTelemetryService.RunConfiguredHealthRemediationAsync(telemetry, cancellationToken);
        foreach (var item in telemetry.Clients.SelectMany(client => client.Queue))
        {
            if (item.Status is not ("importReady" or "completed"))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.SourcePath))
            {
                continue;
            }

            var sourceKey = NormalizeSourceKey(item.SourcePath);
            if (knownImportSources.Contains(sourceKey))
            {
                continue;
            }

            var library = ResolveLibraryForQueueItem(item, libraries);
            if (library is null)
            {
                continue;
            }

            if (string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase))
            {
                var handoff = await platformSettingsRepository.EnsureProcessorHandoffAsync(
                    new CreateProcessorHandoffRequest(
                        library.Id,
                        library.MediaType,
                        item.ClientId,
                        item.Id,
                        item.ReleaseName,
                        item.SourcePath,
                        library.ProcessorName),
                    cancellationToken);
                var currentHandoff = handoff;
                ProcessorSubmissionResult? submission = null;
                var connection = await platformSettingsRepository.FindProcessorConnectionByNameAsync(library.ProcessorName, cancellationToken);
                if (connection is { IsEnabled: true } && handoff.Status == "waiting")
                {
                    submission = await processorConnectionService.SubmitAsync(connection, handoff, cancellationToken);
                    currentHandoff = await platformSettingsRepository.UpdateProcessorHandoffAsync(
                        handoff.Id,
                        submission.Status,
                        null,
                        null,
                        submission.IsAccepted ? null : submission.Message,
                        cancellationToken) ?? handoff;
                    await platformSettingsRepository.RecordProcessorConnectionHealthAsync(
                        connection.Id,
                        submission.IsAccepted ? "healthy" : "degraded",
                        submission.Message,
                        cancellationToken);
                    await activityFeedRepository.RecordActivityAsync(
                        submission.IsAccepted ? "processing.submitted" : "processing.submission-failed",
                        submission.IsAccepted
                            ? $"Deluno submitted {item.Title} to {connection.Name}."
                            : $"Deluno could not submit {item.Title} to {connection.Name}. {submission.Message}",
                        JsonSerializer.Serialize(new
                        {
                            HandoffId = handoff.Id,
                            ConnectionId = connection.Id,
                            connection.Name,
                            connection.Provider,
                            submission.Status,
                            submission.StatusCode
                        }, PayloadJsonOptions),
                        null,
                        "processor-handoff",
                        handoff.Id,
                        cancellationToken);
                }
                var waitKey = handoff.Id;
                if (currentHandoff.Status != "failed" && recentWaitingKeys.Add(waitKey))
                {
                    var waitingMessage = submission?.IsAccepted == true
                        ? $"Deluno submitted {item.Title} to {connection!.Name} and is waiting for a cleaned output."
                        : $"{item.Title} is complete in {item.ClientName}; Deluno is waiting for {library.ProcessorName ?? "the configured processor"} to produce a cleaned output.";
                    await activityFeedRepository.RecordActivityAsync(
                        "processing.waiting",
                        waitingMessage,
                        JsonSerializer.Serialize(new
                        {
                            item.ClientId,
                            item.ClientName,
                            item.ReleaseName,
                            item.SourcePath,
                            HandoffId = handoff.Id,
                            library.Id,
                            library.Name,
                            library.ProcessorName,
                            library.ProcessorOutputPath,
                            library.ProcessorTimeoutMinutes
                        }, PayloadJsonOptions),
                        null,
                        "download",
                        waitKey,
                        cancellationToken);
                }

                continue;
            }

            var dispatchId = await jobQueueRepository.FindRecentDispatchIdAsync(
                item.ClientId,
                item.ReleaseName,
                cancellationToken);

            var request = new ImportExecuteRequest(
                Preview: new ImportPreviewRequest(
                    SourcePath: item.SourcePath,
                    FileName: InferImportFileName(item),
                    MediaType: item.MediaType,
                    Title: item.Title,
                    Year: InferYear(item.ReleaseName),
                    Genres: [],
                    Tags: string.IsNullOrWhiteSpace(item.Category) ? [] : [item.Category],
                    Studio: null,
                    OriginalLanguage: null),
                TransferMode: "auto",
                Overwrite: false,
                AllowCopyFallback: true,
                ForceReplacement: false,
                DispatchId: dispatchId);

            var job = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "download-client",
                    PayloadJson: JsonSerializer.Serialize(request, PayloadJsonOptions),
                    RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                    RelatedEntityId: null),
                cancellationToken);

            knownImportSources.Add(sourceKey);

            await activityFeedRepository.RecordActivityAsync(
                "filesystem.import.auto-queued",
                $"{item.Title} finished in {item.ClientName}; Deluno queued it for import into {library.Name}.",
                JsonSerializer.Serialize(new
                {
                    item.ClientId,
                    item.ClientName,
                    item.ReleaseName,
                    item.SourcePath,
                    LibraryId = library.Id,
                    LibraryName = library.Name,
                    JobId = job.Id
                }, PayloadJsonOptions),
                job.Id,
                "library",
                library.Id,
                cancellationToken);
        }
    }

    private static async Task RecordUnmatchedProcessorOutputsAsync(
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<LibraryItem> libraries,
        ISet<string> knownImportSources,
        IReadOnlyList<ActivityEventItem> recentActivity,
        CancellationToken cancellationToken)
    {
        var reportedOutputKeys = recentActivity
            .Where(item => item.Category == "processing.output.unmatched")
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refineLibraries = libraries
            .Where(library =>
                string.Equals(library.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(library.ProcessorOutputPath) &&
                Directory.Exists(library.ProcessorOutputPath))
            .ToArray();

        foreach (var library in refineLibraries)
        {
            IReadOnlyList<string> files;
            try
            {
                files = Directory
                    .EnumerateFiles(library.ProcessorOutputPath!, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(IsImportableVideoFile)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(10)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "processing.output.scan-failed",
                    $"Deluno could not scan the processor output folder for {library.Name}. Check the path and service permissions.",
                    JsonSerializer.Serialize(new
                    {
                        LibraryId = library.Id,
                        LibraryName = library.Name,
                        library.ProcessorOutputPath,
                        Error = ex.Message
                    }, PayloadJsonOptions),
                    null,
                    "library",
                    library.Id,
                    cancellationToken);
                continue;
            }

            foreach (var file in files)
            {
                var sourceKey = NormalizeSourceKey(file);
                if (knownImportSources.Contains(sourceKey))
                {
                    continue;
                }

                var outputKey = $"{library.Id}:{Path.GetFileName(file).ToLowerInvariant()}";
                if (!reportedOutputKeys.Add(outputKey))
                {
                    continue;
                }

                var title = Path.GetFileNameWithoutExtension(file);
                var summary = $"Deluno found a cleaned output in {library.Name}, but cannot safely match it to a download hand-off.";
                var recommendation = "Use the processor callback with Deluno's hand-off ID, or review this file and import it manually. Deluno did not import it automatically.";
                if (library.MediaType == "tv")
                {
                    await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                        new CreateSeriesImportRecoveryCaseRequest(title, "processor-unmatched-output", summary, recommendation, JsonSerializer.Serialize(new { LibraryId = library.Id, FileName = Path.GetFileName(file) }, PayloadJsonOptions)),
                        cancellationToken);
                }
                else
                {
                    await movieCatalogRepository.AddImportRecoveryCaseAsync(
                        new CreateMovieImportRecoveryCaseRequest(title, "processor-unmatched-output", summary, recommendation, JsonSerializer.Serialize(new { LibraryId = library.Id, FileName = Path.GetFileName(file) }, PayloadJsonOptions)),
                        cancellationToken);
                }
                await activityFeedRepository.RecordActivityAsync(
                    "processing.output.unmatched",
                    summary,
                    JsonSerializer.Serialize(new
                    {
                        LibraryId = library.Id,
                        LibraryName = library.Name,
                        library.MediaType,
                        FileName = Path.GetFileName(file),
                        Recommendation = recommendation
                    }, PayloadJsonOptions),
                    null,
                    "processor-output",
                    outputKey,
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// Completes the processor-agnostic path. A processor does not need to call
    /// Deluno or use a vendor adapter: it writes its result below the configured
    /// processed-output root while retaining the final source-folder name. That
    /// one stable path component lets Deluno match the output to a durable
    /// hand-off without guessing from a release title.
    /// </summary>
    private static async Task ReconcileMatchedProcessorOutputsAsync(
        IJobScheduler jobScheduler,
        IPlatformSettingsRepository platformSettingsRepository,
        IActivityFeedRepository activityFeedRepository,
        IReadOnlyList<LibraryItem> libraries,
        ISet<string> knownImportSources,
        CancellationToken cancellationToken)
    {
        var waitingStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "waiting", "submitted", "accepted", "started"
        };
        var handoffs = await platformSettingsRepository.ListProcessorHandoffsAsync(null, 250, cancellationToken);

        foreach (var handoff in handoffs.Where(item => waitingStatuses.Contains(item.Status)))
        {
            var library = libraries.FirstOrDefault(item =>
                string.Equals(item.Id, handoff.LibraryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.ImportWorkflow, "refine-before-import", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(item.ProcessorOutputPath));
            if (library is null)
            {
                continue;
            }

            var candidates = FindCorrelatedProcessorOutputs(library.ProcessorOutputPath!, handoff.SourcePath)
                .Where(path => !knownImportSources.Contains(NormalizeSourceKey(path)))
                .ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            var outputPath = candidates[0];
            var importPayload = new
            {
                preview = new
                {
                    sourcePath = outputPath,
                    fileName = Path.GetFileName(outputPath),
                    mediaType = library.MediaType,
                    title = Path.GetFileNameWithoutExtension(outputPath),
                    year = (int?)null,
                    genres = Array.Empty<string>(),
                    tags = new[] { "processed" },
                    studio = (string?)null,
                    originalLanguage = (string?)null
                },
                transferMode = "auto",
                overwrite = false,
                allowCopyFallback = true,
                forceReplacement = false
            };

            var importJob = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "filesystem.import.execute",
                    Source: "processor-output-watch",
                    PayloadJson: JsonSerializer.Serialize(importPayload),
                    RelatedEntityType: library.MediaType == "tv" ? "series" : "movie",
                    RelatedEntityId: null,
                    IdempotencyKey: $"processor-output:{library.Id}:{Path.GetFullPath(outputPath).ToLowerInvariant()}"),
                cancellationToken);

            await platformSettingsRepository.UpdateProcessorHandoffAsync(
                handoff.Id,
                "completed",
                outputPath,
                importJob.Id,
                null,
                cancellationToken);
            knownImportSources.Add(NormalizeSourceKey(outputPath));
            await activityFeedRepository.RecordActivityAsync(
                "processing.output.matched.import-queued",
                $"Deluno matched processed output for {handoff.ReleaseName} and queued it for import.",
                JsonSerializer.Serialize(new
                {
                    HandoffId = handoff.Id,
                    library.Id,
                    library.Name,
                    SourcePath = handoff.SourcePath,
                    OutputPath = outputPath,
                    JobId = importJob.Id,
                    Match = "stable processed-output subfolder"
                }, PayloadJsonOptions),
                importJob.Id,
                "processor-handoff",
                handoff.Id,
                cancellationToken);
        }
    }

    private static IReadOnlyList<string> FindCorrelatedProcessorOutputs(string outputRoot, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return [];
        }

        try
        {
            var root = Path.GetFullPath(outputRoot);
            if (!Directory.Exists(root))
            {
                return [];
            }

            var sourceLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
            if (string.IsNullOrWhiteSpace(sourceLeaf) || sourceLeaf is "." or "..")
            {
                return [];
            }

            var directoryNames = new[]
            {
                sourceLeaf,
                Path.GetFileNameWithoutExtension(sourceLeaf)
            }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var matches = new List<string>();
            foreach (var directoryName in directoryNames)
            {
                var expectedDirectory = Path.GetFullPath(Path.Combine(root, directoryName));
                if (!expectedDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(expectedDirectory))
                {
                    continue;
                }

                matches.AddRange(Directory.EnumerateFiles(expectedDirectory, "*.*", SearchOption.AllDirectories)
                    .Where(IsImportableVideoFile)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .Take(2));
            }

            return matches.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static async Task RecordProcessorTimeoutsAsync(
        IPlatformSettingsRepository platformSettingsRepository,
        IActivityFeedRepository activityFeedRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IReadOnlyList<LibraryItem> libraries,
        IReadOnlyList<ActivityEventItem> recentActivity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var timeoutKeys = recentActivity
            .Where(item => item.Category == "processing.timeout")
            .Select(item => item.RelatedEntityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var waiting in recentActivity.Where(item => item.Category == "processing.waiting"))
        {
            if (string.IsNullOrWhiteSpace(waiting.RelatedEntityId) ||
                timeoutKeys.Contains(waiting.RelatedEntityId))
            {
                continue;
            }

            var details = TryReadProcessingWaitDetails(waiting.DetailsJson);
            var library = !string.IsNullOrWhiteSpace(details.LibraryId)
                ? libraries.FirstOrDefault(item => string.Equals(item.Id, details.LibraryId, StringComparison.OrdinalIgnoreCase))
                : null;
            if (library is null)
            {
                continue;
            }

            var timeout = TimeSpan.FromMinutes(Math.Max(1, library.ProcessorTimeoutMinutes));
            if (now - waiting.CreatedUtc < timeout)
            {
                continue;
            }

            var title = details.ReleaseName ?? details.SourcePath ?? "Processor output";
            var summary = $"{title} waited longer than {library.ProcessorTimeoutMinutes} minutes for a cleaned processor output.";
            var recommended = library.ProcessorFailureMode switch
            {
                "import-original" => "Review the original download, then manually queue import if it is acceptable.",
                "manual-review" => "Open Queue recovery and choose retry, manual import, or dismiss.",
                _ => "Check the processor logs and output folder, then retry once the cleaned file exists."
            };

            if (library.MediaType == "tv")
            {
                await seriesCatalogRepository.AddImportRecoveryCaseAsync(
                    new CreateSeriesImportRecoveryCaseRequest(title, "processor-timeout", summary, recommended, waiting.DetailsJson),
                    cancellationToken);
            }
            else
            {
                await movieCatalogRepository.AddImportRecoveryCaseAsync(
                    new CreateMovieImportRecoveryCaseRequest(title, "processor-timeout", summary, recommended, waiting.DetailsJson),
                    cancellationToken);
            }

            await platformSettingsRepository.UpdateProcessorHandoffAsync(
                waiting.RelatedEntityId,
                "timed-out",
                null,
                null,
                summary,
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                "processing.timeout",
                summary,
                waiting.DetailsJson,
                null,
                "download",
                waiting.RelatedEntityId,
                cancellationToken);
        }
    }

    private static bool IsImportableVideoFile(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".mp4" or ".avi" or ".mov" or ".m4v";

    private static ProcessingWaitDetails TryReadProcessingWaitDetails(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
        {
            return new ProcessingWaitDetails(null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(detailsJson);
            var root = document.RootElement;
            return new ProcessingWaitDetails(
                TryGetProperty(root, "libraryId", out var libraryId) && libraryId.ValueKind == JsonValueKind.String ? libraryId.GetString() : null,
                TryGetProperty(root, "releaseName", out var releaseName) && releaseName.ValueKind == JsonValueKind.String ? releaseName.GetString() : null,
                TryGetProperty(root, "sourcePath", out var sourcePath) && sourcePath.ValueKind == JsonValueKind.String ? sourcePath.GetString() : null);
        }
        catch (JsonException)
        {
            return new ProcessingWaitDetails(null, null, null);
        }
    }

    private static LibraryItem? ResolveLibraryForQueueItem(DownloadQueueItem item, IReadOnlyList<LibraryItem> libraries)
    {
        if (!string.IsNullOrWhiteSpace(item.LibraryId))
        {
            var assignedLibrary = libraries.FirstOrDefault(library =>
                string.Equals(library.Id, item.LibraryId, StringComparison.OrdinalIgnoreCase));
            if (assignedLibrary is not null)
            {
                return assignedLibrary;
            }
        }

        var normalizedMediaType = item.MediaType.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
            item.MediaType.Equals("series", StringComparison.OrdinalIgnoreCase)
            ? "tv"
            : "movies";
        var mediaLibraries = libraries
            .Where(library => string.Equals(library.MediaType, normalizedMediaType, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(item.SourcePath))
        {
            var source = NormalizeSourceKey(item.SourcePath);
            var pathMatch = mediaLibraries.FirstOrDefault(library =>
                !string.IsNullOrWhiteSpace(library.DownloadsPath) &&
                source.StartsWith(NormalizeSourceKey(library.DownloadsPath), StringComparison.OrdinalIgnoreCase));
            if (pathMatch is not null)
            {
                return pathMatch;
            }
        }

        return mediaLibraries.FirstOrDefault();
    }

    private static string? TryReadImportSourcePath(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (!TryGetProperty(root, "preview", out var preview) ||
                !TryGetProperty(preview, "sourcePath", out var sourcePath) ||
                sourcePath.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return sourcePath.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string NormalizeSourceKey(string value)
        => value.Trim().TrimEnd('\\', '/').Replace('\\', '/');

    private static string InferImportFileName(DownloadQueueItem item)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(item.ReleaseName
            .Select(character => invalid.Contains(character) ? '.' : character)
            .ToArray())
            .Replace(' ', '.')
            .Trim('.');

        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal);
        }

        if (cleaned.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
            cleaned.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase))
        {
            return cleaned;
        }

        return $"{(string.IsNullOrWhiteSpace(cleaned) ? item.Id : cleaned)}.mkv";
    }

    private static int? InferYear(string value)
    {
        var parts = value.Split([' ', '.', '-', '_', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length == 4 &&
                int.TryParse(part, out var year) &&
                year is >= 1900 and <= 2100)
            {
                return year;
            }
        }

        return null;
    }

    private sealed record ProcessingWaitDetails(
        string? LibraryId,
        string? ReleaseName,
        string? SourcePath);
}
