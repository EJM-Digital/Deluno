using Deluno.Contracts;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Integrations.Metadata;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Platform.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Quality.Data;
using Deluno.Security;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Quality.Contracts;
using Deluno.Realtime;

namespace Deluno.Series;

public static class SeriesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoSeriesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var series = endpoints.MapGroup("/api/series");

        // The list surface for a library that keeps growing. Search, filter,
        // sort and the counts all happen in SQL; the response says how many rows
        // match and hands back a continuation token, so a caller can always tell
        // a complete answer from a partial one.
        //
        series.MapGet("/page", async (
            string? search,
            string? status,
            string? sort,
            string? direction,
            int? pageSize,
            string? pageToken,
            [FromServices] ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var page = await repository.ListPageAsync(
                new CatalogueQuery(
                    Search: search,
                    Status: status,
                    Sort: sort,
                    Descending: !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase),
                    PageSize: pageSize ?? 50,
                    PageToken: pageToken),
                cancellationToken);

            return Results.Ok(page);
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

        series.MapGet("/episodes/wanted", async (
            int? take,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var items = await repository.ListWantedEpisodesAsync(Math.Clamp(take ?? 200, 1, 1000), cancellationToken);
            return Results.Ok(items);
        });

        series.MapGet("/calendar", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? take,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var start = from ?? DateTimeOffset.UtcNow.AddDays(-7);
            var end = to ?? start.AddDays(35);
            if (end <= start)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["to"] = ["The end of the window must be after the start."]
                });
            }

            // A wide window over a full catalogue is a lot of rows; cap it so a
            // calendar request can never turn into a table scan of every episode.
            if ((end - start).TotalDays > 400)
            {
                end = start.AddDays(400);
            }

            var items = await repository.ListCalendarEpisodesAsync(
                start,
                end,
                Math.Clamp(take ?? 500, 1, 2000),
                cancellationToken);
            return Results.Ok(items);
        });

        series.MapGet("/search-history", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListSearchHistoryAsync(cancellationToken);
            return Results.Ok(items);
        });

        series.MapGet("/{id}/removal-preview", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            ILibrariesRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            if (await repository.GetByIdAsync(id, cancellationToken) is null) return Results.NotFound();

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var trackedFiles = new List<TrackedLibraryFile>();
            foreach (var library in libraries)
            {
                await foreach (var file in repository.StreamTrackedFilesAsync(library.Id, cancellationToken))
                {
                    if (string.Equals(file.SeriesId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        trackedFiles.Add(new TrackedLibraryFile(file.LibraryId, file.FilePath));
                    }
                }
            }

            return Results.Ok(LibraryMediaDeletion.Preview(trackedFiles, libraries));
        });

        series.MapPost("/import-recovery", async (
            HttpContext httpContext,
            [FromBody] CreateSeriesImportRecoveryCaseRequest request,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            foreach (var seriesId in request.SeriesIds.Distinct(StringComparer.OrdinalIgnoreCase))
                await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);

            return Results.Ok(new { updated });
        });

        series.MapPost("/{id}/automation/defer", async (
            string id,
            [FromBody] DeferAutomationRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            TimeProvider timeProvider,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(request.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["libraryId"] = ["This title is not attached to an automated library."] });
            }

            var deferredUntilUtc = timeProvider.GetUtcNow().AddHours(Math.Clamp(request.Hours ?? 24, 1, 720));
            var deferred = await repository.DeferWantedSearchAsync(id, request.LibraryId, deferredUntilUtc, cancellationToken);
            if (!deferred) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", id, cancellationToken);
            return Results.Ok(new { deferredUntilUtc });
        });

        series.MapPost("/{id}/automation/skip-once", async (
            string id,
            [FromBody] SkipNextAutomationRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;
            if (string.IsNullOrWhiteSpace(request.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["libraryId"] = ["This title is not attached to an automated library."] });
            }

            var skipped = await repository.SkipNextWantedSearchAsync(id, request.LibraryId, cancellationToken);
            if (!skipped) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", id, cancellationToken);
            return Results.Ok(new { message = "The next scheduled search will be skipped. Manual search remains available." });
        });

        series.MapPut("/episodes/monitoring", async (
            HttpContext httpContext,
            [FromBody] UpdateEpisodeMonitoringRequest request,
            ISeriesCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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

            if (updated > 0)
            {
                foreach (var seriesId in await repository.ListParentSeriesIdsAsync(request.EpisodeIds, cancellationToken))
                {
                    await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);
                }
            }

            return Results.Ok(new { updated });
        });

        series.MapPost("/{id}/search", async (
            string id,
            string? mode,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
                return Results.Ok(new
                {
                    outcome = "blocked",
                    summary = "This series is not currently linked to a searchable library.",
                    reason = MediaSearchReasons.NotSearchable,
                    releaseName = (string?)null,
                    indexerName = (string?)null,
                    dispatchStatus = (string?)null,
                    dispatchMessage = (string?)null,
                    candidates = Array.Empty<object>()
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.Ok(new
                {
                    outcome = "blocked",
                    summary = "Deluno could not find the linked library for this series.",
                    reason = MediaSearchReasons.LibraryMissing,
                    releaseName = (string?)null,
                    indexerName = (string?)null,
                    dispatchStatus = (string?)null,
                    dispatchMessage = (string?)null,
                    candidates = Array.Empty<object>()
                });
            }

            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var customFormats = await ResolveCustomFormatsAsync(qualityRepository, library.QualityProfileId, cancellationToken);

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
                reason = searchPlan.Reason,
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
            IMetadataProvider metadataProvider,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            await SyncCatalogueAsync(
                repository,
                metadataProvider,
                activityFeedRepository,
                item.Id,
                item.Title,
                match.ProviderId,
                cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "metadata.series.refreshed",
                $"{item.Title} metadata was refreshed from {match.Provider.ToUpperInvariant()}.",
                JsonSerializer.Serialize(match),
                null,
                "series",
                item.Id,
                cancellationToken);

            if (updated is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", updated.Id, cancellationToken);
            return Results.Ok(updated);
        });

        series.MapPost("/{id}/metadata/link", async (
            string id,
            [FromBody] MetadataLinkRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IMetadataProvider metadataProvider,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            await SyncCatalogueAsync(
                repository,
                metadataProvider,
                activityFeedRepository,
                item.Id,
                item.Title,
                match.ProviderId,
                cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "metadata.series.linked",
                $"{item.Title} metadata was linked to {match.Provider.ToUpperInvariant()} item {match.ProviderId}.",
                JsonSerializer.Serialize(match),
                null,
                "series",
                item.Id,
                cancellationToken);

            if (updated is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", updated.Id, cancellationToken);
            return Results.Ok(updated);
        });

        series.MapPost("/{id}/metadata/jobs", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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

        series.MapPut("/{id}/metadata/override", async (
            string id,
            [FromBody] MetadataOverrideRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            var updated = await repository.UpdateMetadataAsync(
                item.Id,
                item.MetadataProvider ?? "manual",
                item.MetadataProviderId ?? item.ImdbId ?? item.Id,
                // PUT replaces the override set: a field that arrives blank clears the
                // stored value. Treating blank as "keep" made a manual override
                // impossible to undo — you could only replace it with other text.
                NormalizeOverride(request.OriginalTitle),
                NormalizeOverride(request.Overview),
                NormalizeOverride(request.PosterUrl),
                NormalizeOverride(request.BackdropUrl),
                request.Rating,
                NormalizeOverride(request.Genres),
                NormalizeOverride(request.ExternalUrl),
                NormalizeOverride(request.ImdbId),
                JsonSerializer.Serialize(new
                {
                    kind = "manual-metadata-override",
                    request,
                    updatedUtc = DateTimeOffset.UtcNow
                }),
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                "metadata.series.overridden",
                $"{item.Title} metadata values were manually overridden.",
                JsonSerializer.Serialize(request),
                null,
                "series",
                item.Id,
                cancellationToken);

            if (updated is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", updated.Id, cancellationToken);
            return Results.Ok(updated);
        });

        series.MapPost("/metadata/jobs", async (
            HttpContext httpContext,
            [FromBody] MetadataRefreshJobsRequest request,
            ISeriesCatalogRepository repository,
            IJobScheduler jobScheduler,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var now = timeProvider.GetUtcNow();
            var staleBefore = now - MetadataStalenessWindow.StaleAfter;
            var retryAttemptsBefore = now - MetadataStalenessWindow.AttemptCooldown;

            // "Refresh everything" marks everything, in one statement, and lets
            // the backfill work through it. It used to load the catalogue, take
            // the first few hundred and queue a job each -- which on a large
            // library covered a few percent and said nothing about the rest.
            var requested = request.ForceAll
                ? await repository.RequestMetadataRefreshForAllAsync(cancellationToken)
                : 0;

            var take = Math.Clamp(request.Take ?? 250, 1, 1000);
            var totalStale = await repository.CountStaleMetadataCandidatesAsync(
                staleBefore,
                retryAttemptsBefore,
                cancellationToken);

            // Filtered, ordered and limited in SQL. The queue is primed here for
            // responsiveness; the planner keeps topping it up until nothing is
            // stale, so this number is a head start, not the whole job.
            var candidates = await repository.ListStaleMetadataCandidatesAsync(
                staleBefore,
                retryAttemptsBefore,
                take,
                cancellationToken);

            foreach (var candidate in candidates)
            {
                await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "series.metadata.refresh",
                        Source: "metadata",
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            candidate.Id,
                            candidate.Title,
                            StartYear = candidate.Year,
                            request.ForceAll
                        }),
                        RelatedEntityType: "series",
                        RelatedEntityId: candidate.Id),
                    cancellationToken);
            }

            var remaining = Math.Max(0, totalStale - candidates.Count);
            return Results.Ok(new MetadataRefreshJobsResponse(
                EnqueuedCount: candidates.Count,
                RemainingCount: remaining,
                StaleCount: totalStale,
                MarkedForRefreshCount: requested,
                Message: DescribeRefresh(candidates.Count, remaining)));
        });

        series.MapPost("/{id}/episodes/search", async (
            string id,
            HttpContext httpContext,
            [FromBody] SearchSeriesEpisodesRequest request,
            ISeriesCatalogRepository repository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
                return Results.Ok(new
                {
                    outcome = "blocked",
                    reason = MediaSearchReasons.NotSearchable,
                    searchedEpisodes = targetEpisodes.Count,
                    matchedCount = 0,
                    queuedCount = 0,
                    sentCount = 0,
                    plannedCount = 0,
                    failedCount = 0
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.Ok(new
                {
                    outcome = "blocked",
                    reason = MediaSearchReasons.LibraryMissing,
                    searchedEpisodes = targetEpisodes.Count,
                    matchedCount = 0,
                    queuedCount = 0,
                    sentCount = 0,
                    plannedCount = 0,
                    failedCount = 0
                });
            }

            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var configuredSources = routing?.Sources.Count ?? 0;
            var configuredClients = routing?.DownloadClients.Count ?? 0;
            var now = timeProvider.GetUtcNow();
            var nextEligibleSearchUtc = now.AddHours(Math.Max(1, library.RetryDelayHours));
            var customFormats = await ResolveCustomFormatsAsync(qualityRepository, library.QualityProfileId, cancellationToken);

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
                    reason = configuredSources == 0 ? MediaSearchReasons.NoIndexers : MediaSearchReasons.NoResults,
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
            var searchReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                searchReasons.Add(searchPlan.Reason);

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
                        ["policyVersion"] = Deluno.Quality.MediaPolicyCatalog.CurrentVersion
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
                reason = matchedCount > 0
                    ? MediaSearchReasons.Ok
                    : searchReasons.FirstOrDefault(item => !string.Equals(item, MediaSearchReasons.Ok, StringComparison.OrdinalIgnoreCase)) ?? MediaSearchReasons.NoResults,
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
            ILibrariesRepository platformSettingsRepository,
            [FromServices] IIntakeRepository intakeRepository,
            IJobScheduler jobScheduler,
            IJobQueueRepository jobQueueRepository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
                            var removalMetadata = await RemoveSeriesAsync(
                                series,
                                request,
                                repository,
                                platformSettingsRepository,
                                intakeRepository,
                                jobQueueRepository,
                                activityFeedRepository,
                                cancellationToken);
                            successCount++;
                            results.Add(new BulkSeriesItemResult(series.Id, series.Title, true, null, removalMetadata));
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

                    if (results[^1].Succeeded)
                    {
                        await realtimeEventPublisher.PublishEntityChangedAsync("Series", series.Id, cancellationToken);
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

        series.MapPost("/bulk/quality-profile", async (
            HttpContext httpContext,
            [FromBody] BulkQualityProfileRequest request,
            ISeriesCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Choose at least one series before updating quality."]
                });
            }

            if (string.IsNullOrWhiteSpace(request.QualityProfileId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["qualityProfileId"] = ["Choose a quality profile before applying changes."]
                });
            }

            var updated = 0;
            foreach (var id in request.SeriesIds)
            {
                if (await repository.UpdateQualityProfileAsync(id, request.QualityProfileId.Trim(), cancellationToken))
                {
                    updated++;
                    await realtimeEventPublisher.PublishEntityChangedAsync("Series", id, cancellationToken);
                }
            }

            return Results.Ok(new { updated, qualityProfileId = request.QualityProfileId.Trim() });
        });

        series.MapPost("/bulk/search", async (
            HttpContext httpContext,
            [FromBody] BulkSearchRequest request,
            ISeriesCatalogRepository repository,
            ILibrariesRepository platformSettingsRepository,
            IJobQueueRepository jobQueueRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Choose at least one series to search for."]
                });
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var libraryIds = wanted.RecentItems
                .Where(item => request.SeriesIds.Contains(item.SeriesId, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.LibraryId))
                .Select(item => item.LibraryId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var triggered = 0;
            foreach (var libraryId in libraryIds)
            {
                var library = libraries.FirstOrDefault(l => string.Equals(l.Id, libraryId, StringComparison.OrdinalIgnoreCase));
                if (library is null)
                {
                    continue;
                }

                await jobQueueRepository.RequestLibrarySearchAsync(new LibraryAutomationPlanItem(
                    LibraryId: library.Id,
                    LibraryName: library.Name,
                    MediaType: library.MediaType,
                    AutoSearchEnabled: library.AutoSearchEnabled,
                    MissingSearchEnabled: library.MissingSearchEnabled,
                    UpgradeSearchEnabled: library.UpgradeSearchEnabled,
                    SearchIntervalHours: library.SearchIntervalHours,
                    RetryDelayHours: library.RetryDelayHours,
                    MaxItemsPerRun: library.MaxItemsPerRun,
                    SearchWindowStartHour: library.SearchWindowStartHour,
                    SearchWindowEndHour: library.SearchWindowEndHour), cancellationToken);
                triggered++;
            }

            return Results.Ok(new { searchesTriggered = triggered, libraryCount = libraryIds.Length });
        });

        series.MapPost("/bulk/reassign-library", async (
            HttpContext httpContext,
            [FromBody] BulkReassignLibraryRequest request,
            ISeriesCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.SeriesIds is null || request.SeriesIds.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["At least one series ID is required."]
                });
            }

            if (string.IsNullOrWhiteSpace(request.FromLibraryId) || string.IsNullOrWhiteSpace(request.ToLibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Both fromLibraryId and toLibraryId are required."]
                });
            }

            var count = await repository.ReassignLibraryAsync(
                request.SeriesIds, request.FromLibraryId, request.ToLibraryId, cancellationToken);

            if (count > 0)
                foreach (var seriesId in request.SeriesIds.Distinct(StringComparer.OrdinalIgnoreCase))
                    await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);

            return Results.Ok(new { reassigned = count });
        });

        series.MapPost("/bulk/tags", async (
            HttpContext httpContext,
            [FromBody] BulkTagsRequest request,
            ISeriesCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Choose at least one series before applying tags."]
                });
            }

            var normalizedTags = NormalizeTags(request.Tags);
            var updated = 0;
            foreach (var id in request.SeriesIds)
            {
                var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
                if (seriesItem is null)
                {
                    continue;
                }

                var metadata = ParseMetadataDictionary(seriesItem.MetadataJson);
                metadata["tags"] = normalizedTags;
                await repository.UpdateMetadataAsync(
                    seriesItem.Id,
                    seriesItem.MetadataProvider,
                    seriesItem.MetadataProviderId,
                    seriesItem.OriginalTitle,
                    seriesItem.Overview,
                    seriesItem.PosterUrl,
                    seriesItem.BackdropUrl,
                    seriesItem.Rating,
                    seriesItem.Genres,
                    seriesItem.ExternalUrl,
                    seriesItem.ImdbId,
                    JsonSerializer.Serialize(metadata),
                    cancellationToken);
                updated++;
                await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesItem.Id, cancellationToken);
            }

            return Results.Ok(new { updated, tags = normalizedTags });
        });

        series.MapPost("/bulk/rename-preview", async (
            HttpContext httpContext,
            [FromBody] BulkRenamePreviewRequest request,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Choose at least one series to preview rename output."]
                });
            }

            var settings = await platformSettingsRepository.GetAsync(cancellationToken);
            var template = string.IsNullOrWhiteSpace(request.Template)
                ? settings.SeriesFolderFormat
                : request.Template.Trim();

            var previews = new List<object>();
            foreach (var id in request.SeriesIds)
            {
                var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
                if (seriesItem is null)
                {
                    continue;
                }

                previews.Add(new
                {
                    seriesId = seriesItem.Id,
                    seriesItem.Title,
                    seriesItem.StartYear,
                    template,
                    proposedName = ApplySeriesRenameTemplate(template, seriesItem.Title, seriesItem.StartYear)
                });
            }

            return Results.Ok(new { count = previews.Count, previews });
        });

        series.MapPost("/{id}/grab", async (
            string id,
            [FromBody] ReleaseGrabRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IMediaStateRepository mediaStateRepository,
            IPlatformSettingsRepository platformSettingsRepository,
            ILibrariesRepository librariesRepository,
            IQualityRepository qualityRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await MediaGrabHandler.ExecuteAsync(
                MediaKind.Series,
                id,
                new MediaReleaseGrabRequest(
                    request.ReleaseName,
                    request.IndexerId,
                    request.IndexerName,
                    request.DownloadUrl,
                    request.CandidateQuality,
                    request.SizeBytes,
                    request.Seeders,
                    request.Force,
                    request.OverrideReason),
                mediaStateRepository,
                platformSettingsRepository,
                librariesRepository,
                qualityRepository,
                jobQueueRepository,
                acquisitionPipeline,
                downloadClientGrabService,
                activityFeedRepository,
                timeProvider,
                (seriesId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, cancellationToken) =>
                    repository.RecordSearchAttemptAsync(
                        seriesId,
                        null,
                        libraryId,
                        triggerKind,
                        outcome,
                        now,
                        nextEligibleUtc,
                        lastSearchResult,
                        releaseName,
                        indexerName,
                        detailsJson,
                        cancellationToken),
                cancellationToken);

            if (result.NotFound)
            {
                return Results.NotFound();
            }

            if (result.ValidationErrors is not null)
            {
                return Results.ValidationProblem(result.ValidationErrors);
            }

            return Results.Ok(new
            {
                result.ReleaseName,
                result.IndexerName,
                result.ForceOverride,
                result.OverrideReason,
                result.DispatchStatus,
                result.DispatchMessage
            });
        });

        series.MapPost("/{id}/seasons/{seasonNumber:int}/search", async (
            string id,
            int seasonNumber,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
                return Results.Ok(new
                {
                    outcome = "blocked",
                    reason = MediaSearchReasons.NotSearchable,
                    seasonNumber,
                    searchedEpisodes = seasonEpisodes.Count,
                    matchedCount = 0,
                    queuedCount = 0,
                    releaseName = (string?)null,
                    indexerName = (string?)null,
                    dispatchStatus = (string?)null,
                    dispatchMessage = (string?)null
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.Ok(new
                {
                    outcome = "blocked",
                    reason = MediaSearchReasons.LibraryMissing,
                    seasonNumber,
                    searchedEpisodes = seasonEpisodes.Count,
                    matchedCount = 0,
                    queuedCount = 0,
                    releaseName = (string?)null,
                    indexerName = (string?)null,
                    dispatchStatus = (string?)null,
                    dispatchMessage = (string?)null
                });
            }

            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var configuredSources = routing?.Sources.Count ?? 0;
            var configuredClients = routing?.DownloadClients.Count ?? 0;
            var now = timeProvider.GetUtcNow();
            var nextEligibleSearchUtc = now.AddHours(Math.Max(1, library.RetryDelayHours));
            var customFormats = await ResolveCustomFormatsAsync(qualityRepository, library.QualityProfileId, cancellationToken);

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
                    reason = configuredSources == 0 ? MediaSearchReasons.NoIndexers : MediaSearchReasons.NoResults,
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
                reason = searchPlan.Reason,
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
            ILibrariesRepository platformSettingsRepository,
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
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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

            if (!updated) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", id, cancellationToken);
            return Results.Ok(new { updated = true });
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
            ILibrariesRepository platformSettingsRepository,
            IMediaDecisionService mediaDecisionService,
            IJobScheduler jobScheduler,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
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
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", item.Id, cancellationToken);
            return Results.Created($"/api/series/{item.Id}", item);
        });

        return endpoints;
    }

    /// <summary>Blank means "no override", so it is stored as null rather than kept.</summary>
    private static string? NormalizeOverride(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Pull the provider's season/episode list in after a match is applied.
    ///
    /// This is what makes the library a model of the show rather than a mirror of
    /// the folder: episodes exist, and carry their title and air date, before any
    /// file for them does. A provider that cannot answer leaves the inventory
    /// exactly as it was.
    /// </summary>
    private static async Task<SeriesCatalogueSyncResult> SyncCatalogueAsync(
        ISeriesCatalogRepository repository,
        IMetadataProvider metadataProvider,
        IActivityFeedRepository activityFeedRepository,
        string seriesId,
        string seriesTitle,
        string? providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return SeriesCatalogueSyncResult.None;
        }

        IReadOnlyList<MetadataSeason> seasons;
        try
        {
            seasons = await metadataProvider.GetSeriesCatalogueAsync(providerId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A catalogue that cannot be fetched must never fail the metadata
            // request that triggered it — the title itself linked fine.
            return SeriesCatalogueSyncResult.None;
        }

        var episodes = seasons
            .SelectMany(season => season.Episodes)
            .Select(episode => new CatalogueEpisodeItem(
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.Title,
                episode.Overview,
                episode.AirDateUtc))
            .ToArray();

        if (episodes.Length == 0)
        {
            return SeriesCatalogueSyncResult.None;
        }

        var result = await repository.SyncEpisodeCatalogueAsync(seriesId, episodes, "tmdb", cancellationToken);

        if (result.AddedCount > 0)
        {
            await activityFeedRepository.RecordActivityAsync(
                "metadata.series.catalogue",
                $"Deluno learned {result.AddedCount} more episode{(result.AddedCount == 1 ? "" : "s")} of {seriesTitle} from the metadata provider.",
                JsonSerializer.Serialize(result),
                null,
                "series",
                seriesId,
                cancellationToken);
        }

        return result;
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

    private static string[] NormalizeTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return [];
        }

        return tags
            .Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, object?> ParseMetadataDictionary(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataJson)
                   ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string ApplySeriesRenameTemplate(string template, string title, int? startYear)
    {
        var resolved = (template ?? "{Series Title} ({Series Year})")
            .Replace("{Series Title}", title ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", title ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Series Year}", startYear?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", startYear?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var cleaned = SanitizePathSegment(resolved).Trim();
        return string.IsNullOrWhiteSpace(cleaned)
            ? SanitizePathSegment(title ?? "Untitled")
            : cleaned;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Untitled";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? ' ' : ch)
            .ToArray();

        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
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

    private static async Task<Dictionary<string, string?>> RemoveSeriesAsync(
        SeriesListItem series,
        BulkSeriesRequest request,
        ISeriesCatalogRepository repository,
        ILibrariesRepository platformSettingsRepository,
        IIntakeRepository intakeRepository,
        IJobQueueRepository jobQueueRepository,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string?>();
        var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
        var cancelledJobs = await jobQueueRepository.CancelPendingForRelatedEntityAsync("series", series.Id, cancellationToken);
        metadata["cancelledPendingJobCount"] = cancelledJobs.ToString();

        if (request.DeleteFiles)
        {
            var trackedFiles = new List<TrackedLibraryFile>();
            foreach (var library in libraries)
            {
                await foreach (var file in repository.StreamTrackedFilesAsync(library.Id, cancellationToken))
                {
                    if (string.Equals(file.SeriesId, series.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        trackedFiles.Add(new TrackedLibraryFile(file.LibraryId, file.FilePath));
                    }
                }
            }

            var deletion = LibraryMediaDeletion.Delete(trackedFiles, libraries, cancellationToken);
            metadata["deletedFileCount"] = deletion.DeletedFileCount.ToString();
            metadata["deletedFolderCount"] = deletion.DeletedFolderCount.ToString();
            if (deletion.Warnings.Count > 0)
            {
                metadata["fileDeletionWarnings"] = string.Join(" ", deletion.Warnings);
            }
        }

        if (request.AddImportListExclusion)
        {
            var origins = await intakeRepository.ListIntakeTitleOriginsAsync("tv", series.Id, cancellationToken);
            var exclusionsAdded = 0;
            var exclusionWarnings = new List<string>();
            foreach (var origin in origins.GroupBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                try
                {
                    var exclusion = await intakeRepository.CreateIntakeListExclusionAsync(
                        origin.SourceId,
                        new CreateIntakeListExclusionRequest(series.Title, series.StartYear, series.ImdbId, null),
                        cancellationToken);
                    if (exclusion is not null) exclusionsAdded++;
                }
                catch
                {
                    exclusionWarnings.Add($"Deluno could not add the exclusion for {origin.SourceName}.");
                }
            }

            metadata["importListExclusionsAdded"] = exclusionsAdded.ToString();
            if (exclusionWarnings.Count > 0)
            {
                metadata["importListExclusionWarnings"] = string.Join(" ", exclusionWarnings);
            }
        }

        if (!await repository.DeleteAsync(series.Id, cancellationToken))
        {
            throw new InvalidOperationException("TV show was not removed from Deluno.");
        }

        await activityFeedRepository.RecordActivityAsync(
            "series.removed",
            $"{series.Title} was removed from Deluno.{(request.DeleteFiles ? " Imported library files were also selected for deletion." : string.Empty)}",
            JsonSerializer.Serialize(new { request.DeleteFiles, request.AddImportListExclusion, metadata }),
            null,
            "series",
            series.Id,
            cancellationToken);

        return metadata;
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
        IQualityRepository repository,
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
            cancellationToken,
            result.RuntimeMinutes,
            result.Popularity,
            result.VoteCount);
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

    private sealed record MetadataOverrideRequest(
        string? OriginalTitle,
        string? Overview,
        string? PosterUrl,
        string? BackdropUrl,
        double? Rating,
        string? Genres,
        string? ExternalUrl,
        string? ImdbId);

    private sealed record MetadataRefreshJobsResponse(
        int EnqueuedCount,
        int RemainingCount,
        int StaleCount,
        int MarkedForRefreshCount,
        string Message);
    /// <summary>
    /// What actually happened, in a sentence a person can act on.
    ///
    /// The old endpoint returned a count of queued jobs and nothing else, so on
    /// a 20,000-item library "Queued 500 titles" was indistinguishable from
    /// "finished" while covering 2.5% of it.
    /// </summary>
    private static string DescribeRefresh(int enqueued, int remaining)
    {
        if (enqueued == 0)
        {
            return remaining > 0
                ? "Nothing can be refreshed right now — everything stale was tried recently and is waiting out its cooldown."
                : "Nothing needs refreshing.";
        }

        var queued = $"Queued {enqueued:N0} title{(enqueued == 1 ? string.Empty : "s")}";

        return remaining > 0
            ? $"{queued}. Another {remaining:N0} still to go — Deluno keeps working through them in the background."
            : $"{queued}. That is everything that needs refreshing.";
    }


    private sealed record UpdateSeriesReplacementProtectionRequest(
        bool PreventLowerQualityReplacements);

    private sealed record BulkQualityProfileRequest(
        IReadOnlyList<string>? SeriesIds,
        string? QualityProfileId);

    private sealed record BulkReassignLibraryRequest(
        IReadOnlyList<string>? SeriesIds,
        string? FromLibraryId,
        string? ToLibraryId);

    private sealed record BulkTagsRequest(
        IReadOnlyList<string>? SeriesIds,
        string? Tags);

    private sealed record BulkRenamePreviewRequest(
        IReadOnlyList<string>? SeriesIds,
        string? Template);

    private sealed record BulkSearchRequest(IReadOnlyList<string>? SeriesIds);

}
