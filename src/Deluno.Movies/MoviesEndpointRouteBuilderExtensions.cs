using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Platform.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform;
using Deluno.Platform.Quality;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Movies;

public static class MoviesEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoMoviesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var movies = endpoints.MapGroup("/api/movies");

        movies.MapGet("/", async (IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListAsync(cancellationToken);
            return Results.Ok(items);
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

        movies.MapGet("/search-history", async (IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var items = await repository.ListSearchHistoryAsync(cancellationToken);
            return Results.Ok(items);
        });

        movies.MapPost("/import-recovery", async (
            HttpContext httpContext,
            [FromBody] CreateMovieImportRecoveryCaseRequest request,
            IMovieCatalogRepository repository,
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

        movies.MapPost("/import-recovery/{id}/resolve", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

        movies.MapPost("/import-recovery/{id}/dismiss", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

        movies.MapDelete("/import-recovery/{id}", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

        movies.MapGet("/{id}", async (string id, IMovieCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            return movie is null ? Results.NotFound() : Results.Ok(movie);
        });

        movies.MapPut("/monitoring", async (
            HttpContext httpContext,
            [FromBody] UpdateMovieMonitoringRequest request,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
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

            return Results.Ok(new { updated });
        });

        movies.MapPost("/{id}/search", async (
            string id,
            string? mode,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.MovieId == id);
            if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieId"] = ["This movie is not currently linked to a searchable library."]
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Deluno could not find the linked library for this movie."]
                });
            }

            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var customFormats = await ResolveCustomFormatsAsync(platformSettingsRepository, library.QualityProfileId, cancellationToken);

            var decisionPlan = await acquisitionPipeline.PlanAsync(
                new AcquisitionDecisionRequest(
                    movie.Title,
                    movie.ReleaseYear,
                    "movies",
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
                    "movies",
                    "movie",
                    movie.Id,
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
                movie.Id,
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
                    Scope: "movie.search",
                    Status: outcome,
                    Reason: decisionPlan.SearchResult,
                    Inputs: new Dictionary<string, string?>
                    {
                        ["title"] = movie.Title,
                        ["year"] = movie.ReleaseYear?.ToString(),
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
                "movie",
                movie.Id,
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                "movie.search.manual",
                $"{movie.Title} was searched manually from the Deluno workspace.",
                null,
                null,
                "movie",
                movie.Id,
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
                    candidate.PolicyVersion,
                    candidate.MatchedCustomFormats
                }).ToArray()
            });
        });

        movies.MapPost("/bulk", async (
            HttpContext httpContext,
            BulkMovieRequest request,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
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
                            await repository.DeleteAsync(movie.Id, cancellationToken);
                            successCount++;
                            results.Add(new BulkMovieItemResult(movie.Id, movie.Title, true));
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

            return Results.Ok(new BulkMovieResponse(request.MovieIds.Count, successCount, failureCount, operation, results));
        });

        movies.MapPost("/{id}/grab", async (
            string id,
            [FromBody] ReleaseGrabRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            var validation = ValidateReleaseGrab(request);
            if (validation.Count > 0)
            {
                return Results.ValidationProblem(validation);
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var wantedItem = wanted.RecentItems.FirstOrDefault(item => item.MovieId == id);
            if (wantedItem is null || string.IsNullOrWhiteSpace(wantedItem.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieId"] = ["This movie is not currently linked to a searchable library."]
                });
            }

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == wantedItem.LibraryId);
            if (library is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Deluno could not find the linked library for this movie."]
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

            var category = library.MediaType == "tv" ? "tv" : "movies";
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
                    "movies",
                    category,
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
                "movies",
                "movie",
                movie.Id,
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
                movie.Id,
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
                forceOverride ? "movie.release.force-grabbed" : "movie.release.grabbed",
                forceOverride
                    ? $"{movie.Title} release was force grabbed and sent to {downloadClient.DownloadClientName}."
                    : $"{movie.Title} release was manually selected and sent to {downloadClient.DownloadClientName}.",
                JsonSerializer.Serialize(auditPayload),
                null,
                "movie",
                movie.Id,
                cancellationToken);

            await activityFeedRepository.RecordDecisionAsync(
                new DecisionExplanationPayload(
                    Scope: forceOverride ? "movie.grab.force" : "movie.grab.manual",
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
                "movie",
                movie.Id,
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

        movies.MapGet("/{id}/workflow-status", async (
            string id,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
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
                var profiles = await platformSettingsRepository.ListQualityProfilesAsync(cancellationToken);
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
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
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

            return Results.NoContent();
        });

        movies.MapPost("/{id}/evaluate-candidate", async (
            string id,
            [FromBody] EvaluateCandidateRequest request,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
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
                var profiles = await platformSettingsRepository.ListQualityProfilesAsync(cancellationToken);
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
            await activityFeedRepository.RecordActivityAsync(
                "metadata.movie.refreshed",
                $"{movie.Title} metadata was refreshed from {match.Provider.ToUpperInvariant()}.",
                JsonSerializer.Serialize(match),
                null,
                "movie",
                movie.Id,
                cancellationToken);

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        movies.MapPost("/{id}/metadata/link", async (
            string id,
            [FromBody] MetadataLinkRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
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
            await activityFeedRepository.RecordActivityAsync(
                "metadata.movie.linked",
                $"{movie.Title} metadata was linked to {match.Provider.ToUpperInvariant()} item {match.ProviderId}.",
                JsonSerializer.Serialize(match),
                null,
                "movie",
                movie.Id,
                cancellationToken);

            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        movies.MapPost("/{id}/metadata/jobs", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
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

        movies.MapPost("/metadata/jobs", async (
            HttpContext httpContext,
            [FromBody] MetadataRefreshJobsRequest request,
            IMovieCatalogRepository repository,
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
            var moviesToRefresh = (await repository.ListAsync(cancellationToken))
                .Where(item => request.ForceAll || string.IsNullOrWhiteSpace(item.MetadataProviderId) || item.MetadataUpdatedUtc is null)
                .OrderBy(item => item.MetadataUpdatedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .ToArray();
            var jobs = new List<Deluno.Jobs.Contracts.JobQueueItem>();

            foreach (var movie in moviesToRefresh)
            {
                var job = await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "movies.metadata.refresh",
                        Source: "metadata",
                        PayloadJson: JsonSerializer.Serialize(new { movie.Id, movie.Title, movie.ReleaseYear, request.ForceAll }),
                        RelatedEntityType: "movie",
                        RelatedEntityId: movie.Id),
                    cancellationToken);
                jobs.Add(job);
            }

            return Results.Ok(new MetadataRefreshJobsResponse(jobs.Count, jobs));
        });

        movies.MapPost("/", async (
            HttpContext httpContext,
            [FromBody] CreateMovieRequest request,
            IMovieCatalogRepository repository,
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
            return Results.Created($"/api/movies/{movie.Id}", movie);
        });

        movies.MapGet("/duplicates", async (
            IMovieCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var duplicates = await repository.FindCrossLibraryDuplicatesAsync(cancellationToken);
            return Results.Ok(duplicates);
        });

        movies.MapPost("/bulk/search", async (
            HttpContext httpContext,
            [FromBody] BulkSearchRequest request,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobQueueRepository jobQueueRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Choose at least one movie to search for."]
                });
            }

            var wanted = await repository.GetWantedSummaryAsync(cancellationToken);
            var libraryIds = wanted.RecentItems
                .Where(item => request.MovieIds.Contains(item.MovieId, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.LibraryId))
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

        movies.MapDelete("/bulk", async (
            HttpContext httpContext,
            [FromBody] BulkDeleteMoviesRequest request,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Choose at least one movie to remove."]
                });
            }

            var removed = await repository.UpdateMonitoredAsync(request.MovieIds, false, cancellationToken);
            return Results.Ok(new { unmonitored = removed });
        });

        movies.MapPost("/bulk/reassign-library", async (
            HttpContext httpContext,
            [FromBody] BulkReassignLibraryRequest request,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, platformSettingsRepository, cancellationToken);
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

            return Results.Ok(new { reassigned = count });
        });

        return endpoints;
    }

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

    private static Task<MovieListItem?> ApplyMetadataAsync(
        IMovieCatalogRepository repository,
        string movieId,
        MetadataSearchResult result,
        CancellationToken cancellationToken)
    {
        return repository.UpdateMetadataAsync(
            movieId,
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

    private sealed record UpdateReplacementProtectionRequest(
        bool PreventLowerQualityReplacements);

    private sealed record EvaluateCandidateRequest(
        string CandidateQuality);

    private sealed record BulkReassignLibraryRequest(
        IReadOnlyList<string>? MovieIds,
        string? FromLibraryId,
        string? ToLibraryId);

    private sealed record BulkSearchRequest(IReadOnlyList<string>? MovieIds);

    private sealed record BulkDeleteMoviesRequest(IReadOnlyList<string>? MovieIds);
}
