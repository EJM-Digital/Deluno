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
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Platform.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform;
using Deluno.Quality;
using Deluno.Quality.Data;
using Deluno.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Quality.Contracts;

namespace Deluno.Movies;

public static class MoviesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoMoviesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var movies = endpoints.MapGroup("/api/movies");

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
        movies.MapGet("/controls", () => Results.Ok(CatalogueControls.For(MediaKind.Movie)));

        movies.MapGet("/genres", async (
            [FromServices] IMovieCatalogRepository repository,
            CancellationToken cancellationToken) => Results.Ok(await repository.ListGenresAsync(cancellationToken)));

        movies.MapGet("/page", async (
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
            [FromServices] IMovieCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (!CatalogueFilters.TryBuild(
                    MediaKind.Movie, f, quality, genre, minSizeGb, maxSizeGb,
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

        movies.MapGet("/import-recovery", async (IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetImportRecoverySummaryAsync(cancellationToken);
            return Results.Ok(summary);
        });

        movies.MapGet("/wanted", async (IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetWantedSummaryAsync(cancellationToken);
            return Results.Ok(summary);
        });

        movies.MapGet("/calendar", async (
            DateOnly? from,
            DateOnly? to,
            int? take,
            IMovieCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7);
            var end = to ?? start.AddDays(35);
            if (end <= start)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["to"] = ["The end of the window must be after the start."]
                });
            }

            if (end.DayNumber - start.DayNumber > 400)
            {
                end = start.AddDays(400);
            }

            var items = await repository.ListCalendarMoviesAsync(
                start,
                end,
                Math.Clamp(take ?? 500, 1, 2000),
                cancellationToken);
            return Results.Ok(items);
        });

        movies.MapGet("/search-history", async (IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListSearchHistoryAsync(cancellationToken);
            return Results.Ok(items);
        });

        movies.MapGet("/{id}/removal-preview", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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
                    if (string.Equals(file.MovieId, id, StringComparison.OrdinalIgnoreCase))
                    {
                        trackedFiles.Add(new TrackedLibraryFile(file.LibraryId, file.FilePath));
                    }
                }
            }

            return Results.Ok(LibraryMediaDeletion.Preview(trackedFiles, libraries));
        });

        movies.MapPost("/import-recovery", async (
            HttpContext httpContext,
            [FromBody] CreateMovieImportRecoveryCaseRequest request,
            IMovieCatalogRepository repository,
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

        movies.MapPost("/import-recovery/{id}/resolve", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

        movies.MapPost("/import-recovery/{id}/dismiss", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

        movies.MapDelete("/import-recovery/{id}", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

        movies.MapGet("/{id}", async (string id, IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            return movie is null ? Results.NotFound() : Results.Ok(movie);
        });

        movies.MapPut("/monitoring", async (
            HttpContext httpContext,
            [FromBody] UpdateMovieMonitoringRequest request,
            IMovieCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Choose at least one movie before updating monitoring."]
                });
            }

            var updated = await repository.UpdateMonitoredAsync(
                request.MovieIds,
                request.Monitored,
                cancellationToken);

            foreach (var movieId in request.MovieIds.Distinct(StringComparer.OrdinalIgnoreCase))
                await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movieId, cancellationToken);

            return Results.Ok(new { updated });
        });

        movies.MapPost("/{id}/automation/defer", async (
            string id,
            [FromBody] DeferAutomationRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
            return Results.Ok(new { deferredUntilUtc });
        });

        movies.MapPost("/{id}/automation/skip-once", async (
            string id,
            [FromBody] SkipNextAutomationRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
            return Results.Ok(new { message = "The next scheduled search will be skipped. Manual search remains available." });
        });

        movies.MapPost("/{id}/search", async (
            string id,
            string? mode,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IMediaStateRepository mediaStateRepository,
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

            var result = await MediaSearchHandler.ExecuteAsync(
                MediaKind.Movie,
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
                (movieId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, cancellationToken) =>
                    repository.RecordSearchAttemptAsync(
                        movieId,
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

            return Results.Ok(new
            {
                result.Outcome,
                result.Summary,
                result.Reason,
                result.ReleaseName,
                result.IndexerName,
                result.DispatchStatus,
                result.DispatchMessage,
                candidates = result.Candidates.Select(candidate => new
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
                    candidate.PolicyVersion,
                    candidate.MatchedCustomFormats
                }).ToArray()
            });
        });

        movies.MapPost("/bulk", async (
            HttpContext httpContext,
            BulkMovieRequest request,
            IMovieCatalogRepository repository,
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

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Select at least one movie before performing bulk operations."]
                });
            }

            if (string.IsNullOrWhiteSpace(request.Operation))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["operation"] = ["Specify which operation to perform: remove, quality, monitoring, search, or grab."]
                });
            }

            var operation = request.Operation.ToLowerInvariant();
            var results = new List<BulkMovieItemResult>();
            int successCount = 0;
            int failureCount = 0;

            foreach (var movieId in request.MovieIds)
            {
                try
                {
                    var movie = await repository.GetByIdAsync(movieId, cancellationToken);
                    if (movie is null)
                    {
                        failureCount++;
                        results.Add(new BulkMovieItemResult(movieId, "Unknown", false, "Movie not found"));
                        continue;
                    }

                    switch (operation)
                    {
                        case "remove":
                            var removalMetadata = await RemoveMovieAsync(
                                movie,
                                request,
                                repository,
                                platformSettingsRepository,
                                intakeRepository,
                                jobQueueRepository,
                                activityFeedRepository,
                                cancellationToken);
                            successCount++;
                            results.Add(new BulkMovieItemResult(movie.Id, movie.Title, true, null, removalMetadata));
                            break;

                        case "monitoring":
                            if (!request.Monitored.HasValue)
                            {
                                failureCount++;
                                results.Add(new BulkMovieItemResult(movie.Id, movie.Title, false,
                                    "Monitored state must be specified for monitoring operation"));
                            }
                            else
                            {
                                await repository.UpdateMonitoredAsync([movie.Id], request.Monitored.Value, cancellationToken);
                                successCount++;
                                results.Add(new BulkMovieItemResult(movie.Id, movie.Title, true, null,
                                    new Dictionary<string, string?> { ["monitored"] = request.Monitored.Value.ToString() }));
                            }
                            break;

                        case "quality":
                            if (string.IsNullOrWhiteSpace(request.QualityProfileId))
                            {
                                failureCount++;
                                results.Add(new BulkMovieItemResult(movie.Id, movie.Title, false,
                                    "Quality profile ID must be specified for quality operation"));
                            }
                            else
                            {
                                await repository.UpdateQualityProfileAsync(movie.Id, request.QualityProfileId, cancellationToken);
                                successCount++;
                                results.Add(new BulkMovieItemResult(movie.Id, movie.Title, true, null,
                                    new Dictionary<string, string?> { ["qualityProfileId"] = request.QualityProfileId }));
                            }
                            break;

                        case "search":
                            var job = await jobScheduler.EnqueueAsync(
                                new EnqueueJobRequest(
                                    JobType: "movies.search.manual",
                                    Source: "bulk",
                                    PayloadJson: JsonSerializer.Serialize(new { movie.Id, movie.Title, movie.ReleaseYear }),
                                    RelatedEntityType: "movie",
                                    RelatedEntityId: movie.Id),
                                cancellationToken);
                            successCount++;
                            results.Add(new BulkMovieItemResult(movie.Id, movie.Title, true, null,
                                new Dictionary<string, string?> { ["jobId"] = job.Id }));
                            break;

                        default:
                            failureCount++;
                            results.Add(new BulkMovieItemResult(movie.Id, movie.Title, false,
                                $"Unknown operation: {request.Operation}"));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failureCount++;
                    results.Add(new BulkMovieItemResult(movieId, "Unknown", false, ex.Message));
                }
            }

            foreach (var result in results.Where(item => item.Succeeded).Select(item => item.MovieId).Distinct(StringComparer.OrdinalIgnoreCase))
                await realtimeEventPublisher.PublishEntityChangedAsync("Movie", result, cancellationToken);

            return Results.Ok(new BulkMovieResponse(request.MovieIds.Count, successCount, failureCount, operation, results));
        });

        movies.MapPost("/{id}/grab", async (
            string id,
            [FromBody] ReleaseGrabRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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
                MediaKind.Movie,
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
                (movieId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, cancellationToken) =>
                    repository.RecordSearchAttemptAsync(
                        movieId,
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

        movies.MapGet("/{id}/workflow-status", async (
            string id,
            IMovieCatalogRepository repository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IMovieWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.MovieId == id);
            if (wantedItem is null)
            {
                return Results.NotFound();
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);

            QualityProfileItem? profile = null;
            if (library?.QualityProfileId is not null)
            {
                var profiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
                profile = profiles.FirstOrDefault(item => item.Id == library.QualityProfileId);
            }

            var decision = workflowService.EvaluateWantedStatus(
                wantedItem.CurrentQuality,
                wantedItem.TargetQuality,
                wantedItem.QualityCutoffMet,
                profile?.UpgradeUntilCutoff ?? true,
                profile?.UpgradeUnknownItems ?? false);

            return Results.Ok(new
            {
                movieId = wantedItem.MovieId,
                title = wantedItem.Title,
                releaseYear = wantedItem.ReleaseYear,
                libraryId = wantedItem.LibraryId,
                wantedStatus = decision.WantedStatus,
                reason = decision.Reason,
                currentQuality = decision.CurrentQuality,
                targetQuality = decision.TargetQuality,
                preventLowerQualityReplacements = wantedItem.PreventLowerQualityReplacements,
                lastQualityDeltaDecision = wantedItem.LastQualityDeltaDecision
            });
        });

        movies.MapPut("/{id}/replacement-protection", async (
            string id,
            [FromBody] UpdateReplacementProtectionRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.MovieId == id);
            if (wantedItem is null)
            {
                return Results.NotFound();
            }

            var updated = await repository.UpdateMovieReplacementPolicyAsync(
                id,
                wantedItem.LibraryId,
                request.PreventLowerQualityReplacements,
                cancellationToken);

            if (!updated)
            {
                return Results.NotFound();
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
            return Results.NoContent();
        });

        movies.MapPost("/{id}/evaluate-candidate", async (
            string id,
            [FromBody] EvaluateCandidateRequest request,
            IMovieCatalogRepository repository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IMovieWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.CandidateQuality))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["candidateQuality"] = ["Provide the quality of the candidate release."]
                });
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.MovieId == id);
            if (wantedItem is null)
            {
                return Results.NotFound();
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);

            QualityProfileItem? profile = null;
            if (library?.QualityProfileId is not null)
            {
                var profiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
                profile = profiles.FirstOrDefault(item => item.Id == library.QualityProfileId);
            }

            var evaluation = workflowService.EvaluateCandidate(new MovieCandidateEvaluationInput(
                MovieId: id,
                CurrentQuality: wantedItem.CurrentQuality,
                CandidateQuality: request.CandidateQuality.Trim(),
                TargetQuality: wantedItem.TargetQuality,
                UpgradeUntilCutoff: profile?.UpgradeUntilCutoff ?? true,
                UpgradeUnknownItems: profile?.UpgradeUnknownItems ?? false,
                PreventLowerQualityReplacements: wantedItem.PreventLowerQualityReplacements,
                Profile: profile));

            return Results.Ok(new
            {
                evaluation.WantedStatus,
                evaluation.Reason,
                evaluation.IsReplacementAllowed,
                evaluation.QualityDelta,
                evaluation.CurrentQuality,
                evaluation.TargetQuality
            });
        });

        movies.MapPost("/{id}/metadata/refresh", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            var matches = await metadataProvider.SearchAsync(
                new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, movie.MetadataProviderId),
                cancellationToken);
            var match = matches.FirstOrDefault();
            if (match is null)
            {
                return Results.NotFound(new { message = "No metadata match was found for this movie." });
            }

            var updated = await ApplyMetadataAsync(repository, movie.Id, match, cancellationToken);
            await SyncReleaseDatesAsync(repository, metadataProvider, movie.Id, match.ProviderId, cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "metadata.movie.refreshed",
                $"{movie.Title} metadata was refreshed from {match.Provider.ToUpperInvariant()}.",
                JsonSerializer.Serialize(match),
                null,
                "movie",
                movie.Id,
                cancellationToken);

            if (updated is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", updated.Id, cancellationToken);
            return Results.Ok(updated);
        });

        movies.MapPost("/{id}/metadata/link", async (
            string id,
            [FromBody] MetadataLinkRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["providerId"] = ["Choose the metadata match Deluno should link to this movie."]
                });
            }

            var matches = await metadataProvider.SearchAsync(
                new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, request.ProviderId.Trim()),
                cancellationToken);
            var match = matches.FirstOrDefault(item => string.Equals(item.ProviderId, request.ProviderId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return Results.NotFound(new { message = "The selected metadata match could not be refreshed from the provider." });
            }

            var updated = await ApplyMetadataAsync(repository, movie.Id, match, cancellationToken);
            await SyncReleaseDatesAsync(repository, metadataProvider, movie.Id, match.ProviderId, cancellationToken);
            await activityFeedRepository.RecordActivityAsync(
                "metadata.movie.linked",
                $"{movie.Title} metadata was linked to {match.Provider.ToUpperInvariant()} item {match.ProviderId}.",
                JsonSerializer.Serialize(match),
                null,
                "movie",
                movie.Id,
                cancellationToken);

            if (updated is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", updated.Id, cancellationToken);
            return Results.Ok(updated);
        });

        movies.MapPost("/{id}/metadata/jobs", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            var job = await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "movies.metadata.refresh",
                    Source: "metadata",
                    PayloadJson: JsonSerializer.Serialize(new { movie.Id, movie.Title, movie.ReleaseYear }),
                    RelatedEntityType: "movie",
                    RelatedEntityId: movie.Id),
                cancellationToken);

            return Results.Ok(job);
        });

        movies.MapPut("/{id}/metadata/override", async (
            string id,
            [FromBody] MetadataOverrideRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            var updated = await repository.UpdateMetadataAsync(
                new MediaMetadataUpdate(
                    movie.Id,
                    movie.MetadataProvider ?? "manual",
                    movie.MetadataProviderId ?? movie.ImdbId ?? movie.Id,
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
                    // The provider facts are not part of an override and are
                    // left alone rather than blanked: the write COALESCEs them,
                    // so an override of the overview does not throw away the
                    // certification a refresh found.
                    RuntimeMinutes: null,
                    Popularity: null,
                    VoteCount: null),
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                "metadata.movie.overridden",
                $"{movie.Title} metadata values were manually overridden.",
                JsonSerializer.Serialize(request),
                null,
                "movie",
                movie.Id,
                cancellationToken);

            if (updated is null) return Results.NotFound();
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", updated.Id, cancellationToken);
            return Results.Ok(updated);
        });

        movies.MapPost("/metadata/jobs", async (
            HttpContext httpContext,
            [FromBody] MetadataRefreshJobsRequest request,
            IMovieCatalogRepository repository,
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
                        JobType: "movies.metadata.refresh",
                        Source: "metadata",
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            candidate.Id,
                            candidate.Title,
                            ReleaseYear = candidate.Year,
                            request.ForceAll
                        }),
                        RelatedEntityType: "movie",
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

        movies.MapPost("/", async (
            HttpContext httpContext,
            [FromBody] CreateMovieRequest request,
            IMovieCatalogRepository repository,
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

            var movie = await repository.AddAsync(request, cancellationToken);
            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            foreach (var library in libraries.Where(item => item.MediaType == "movies"))
            {
                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: library.MediaType,
                    HasFile: false,
                    CurrentQuality: null,
                    CutoffQuality: library.CutoffQuality,
                    UpgradeUntilCutoff: library.UpgradeUntilCutoff,
                    UpgradeUnknownItems: library.UpgradeUnknownItems));

                await repository.EnsureWantedStateAsync(
                    movie.Id,
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
                    JobType: "movies.catalog.refresh",
                    Source: "movies",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        movie.Id,
                        movie.Title,
                        movie.ImdbId
                    }),
                    RelatedEntityType: "movie",
                    RelatedEntityId: movie.Id),
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movie.Id, cancellationToken);
            return Results.Created($"/api/movies/{movie.Id}", movie);
        });

        movies.MapGet("/duplicates", async (
            IMovieCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var duplicates = await repository.FindCrossLibraryDuplicatesAsync(cancellationToken);
            return Results.Ok(duplicates);
        });

        movies.MapPost("/bulk/quality-profile", async (
            HttpContext httpContext,
            [FromBody] BulkQualityProfileRequest request,
            IMovieCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Choose at least one movie before updating quality."]
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
            foreach (var id in request.MovieIds)
            {
                if (await repository.UpdateQualityProfileAsync(id, request.QualityProfileId.Trim(), cancellationToken))
                {
                    updated++;
                    await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
                }
            }

            return Results.Ok(new { updated, qualityProfileId = request.QualityProfileId.Trim() });
        });

        movies.MapPost("/bulk/search", async (
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
                MediaKind.Movie,
                request.MovieIds,
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

        movies.MapPost("/bulk/reassign-library", async (
            HttpContext httpContext,
            [FromBody] BulkReassignLibraryRequest request,
            IMovieCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.MovieIds is null || request.MovieIds.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["At least one movie ID is required."]
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
                request.MovieIds, request.FromLibraryId, request.ToLibraryId, cancellationToken);

            if (count > 0)
                foreach (var movieId in request.MovieIds.Distinct(StringComparer.OrdinalIgnoreCase))
                    await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movieId, cancellationToken);

            return Results.Ok(new { reassigned = count });
        });

        movies.MapPost("/bulk/tags", async (
            HttpContext httpContext,
            [FromBody] BulkTagsRequest request,
            IMovieCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Choose at least one movie before applying tags."]
                });
            }

            var normalizedTags = NormalizeTags(request.Tags);
            var updated = 0;
            foreach (var id in request.MovieIds)
            {
                var movie = await repository.GetByIdAsync(id, cancellationToken);
                if (movie is null)
                {
                    continue;
                }

                var metadata = ParseMetadataDictionary(movie.MetadataJson);
                metadata["tags"] = normalizedTags;
                await repository.UpdateMetadataAsync(
                    new MediaMetadataUpdate(
                        movie.Id,
                        movie.MetadataProvider,
                        movie.MetadataProviderId,
                        movie.OriginalTitle,
                        movie.Overview,
                        movie.PosterUrl,
                        movie.BackdropUrl,
                        movie.Rating,
                        movie.Genres,
                        movie.ExternalUrl,
                        movie.ImdbId,
                        JsonSerializer.Serialize(metadata),
                        RuntimeMinutes: null,
                        Popularity: null,
                        VoteCount: null),
                    cancellationToken);
                updated++;
                await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movie.Id, cancellationToken);
            }

            return Results.Ok(new { updated, tags = normalizedTags });
        });

        movies.MapPost("/bulk/rename-preview", async (
            HttpContext httpContext,
            [FromBody] BulkRenamePreviewRequest request,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Choose at least one movie to preview rename output."]
                });
            }

            var settings = await platformSettingsRepository.GetAsync(cancellationToken);
            var template = string.IsNullOrWhiteSpace(request.Template)
                ? settings.MovieFolderFormat
                : request.Template.Trim();

            var previews = new List<object>();
            foreach (var id in request.MovieIds)
            {
                var movie = await repository.GetByIdAsync(id, cancellationToken);
                if (movie is null)
                {
                    continue;
                }

                previews.Add(new
                {
                    movieId = movie.Id,
                    movie.Title,
                    movie.ReleaseYear,
                    template,
                    proposedName = ApplyMovieRenameTemplate(template, movie.Title, movie.ReleaseYear)
                });
            }

            return Results.Ok(new { count = previews.Count, previews });
        });

        return endpoints;
    }

    /// <summary>
    /// Pull the provider's release dates in after a match is applied, so Deluno
    /// knows when the movie is actually obtainable rather than only what year it
    /// came out. A provider that cannot answer leaves the stored dates alone.
    /// </summary>
    private static async Task SyncReleaseDatesAsync(
        IMovieCatalogRepository repository,
        IMetadataProvider metadataProvider,
        string movieId,
        string? providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        MetadataReleaseDates dates;
        try
        {
            dates = await metadataProvider.GetMovieReleaseDatesAsync(providerId, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (dates.HasAny)
        {
            await repository.UpdateReleaseDatesAsync(movieId, dates.InCinemas, dates.Digital, dates.Physical, cancellationToken);
        }
    }

    /// <summary>Blank means "no override", so it is stored as null rather than kept.</summary>
    private static string? NormalizeOverride(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string[]> Validate(CreateMovieRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            errors["title"] = ["A movie title is required."];
        }

        if (request.ReleaseYear is < 1888 or > 2100)
        {
            errors["releaseYear"] = ["Release year must be between 1888 and 2100."];
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

    private static string ApplyMovieRenameTemplate(string template, string title, int? releaseYear)
    {
        var resolved = (template ?? "{Movie Title} ({Release Year})")
            .Replace("{Movie Title}", title ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", title ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Release Year}", releaseYear?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", releaseYear?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

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
            errors["title"] = ["Give this import issue a movie title."];
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            errors["summary"] = ["Add a short summary so Deluno can explain what went wrong."];
        }

        return errors;
    }

    private static async Task<Dictionary<string, string?>> RemoveMovieAsync(
        MovieListItem movie,
        BulkMovieRequest request,
        IMovieCatalogRepository repository,
        ILibrariesRepository platformSettingsRepository,
        IIntakeRepository intakeRepository,
        IJobQueueRepository jobQueueRepository,
        IActivityFeedRepository activityFeedRepository,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string?>();
        var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
        var cancelledJobs = await jobQueueRepository.CancelPendingForRelatedEntityAsync("movie", movie.Id, cancellationToken);
        metadata["cancelledPendingJobCount"] = cancelledJobs.ToString();

        if (request.DeleteFiles)
        {
            var trackedFiles = new List<TrackedLibraryFile>();
            foreach (var library in libraries)
            {
                await foreach (var file in repository.StreamTrackedFilesAsync(library.Id, cancellationToken))
                {
                    if (string.Equals(file.MovieId, movie.Id, StringComparison.OrdinalIgnoreCase))
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
            var origins = await intakeRepository.ListIntakeTitleOriginsAsync("movies", movie.Id, cancellationToken);
            var exclusionsAdded = 0;
            var exclusionWarnings = new List<string>();
            foreach (var origin in origins.GroupBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                try
                {
                    var exclusion = await intakeRepository.CreateIntakeListExclusionAsync(
                        origin.SourceId,
                        new CreateIntakeListExclusionRequest(movie.Title, movie.ReleaseYear, movie.ImdbId, null),
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

        if (!await repository.DeleteAsync(movie.Id, cancellationToken))
        {
            throw new InvalidOperationException("Movie was not removed from Deluno.");
        }

        await activityFeedRepository.RecordActivityAsync(
            "movie.removed",
            $"{movie.Title} was removed from Deluno.{(request.DeleteFiles ? " Imported library files were also selected for deletion." : string.Empty)}",
            JsonSerializer.Serialize(new { request.DeleteFiles, request.AddImportListExclusion, metadata }),
            null,
            "movie",
            movie.Id,
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
    /// arguments, and it never passed <c>Status</c> or <c>Studio</c> — the two
    /// fields V0020 added a column for. The write succeeded, the endpoint
    /// returned 200, and the filter over the column returned nothing. The
    /// mapping now lives in <c>CatalogueMetadata</c>, once.</para>
    /// </summary>
    private static Task<MovieListItem?> ApplyMetadataAsync(
        IMovieCatalogRepository repository,
        string movieId,
        MetadataSearchResult result,
        CancellationToken cancellationToken)
        => repository.UpdateMetadataAsync(movieId, result, cancellationToken);

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


    private sealed record UpdateReplacementProtectionRequest(
        bool PreventLowerQualityReplacements);

    private sealed record EvaluateCandidateRequest(
        string CandidateQuality);

    private sealed record BulkReassignLibraryRequest(
        IReadOnlyList<string>? MovieIds,
        string? FromLibraryId,
        string? ToLibraryId);

    private sealed record BulkQualityProfileRequest(
        IReadOnlyList<string>? MovieIds,
        string? QualityProfileId);

    private sealed record BulkTagsRequest(
        IReadOnlyList<string>? MovieIds,
        string? Tags);

    private sealed record BulkRenamePreviewRequest(
        IReadOnlyList<string>? MovieIds,
        string? Template);

    private sealed record BulkSearchRequest(IReadOnlyList<string>? MovieIds);

}
