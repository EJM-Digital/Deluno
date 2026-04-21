using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Series.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Deluno.Worker.Services;

public sealed class DelunoHeartbeatWorker(
    ILogger<DelunoHeartbeatWorker> logger,
    IJobQueueRepository jobQueueRepository,
    IPlatformSettingsRepository platformSettingsRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IActivityFeedRepository activityFeedRepository,
    TimeProvider timeProvider)
    : BackgroundService
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _workerId = $"worker-{Environment.MachineName.ToLowerInvariant()}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        logger.LogInformation("Deluno worker runtime started as {WorkerId}.", _workerId);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await jobQueueRepository.HeartbeatAsync(_workerId, stoppingToken);

            var settings = await platformSettingsRepository.GetAsync(stoppingToken);
            if (!settings.AutoStartJobs)
            {
                logger.LogDebug("Worker {WorkerId} heartbeat tick with auto-start disabled.", _workerId);
                continue;
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(stoppingToken);
            var automationPlans = libraries
                .Select(library => new Deluno.Jobs.Contracts.LibraryAutomationPlanItem(
                    LibraryId: library.Id,
                    LibraryName: library.Name,
                    MediaType: library.MediaType,
                    AutoSearchEnabled: library.AutoSearchEnabled,
                    MissingSearchEnabled: library.MissingSearchEnabled,
                    UpgradeSearchEnabled: library.UpgradeSearchEnabled,
                    SearchIntervalHours: library.SearchIntervalHours,
                    RetryDelayHours: library.RetryDelayHours,
                    MaxItemsPerRun: library.MaxItemsPerRun))
                .ToArray();

            await jobQueueRepository.PlanLibrarySearchesAsync(automationPlans, stoppingToken);

            var job = await jobQueueRepository.LeaseNextAsync(
                _workerId,
                TimeSpan.FromMinutes(2),
                stoppingToken);

            if (job is null)
            {
                logger.LogDebug("Worker {WorkerId} heartbeat tick with no pending jobs.", _workerId);
                continue;
            }

            try
            {
                logger.LogInformation("Processing job {JobId} of type {JobType}.", job.Id, job.JobType);
                var message = await ProcessJobAsync(
                    job,
                    platformSettingsRepository,
                    movieCatalogRepository,
                    seriesCatalogRepository,
                    activityFeedRepository,
                    timeProvider,
                    stoppingToken);

                await jobQueueRepository.CompleteAsync(job.Id, _workerId, message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker {WorkerId} failed processing job {JobId}.", _workerId, job.Id);
                await jobQueueRepository.FailAsync(job.Id, _workerId, ex.Message, stoppingToken);
            }
        }
    }

    private static async Task<string> ProcessJobAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IPlatformSettingsRepository platformSettingsRepository,
        IMovieCatalogRepository movieCatalogRepository,
        ISeriesCatalogRepository seriesCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (job.JobType == "library.search")
        {
            var payload = ParseLibraryPayload(job.PayloadJson);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.LibraryName))
            {
                var now = timeProvider.GetUtcNow();
                var routing = await platformSettingsRepository.GetLibraryRoutingAsync(payload.LibraryId, cancellationToken);
                var configuredSources = routing?.Sources.Count ?? 0;
                var configuredClients = routing?.DownloadClients.Count ?? 0;

                if (payload.MediaType == "movies")
                {
                    var candidates = await movieCatalogRepository.ListEligibleWantedAsync(
                        payload.LibraryId,
                        payload.MaxItems,
                        now,
                        cancellationToken);

                    foreach (var candidate in candidates)
                    {
                        var result = configuredSources == 0
                            ? "No indexers are linked to this library yet."
                            : configuredClients == 0
                                ? "No download client is linked to this library yet."
                                : $"Checked against {configuredSources} source{(configuredSources == 1 ? "" : "s")} and {configuredClients} download client{(configuredClients == 1 ? "" : "s")}.";

                        await movieCatalogRepository.RecordSearchAttemptAsync(
                            candidate.MovieId,
                            payload.LibraryId,
                            payload.TriggeredBy,
                            configuredSources == 0 || configuredClients == 0 ? "blocked" : "checked",
                            now,
                            now.AddHours(Math.Max(1, payload.RetryDelayHours)),
                            result,
                            cancellationToken);
                    }

                    await activityFeedRepository.RecordActivityAsync(
                        "library.search.executed",
                        FormatExecutionMessage(payload.LibraryName, candidates.Count, configuredSources, configuredClients, "movie"),
                        null,
                        job.Id,
                        "library",
                        payload.LibraryId,
                        cancellationToken);

                    return FormatCompletionMessage(payload.LibraryName, candidates.Count, configuredSources, configuredClients, "movie");
                }

                var seriesCandidates = await seriesCatalogRepository.ListEligibleWantedAsync(
                    payload.LibraryId,
                    payload.MaxItems,
                    now,
                    cancellationToken);

                foreach (var candidate in seriesCandidates)
                {
                    var result = configuredSources == 0
                        ? "No indexers are linked to this library yet."
                        : configuredClients == 0
                            ? "No download client is linked to this library yet."
                            : $"Checked against {configuredSources} source{(configuredSources == 1 ? "" : "s")} and {configuredClients} download client{(configuredClients == 1 ? "" : "s")}.";

                    await seriesCatalogRepository.RecordSearchAttemptAsync(
                        candidate.SeriesId,
                        payload.LibraryId,
                        payload.TriggeredBy,
                        configuredSources == 0 || configuredClients == 0 ? "blocked" : "checked",
                        now,
                        now.AddHours(Math.Max(1, payload.RetryDelayHours)),
                        result,
                        cancellationToken);
                }

                await activityFeedRepository.RecordActivityAsync(
                    "library.search.executed",
                    FormatExecutionMessage(payload.LibraryName, seriesCandidates.Count, configuredSources, configuredClients, "TV show"),
                    null,
                    job.Id,
                    "library",
                    payload.LibraryId,
                    cancellationToken);

                return FormatCompletionMessage(payload.LibraryName, seriesCandidates.Count, configuredSources, configuredClients, "TV show");
            }

            return "Finished checking a library.";
        }

        return job.JobType switch
        {
            "movies.quality.recalculate" => await RecalculateMovieQualityAsync(job, movieCatalogRepository, activityFeedRepository, stoppingToken: cancellationToken),
            "series.quality.recalculate" => await RecalculateSeriesQualityAsync(job, seriesCatalogRepository, activityFeedRepository, stoppingToken: cancellationToken),
            "movies.catalog.refresh" => "Finished checking your movie library.",
            "series.catalog.refresh" => "Finished checking your TV show library.",
            _ => "Finished a background task."
        };
    }

    private static async Task<string> RecalculateMovieQualityAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        IMovieCatalogRepository movieCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken stoppingToken)
    {
        var payload = ParseQualityPayload(job.PayloadJson);
        if (payload is null)
        {
            return "Finished refreshing movie quality decisions.";
        }

        var updated = await movieCatalogRepository.ReevaluateLibraryWantedStateAsync(
            payload.LibraryId,
            payload.CutoffQuality,
            payload.UpgradeUntilCutoff,
            payload.UpgradeUnknownItems,
            stoppingToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.quality.recalculated",
            $"Deluno refreshed quality decisions for {payload.LibraryName} across {updated} movie record{(updated == 1 ? "" : "s")}.",
            null,
            job.Id,
            "library",
            payload.LibraryId,
            stoppingToken);

        return $"Finished refreshing quality decisions for {payload.LibraryName}.";
    }

    private static async Task<string> RecalculateSeriesQualityAsync(
        Deluno.Jobs.Contracts.JobQueueItem job,
        ISeriesCatalogRepository seriesCatalogRepository,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken stoppingToken)
    {
        var payload = ParseQualityPayload(job.PayloadJson);
        if (payload is null)
        {
            return "Finished refreshing TV quality decisions.";
        }

        var updated = await seriesCatalogRepository.ReevaluateLibraryWantedStateAsync(
            payload.LibraryId,
            payload.CutoffQuality,
            payload.UpgradeUntilCutoff,
            payload.UpgradeUnknownItems,
            stoppingToken);

        await activityFeedRepository.RecordActivityAsync(
            "library.quality.recalculated",
            $"Deluno refreshed quality decisions for {payload.LibraryName} across {updated} TV show record{(updated == 1 ? "" : "s")}.",
            null,
            job.Id,
            "library",
            payload.LibraryId,
            stoppingToken);

        return $"Finished refreshing quality decisions for {payload.LibraryName}.";
    }

    private static string FormatExecutionMessage(
        string libraryName,
        int candidateCount,
        int sourceCount,
        int clientCount,
        string mediaLabel)
    {
        if (candidateCount == 0)
        {
            return $"Deluno checked {libraryName} and found nothing else to look for right now.";
        }

        if (sourceCount == 0)
        {
            return $"Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} to search in {libraryName}, but this library does not have any indexers linked yet.";
        }

        if (clientCount == 0)
        {
            return $"Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} to search in {libraryName}, but it still needs a download client for this library.";
        }

        return $"Deluno checked {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} in {libraryName} using {sourceCount} source{(sourceCount == 1 ? "" : "s")}.";
    }

    private static string FormatCompletionMessage(
        string libraryName,
        int candidateCount,
        int sourceCount,
        int clientCount,
        string mediaLabel)
    {
        if (candidateCount == 0)
        {
            return $"Finished checking {libraryName}. Nothing else needs attention right now.";
        }

        if (sourceCount == 0)
        {
            return $"Finished checking {libraryName}. Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} but this library still needs indexers.";
        }

        if (clientCount == 0)
        {
            return $"Finished checking {libraryName}. Deluno found {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} but this library still needs a download client.";
        }

        return $"Finished checking {libraryName}. Deluno reviewed {candidateCount} {mediaLabel}{(candidateCount == 1 ? "" : "s")} for new or better releases.";
    }

    private static LibrarySearchPayload? ParseLibraryPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibrarySearchPayload>(payloadJson ?? "{}", PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static LibraryQualityPayload? ParseQualityPayload(string? payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<LibraryQualityPayload>(payloadJson ?? "{}", PayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed record LibrarySearchPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        bool CheckMissing,
        bool CheckUpgrades,
        int MaxItems,
        int RetryDelayHours,
        string TriggeredBy);

    private sealed record LibraryQualityPayload(
        string LibraryId,
        string LibraryName,
        string MediaType,
        string? CutoffQuality,
        bool UpgradeUntilCutoff,
        bool UpgradeUnknownItems);
}
