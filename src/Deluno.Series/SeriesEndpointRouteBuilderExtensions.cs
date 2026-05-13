using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Integrations.Metadata;
using Deluno.Platform.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform;
using Deluno.Platform.Quality;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Series;

public static class SeriesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSeriesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var series = endpoints.MapGroup("/api/series");

        series.MapGet("/", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListAsync(cancellationToken);
            return Results.Ok(items);
        });

        series.MapGet("/import-recovery", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetImportRecoverySummaryAsync(cancellationToken);
            return Results.Ok(summary);
        });

        series.MapGet("/wanted", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetWantedSummaryAsync(cancellationToken);
            return Results.Ok(summary);
        });

        series.MapGet("/inventory", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetInventorySummaryAsync(cancellationToken);
            return Results.Ok(summary);
        });

        series.MapGet("/{id}/inventory", async (
            string id,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var detail = await repository.GetInventoryDetailAsync(id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        series.MapGet("/upcoming", async (
            int? take,
            int? hours,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var now = DateTimeOffset.UtcNow;
            var requestedTake = take is > 0 and <= 100 ? take.Value : 12;
            var requestedHours = hours is > 0 and <= 336 ? hours.Value : 72;
            var items = await repository.ListUpcomingEpisodesAsync(
                now,
                now.AddHours(requestedHours),
                requestedTake,
                cancellationToken);
            return Results.Ok(items);
        });

        series.MapGet("/search-history", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListSearchHistoryAsync(cancellationToken);
            return Results.Ok(items);
        });

        series.MapPost("/import-recovery", async (
            HttpContext httpContext,
            [FromBody] CreateSeriesImportRecoveryCaseRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = ValidateImportRecovery(request.Title, request.Summary);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.AddImportRecoveryCaseAsync(request, cancellationToken);
            return Results.Ok(item);
        });

        series.MapPost("/import-recovery/{id}/resolve", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var updated = await repository.ResolveImportRecoveryCaseAsync(id, "resolved", cancellationToken);
            if (updated is null)
            {
                return Results.NotFound();
            }

            await repository.AddImportRecoveryEventAsync(id, "case_resolved", "Case marked resolved by user.", null, cancellationToken);
            return Results.Ok(updated);
        });

        series.MapPost("/import-recovery/{id}/dismiss", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var updated = await repository.ResolveImportRecoveryCaseAsync(id, "dismissed", cancellationToken);
            if (updated is null)
            {
                return Results.NotFound();
            }

            await repository.AddImportRecoveryEventAsync(id, "case_dismissed", "Case dismissed by user.", null, cancellationToken);
            return Results.Ok(updated);
        });

        series.MapDelete("/import-recovery/{id}", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await repository.DeleteImportRecoveryCaseAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        series.MapGet("/{id}", async (string id, ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var item = await repository.GetByIdAsync(id, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        series.MapPut("/monitoring", async (
            HttpContext httpContext,
            [FromBody] UpdateSeriesMonitoringRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Choose at least one series before updating monitoring."]
                });
            }

            var updated = await repository.UpdateMonitoredAsync(
                request.SeriesIds,
                request.Monitored,
                cancellationToken);

            return Results.Ok(new { updated });
        });

        series.MapPut("/episodes/monitoring", async (
            HttpContext httpContext,
            [FromBody] UpdateEpisodeMonitoringRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.EpisodeIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["episodeIds"] = ["Choose at least one episode before updating monitoring."]
                });
            }

            var updated = await repository.UpdateEpisodeMonitoredAsync(
                request.EpisodeIds,
                request.Monitored,
                cancellationToken);

            return Results.Ok(new { updated });
        });

        series.MapPost("/{id}/search", async (
            string id,
            string? mode,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.SeriesId == id);
            if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesId"] = ["This series is not currently linked to a searchable library."]
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Deluno could not find the linked library for this series."]
                });
            }

            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var customFormats = await ResolveCustomFormatsAsync(platformSettingsRepository, library.QualityProfileId, cancellationToken);

            var decisionPlan = await acquisitionPipeline.PlanAsync(
                new AcquisitionDecisionRequest(
                    seriesItem.Title,
                    seriesItem.StartYear,
                    "tv",
                    wantedItem.CurrentQuality,
                    wantedItem.TargetQuality,
                    routing?.Sources ?? [],
                    routing?.DownloadClients ?? [],
                    customFormats,
                    PreviewOnly: string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase)),
                cancellationToken);
            var searchPlan = decisionPlan.SearchPlan;
            var bestCandidate = searchPlan.BestCandidate;
            var outcome = decisionPlan.Outcome;
            DownloadClientGrabResult? grabResult = null;

            if (decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
            {
                var downloadClient = decisionPlan.SelectedDownloadClient;
                grabResult = bestCandidate!.DownloadUrl is null
                    ? new DownloadClientGrabResult(downloadClient.DownloadClientId, bestCandidate.ReleaseName, false, "planned", "No download URL was available.")
                    : await downloadClientGrabService.GrabAsync(downloadClient.DownloadClientId, decisionPlan.DispatchRequest, cancellationToken);
                await jobQueueRepository.RecordDownloadDispatchAsync(
                    library.Id,
                    "tv",
                    "series",
                    seriesItem.Id,
                    bestCandidate!.ReleaseName,
                    bestCandidate.IndexerName,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    grabResult.Status,
                    JsonSerializer.Serialize(new { searchPlan, grabResult }),
                    grabResponseCode: grabResult.Succeeded ? 200 : 400,
                    grabFailureCode: null,
                    cancellationToken: cancellationToken);
            }

            await repository.RecordSearchAttemptAsync(
                seriesItem.Id,
                null,
                library.Id,
                "manual",
                outcome,
                now,
                now.AddHours(Math.Max(1, library.RetryDelayHours)),
                decisionPlan.SearchResult,
                bestCandidate?.ReleaseName,
                bestCandidate?.IndexerName,
                searchPlan.Candidates.Count == 0 ? null : JsonSerializer.Serialize(searchPlan),
                cancellationToken);

            await activityFeedRepository.RecordDecisionAsync(
                new DecisionExplanationPayload(
                    Scope: "series.search",
                    Status: outcome,
                    Reason: decisionPlan.SearchResult,
                    Inputs: new Dictionary<string, string?>
                    {
                        ["title"] = seriesItem.Title,
                        ["year"] = seriesItem.StartYear?.ToString(),
                        ["libraryId"] = library.Id,
                        ["sourceCount"] = decisionPlan.SourceCount.ToString(),
                        ["downloadClientCount"] = decisionPlan.DownloadClientCount.ToString(),
                        ["policyVersion"] = decisionPlan.PolicyVersion,
                        ["mode"] = string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase) ? "preview" : "manual"
                    },
                    Outcome: grabResult is null
                        ? searchPlan.Summary
                        : $"{grabResult.Status}: {grabResult.Message}",
                    Alternatives: decisionPlan.Alternatives),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                "series.search.manual",
                $"{seriesItem.Title} was searched manually from the Deluno workspace.",
                null,
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            return Results.Ok(new
            {
                outcome,
                summary = searchPlan.Summary,
                releaseName = bestCandidate?.ReleaseName,
                indexerName = bestCandidate?.IndexerName,
                dispatchStatus = grabResult?.Status,
                dispatchMessage = grabResult?.Message,
                candidates = searchPlan.Candidates.Select(candidate => new
                {
                    candidate.ReleaseName,
                    candidate.IndexerName,
                    candidate.Quality,
                    candidate.Score,
                    candidate.MeetsCutoff,
                    candidate.Summary,
                    candidate.DownloadUrl,
                    candidate.SizeBytes,
                    candidate.Seeders,
                    candidate.DecisionStatus,
                    candidate.DecisionReasons,
                    candidate.RiskFlags,
                    candidate.QualityDelta,
                    candidate.CustomFormatScore,
                    candidate.SeederScore,
                    candidate.SizeScore,
                    candidate.ReleaseGroup,
                    candidate.EstimatedBitrateMbps,
                    candidate.PolicyVersion
                }).ToArray()
            });
        });

        series.MapPost("/{id}/metadata/refresh", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IMetadataProvider metadataProvider,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            var matches = await metadataProvider.SearchAsync(
                new MetadataLookupRequest(item.Title, "tv", item.StartYear, item.MetadataProviderId),
                cancellationToken);
            var match = matches.FirstOrDefault();
            if (match is null)
            {
                return Results.NotFound(new { message = "No metadata match was found for this TV show." });
            }

            var updated = await ApplyMetadataAsync(repository, item.Id, match, cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "metadata.series.refreshed",
                $"{item.Title} metadata was refreshed from {match.Provider.ToUpperInvariant()}.",
                JsonSerializer.Serialize(match),
                null,
                "series",
                item.Id,
                cancellationToken);

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        series.MapPost("/{id}/metadata/link", async (
            string id,
            [FromBody] MetadataLinkRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IMetadataProvider metadataProvider,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["providerId"] = ["Choose the metadata match Deluno should link to this series."]
                });
            }

            var matches = await metadataProvider.SearchAsync(
                new MetadataLookupRequest(item.Title, "tv", item.StartYear, request.ProviderId.Trim()),
                cancellationToken);
            var match = matches.FirstOrDefault(match => string.Equals(match.ProviderId, request.ProviderId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return Results.NotFound(new { message = "The selected metadata match could not be refreshed from the provider." });
            }

            var updated = await ApplyMetadataAsync(repository, item.Id, match, cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "metadata.series.linked",
                $"{item.Title} metadata was linked to {match.Provider.ToUpperInvariant()} item {match.ProviderId}.",
                JsonSerializer.Serialize(match),
                null,
                "series",
                item.Id,
                cancellationToken);

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        series.MapPost("/{id}/metadata/jobs", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            var job = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "series.metadata.refresh",
                    Source: "metadata",
                    PayloadJson: JsonSerializer.Serialize(new { item.Id, item.Title, item.StartYear }),
                    RelatedEntityType: "series",
                    RelatedEntityId: item.Id),
                cancellationToken);

            return Results.Ok(job);
        });

        series.MapPost("/metadata/jobs", async (
            HttpContext httpContext,
            [FromBody] MetadataRefreshJobsRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var take = Math.Clamp(request.Take ?? 250, 1, 1000);
            var seriesToRefresh = (await repository.ListAsync(cancellationToken))
                .Where(item => request.ForceAll || string.IsNullOrWhiteSpace(item.MetadataProviderId) || item.MetadataUpdatedUtc is null)
                .OrderBy(item => item.MetadataUpdatedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .ToArray();
            var jobs = new List<Deluno.Jobs.Contracts.JobQueueItem>();

            foreach (var item in seriesToRefresh)
            {
                var job = await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "series.metadata.refresh",
                        Source: "metadata",
                        PayloadJson: JsonSerializer.Serialize(new { item.Id, item.Title, item.StartYear, request.ForceAll }),
                        RelatedEntityType: "series",
                        RelatedEntityId: item.Id),
                    cancellationToken);
                jobs.Add(job);
            }

            return Results.Ok(new MetadataRefreshJobsResponse(jobs.Count, jobs));
        });

        series.MapPost("/{id}/episodes/search", async (
            string id,
            HttpContext httpContext,
            [FromBody] SearchSeriesEpisodesRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.EpisodeIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["episodeIds"] = ["Choose at least one episode before starting a targeted search."]
                });
            }

            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var inventory = await repository.GetInventoryDetailAsync(id, cancellationToken);
            if (inventory is null)
            {
                return Results.NotFound();
            }

            var targetEpisodes = inventory.Episodes
                .Where(item => request.EpisodeIds.Contains(item.EpisodeId, StringComparer.OrdinalIgnoreCase))
                .Where(item => !request.MonitoredOnly || item.Monitored)
                .OrderBy(item => item.SeasonNumber)
                .ThenBy(item => item.EpisodeNumber)
                .ToList();

            if (targetEpisodes.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["episodeIds"] = [request.MonitoredOnly
                        ? "No monitored episodes were found in the provided list."
                        : "Deluno could not find those episodes in the tracked inventory."]
                });
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.SeriesId == id);
            if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesId"] = ["This series is not currently linked to a searchable library."]
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Deluno could not find the linked library for this series."]
                });
            }

            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var configuredSources = routing?.Sources.Count ?? 0;
            var configuredClients = routing?.DownloadClients.Count ?? 0;
            var now = timeProvider.GetUtcNow();
            var nextEligibleSearchUtc = now.AddHours(Math.Max(1, library.RetryDelayHours));
            var customFormats = await ResolveCustomFormatsAsync(platformSettingsRepository, library.QualityProfileId, cancellationToken);

            if (configuredSources == 0 || configuredClients == 0)
            {
                foreach (var episode in targetEpisodes)
                {
                    await repository.RecordSearchAttemptAsync(
                        seriesItem.Id,
                        episode.EpisodeId,
                        library.Id,
                        "manual-episode",
                        "blocked",
                        now,
                        nextEligibleSearchUtc,
                        configuredSources == 0
                            ? "No indexers are linked to this library yet."
                            : "No download client is linked to this library yet.",
                        null,
                        null,
                        null,
                        cancellationToken);
                }

                await activityFeedRepository.RecordActivityAsync(
                    "series.search.episode",
                    $"{seriesItem.Title} episode search was blocked because routing is incomplete.",
                    null,
                    null,
                    "series",
                    seriesItem.Id,
                    cancellationToken);

                return Results.Ok(new
                {
                    outcome = "blocked",
                    searchedEpisodes = targetEpisodes.Count,
                    matchedCount = 0,
                    queuedCount = 0
                });
            }

            var matchedCount = 0;
            var queuedCount = 0;
            var sentCount = 0;
            var plannedCount = 0;
            var failedCount = 0;

            foreach (var episode in targetEpisodes)
            {
                var queryTitle = BuildEpisodeSearchTitle(seriesItem.Title, episode.SeasonNumber, episode.EpisodeNumber);
                var decisionPlan = await acquisitionPipeline.PlanAsync(
                    new AcquisitionDecisionRequest(
                        queryTitle,
                        seriesItem.StartYear,
                        "tv",
                        wantedItem.CurrentQuality,
                        wantedItem.TargetQuality,
                        routing?.Sources ?? [],
                        routing?.DownloadClients ?? [],
                        customFormats,
                        SeasonNumber: episode.SeasonNumber,
                        EpisodeNumber: episode.EpisodeNumber),
                    cancellationToken);
                var searchPlan = decisionPlan.SearchPlan;
                var bestCandidate = searchPlan.BestCandidate;
                var outcome = decisionPlan.Outcome;

                if (decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
                {
                    matchedCount++;
                    queuedCount++;
                    var downloadClient = decisionPlan.SelectedDownloadClient;
                    var grabResult = bestCandidate!.DownloadUrl is null
                        ? new DownloadClientGrabResult(downloadClient.DownloadClientId, bestCandidate.ReleaseName, false, "planned", "No download URL was available.")
                        : await downloadClientGrabService.GrabAsync(downloadClient.DownloadClientId, decisionPlan.DispatchRequest, cancellationToken);
                    if (grabResult.Status == "sent")
                    {
                        sentCount++;
                    }
                    else if (grabResult.Status == "failed")
                    {
                        failedCount++;
                    }
                    else
                    {
                        plannedCount++;
                    }

                    await jobQueueRepository.RecordDownloadDispatchAsync(
                        library.Id,
                        "tv",
                        "episode",
                        episode.EpisodeId,
                        bestCandidate.ReleaseName,
                        bestCandidate.IndexerName,
                        downloadClient.DownloadClientId,
                        downloadClient.DownloadClientName,
                        grabResult.Status,
                        JsonSerializer.Serialize(new
                        {
                            queryTitle,
                            episode.EpisodeId,
                            episode.SeasonNumber,
                            episode.EpisodeNumber,
                            searchPlan,
                            grabResult
                        }),
                        grabResponseCode: grabResult.Succeeded ? 200 : 400,
                        grabFailureCode: null,
                        cancellationToken: cancellationToken);
                }

                await repository.RecordSearchAttemptAsync(
                    seriesItem.Id,
                    episode.EpisodeId,
                    library.Id,
                    "manual-episode",
                    outcome,
                    now,
                    nextEligibleSearchUtc,
                    decisionPlan.SearchResult,
                    searchPlan.BestCandidate?.ReleaseName,
                    searchPlan.BestCandidate?.IndexerName,
                    searchPlan.Candidates.Count == 0
                        ? JsonSerializer.Serialize(new
                        {
                            queryTitle,
                            episode.EpisodeId,
                            episode.SeasonNumber,
                            episode.EpisodeNumber
                        })
                        : JsonSerializer.Serialize(new
                        {
                            queryTitle,
                            episode.EpisodeId,
                            episode.SeasonNumber,
                            episode.EpisodeNumber,
                            searchPlan
                        }),
                    cancellationToken);
            }

            await activityFeedRepository.RecordActivityAsync(
                "series.search.episode",
                $"{seriesItem.Title} searched {targetEpisodes.Count} episode{(targetEpisodes.Count == 1 ? string.Empty : "s")} from the TV workspace.",
                JsonSerializer.Serialize(new
                {
                    episodeIds = targetEpisodes.Select(item => item.EpisodeId).ToArray(),
                    matchedCount,
                    queuedCount
                }),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            await activityFeedRepository.RecordDecisionAsync(
                new DecisionExplanationPayload(
                    Scope: "series.episode-search",
                    Status: matchedCount > 0 ? "matched" : "checked",
                    Reason: matchedCount > 0
                        ? $"{matchedCount} episode search result{(matchedCount == 1 ? string.Empty : "s")} met the active quality and policy rules."
                        : "No selected episodes produced a release that satisfied the active quality and policy rules.",
                    Inputs: new Dictionary<string, string?>
                    {
                        ["title"] = seriesItem.Title,
                        ["libraryId"] = library.Id,
                        ["episodeCount"] = targetEpisodes.Count.ToString(),
                        ["sourceCount"] = configuredSources.ToString(),
                        ["downloadClientCount"] = configuredClients.ToString(),
                        ["policyVersion"] = Deluno.Platform.Quality.MediaPolicyCatalog.CurrentVersion
                    },
                    Outcome: $"{sentCount} sent, {plannedCount} planned, {failedCount} failed.",
                    Alternatives: []),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            return Results.Ok(new
            {
                outcome = matchedCount > 0 ? "matched" : "checked",
                searchedEpisodes = targetEpisodes.Count,
                matchedCount,
                queuedCount,
                sentCount,
                plannedCount,
                failedCount
            });
        });

        series.MapPost("/bulk", async (
            HttpContext httpContext,
            BulkSeriesRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Select at least one series before performing bulk operations."]
                });
            }

            if (string.IsNullOrWhiteSpace(request.Operation))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["operation"] = ["Specify which operation to perform: remove, quality, monitoring, or search."]
                });
            }

            var operation = request.Operation.ToLowerInvariant();
            var results = new List<BulkSeriesItemResult>();
            int successCount = 0;
            int failureCount = 0;

            foreach (var seriesId in request.SeriesIds)
            {
                try
                {
                    var series = await repository.GetByIdAsync(seriesId, cancellationToken);
                    if (series is null)
                    {
                        failureCount++;
                        results.Add(new BulkSeriesItemResult(seriesId, "Unknown", false, "Series not found"));
                        continue;
                    }

                    switch (operation)
                    {
                        case "remove":
                            await repository.DeleteAsync(series.Id, cancellationToken);
                            successCount++;
                            results.Add(new BulkSeriesItemResult(series.Id, series.Title, true));
                            break;

                        case "monitoring":
                            if (!request.Monitored.HasValue)
                            {
                                failureCount++;
                                results.Add(new BulkSeriesItemResult(series.Id, series.Title, false,
                                    "Monitored state must be specified for monitoring operation"));
                            }
                            else
                            {
                                await repository.UpdateMonitoredAsync([series.Id], request.Monitored.Value, cancellationToken);
                                successCount++;
                                results.Add(new BulkSeriesItemResult(series.Id, series.Title, true, null,
                                    new Dictionary<string, string?> { ["monitored"] = request.Monitored.Value.ToString() }));
                            }
                            break;

                        case "quality":
                            if (string.IsNullOrWhiteSpace(request.QualityProfileId))
                            {
                                failureCount++;
                                results.Add(new BulkSeriesItemResult(series.Id, series.Title, false,
                                    "Quality profile ID must be specified for quality operation"));
                            }
                            else
                            {
                                await repository.UpdateQualityProfileAsync(series.Id, request.QualityProfileId, cancellationToken);
                                successCount++;
                                results.Add(new BulkSeriesItemResult(series.Id, series.Title, true, null,
                                    new Dictionary<string, string?> { ["qualityProfileId"] = request.QualityProfileId }));
                            }
                            break;

                        case "search":
                            var job = await jobScheduler.EnqueueAsync(
                                new EnqueueJobRequest(
                                    JobType: "series.search.manual",
                                    Source: "bulk",
                                    PayloadJson: JsonSerializer.Serialize(new { series.Id, series.Title, series.StartYear }),
                                    RelatedEntityType: "series",
                                    RelatedEntityId: series.Id),
                                cancellationToken);
                            successCount++;
                            results.Add(new BulkSeriesItemResult(series.Id, series.Title, true, null,
                                new Dictionary<string, string?> { ["jobId"] = job.Id }));
                            break;

                        default:
                            failureCount++;
                            results.Add(new BulkSeriesItemResult(series.Id, series.Title, false,
                                $"Unknown operation: {request.Operation}"));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failureCount++;
                    results.Add(new BulkSeriesItemResult(seriesId, "Unknown", false, ex.Message));
                }
            }

            return Results.Ok(new BulkSeriesResponse(request.SeriesIds.Count, successCount, failureCount, operation, results));
        });

        series.MapPost("/{id}/grab", async (
            string id,
            [FromBody] ReleaseGrabRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var validation = ValidateReleaseGrab(request);
            if (validation.Count > 0)
            {
                return Results.ValidationProblem(validation);
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.SeriesId == id);
            if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesId"] = ["This series is not currently linked to a searchable library."]
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Deluno could not find the linked library for this series."]
                });
            }

            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var downloadClient = routing?.DownloadClients.OrderBy(item => item.Priority).FirstOrDefault();
            if (downloadClient is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["downloadClient"] = ["Link a download client to this library before grabbing a release."]
                });
            }

            var forceOverride = request.Force == true;
            var overrideReason = string.IsNullOrWhiteSpace(request.OverrideReason)
                ? "User manually forced this release from search results."
                : request.OverrideReason.Trim();
            var customFormats = await ResolveCustomFormatsAsync(platformSettingsRepository, library.QualityProfileId, cancellationToken);
            var sourcePriorityScore = routing?.Sources
                .FirstOrDefault(item => string.Equals(item.IndexerId, request.IndexerId, StringComparison.OrdinalIgnoreCase)) is { } source
                    ? Math.Max(0, 200 - source.Priority)
                    : 0;
            var selectedDecision = acquisitionPipeline.EvaluateSelectedRelease(
                new AcquisitionSelectedReleaseRequest(
                    request.ReleaseName.Trim(),
                    request.IndexerId?.Trim(),
                    request.IndexerName?.Trim(),
                    request.DownloadUrl!.Trim(),
                    wantedItem.CurrentQuality,
                    wantedItem.TargetQuality,
                    request.CandidateQuality?.Trim(),
                    request.SizeBytes,
                    request.Seeders,
                    sourcePriorityScore,
                    customFormats,
                    ForceOverride: forceOverride,
                    OverrideReason: forceOverride ? overrideReason : null,
                    PreventLowerQualityReplacements: wantedItem.PreventLowerQualityReplacements));
            if (!selectedDecision.CanDispatch)
            {
                var hint = selectedDecision.RequiresOverride
                    ? " Use force override if you still want this exact release."
                    : string.Empty;
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["force"] = [$"{selectedDecision.Reason}{hint}"]
                });
            }

            var grabResult = await downloadClientGrabService.GrabAsync(
                downloadClient.DownloadClientId,
                new DownloadClientGrabRequest(
                    request.ReleaseName.Trim(),
                    request.DownloadUrl!.Trim(),
                    "tv",
                    "tv",
                    request.IndexerName?.Trim()),
                cancellationToken);

            var auditPayload = new
            {
                selectedRelease = request,
                decision = selectedDecision,
                forceOverride,
                overrideReason = forceOverride ? overrideReason : null,
                grabResult
            };

            await jobQueueRepository.RecordDownloadDispatchAsync(
                library.Id,
                "tv",
                "series",
                seriesItem.Id,
                request.ReleaseName.Trim(),
                string.IsNullOrWhiteSpace(request.IndexerName) ? "Manual selection" : request.IndexerName.Trim(),
                downloadClient.DownloadClientId,
                downloadClient.DownloadClientName,
                grabResult.Status,
                JsonSerializer.Serialize(auditPayload),
                grabResponseCode: grabResult.Succeeded ? 200 : 400,
                grabFailureCode: null,
                cancellationToken: cancellationToken);

            var now = timeProvider.GetUtcNow();
            await repository.RecordSearchAttemptAsync(
                seriesItem.Id,
                null,
                library.Id,
                forceOverride ? "manual-force-grab" : "manual-grab",
                grabResult.Status == "sent" ? "matched" : "checked",
                now,
                now.AddHours(Math.Max(1, library.RetryDelayHours)),
                forceOverride ? $"{grabResult.Message} Force override: {overrideReason}" : grabResult.Message,
                request.ReleaseName.Trim(),
                request.IndexerName?.Trim(),
                JsonSerializer.Serialize(auditPayload),
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                forceOverride ? "series.release.force-grabbed" : "series.release.grabbed",
                forceOverride
                    ? $"{seriesItem.Title} release was force grabbed and sent to {downloadClient.DownloadClientName}."
                    : $"{seriesItem.Title} release was manually selected and sent to {downloadClient.DownloadClientName}.",
                JsonSerializer.Serialize(auditPayload),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            await activityFeedRepository.RecordDecisionAsync(
                new DecisionExplanationPayload(
                    Scope: forceOverride ? "series.grab.force" : "series.grab.manual",
                    Status: grabResult.Status,
                    Reason: forceOverride
                        ? $"User override selected {request.ReleaseName.Trim()}: {overrideReason}"
                        : selectedDecision.Reason,
                    Inputs: new Dictionary<string, string?>
                    {
                        ["releaseName"] = request.ReleaseName.Trim(),
                        ["indexerName"] = request.IndexerName?.Trim(),
                        ["downloadClientId"] = downloadClient.DownloadClientId,
                        ["downloadClientName"] = downloadClient.DownloadClientName,
                        ["policyVersion"] = selectedDecision.PolicyVersion,
                        ["forceOverride"] = forceOverride.ToString()
                    },
                    Outcome: grabResult.Message,
                    Alternatives: selectedDecision.Alternatives),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            return Results.Ok(new
            {
                releaseName = request.ReleaseName.Trim(),
                indexerName = request.IndexerName?.Trim(),
                forceOverride,
                overrideReason = forceOverride ? overrideReason : null,
                dispatchStatus = grabResult.Status,
                dispatchMessage = grabResult.Message
            });
        });

        series.MapPost("/{id}/seasons/{seasonNumber:int}/search", async (
            string id,
            int seasonNumber,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var inventory = await repository.GetInventoryDetailAsync(id, cancellationToken);
            if (inventory is null)
            {
                return Results.NotFound();
            }

            var seasonEpisodes = inventory.Episodes
                .Where(item => item.SeasonNumber == seasonNumber)
                .OrderBy(item => item.EpisodeNumber)
                .ToList();

            if (seasonEpisodes.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seasonNumber"] = ["Deluno could not find that season in the tracked inventory."]
                });
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.SeriesId == id);
            if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesId"] = ["This series is not currently linked to a searchable library."]
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Deluno could not find the linked library for this series."]
                });
            }

            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var configuredSources = routing?.Sources.Count ?? 0;
            var configuredClients = routing?.DownloadClients.Count ?? 0;
            var now = timeProvider.GetUtcNow();
            var nextEligibleSearchUtc = now.AddHours(Math.Max(1, library.RetryDelayHours));
            var customFormats = await ResolveCustomFormatsAsync(platformSettingsRepository, library.QualityProfileId, cancellationToken);

            if (configuredSources == 0 || configuredClients == 0)
            {
                foreach (var episode in seasonEpisodes)
                {
                    await repository.RecordSearchAttemptAsync(
                        seriesItem.Id,
                        episode.EpisodeId,
                        library.Id,
                        "manual-season",
                        "blocked",
                        now,
                        nextEligibleSearchUtc,
                        configuredSources == 0
                            ? "No indexers are linked to this library yet."
                            : "No download client is linked to this library yet.",
                        null,
                        null,
                        null,
                        cancellationToken);
                }

                return Results.Ok(new
                {
                    outcome = "blocked",
                    seasonNumber,
                    searchedEpisodes = seasonEpisodes.Count,
                    matchedCount = 0,
                    queuedCount = 0
                });
            }

            var seasonQueryTitle = BuildSeasonSearchTitle(seriesItem.Title, seasonNumber);
            var decisionPlan = await acquisitionPipeline.PlanAsync(
                new AcquisitionDecisionRequest(
                    seasonQueryTitle,
                    seriesItem.StartYear,
                    "tv",
                    wantedItem.CurrentQuality,
                    wantedItem.TargetQuality,
                    routing?.Sources ?? [],
                    routing?.DownloadClients ?? [],
                    customFormats,
                    SeasonNumber: seasonNumber),
                cancellationToken);
            var searchPlan = decisionPlan.SearchPlan;
            var bestCandidate = searchPlan.BestCandidate;
            var outcome = decisionPlan.Outcome;
            DownloadClientGrabResult? grabResult = null;
            if (decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
            {
                var downloadClient = decisionPlan.SelectedDownloadClient;
                grabResult = bestCandidate!.DownloadUrl is null
                    ? new DownloadClientGrabResult(downloadClient.DownloadClientId, bestCandidate.ReleaseName, false, "planned", "No download URL was available.")
                    : await downloadClientGrabService.GrabAsync(downloadClient.DownloadClientId, decisionPlan.DispatchRequest, cancellationToken);
                await jobQueueRepository.RecordDownloadDispatchAsync(
                    library.Id,
                    "tv",
                    "season",
                    $"{seriesItem.Id}:season:{seasonNumber}",
                    bestCandidate.ReleaseName,
                    bestCandidate.IndexerName,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    grabResult.Status,
                    JsonSerializer.Serialize(new
                    {
                        seasonNumber,
                        episodeIds = seasonEpisodes.Select(item => item.EpisodeId).ToArray(),
                        searchPlan,
                        grabResult
                    }),
                    grabResponseCode: grabResult.Succeeded ? 200 : 400,
                    grabFailureCode: null,
                    cancellationToken: cancellationToken);
            }

            foreach (var episode in seasonEpisodes)
            {
                await repository.RecordSearchAttemptAsync(
                    seriesItem.Id,
                    episode.EpisodeId,
                    library.Id,
                    "manual-season",
                    outcome,
                    now,
                    nextEligibleSearchUtc,
                    decisionPlan.SearchResult,
                    searchPlan.BestCandidate?.ReleaseName,
                    searchPlan.BestCandidate?.IndexerName,
                    searchPlan.Candidates.Count == 0
                        ? JsonSerializer.Serialize(new
                        {
                            seasonNumber,
                            episode.EpisodeId,
                            episode.SeasonNumber,
                            episode.EpisodeNumber
                        })
                        : JsonSerializer.Serialize(new
                        {
                            seasonNumber,
                            episode.EpisodeId,
                            episode.SeasonNumber,
                            episode.EpisodeNumber,
                            searchPlan
                        }),
                    cancellationToken);
            }

            await activityFeedRepository.RecordActivityAsync(
                "series.search.season",
                $"{seriesItem.Title} season {seasonNumber} was searched from the TV workspace.",
                JsonSerializer.Serialize(new
                {
                    seasonNumber,
                    episodeIds = seasonEpisodes.Select(item => item.EpisodeId).ToArray(),
                    matched = searchPlan.BestCandidate is not null
                }),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            await activityFeedRepository.RecordDecisionAsync(
                new DecisionExplanationPayload(
                    Scope: "series.season-search",
                    Status: outcome,
                    Reason: decisionPlan.SearchResult,
                    Inputs: new Dictionary<string, string?>
                    {
                        ["title"] = seriesItem.Title,
                        ["seasonNumber"] = seasonNumber.ToString(),
                        ["libraryId"] = library.Id,
                        ["episodeCount"] = seasonEpisodes.Count.ToString(),
                        ["sourceCount"] = configuredSources.ToString(),
                        ["downloadClientCount"] = configuredClients.ToString(),
                        ["policyVersion"] = decisionPlan.PolicyVersion
                    },
                    Outcome: grabResult is null
                        ? searchPlan.Summary
                        : $"{grabResult.Status}: {grabResult.Message}",
                    Alternatives: decisionPlan.Alternatives),
                null,
                "series",
                seriesItem.Id,
                cancellationToken);

            return Results.Ok(new
            {
                outcome,
                seasonNumber,
                searchedEpisodes = seasonEpisodes.Count,
                matchedCount = searchPlan.BestCandidate is null ? 0 : seasonEpisodes.Count,
                queuedCount = searchPlan.BestCandidate is null ? 0 : 1,
                releaseName = searchPlan.BestCandidate?.ReleaseName,
                indexerName = searchPlan.BestCandidate?.IndexerName,
                dispatchStatus = grabResult?.Status,
                dispatchMessage = grabResult?.Message
            });
        });

        series.MapGet("/{id}/workflow-status", async (
            string id,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            ISeriesWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.SeriesId == id);
            if (wantedItem is null)
            {
                return Results.Ok(new
                {
                    wantedStatus = "untracked",
                    reason = "This series is not linked to any library.",
                    isReplacementAllowed = true,
                    qualityDelta = (int?)null,
                    currentQuality = (string?)null,
                    targetQuality = (string?)null,
                    preventLowerQualityReplacements = true,
                    lastQualityDeltaDecision = (int?)null
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            var upgradeUntilCutoff = library?.UpgradeUntilCutoff ?? false;
            var upgradeUnknownItems = library?.UpgradeUnknownItems ?? false;
            var qualityCutoffMet = wantedItem.QualityCutoffMet;

            var decision = workflowService.EvaluateEpisodeWantedStatus(
                wantedItem.CurrentQuality,
                wantedItem.TargetQuality,
                qualityCutoffMet,
                upgradeUntilCutoff,
                upgradeUnknownItems);

            return Results.Ok(new
            {
                wantedStatus = decision.WantedStatus,
                reason = decision.Reason,
                isReplacementAllowed = decision.IsReplacementAllowed,
                qualityDelta = decision.QualityDelta,
                currentQuality = wantedItem.CurrentQuality,
                targetQuality = wantedItem.TargetQuality,
                preventLowerQualityReplacements = wantedItem.PreventLowerQualityReplacements,
                lastQualityDeltaDecision = wantedItem.LastQualityDeltaDecision
            });
        });

        series.MapPut("/{id}/replacement-protection", async (
            string id,
            [FromBody] UpdateSeriesReplacementProtectionRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.SeriesId == id);
            if (wantedItem is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesId"] = ["This series is not currently linked to a searchable library."]
                });
            }

            var updated = await repository.UpdateSeriesReplacementPolicyAsync(
                id,
                wantedItem.LibraryId,
                request.PreventLowerQualityReplacements,
                cancellationToken);

            return updated ? Results.Ok(new { updated = true }) : Results.NotFound();
        });

        series.MapGet("/{id}/monitored-missing", async (
            string id,
            ISeriesCatalogRepository repository,
            ISeriesWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.SeriesId == id);
            if (wantedItem is null)
            {
                return Results.Ok(new { episodes = Array.Empty<object>(), seasonPackRecommendations = Array.Empty<object>() });
            }

            var missingEpisodes = await repository.ListMonitoredMissingEpisodesAsync(id, wantedItem.LibraryId, cancellationToken);

            var seasonGroups = missingEpisodes
                .GroupBy(e => e.SeasonNumber)
                .Select(g =>
                {
                    var inventory = (IReadOnlyList<SeriesEpisodeInventoryItem>)g.ToList();
                    var allSeasonEpisodes = missingEpisodes.Where(e => e.SeasonNumber == g.Key).ToList();
                    var seasonDecision = workflowService.EvaluateSeasonPackStrategy(allSeasonEpisodes, monitoredOnly: true);
                    return new
                    {
                        seasonNumber = g.Key,
                        missingEpisodeCount = g.Count(),
                        preferSeasonPack = seasonDecision.PreferSeasonPack,
                        reason = seasonDecision.Reason,
                        monitoredMissingCount = seasonDecision.MonitoredMissingCount,
                        totalMonitoredCount = seasonDecision.TotalMonitoredCount
                    };
                })
                .ToArray();

            return Results.Ok(new
            {
                seriesId = id,
                totalMissingMonitored = missingEpisodes.Count,
                episodes = missingEpisodes.Select(e => new
                {
                    e.EpisodeId,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.Title,
                    e.WantedStatus,
                    e.LastSearchUtc
                }).ToArray(),
                seasonPackRecommendations = seasonGroups
            });
        });

        series.MapPost("/", async (
            HttpContext httpContext,
            [FromBody] CreateSeriesRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IMediaDecisionService mediaDecisionService,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var errors = Validate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var item = await repository.AddAsync(request, cancellationToken);
            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            foreach (var library in libraries.Where(entry => entry.MediaType == "tv"))
            {
                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: library.MediaType,
                    HasFile: false,
                    CurrentQuality: null,
                    CutoffQuality: library.CutoffQuality,
                    UpgradeUntilCutoff: library.UpgradeUntilCutoff,
                    UpgradeUnknownItems: library.UpgradeUnknownItems));

                await repository.EnsureWantedStateAsync(
                    item.Id,
                    library.Id,
                    decision.WantedStatus,
                    decision.WantedReason,
                    false,
                    decision.CurrentQuality,
                    decision.TargetQuality,
                    decision.QualityCutoffMet,
                    cancellationToken);
            }

            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "series.catalog.refresh",
                    Source: "series",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        item.Id,
                        item.Title,
                        item.ImdbId
                    }),
                    RelatedEntityType: "series",
                    RelatedEntityId: item.Id),
                cancellationToken);
            return Results.Created($"/api/series/{item.Id}", item);
        });

        return endpoints;
    }

    private static Dictionary<string, string[]> Validate(CreateSeriesRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors["title"] = ["A series title is required."];
        }

        if (request.StartYear is < 1888 or > 2100)
        {
            errors["startYear"] = ["Start year must be between 1888 and 2100."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateImportRecovery(string? title, string? summary)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(title))
        {
            errors["title"] = ["Give this import issue a TV show title."];
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            errors["summary"] = ["Add a short summary so Deluno can explain what went wrong."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateReleaseGrab(ReleaseGrabRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.ReleaseName))
        {
            errors["releaseName"] = ["Choose a release before sending it to a download client."];
        }

        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
        {
            errors["downloadUrl"] = ["This release does not include a downloadable URL. Choose a different release or check the indexer configuration."];
        }
        else if (!Uri.TryCreate(request.DownloadUrl, UriKind.Absolute, out _))
        {
            errors["downloadUrl"] = ["The selected release has an invalid download URL."];
        }

        return errors;
    }

    private static string BuildEpisodeSearchTitle(string title, int seasonNumber, int episodeNumber)
    {
        return $"{title} S{seasonNumber:D2}E{episodeNumber:D2}";
    }

    private static string BuildSeasonSearchTitle(string title, int seasonNumber)
    {
        return $"{title} Season {seasonNumber:D2}";
    }

    private static async Task<IReadOnlyList<CustomFormatItem>> ResolveCustomFormatsAsync(
        IPlatformSettingsRepository repository,
        string? qualityProfileId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(qualityProfileId))
        {
            return [];
        }

        var profiles = await repository.ListQualityProfilesAsync(cancellationToken);
        var profile = profiles.FirstOrDefault(item => item.Id == qualityProfileId);
        if (profile is null || string.IsNullOrWhiteSpace(profile.CustomFormatIds))
        {
            return [];
        }

        var ids = profile.CustomFormatIds
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0)
        {
            return [];
        }

        var formats = await repository.ListCustomFormatsAsync(cancellationToken);
        return formats.Where(item => ids.Contains(item.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static Task<SeriesListItem?> ApplyMetadataAsync(
        ISeriesCatalogRepository repository,
        string seriesId,
        MetadataSearchResult result,
        CancellationToken cancellationToken)
    {
        return repository.UpdateMetadataAsync(
            seriesId,
            result.Provider,
            result.ProviderId,
            result.OriginalTitle,
            result.Overview,
            result.PosterUrl,
            result.BackdropUrl,
            result.Rating,
            string.Join(", ", result.Genres),
            result.ExternalUrl,
            result.ImdbId,
            JsonSerializer.Serialize(result),
            cancellationToken);
    }

    private sealed record ReleaseGrabRequest(
        string ReleaseName,
        string? IndexerId,
        string? IndexerName,
        string? DownloadUrl,
        string? CandidateQuality,
        long? SizeBytes,
        int? Seeders,
        bool? Force,
        string? OverrideReason);

    private sealed record MetadataRefreshJobsRequest(
        bool ForceAll,
        int? Take);

    private sealed record MetadataLinkRequest(string? ProviderId);

    private sealed record MetadataRefreshJobsResponse(
        int EnqueuedCount,
        IReadOnlyList<Deluno.Jobs.Contracts.JobQueueItem> Jobs);

    private sealed record UpdateSeriesReplacementProtectionRequest(
        bool PreventLowerQualityReplacements);
}
