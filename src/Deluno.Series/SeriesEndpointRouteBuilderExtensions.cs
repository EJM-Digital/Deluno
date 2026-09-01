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
using Deluno.Quality.Guides;
using Deluno.Security;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Deluno.Series.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Quality.Contracts;

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
        // What the genre filter can offer. Its own endpoint rather than a facet
        // on the page, because it is asked for once when somebody opens the
        // filter panel and never again while they page through results.
        // What this shelf can be asked, ordered by, and draw — declared once,
        // per media kind, and served rather than copied into the browser.
        //
        // The browser used to keep its own sort list beside the server's and its
        // own poster-option list beside nothing at all, and `variant` decided
        // exactly two things in the filter panel: a hint under Year and which
        // genres endpoint to call. So a TV shelf was offered a film's controls.
        // One list, on the side that has to perform it (#324).
        series.MapGet("/controls", () => Results.Ok(CatalogueControls.For(MediaKind.Series)));

        series.MapGet("/genres", async (
            [FromServices] ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) => Results.Ok(await repository.ListGenresAsync(cancellationToken)));

        series.MapGet("/{id}/tags", async (
            string id,
            [FromServices] IMediaTagStore tagStore,
            CancellationToken cancellationToken) =>
            Results.Ok(await tagStore.ListAsync(MediaKind.Series, id, cancellationToken)));

        series.MapGet("/{id}/numbering", async (
            string id,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var numbering = await repository.GetNumberingAsync(id, cancellationToken);
            return numbering is null ? Results.NotFound() : Results.Ok(numbering);
        });

        series.MapPut("/{id}/numbering", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateSeriesNumberingRequest request,
            ISeriesCatalogRepository repository,
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

            try
            {
                var numbering = await repository.UpdateNumberingAsync(id, request, cancellationToken);
                return numbering is null ? Results.NotFound() : Results.Ok(numbering);
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["mappings"] = [exception.Message]
                });
            }
        });

        series.MapGet("/page", async (
            string? search,
            string? status,
            // A separate axis from `status`, so the two can be asked together.
            // Absent is "either"; "true"/"false" narrow it.
            bool? monitored,
            string? libraryId,
            string? sort,
            string? direction,
            int? pageSize,
            string? pageToken,
            // The custom narrowing, flat on the query string rather than a JSON
            // blob: these travel in a URL people bookmark, share and read.
            //
            // One `f` per condition — `f=quality:in:WEB 2160p|Remux 2160p` — read
            // against the field registry for this media kind. The nine named
            // parameters below it are what this shipped with, kept because URLs
            // outlive deploys, and translated into the same conditions.
            [FromQuery(Name = "f")] string[]? f,
            string? quality,
            string? genre,
            double? minSizeGb,
            double? maxSizeGb,
            int? minYear,
            int? maxYear,
            int? minRuntime,
            int? maxRuntime,
            double? minRating,
            [FromServices] ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!CatalogueFilters.TryBuild(
                    MediaKind.Series, f, quality, genre, minSizeGb, maxSizeGb,
                    minYear, maxYear, minRuntime, maxRuntime, minRating,
                    out var filters,
                    out var problems))
            {
                // A condition this kind cannot answer is refused, never dropped.
                // The rule engine deleted in #302 dropped them, and two of its
                // branches matched zero rows forever without anybody noticing.
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["f"] = [.. problems]
                });
            }

            var page = await repository.ListPageAsync(
                new CatalogueQuery(
                    Search: search,
                    Status: status,
                    Monitored: monitored,
                    LibraryId: libraryId,
                    Sort: sort,
                    Descending: !string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase),
                    PageSize: pageSize ?? 50,
                    PageToken: pageToken,
                    Filters: filters),
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

        series.MapGet("/{id}/preference-evaluation", async (
            string id,
            string? libraryId,
            string? fileIdentity,
            IMediaStateRepository mediaStateRepository,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
                MediaKind.Series,
                id,
                libraryId,
                fileIdentity,
                cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
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
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            IMediaTagStore tagStore,
            IReleasePreferencePlanRepository releasePreferencePlanRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await MediaSearchHandler.ExecuteAsync(
                MediaKind.Series,
                id,
                mode,
                mediaStateRepository,
                platformSettingsRepository,
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
                cancellationToken,
                tagStore,
                releasePreferencePlanRepository);

            if (result.NotFound)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                result.Outcome,
                result.Summary,
                result.Reason,
                result.ReleaseName,
                result.IndexerName,
                result.DispatchStatus,
                result.DispatchMessage,
                failures = result.Failures ?? [],
                candidates = result.Candidates.Select(candidate => new
                {
                    candidate.ReleaseName,
                    candidate.IndexerName,
                    candidate.Quality,
                    Score = candidate.PreferenceEvaluation is null ? (int?)candidate.Score : null,
                    candidate.MeetsCutoff,
                    candidate.Summary,
                    candidate.DownloadUrl,
                    candidate.SizeBytes,
                    candidate.Seeders,
                    candidate.DecisionStatus,
                    candidate.DecisionReasons,
                    candidate.RiskFlags,
                    candidate.QualityDelta,
                    CustomFormatScore = candidate.PreferenceEvaluation is null ? (int?)candidate.CustomFormatScore : null,
                    SeederScore = candidate.PreferenceEvaluation is null ? (int?)candidate.SeederScore : null,
                    SizeScore = candidate.PreferenceEvaluation is null ? (int?)candidate.SizeScore : null,
                    candidate.ReleaseGroup,
                    candidate.EstimatedBitrateMbps,
                    candidate.PolicyVersion,
                    candidate.PreferenceEvaluation,
                    candidate.PreferenceComparison
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

            MetadataSearchResult? match;
            if (!string.IsNullOrWhiteSpace(item.MetadataProviderId))
            {
                var lookup = await metadataProvider.ResolveProviderRecordAsync(
                    new MetadataLookupRequest(item.Title, "tv", item.StartYear, item.MetadataProviderId),
                    cancellationToken);
                if (lookup.Status == MetadataProviderRecordStatus.Missing)
                {
                    var issue = MissingProviderIssue("series", lookup);
                    var isNewEvidence = await repository.RecordMetadataProviderIssueAsync(item.Id, issue, cancellationToken);
                    if (isNewEvidence)
                    {
                        await activityFeedRepository.RecordActivityAsync(
                            "metadata.series.provider-record-missing",
                            $"{item.Title} was kept in Deluno because its linked {lookup.Provider.ToUpperInvariant()} record is no longer available.",
                            JsonSerializer.Serialize(issue),
                            null,
                            "series",
                            item.Id,
                            cancellationToken);
                    }

                    return Results.Conflict(new
                    {
                        code = "metadata-provider-record-missing",
                        message = $"{item.Title} was kept. Its linked {lookup.Provider.ToUpperInvariant()} record is no longer available."
                    });
                }

                if (lookup.Status == MetadataProviderRecordStatus.Unavailable)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                match = lookup.Result;
            }
            else
            {
                var matches = await metadataProvider.SearchAsync(
                    new MetadataLookupRequest(item.Title, "tv", item.StartYear, null),
                    cancellationToken);
                match = matches.FirstOrDefault();
            }

            if (match is null)
            {
                return Results.NotFound(new { message = "No metadata match was found for this TV show." });
            }

            SeriesListItem? updated;
            try
            {
                updated = await ApplyMetadataAsync(repository, item.Id, match, cancellationToken);
            }
            catch (MetadataIdentityConflictException)
            {
                return Results.Conflict(new
                {
                    code = "metadata-link-identity-claimed",
                    message = "Another held show claimed this metadata identity after the preview. Review the remap again."
                });
            }
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

        series.MapGet("/{id}/metadata/issue", async (
            string id,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (await repository.GetByIdAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await repository.GetMetadataProviderIssueAsync(id, cancellationToken));
        });

        series.MapPost("/{id}/metadata/issue/acknowledge", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null) return Results.NotFound();

            var before = await repository.GetMetadataProviderIssueAsync(id, cancellationToken);
            if (before is null) return Results.NoContent();

            var issue = await repository.AcknowledgeMetadataProviderIssueAsync(id, cancellationToken);
            if (before.AcknowledgedUtc is null)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "metadata.series.provider-record-missing.acknowledged",
                    $"The metadata notice for {item.Title} was acknowledged. The show and its files were kept.",
                    JsonSerializer.Serialize(issue),
                    null,
                    "series",
                    item.Id,
                    cancellationToken);
            }

            return Results.Ok(issue);
        });

        series.MapPost("/{id}/metadata/link/preview", async (
            string id,
            [FromBody] MetadataLinkRequest request,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IMetadataProvider metadataProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["providerId"] = ["Choose the metadata match Deluno should preview for this series."]
                });
            }

            var plan = await BuildMetadataLinkPlanAsync(item, request.ProviderId, repository, metadataProvider, cancellationToken);
            return plan.Status switch
            {
                MetadataProviderRecordStatus.Missing => Results.NotFound(new { message = "The selected metadata record no longer exists." }),
                MetadataProviderRecordStatus.Unavailable => Results.Json(
                    new { message = "The metadata provider or its episode catalogue is temporarily unavailable. Nothing was changed." },
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Ok(plan.Preview)
            };
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

            if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["confirmationToken"] = ["Preview this metadata remap before applying it."]
                });
            }

            var plan = await BuildMetadataLinkPlanAsync(item, request.ProviderId, repository, metadataProvider, cancellationToken);
            if (plan.Status == MetadataProviderRecordStatus.Missing)
            {
                return Results.NotFound(new { message = "The selected metadata record no longer exists. Nothing was changed." });
            }
            if (plan.Status == MetadataProviderRecordStatus.Unavailable)
            {
                return Results.Json(
                    new { message = "The metadata provider or its episode catalogue is temporarily unavailable. Nothing was changed." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (plan.Preview is null || plan.Match is null)
            {
                return Results.NotFound(new { message = "The selected metadata match could not be resolved." });
            }
            if (!plan.Preview.CanApply)
            {
                return Results.Conflict(new
                {
                    code = "metadata-link-blocked",
                    message = plan.Preview.BlockReason,
                    preview = plan.Preview
                });
            }
            if (!string.Equals(request.ConfirmationToken, plan.Preview.ConfirmationToken, StringComparison.Ordinal))
            {
                return Results.Conflict(new
                {
                    code = "metadata-link-preview-stale",
                    message = "The title, episode catalogue, or provider record changed after the preview. Review the remap again.",
                    preview = plan.Preview
                });
            }

            var match = plan.Match;

            SeriesListItem? updated;
            try
            {
                updated = await ApplyMetadataAsync(repository, item.Id, match, cancellationToken, replaceIdentity: true);
            }
            catch (MetadataIdentityConflictException)
            {
                return Results.Conflict(new
                {
                    code = "metadata-link-identity-claimed",
                    message = "Another held show claimed this metadata identity after the preview. Review the remap again."
                });
            }
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
                new MediaMetadataUpdate(
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
                    // Provider facts are not part of an override and are left
                    // alone rather than blanked: the write COALESCEs them.
                    RuntimeMinutes: null,
                    Popularity: null,
                    VoteCount: null),
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
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IReleasePreferencePlanRepository releasePreferencePlanRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            IMediaTagStore tagStore,
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

            var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                    MediaKind.Series,
                    [id],
                    cancellationToken))
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
                .FirstOrDefault();
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
            var allowedQualities = await QualityProfileResolver.ResolveAllowedQualitiesAsync(qualityRepository, library.QualityProfileId, cancellationToken);
            var upgradeUntilCutoff = await QualityProfileResolver.ResolveUpgradeUntilCutoffAsync(qualityRepository, library.QualityProfileId, cancellationToken);
            var preferencePlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
                qualityRepository,
                releasePreferencePlanRepository,
                library.QualityProfileId,
                cancellationToken,
                customFormats);
            var tagNames = (await tagStore.ListAsync(MediaKind.Series, seriesItem.Id, cancellationToken))
                .Select(tag => tag.Name)
                .ToArray();
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
            var failures = new List<IntegrationFailure>();

            foreach (var episode in targetEpisodes)
            {
                var baseline = await SeriesSearchBaselineResolver.ResolveEpisodeAsync(
                    repository,
                    mediaStateRepository,
                    seriesItem.Id,
                    episode.EpisodeId,
                    library.Id,
                    cancellationToken);
                // The planner owns the numbering suffix. Keeping the title
                // itself clean is important for TV-search APIs, where the
                // canonical season/episode (or an alternate numbering key)
                // is sent in dedicated fields.
                var queryTitle = seriesItem.Title;
                var decisionPlan = await acquisitionPipeline.PlanAsync(
                    new AcquisitionDecisionRequest(
                        queryTitle,
                        seriesItem.StartYear,
                        "tv",
                        baseline.CurrentQuality,
                        baseline.TargetQuality,
                        routing?.Sources ?? [],
                        routing?.DownloadClients ?? [],
                        customFormats,
                        SeasonNumber: episode.SeasonNumber,
                        EpisodeNumber: episode.EpisodeNumber,
                        AllowedQualities: allowedQualities,
                        TagNames: tagNames,
                        SearchKind: AcquisitionSearchKinds.Interactive,
                        AvailableUtc: wantedItem.AvailableUtc,
                        CurrentFilePresent: !string.IsNullOrWhiteSpace(baseline.FilePath),
                        CurrentReleaseName: baseline.FilePath,
                        UpgradeUntilCutoff: upgradeUntilCutoff,
                        NumberingScheme: seriesItem.NumberingScheme,
                        AbsoluteNumber: episode.AbsoluteNumber,
                        AirDate: episode.AirDate,
                        SceneSeasonNumber: episode.SceneSeasonNumber,
                        SceneEpisodeNumber: episode.SceneEpisodeNumber,
                        CurrentPreferenceEvaluation: baseline.PreferenceEvaluation,
                        PreferencePlan: preferencePlan),
                    cancellationToken);
                var searchPlan = decisionPlan.SearchPlan;
                var bestCandidate = searchPlan.BestCandidate;
                var outcome = decisionPlan.Outcome;
                searchReasons.Add(searchPlan.Reason);
                failures.AddRange(searchPlan.Failures ?? []);

                if (decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
                {
                    matchedCount++;
                    queuedCount++;
                    var downloadClient = decisionPlan.SelectedDownloadClient;
                    var grabResult = bestCandidate!.DownloadUrl is null
                        ? new DownloadClientGrabResult(downloadClient.DownloadClientId, bestCandidate.ReleaseName, false, "planned", "No download URL was available.")
                        {
                            Failure = IntegrationFailureFactory.FromLegacy(
                                "download-client",
                                downloadClient.DownloadClientId,
                                downloadClient.DownloadClientName,
                                "grab",
                                "planned",
                                "No downloadable URL was available for this release.")
                        }
                        : await downloadClientGrabService.GrabAsync(downloadClient.DownloadClientId, decisionPlan.DispatchRequest, cancellationToken);
                    if (grabResult.Failure is { } dispatchFailure)
                    {
                        failures.Add(dispatchFailure);
                    }
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
                        grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
                        cancellationToken: cancellationToken,
                        failure: grabResult.Failure,
                        replacementAuthorized: !string.IsNullOrWhiteSpace(baseline.FilePath),
                        replacementExpectedPath: baseline.FilePath,
                        clientExternalId: grabResult.ExternalId);
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
                failedCount,
                failures = failures.Distinct().ToArray()
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
            IRecycleBinService recycleBinService,
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
                                recycleBinService,
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

        /*
          Re-read the subtitles Deluno already has on disk, for a selection.

          Not a new job type: a title becomes a scan candidate when it has no
          scan row, so forgetting the probe is the whole instruction and the
          library's existing subtitle pass does the work. A per-title subtitle
          job would have been a second way to do the same thing, racing the
          first (DESIGN-002 rule 3).
        */
        series.MapPost("/bulk/subtitle-rescan", async (
            HttpContext httpContext,
            [FromBody] BulkSubtitleRescanRequest request,
            IMediaSubtitleRepository subtitles,
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
                    ["seriesIds"] = ["Choose at least one title before re-reading subtitles."]
                });
            }

            var cleared = await subtitles.ClearScansAsync(MediaKind.Series, request.SeriesIds, cancellationToken);

            // "queued" rather than "done": the pass that re-reads them runs on
            // the library's own schedule, and saying otherwise would promise
            // something this request has not made happen.
            return Results.Ok(new { queued = request.SeriesIds.Count, cleared });
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
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository platformSettingsRepository,
            IJobQueueRepository jobQueueRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await MediaBulkSearchHandler.ExecuteAsync(
                MediaKind.Series,
                request.SeriesIds,
                mediaStateRepository,
                platformSettingsRepository,
                jobQueueRepository,
                cancellationToken);

            if (result.ValidationErrors is not null)
            {
                return Results.ValidationProblem(result.ValidationErrors);
            }

            return Results.Ok(new
            {
                searchesTriggered = result.SearchesTriggered,
                libraryCount = result.LibraryCount
            });
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
            IMediaTagStore tagStore,
            IPlatformSettingsRepository platformSettingsRepository,
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
            var managedTags = await platformSettingsRepository.ListTagsAsync(cancellationToken);
            var assignments = normalizedTags
                .Select(name =>
                {
                    var managed = managedTags.FirstOrDefault(tag =>
                        string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase));
                    return new MediaTagAssignment(managed?.Id ?? MediaTagIds.ForLegacyName(name), name);
                })
                .ToArray();
            var updated = 0;
            foreach (var id in request.SeriesIds)
            {
                var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
                if (seriesItem is null)
                {
                    continue;
                }

                await tagStore.ReplaceAsync(MediaKind.Series, seriesItem.Id, assignments, cancellationToken);
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
                    proposedName = NamingTemplateRenderer.RenderSegment(
                        template,
                        seriesItem.Title,
                        seriesItem.StartYear,
                        seriesItem.ImdbId,
                        tvDbId: ReadMetadataText(seriesItem.MetadataJson, "TvDbId", "tvdbId", "tvdb_id"),
                        network: ReadMetadataText(seriesItem.MetadataJson, "Network", "network"),
                        genre: seriesItem.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
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
            IMediaTagStore tagStore,
            IGuidePackageStore guidePackageStore,
            IReleasePreferencePlanRepository releasePreferencePlanRepository,
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
                cancellationToken,
                guidePackageStore,
                releasePreferencePlanRepository);

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
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IReleasePreferencePlanRepository releasePreferencePlanRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
            IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            IMediaTagStore tagStore,
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

            var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                    MediaKind.Series,
                    [id],
                    cancellationToken))
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
                .FirstOrDefault();
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
            var allowedQualities = await QualityProfileResolver.ResolveAllowedQualitiesAsync(qualityRepository, library.QualityProfileId, cancellationToken);
            var upgradeUntilCutoff = await QualityProfileResolver.ResolveUpgradeUntilCutoffAsync(qualityRepository, library.QualityProfileId, cancellationToken);
            var preferencePlan = await QualityProfileResolver.ResolveReleasePreferencePlanAsync(
                qualityRepository,
                releasePreferencePlanRepository,
                library.QualityProfileId,
                cancellationToken,
                customFormats);
            var tagNames = (await tagStore.ListAsync(MediaKind.Series, seriesItem.Id, cancellationToken))
                .Select(tag => tag.Name)
                .ToArray();

            // A season pack is one candidate, but an installed season has one
            // independently evaluated current file per episode. Load every
            // exact baseline now; a missing or stale snapshot holds before an
            // indexer query, while complete evidence is compared after the
            // search against the same candidate and immutable plan.
            var installedEpisodeIds = await SeriesSearchBaselineResolver.ListInstalledEpisodeIdsAsync(
                repository,
                seasonEpisodes.Select(episode => episode.EpisodeId).ToArray(),
                cancellationToken);
            var installedEpisodes = new List<SeasonPackInstalledEpisode>();
            foreach (var episodeId in installedEpisodeIds)
            {
                var baseline = await SeriesSearchBaselineResolver.ResolveEpisodeAsync(
                    repository,
                    mediaStateRepository,
                    seriesItem.Id,
                    episodeId,
                    library.Id,
                    cancellationToken);
                installedEpisodes.Add(new SeasonPackInstalledEpisode(
                    episodeId,
                    baseline.FilePath ?? string.Empty,
                    baseline.PreferenceEvaluation));
            }
            var missingInstalledEvidence = preferencePlan is null
                ? installedEpisodes
                : installedEpisodes.Where(item =>
                        string.IsNullOrWhiteSpace(item.FilePath) ||
                        item.PreferenceEvaluation is null ||
                        !string.Equals(item.PreferenceEvaluation.PlanId, preferencePlan.Id, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(item.PreferenceEvaluation.PlanVersion, preferencePlan.Version, StringComparison.Ordinal) ||
                        !string.Equals(item.PreferenceEvaluation.PlanHash, preferencePlan.PlanHash, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (missingInstalledEvidence.Count > 0)
            {
                const string holdMessage = "Whole-season replacement was held because at least one installed episode does not have an exact evaluation under the current release-preference plan.";
                foreach (var episode in seasonEpisodes)
                {
                    await repository.RecordSearchAttemptAsync(
                        seriesItem.Id,
                        episode.EpisodeId,
                        library.Id,
                        "manual-season",
                        "held",
                        now,
                        nextEligibleSearchUtc,
                        holdMessage,
                        null,
                        null,
                        JsonSerializer.Serialize(new
                        {
                            episode.EpisodeId,
                            episode.SeasonNumber,
                            episode.EpisodeNumber,
                            installedFilePresent = installedEpisodeIds.Contains(episode.EpisodeId, StringComparer.OrdinalIgnoreCase),
                            currentPlanEvidencePresent = !missingInstalledEvidence.Any(item => string.Equals(item.EpisodeId, episode.EpisodeId, StringComparison.OrdinalIgnoreCase)),
                            preferencePlanId = preferencePlan?.Id,
                            preferencePlanVersion = preferencePlan?.Version,
                            preferencePlanHash = preferencePlan?.PlanHash
                        }),
                        cancellationToken);
                }

                await activityFeedRepository.RecordActivityAsync(
                    "series.search.season",
                    $"{seriesItem.Title} season {seasonNumber} search was held until installed-file evidence is current.",
                    JsonSerializer.Serialize(new
                    {
                        seasonNumber,
                        installedEpisodeIds,
                        reason = MediaSearchReasons.SeasonPackInstalledEvidenceMissing,
                        missingEvidenceEpisodeIds = missingInstalledEvidence.Select(item => item.EpisodeId).ToArray()
                    }),
                    null,
                    "series",
                    seriesItem.Id,
                    cancellationToken);

                await activityFeedRepository.RecordDecisionAsync(
                    new DecisionExplanationPayload(
                        Scope: "series.season-search",
                        Status: "held",
                        Reason: holdMessage,
                        Inputs: new Dictionary<string, string?>
                        {
                            ["title"] = seriesItem.Title,
                            ["seasonNumber"] = seasonNumber.ToString(),
                            ["libraryId"] = library.Id,
                            ["episodeCount"] = seasonEpisodes.Count.ToString(),
                            ["installedEpisodeCount"] = installedEpisodeIds.Count.ToString(),
                            ["preferencePlanId"] = preferencePlan?.Id,
                            ["preferencePlanVersion"] = preferencePlan?.Version,
                            ["preferencePlanHash"] = preferencePlan?.PlanHash
                        },
                        Outcome: "No indexer query or download dispatch was made until every installed episode has same-plan evidence.",
                        Alternatives:
                        [
                            new DecisionAlternativeExplanation(
                                "Targeted episode search",
                                "available",
                                "Search selected episodes individually or let the file probe produce the missing current-plan evaluation.")
                        ]),
                    null,
                    "series",
                    seriesItem.Id,
                    cancellationToken);

                return Results.Ok(new
                {
                    outcome = "held",
                    reason = MediaSearchReasons.SeasonPackInstalledEvidenceMissing,
                    seasonNumber,
                    searchedEpisodes = seasonEpisodes.Count,
                    installedEpisodeCount = installedEpisodeIds.Count,
                    missingEvidenceEpisodeCount = missingInstalledEvidence.Count,
                    matchedCount = 0,
                    queuedCount = 0,
                    releaseName = (string?)null,
                    indexerName = (string?)null,
                    dispatchStatus = (string?)null,
                    dispatchMessage = holdMessage
                });
            }

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

            var decisionPlan = await acquisitionPipeline.PlanAsync(
                new AcquisitionDecisionRequest(
                    // The planner owns the season suffix and the TV API's
                    // dedicated season parameter. Passing a title that already
                    // contains "Season 01" produced queries such as
                    // "Show Season 01 S01" and made season-pack matching
                    // dependent on an accidental duplicate token.
                    seriesItem.Title,
                    seriesItem.StartYear,
                    "tv",
                    null,
                    wantedItem.TargetQuality,
                    routing?.Sources ?? [],
                    routing?.DownloadClients ?? [],
                    customFormats,
                    SeasonNumber: seasonNumber,
                    AllowedQualities: allowedQualities,
                    TagNames: tagNames,
                    SearchKind: AcquisitionSearchKinds.Interactive,
                    AvailableUtc: wantedItem.AvailableUtc,
                    UpgradeUntilCutoff: upgradeUntilCutoff,
                    NumberingScheme: seriesItem.NumberingScheme,
                    PreferencePlan: preferencePlan),
                cancellationToken);
            var searchPlan = decisionPlan.SearchPlan;
            var bestCandidate = searchPlan.BestCandidate;
            SeasonPackReplacementDecision? replacementDecision = null;
            if (installedEpisodes.Count > 0 && preferencePlan is not null && bestCandidate is not null)
            {
                replacementDecision = SeriesSearchBaselineResolver.EvaluateSeasonPackCandidate(
                    preferencePlan,
                    bestCandidate,
                    installedEpisodes);
            }
            var replacementHeld = replacementDecision is { Authorized: false };
            var outcome = replacementHeld ? "held" : decisionPlan.Outcome;
            var failures = (searchPlan.Failures ?? []).ToList();
            DownloadClientGrabResult? grabResult = null;
            if (!replacementHeld && decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null)
            {
                var downloadClient = decisionPlan.SelectedDownloadClient;
                grabResult = bestCandidate!.DownloadUrl is null
                    ? new DownloadClientGrabResult(downloadClient.DownloadClientId, bestCandidate.ReleaseName, false, "planned", "No download URL was available.")
                    {
                        Failure = IntegrationFailureFactory.FromLegacy(
                            "download-client",
                            downloadClient.DownloadClientId,
                            downloadClient.DownloadClientName,
                            "grab",
                            "planned",
                            "No downloadable URL was available for this release.")
                    }
                    : await downloadClientGrabService.GrabAsync(downloadClient.DownloadClientId, decisionPlan.DispatchRequest, cancellationToken);
                if (grabResult.Failure is { } dispatchFailure)
                {
                    failures.Add(dispatchFailure);
                }
                await jobQueueRepository.RecordDownloadDispatchAsync(
                    library.Id,
                    "tv",
                    "series",
                    seriesItem.Id,
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
                        replacementComparisons = replacementDecision?.Comparisons,
                        grabResult
                    }),
                    grabResponseCode: grabResult.Succeeded ? 200 : 400,
                    grabFailureCode: grabResult.Failure?.Code ?? grabResult.FailureCode,
                    cancellationToken: cancellationToken,
                    failure: grabResult.Failure,
                    replacementAuthorized: replacementDecision is { Authorized: true, Targets.Count: > 0 },
                    replacementTargets: replacementDecision?.Targets,
                    clientExternalId: grabResult.ExternalId);
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
                    replacementHeld ? replacementDecision!.Reason : decisionPlan.SearchResult,
                    searchPlan.BestCandidate?.ReleaseName,
                    searchPlan.BestCandidate?.IndexerName,
                    // The route's `seasonNumber` and `episode.SeasonNumber` differ
                    // only by case, and System.Text.Json refuses to bind two such
                    // members to one constructor parameter — it threw on every
                    // season search, so the endpoint always returned 500 (#285).
                    // The per-episode value is the accurate one and the route
                    // value was redundant: seasonEpisodes is already filtered to
                    // that season.
                    searchPlan.Candidates.Count == 0
                        ? JsonSerializer.Serialize(new
                        {
                            episode.EpisodeId,
                            episode.SeasonNumber,
                            episode.EpisodeNumber
                        })
                        : JsonSerializer.Serialize(new
                        {
                            episode.EpisodeId,
                            episode.SeasonNumber,
                            episode.EpisodeNumber,
                            searchPlan,
                            replacementComparisons = replacementDecision?.Comparisons
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
                    Reason: replacementHeld ? replacementDecision!.Reason : decisionPlan.SearchResult,
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
                    Outcome: replacementHeld
                        ? "No dispatch was made because the candidate was not a proven upgrade for every installed episode."
                        : grabResult is null
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
                reason = replacementHeld
                    ? MediaSearchReasons.SeasonPackCandidateNotUpgradeForEveryEpisode
                    : searchPlan.Reason,
                searchedEpisodes = seasonEpisodes.Count,
                installedEpisodeCount = installedEpisodes.Count,
                matchedCount = searchPlan.BestCandidate is null || replacementHeld ? 0 : seasonEpisodes.Count,
                queuedCount = searchPlan.BestCandidate is null || replacementHeld ? 0 : 1,
                releaseName = searchPlan.BestCandidate?.ReleaseName,
                indexerName = searchPlan.BestCandidate?.IndexerName,
                dispatchStatus = grabResult?.Status,
                dispatchMessage = replacementHeld ? replacementDecision!.Reason : grabResult?.Message,
                replacementComparisons = replacementDecision?.Comparisons,
                failures = failures.Distinct().ToArray()
            });
        });

        series.MapGet("/{id}/workflow-status", async (
            string id,
            ISeriesCatalogRepository repository,
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository platformSettingsRepository,
            ISeriesWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                    MediaKind.Series,
                    [id],
                    cancellationToken))
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
                .FirstOrDefault();
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
            IMediaStateRepository mediaStateRepository,
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

            var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                    MediaKind.Series,
                    [id],
                    cancellationToken))
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
                .FirstOrDefault();
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
            IMediaStateRepository mediaStateRepository,
            ISeriesWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
            if (seriesItem is null)
            {
                return Results.NotFound();
            }

            var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                    MediaKind.Series,
                    [id],
                    cancellationToken))
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
                .FirstOrDefault();
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

        if (request.SeriesType is not null && !SeriesTypes.IsKnown(request.SeriesType))
        {
            errors["seriesType"] = ["Series type must be standard, daily, or anime."];
        }

        if (request.NumberingScheme is not null && !SeriesNumberingSchemes.IsKnown(request.NumberingScheme))
        {
            errors["numberingScheme"] = ["Numbering scheme must be standard, airdate, absolute, or scene."];
        }

        if (request.NumberingSource is not null &&
            !string.Equals(request.NumberingSource, SeriesNumberingSources.Provider, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.NumberingSource, SeriesNumberingSources.Owner, StringComparison.OrdinalIgnoreCase))
        {
            errors["numberingSource"] = ["Numbering source must be provider or owner."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> Validate(UpdateSeriesNumberingRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (request.SeriesType is not null && !SeriesTypes.IsKnown(request.SeriesType))
        {
            errors["seriesType"] = ["Series type must be standard, daily, or anime."];
        }

        if (request.NumberingScheme is not null && !SeriesNumberingSchemes.IsKnown(request.NumberingScheme))
        {
            errors["numberingScheme"] = ["Numbering scheme must be standard, airdate, absolute, or scene."];
        }

        if (request.NumberingSource is not null &&
            !string.Equals(request.NumberingSource, SeriesNumberingSources.Provider, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.NumberingSource, SeriesNumberingSources.Owner, StringComparison.OrdinalIgnoreCase))
        {
            errors["numberingSource"] = ["Numbering source must be provider or owner."];
        }

        if (request.Mappings is not null)
        {
            var duplicateIds = request.Mappings
                .Where(mapping => !string.IsNullOrWhiteSpace(mapping.EpisodeId))
                .GroupBy(mapping => mapping.EpisodeId, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateIds.Length > 0)
            {
                errors["mappings"] = ["Each episode may appear only once in a numbering mapping."];
            }

            for (var index = 0; index < request.Mappings.Count; index++)
            {
                var mapping = request.Mappings[index];
                if (string.IsNullOrWhiteSpace(mapping.EpisodeId))
                {
                    errors[$"mappings[{index}].episodeId"] = ["A numbering mapping must identify an episode."];
                }

                if (mapping.AbsoluteNumber is <= 0 or > 9999)
                {
                    errors[$"mappings[{index}].absoluteNumber"] = ["Absolute episode numbers must be between 1 and 9999."];
                }

                if ((mapping.SceneSeasonNumber is null) != (mapping.SceneEpisodeNumber is null))
                {
                    errors[$"mappings[{index}].scene"] = ["Scene season and episode numbers must be supplied together."];
                }
            }
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

    private static string? ReadMetadataText(string? metadataJson, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
            }
        }
        catch (JsonException)
        {
            // Metadata is user/provider data. A malformed blob must not stop a
            // rename preview from showing the safe title fallback.
        }

        return null;
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
        IRecycleBinService recycleBinService,
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

            var deletion = await recycleBinService.MoveAsync(trackedFiles, libraries, cancellationToken);
            metadata["deletedFileCount"] = deletion.MovedFileCount.ToString();
            metadata["deletedFolderCount"] = deletion.MovedFolderCount.ToString();
            metadata["recycleBinItemCount"] = deletion.Items.Count.ToString();
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
                        new CreateIntakeListExclusionRequest(series.Title, series.StartYear, series.ImdbId, null, "Removed from library by user"),
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

    /// <summary>
    /// Hand a provider result to the catalogue.
    ///
    /// <para>This used to spell the mapping out as sixteen positional
    /// arguments, and it never passed <c>Status</c> or <c>Network</c> — the two
    /// fields V0020 added a column for. The write succeeded, the endpoint
    /// returned 200, and the filter over the column returned nothing. The
    /// mapping now lives in <c>CatalogueMetadata</c>, once.</para>
    /// </summary>
    private static Task<SeriesListItem?> ApplyMetadataAsync(
        ISeriesCatalogRepository repository,
        string seriesId,
        MetadataSearchResult result,
        CancellationToken cancellationToken,
        bool replaceIdentity = false)
        => repository.UpdateMetadataAsync(
            CatalogueMetadata.ToUpdate(seriesId, result, result.Network) with
            {
                Title = replaceIdentity ? result.Title : null,
                Year = replaceIdentity ? result.Year : null
            },
            cancellationToken);

    private static MetadataProviderIssue MissingProviderIssue(
        string mediaType,
        MetadataProviderRecordLookup lookup)
        => new(
            "provider-record-missing",
            lookup.Provider,
            lookup.ProviderId,
            $"{lookup.Provider}:{mediaType}:{lookup.ProviderId}:missing".ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            null);

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

    private static async Task<SeriesMetadataLinkPlan> BuildMetadataLinkPlanAsync(
        SeriesListItem item,
        string providerId,
        ISeriesCatalogRepository repository,
        IMetadataProvider metadataProvider,
        CancellationToken cancellationToken)
    {
        var lookup = await metadataProvider.ResolveProviderRecordAsync(
            new MetadataLookupRequest(item.Title, "tv", item.StartYear, providerId.Trim()),
            cancellationToken);
        if (lookup.Status != MetadataProviderRecordStatus.Found || lookup.Result is null)
        {
            return new SeriesMetadataLinkPlan(lookup.Status, null, null);
        }

        IReadOnlyList<MetadataSeason> seasons;
        try
        {
            seasons = await metadataProvider.GetSeriesCatalogueAsync(lookup.Result.ProviderId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new SeriesMetadataLinkPlan(MetadataProviderRecordStatus.Unavailable, null, null);
        }

        var match = lookup.Result;
        var inventory = await repository.GetInventoryDetailAsync(item.Id, cancellationToken);
        var evaluation = MetadataCatalogueSafety.Evaluate(
            (inventory?.Episodes ?? []).Select(episode => new MetadataEpisodeIdentity(
                episode.SeasonNumber,
                episode.EpisodeNumber,
                episode.HasFile)),
            seasons
            .SelectMany(season => season.Episodes)
            .Select(episode => new MetadataEpisodeIdentity(
                episode.SeasonNumber,
                episode.EpisodeNumber)));
        var proposedKeys = evaluation.ProposedKeys;
        var existingEpisodes = inventory?.Episodes ?? [];
        var impact = evaluation.Impact;

        var current = new MetadataLinkIdentity(
            item.MetadataProvider,
            item.MetadataProviderId,
            item.Title,
            item.StartYear,
            item.ImdbId,
            ReadMetadataText(item.MetadataJson, "Network", "network"));
        var proposed = new MetadataLinkIdentity(
            match.Provider,
            match.ProviderId,
            match.Title,
            match.Year,
            match.ImdbId,
            match.Network);
        var changes = DescribeMetadataIdentityChanges(current, proposed);
        var conflict = await repository.FindMetadataIdentityConflictAsync(
            item.Id,
            match.Title,
            match.Year,
            match.ImdbId,
            match.Provider,
            match.ProviderId,
            cancellationToken);
        var consequences = new List<string>
        {
            "Imported files, monitoring, history, tags, numbering overrides, and plan assignments will be kept.",
            "Provider artwork, overview, ratings, genres, IDs, network, and episode catalogue will be refreshed from the selected record.",
            $"The selected catalogue has {impact.ProposedEpisodeCount} episodes across {impact.ProposedSeasonCount} seasons; {evaluation.NewEpisodeCount} new episode rows will be added."
        };
        if (!string.Equals(current.Context, proposed.Context, StringComparison.OrdinalIgnoreCase))
        {
            consequences.Add($"Network will change from {DisplayMetadataValue(current.Context)} to {DisplayMetadataValue(proposed.Context)}.");
        }
        if (!evaluation.PreservesExistingCatalogue)
        {
            consequences.Add($"{impact.ExistingEpisodesOutsideProposed} existing episode row{(impact.ExistingEpisodesOutsideProposed == 1 ? " is" : "s are")} absent from the selected catalogue.");
        }

        string? blockReason = null;
        if (conflict is not null)
        {
            blockReason = $"{conflict.Title} already owns the proposed {DescribeConflictReason(conflict.Reason)}. Deluno will not merge or duplicate the two shows.";
        }
        else if (!evaluation.PreservesExistingCatalogue)
        {
            blockReason = "This remap would mix the current episode identity with a different provider catalogue. Resolve the unmatched episodes before linking it.";
        }

        var token = MetadataLinkPreviewTokens.Create(item.Id, item.UpdatedUtc, proposed, proposedKeys);
        var preview = new MetadataLinkPreview(
            "tv",
            item.Id,
            current,
            proposed,
            changes,
            consequences,
            conflict,
            impact,
            blockReason is null,
            blockReason,
            token);
        return new SeriesMetadataLinkPlan(lookup.Status, match, preview);
    }

    private static IReadOnlyList<string> DescribeMetadataIdentityChanges(
        MetadataLinkIdentity current,
        MetadataLinkIdentity proposed)
    {
        var changes = new List<string>();
        AddMetadataChange(changes, "Provider record", current.ProviderId, proposed.ProviderId);
        AddMetadataChange(changes, "Title", current.Title, proposed.Title);
        AddMetadataChange(changes, "Year", current.Year?.ToString(), proposed.Year?.ToString());
        AddMetadataChange(changes, "IMDb ID", current.ImdbId, proposed.ImdbId);
        return changes.Count == 0 ? ["The core identity is unchanged; provider metadata will be refreshed."] : changes;
    }

    private static void AddMetadataChange(List<string> changes, string label, string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) return;
        changes.Add($"{label}: {DisplayMetadataValue(before)} → {DisplayMetadataValue(after)}");
    }

    private static string DisplayMetadataValue(string? value) => string.IsNullOrWhiteSpace(value) ? "not set" : value;

    private static string DescribeConflictReason(string reason) => reason switch
    {
        "provider-id" => "provider record",
        "imdb-id" => "IMDb identity",
        _ => "title and year"
    };

    private sealed record SeriesMetadataLinkPlan(
        MetadataProviderRecordStatus Status,
        MetadataSearchResult? Match,
        MetadataLinkPreview? Preview);

    private sealed record MetadataLinkRequest(string? ProviderId, string? ConfirmationToken = null);

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

    private sealed record BulkSubtitleRescanRequest(IReadOnlyList<string>? SeriesIds);

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
