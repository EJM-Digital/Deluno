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
using Deluno.Quality.Guides;
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
        var collections = endpoints.MapGroup("/api/movie-collections");

        collections.MapGet("/", async (
            IMovieCollectionsRepository repository,
            CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListAsync(cancellationToken)));

        collections.MapGet("/{id}", async (
            string id,
            IMovieCollectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var collection = await repository.GetAsync(id, cancellationToken);
            return collection is null ? Results.NotFound() : Results.Ok(collection);
        });

        collections.MapGet("/{id}/members", async (
            string id,
            IMovieCollectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (await repository.GetAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await repository.ListMembersAsync(id, cancellationToken));
        });

        collections.MapPost("/", async (
            HttpContext httpContext,
            [FromBody] CreateMovieCollectionRequest request,
            IMovieCollectionService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(request.ProviderId) || !int.TryParse(request.ProviderId, out _))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["providerId"] = ["Enter a numeric TMDb collection id."]
                });
            }

            if (string.IsNullOrWhiteSpace(request.LibraryId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Choose a movie library for the collection."]
                });
            }

            try
            {
                var collection = await service.CreateOrUpdateAsync(request, cancellationToken);
                return collection is null
                    ? Results.NotFound(new { message = "TMDb did not return that collection." })
                    : Results.Created($"/api/movie-collections/{collection.Id}", collection);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["collection"] = [ex.Message]
                });
            }
        });

        collections.MapPut("/{id}", async (
            string id,
            HttpContext httpContext,
            [FromBody] UpdateMovieCollectionRequest request,
            IMovieCollectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.MinimumAvailability is not null
                && !MovieAvailability.All.Contains(request.MinimumAvailability.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minimumAvailability"] = [$"Choose one of: {string.Join(", ", MovieAvailability.All)}."]
                });
            }

            var updated = await repository.UpdateAsync(id, request, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        collections.MapPost("/{id}/refresh", async (
            string id,
            HttpContext httpContext,
            IMovieCollectionService service,
            IMovieCollectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (await repository.GetAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await service.SyncAsync(id, cancellationToken));
        });

        collections.MapPut("/{id}/members/{providerId}/exclusion", async (
            string id,
            string providerId,
            HttpContext httpContext,
            [FromBody] MovieCollectionExclusionRequest request,
            IMovieCollectionsRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var updated = await repository.SetMemberExcludedAsync(id, providerId, request.Excluded, cancellationToken);
            return updated ? Results.Ok(new { excluded = request.Excluded }) : Results.NotFound();
        });

        // The list surface for a library that keeps growing. Search, filter,
        // sort and the counts all happen in SQL; the response says how many rows
        // match and hands back a continuation token, so a caller can always tell
        // a complete answer from a partial one.
        //
        // What the genre filter can offer. Its own endpoint rather than a facet
        // on the page, because it is asked for once when somebody opens the
        // filter panel and never again while they page through results.
        // What this shelf can be asked, ordered by, and draw â€” declared once,
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

        movies.MapGet("/{id}/tags", async (
            string id,
            [FromServices] IMediaTagStore tagStore,
            CancellationToken cancellationToken) =>
            Results.Ok(await tagStore.ListAsync(MediaKind.Movie, id, cancellationToken)));

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
            // One `f` per condition â€” `f=quality:in:WEB 2160p|Remux 2160p` â€” read
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

        movies.MapGet("/{id}/preference-evaluation", async (
            string id,
            string? libraryId,
            string? fileIdentity,
            IMediaStateRepository mediaStateRepository,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
                MediaKind.Movie,
                id,
                libraryId,
                fileIdentity,
                cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
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

        // Why a title will not download, and what could be done about it.
        //
        // The question a person asks of a title that never arrives is not "what
        // is its wanted status" â€” it is "why is nothing happening". Every media
        // manager accumulates records that quietly answer that and never say
        // so: a client that already holds the release, a processor still
        // holding the file, an exclusion added when it was removed. This says
        // them out loud, and marks the ones a person is allowed to override.
        movies.MapGet("/{id}/acquisition-blockers", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            AcquisitionBlockerGatherer gatherer,
            IUnifiedExclusionRepository exclusions,
            IDownloadDispatchesRepository dispatches,
            IProcessorRepository processors,
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

            var held = await AcquisitionBlockerSources.FindAsync(
                dispatches, processors, "movies", id, cancellationToken);
            var excluded = await AcquisitionBlockerSources.IsExcludedAsync(
                exclusions, "movies", item.Title, item.ImdbId, cancellationToken);
            // What Deluno fetched before and no longer holds — read from its
            // own dispatch record, so it answers with the client switched off.
            var fetchedBefore = await AcquisitionBlockerSources.FindPreviousFetchAsync(
                dispatches, "movies", id, cancellationToken);

            return Results.Ok(await gatherer.GatherAsync(
                MediaKind.Movie,
                id,
                item.Title,
                held.DownloadClientName,
                held.ProcessorName,
                excluded,
                cancellationToken,
                fetchedBefore?.ClientName,
                fetchedBefore?.WhenUtc));
        });

        // Clear what is standing in the way, deliberately and on the record.
        //
        // Destructive across systems Deluno does not own â€” it removes a
        // download and its files, and restarts a processor hand-off â€” so it is
        // a POST that reports every step, rather than something that happens
        // quietly on the way to a search.
        movies.MapPost("/{id}/force-redownload", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            AcquisitionOverrideService overrides,
            IUnifiedExclusionRepository exclusions,
            IDownloadDispatchesRepository dispatches,
            IProcessorRepository processors,
            IActivityFeedRepository activityFeed,
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IJobQueueRepository jobQueueRepository,
            IAcquisitionDecisionPipeline acquisitionPipeline,
            IDownloadClientGrabService downloadClientGrabService,
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

            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            var held = await AcquisitionBlockerSources.FindAsync(
                dispatches, processors, "movies", id, cancellationToken);
            var exclusionIds = await AcquisitionBlockerSources.ExclusionIdsAsync(
                exclusions, "movies", item.Title, item.ImdbId, cancellationToken);

            // Nothing in flight is the case this feature exists for: the
            // download finished, the file has gone, and the client is still
            // refusing the release on the strength of remembering it. Fall
            // back to that completed dispatch so the force has something to
            // ask the client to forget.
            var fetchedBefore = held.DownloadClientId is { Length: > 0 }
                ? null
                : await AcquisitionBlockerSources.FindPreviousFetchAsync(
                    dispatches, "movies", id, cancellationToken);

            var result = await overrides.ForceAsync(
                new AcquisitionOverrideRequest(
                    id,
                    item.Title,
                    held.HandoffId,
                    held.DownloadClientId ?? fetchedBefore?.ClientId,
                    held.DownloadClientName ?? fetchedBefore?.ClientName,
                    held.QueueItemId ?? fetchedBefore?.QueueItemId,
                    exclusionIds),
                cancellationToken);

            // And then actually look for it. Clearing the obstacles and stopping
            // there would leave the person exactly where they were, one button
            // press poorer â€” "force a re-download" has to end in a download
            // being looked for, or the word force is a lie.
            //
            // The search runs whether or not anything was cleared: someone who
            // presses this has decided they want the title now, and "there was
            // nothing in the way" is not a reason to refuse to go and find it.
            var searchStarted = false;
            var outcome = string.Empty;
            try
            {
                var search = await RunSearchAsync(
                    id,
                    null,
                    repository,
                    mediaStateRepository,
                    platformSettingsRepository,
                    qualityRepository,
                    jobQueueRepository,
                    acquisitionPipeline,
                    downloadClientGrabService,
                    activityFeed,
                    timeProvider,
                    tagStore,
                    releasePreferencePlanRepository,
                    cancellationToken);

                searchStarted = !search.NotFound;
                outcome = search.Summary;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The clearing already happened and is not undone by this. Say
                // so rather than returning a 500 that hides what did work.
                outcome = "The search could not be started, so try it by hand.";
            }

            var answer = result with
            {
                SearchStarted = searchStarted,
                Summary = outcome.Length > 0 ? $"{result.Summary} {outcome}" : result.Summary
            };

            await activityFeed.RecordActivityAsync(
                "acquisition.override",
                $"Someone forced a re-download of {item.Title}. {answer.Summary}",
                JsonSerializer.Serialize(new { answer.Cleared, answer.CouldNotClear, answer.SearchStarted }),
                null,
                "movies",
                id,
                cancellationToken);

            return Results.Ok(answer);
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
            IMediaTagStore tagStore,
            IReleasePreferencePlanRepository releasePreferencePlanRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await RunSearchAsync(
                id,
                mode,
                repository,
                mediaStateRepository,
                platformSettingsRepository,
                qualityRepository,
                jobQueueRepository,
                acquisitionPipeline,
                downloadClientGrabService,
                activityFeedRepository,
                timeProvider,
                tagStore,
                releasePreferencePlanRepository,
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
                    MatchedCustomFormats = candidate.PreferenceEvaluation is null ? candidate.MatchedCustomFormats : null,
                    candidate.PreferenceEvaluation,
                    candidate.PreferenceComparison
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
            IRecycleBinService recycleBinService,
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
                                recycleBinService,
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

        movies.MapGet("/{id}/workflow-status", async (
            string id,
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository platformSettingsRepository,
            IQualityRepository qualityRepository,
            IMovieWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                    MediaKind.Movie,
                    [id],
                    cancellationToken))
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
                .FirstOrDefault();
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
                movieId = wantedItem.Id,
                title = wantedItem.Title,
                releaseYear = wantedItem.Year,
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
            IMediaStateRepository mediaStateRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                    MediaKind.Movie,
                    [id],
                    cancellationToken))
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
                .FirstOrDefault();
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
            IMediaStateRepository mediaStateRepository,
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

            var wantedItem = (await mediaStateRepository.ListWantedByIdsAsync(
                    MediaKind.Movie,
                    [id],
                    cancellationToken))
                .OrderByDescending(item => item.UpdatedUtc)
                .ThenBy(item => item.LibraryId, StringComparer.Ordinal)
                .FirstOrDefault();
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

            MetadataSearchResult? match;
            if (!string.IsNullOrWhiteSpace(movie.MetadataProviderId))
            {
                var lookup = await metadataProvider.ResolveProviderRecordAsync(
                    new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, movie.MetadataProviderId),
                    cancellationToken);
                if (lookup.Status == MetadataProviderRecordStatus.Missing)
                {
                    var issue = MissingProviderIssue("movie", lookup);
                    var isNewEvidence = await repository.RecordMetadataProviderIssueAsync(movie.Id, issue, cancellationToken);
                    if (isNewEvidence)
                    {
                        await activityFeedRepository.RecordActivityAsync(
                            "metadata.movie.provider-record-missing",
                            $"{movie.Title} was kept in Deluno because its linked {lookup.Provider.ToUpperInvariant()} record is no longer available.",
                            JsonSerializer.Serialize(issue),
                            null,
                            "movie",
                            movie.Id,
                            cancellationToken);
                    }

                    return Results.Conflict(new
                    {
                        code = "metadata-provider-record-missing",
                        message = $"{movie.Title} was kept. Its linked {lookup.Provider.ToUpperInvariant()} record is no longer available."
                    });
                }

                if (lookup.Status == MetadataProviderRecordStatus.Unavailable)
                {
                    return Results.Json(
                        MetadataProviderResponses.Unavailable(
                            lookup,
                            $"{movie.Title} was left exactly as it is."),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                match = lookup.Result;
            }
            else
            {
                var matches = await metadataProvider.SearchAsync(
                    new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, null),
                    cancellationToken);
                match = matches.FirstOrDefault();
            }

            if (match is null)
            {
                return Results.NotFound(new { message = "No metadata match was found for this movie." });
            }

            MovieListItem? updated;
            try
            {
                updated = await ApplyMetadataAsync(repository, movie.Id, match, cancellationToken);
            }
            catch (MetadataIdentityConflictException)
            {
                return Results.Conflict(new
                {
                    code = "metadata-link-identity-claimed",
                    message = "Another held movie claimed this metadata identity after the preview. Review the remap again."
                });
            }
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

        movies.MapGet("/{id}/metadata/issue", async (
            string id,
            IMovieCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            if (await repository.GetByIdAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(await repository.GetMetadataProviderIssueAsync(id, cancellationToken));
        });

        movies.MapPost("/{id}/metadata/issue/acknowledge", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null) return Results.NotFound();

            var before = await repository.GetMetadataProviderIssueAsync(id, cancellationToken);
            if (before is null) return Results.NoContent();

            var issue = await repository.AcknowledgeMetadataProviderIssueAsync(id, cancellationToken);
            if (before.AcknowledgedUtc is null)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "metadata.movie.provider-record-missing.acknowledged",
                    $"The metadata notice for {movie.Title} was acknowledged. The movie and its files were kept.",
                    JsonSerializer.Serialize(issue),
                    null,
                    "movie",
                    movie.Id,
                    cancellationToken);
            }

            return Results.Ok(issue);
        });

        movies.MapPost("/{id}/metadata/link/preview", async (
            string id,
            [FromBody] MetadataLinkRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IMetadataProvider metadataProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null) return denied;

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.ProviderId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["providerId"] = ["Choose the metadata match Deluno should preview for this movie."]
                });
            }

            var plan = await BuildMetadataLinkPlanAsync(movie, request.ProviderId, repository, metadataProvider, cancellationToken);
            return plan.Status switch
            {
                MetadataProviderRecordStatus.Missing => Results.NotFound(new { message = "The selected metadata record no longer exists." }),
                MetadataProviderRecordStatus.Unavailable => Results.Json(
                    MetadataProviderResponses.Unavailable(plan.Provider, plan.Failure, "Nothing was changed."),
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Ok(plan.Preview)
            };
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

            if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["confirmationToken"] = ["Preview this metadata remap before applying it."]
                });
            }

            var plan = await BuildMetadataLinkPlanAsync(movie, request.ProviderId, repository, metadataProvider, cancellationToken);
            if (plan.Status == MetadataProviderRecordStatus.Missing)
            {
                return Results.NotFound(new { message = "The selected metadata record no longer exists. Nothing was changed." });
            }
            if (plan.Status == MetadataProviderRecordStatus.Unavailable)
            {
                return Results.Json(
                    MetadataProviderResponses.Unavailable(plan.Provider, plan.Failure, "Nothing was changed."),
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
                    message = "The title or provider record changed after the preview. Review the remap again.",
                    preview = plan.Preview
                });
            }

            var match = plan.Match;

            MovieListItem? updated;
            try
            {
                updated = await ApplyMetadataAsync(repository, movie.Id, match, cancellationToken, replaceIdentity: true);
            }
            catch (MetadataIdentityConflictException)
            {
                return Results.Conflict(new
                {
                    code = "metadata-link-identity-claimed",
                    message = "Another held movie claimed this metadata identity after the preview. Review the remap again."
                });
            }
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
                    // impossible to undo â€” you could only replace it with other text.
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
                // The film's own state, not a fresh install's.
                //
                // AddAsync dedupes: adding a title the catalogue already holds
                // returns the row it already has. Passing false here then went
                // on to overwrite it â€” EnsureWantedStateAsync upserts with
                // `has_file = excluded.has_file` â€” so re-adding a film you
                // already had wiped the record that its file existed, while
                // leaving the file and its path on the entry untouched.
                //
                // That is the hasFile=false-with-a-filePath state seen on the
                // lab, and the reason reconciliation then called that same file
                // an orphan: the tracked-file query selects on has_file = 1, so
                // a file whose flag has been cleared is a file nothing owns.
                // Worse than cosmetic â€” the title goes back on the wanted list
                // and Deluno re-downloads what it is already holding.
                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: library.MediaType,
                    HasFile: movie.HasFile,
                    CurrentQuality: movie.CurrentQuality,
                    CutoffQuality: library.CutoffQuality,
                    UpgradeUntilCutoff: library.UpgradeUntilCutoff,
                    UpgradeUnknownItems: library.UpgradeUnknownItems));

                await repository.EnsureWantedStateAsync(
                    movie.Id,
                    library.Id,
                    decision.WantedStatus,
                    decision.WantedReason,
                    movie.HasFile,
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

        /*
          Removing one film.

          This existed only as POST /bulk with an array of one, which is how the
          UI still reaches it. Twenty-eight other entities have a single-item
          DELETE - including a movie's own import-recovery case - and the cost of
          the exception was not the awkwardness but the status code: a removal
          that failed entirely came back 200 with successCount: 0, so a caller
          checking the response status was told it had worked (#421).

          The options are the same ones the removal dialog already sends, as
          query parameters because a DELETE body is poorly supported by clients.
        */
        movies.MapDelete("/{id}", async (
            string id,
            HttpContext httpContext,
            bool? deleteFiles,
            bool? addImportListExclusion,
            IMovieCatalogRepository repository,
            ILibrariesRepository platformSettingsRepository,
            [FromServices] IIntakeRepository intakeRepository,
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

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            await RemoveMovieAsync(
                movie,
                new BulkMovieRequest(
                    [id],
                    "remove",
                    DeleteFiles: deleteFiles ?? false,
                    AddImportListExclusion: addImportListExclusion ?? false),
                repository,
                platformSettingsRepository,
                intakeRepository,
                jobQueueRepository,
                activityFeedRepository,
                recycleBinService,
                cancellationToken);

            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
            return Results.NoContent();
        });

        /*
          Both kinds of duplicate, because they are different problems.

          This used to return only the cross-library kind - one catalogue row
          appearing in two libraries - which is usually deliberate. The one
          people actually get, two rows for the same film, could never appear
          here: the query grouped by movie id, and two rows have two ids. So the
          single feature named "duplicates" was structurally incapable of finding
          duplicates (#419).
        */
        movies.MapGet("/duplicates", async (
            IMovieCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var sameFilmTwice = await repository.FindDuplicateTitlesAsync(cancellationToken);
            var acrossLibraries = await repository.FindCrossLibraryDuplicatesAsync(cancellationToken);
            return Results.Ok(new MovieDuplicateReport(sameFilmTwice, acrossLibraries));
        });

        /*
          The point a film becomes worth searching for, set across a selection.

          It has always been settable one film at a time and never in bulk,
          which is the wrong way round: minimum availability is the setting you
          change after realising a whole shelf is wrong â€” every film you added
          from a "coming soon" list sitting at Announced and burning searches on
          releases that do not exist yet.
        */
        movies.MapPost("/bulk/minimum-availability", async (
            HttpContext httpContext,
            [FromBody] BulkMinimumAvailabilityRequest request,
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
                    ["movieIds"] = ["Choose at least one movie before changing availability."]
                });
            }

            // Refused rather than normalised. MovieAvailability.Normalize falls
            // back to "released" for anything it does not know, which is the
            // right behaviour when reading a stored value and the wrong one
            // here: a typo would silently set the whole selection to Released.
            var availability = request.MinimumAvailability?.Trim() ?? string.Empty;
            if (!MovieAvailability.All.Contains(availability, StringComparer.OrdinalIgnoreCase))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["minimumAvailability"] = [$"Choose one of: {string.Join(", ", MovieAvailability.All)}."]
                });
            }

            var normalized = MovieAvailability.Normalize(availability);
            var updated = 0;
            foreach (var id in request.MovieIds)
            {
                if (await repository.UpdateMinimumAvailabilityAsync(id, normalized, cancellationToken))
                {
                    updated++;
                    await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
                }
            }

            return Results.Ok(new { updated, minimumAvailability = normalized });
        });

        /*
          Re-read the subtitles Deluno already has on disk, for a selection.

          Not a new job type: a title becomes a scan candidate when it has no
          scan row, so forgetting the probe is the whole instruction and the
          library's existing subtitle pass does the work. A per-title subtitle
          job would have been a second way to do the same thing, racing the
          first (DESIGN-002 rule 3).
        */
        movies.MapPost("/bulk/subtitle-rescan", async (
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

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Choose at least one title before re-reading subtitles."]
                });
            }

            var cleared = await subtitles.ClearScansAsync(MediaKind.Movie, request.MovieIds, cancellationToken);

            // "queued" rather than "done": the pass that re-reads them runs on
            // the library's own schedule, and saying otherwise would promise
            // something this request has not made happen.
            return Results.Ok(new { queued = request.MovieIds.Count, cleared });
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

            if (request.MovieIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["movieIds"] = ["Choose at least one movie before applying tags."]
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
            foreach (var id in request.MovieIds)
            {
                var movie = await repository.GetByIdAsync(id, cancellationToken);
                if (movie is null)
                {
                    continue;
                }

                await tagStore.ReplaceAsync(MediaKind.Movie, movie.Id, assignments, cancellationToken);
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
                    proposedName = NamingTemplateRenderer.RenderSegment(
                        template,
                        movie.Title,
                        movie.ReleaseYear,
                        movie.ImdbId,
                        genre: movie.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
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
    /// <summary>
    /// One search, called from both places that start one.
    ///
    /// <para>The second place is a forced re-download, which would not be
    /// forcing anything if it cleared every obstacle and then left the title
    /// sitting exactly where it was. Extracted rather than copied because the
    /// wiring is twenty-eight lines of service plumbing, and two copies of that
    /// drift the moment one of them gains an argument.</para>
    /// </summary>
    private static Task<MediaSearchResult> RunSearchAsync(
        string id,
        string? mode,
        IMovieCatalogRepository repository,
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
        CancellationToken cancellationToken)
        => MediaSearchHandler.ExecuteAsync(
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
            (movieId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token) =>
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
                    token),
            cancellationToken,
            tagStore,
            releasePreferencePlanRepository);

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
        IRecycleBinService recycleBinService,
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
            var origins = await intakeRepository.ListIntakeTitleOriginsAsync("movies", movie.Id, cancellationToken);
            var exclusionsAdded = 0;
            var exclusionWarnings = new List<string>();
            foreach (var origin in origins.GroupBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
            {
                try
                {
                    var exclusion = await intakeRepository.CreateIntakeListExclusionAsync(
                        origin.SourceId,
                        new CreateIntakeListExclusionRequest(movie.Title, movie.ReleaseYear, movie.ImdbId, null, "Removed from library by user"),
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
    /// arguments, and it never passed <c>Status</c> or <c>Studio</c> â€” the two
    /// fields V0020 added a column for. The write succeeded, the endpoint
    /// returned 200, and the filter over the column returned nothing. The
    /// mapping now lives in <c>CatalogueMetadata</c>, once.</para>
    /// </summary>
    private static Task<MovieListItem?> ApplyMetadataAsync(
        IMovieCatalogRepository repository,
        string movieId,
        MetadataSearchResult result,
        CancellationToken cancellationToken,
        bool replaceIdentity = false)
        => repository.UpdateMetadataAsync(
            CatalogueMetadata.ToUpdate(movieId, result, result.Studio) with
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

    private static async Task<MovieMetadataLinkPlan> BuildMetadataLinkPlanAsync(
        MovieListItem movie,
        string providerId,
        IMovieCatalogRepository repository,
        IMetadataProvider metadataProvider,
        CancellationToken cancellationToken)
    {
        var lookup = await metadataProvider.ResolveProviderRecordAsync(
            new MetadataLookupRequest(movie.Title, "movies", movie.ReleaseYear, providerId.Trim()),
            cancellationToken);
        if (lookup.Status != MetadataProviderRecordStatus.Found || lookup.Result is null)
        {
            return new MovieMetadataLinkPlan(lookup.Status, null, null, lookup.Provider, lookup.Failure);
        }

        var match = lookup.Result;
        var current = new MetadataLinkIdentity(
            movie.MetadataProvider,
            movie.MetadataProviderId,
            movie.Title,
            movie.ReleaseYear,
            movie.ImdbId,
            ReadMetadataText(movie.MetadataJson, "Collection", "collection"));
        var proposed = new MetadataLinkIdentity(
            match.Provider,
            match.ProviderId,
            match.Title,
            match.Year,
            match.ImdbId,
            match.Collection);
        var changes = DescribeMetadataIdentityChanges(current, proposed);
        var conflict = await repository.FindMetadataIdentityConflictAsync(
            movie.Id,
            match.Title,
            match.Year,
            match.ImdbId,
            match.Provider,
            match.ProviderId,
            cancellationToken);
        var consequences = new List<string>
        {
            "Imported files, edition and release facts, monitoring, history, tags, and plan assignments will be kept.",
            "Provider artwork, overview, ratings, genres, IDs, collection, and release dates will be refreshed from the selected record."
        };
        if (!string.Equals(current.Context, proposed.Context, StringComparison.OrdinalIgnoreCase))
        {
            consequences.Add($"Collection will change from {DisplayMetadataValue(current.Context)} to {DisplayMetadataValue(proposed.Context)}.");
        }

        var blockReason = conflict is null
            ? null
            : $"{conflict.Title} already owns the proposed {DescribeConflictReason(conflict.Reason)}. Deluno will not merge or duplicate the two movies.";
        var token = MetadataLinkPreviewTokens.Create(movie.Id, movie.UpdatedUtc, proposed);
        var preview = new MetadataLinkPreview(
            "movies",
            movie.Id,
            current,
            proposed,
            changes,
            consequences,
            conflict,
            null,
            conflict is null,
            blockReason,
            token);
        return new MovieMetadataLinkPlan(lookup.Status, match, preview, lookup.Provider, lookup.Failure);
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
        changes.Add($"{label}: {DisplayMetadataValue(before)} â†’ {DisplayMetadataValue(after)}");
    }

    private static string DisplayMetadataValue(string? value) => string.IsNullOrWhiteSpace(value) ? "not set" : value;

    private static string DescribeConflictReason(string reason) => reason switch
    {
        "provider-id" => "provider record",
        "imdb-id" => "IMDb identity",
        _ => "title and year"
    };

    private static string? ReadMetadataText(string? metadataJson, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // A malformed stored metadata blob cannot make an identity preview destructive.
        }
        return null;
    }

    private sealed record MovieMetadataLinkPlan(
        MetadataProviderRecordStatus Status,
        MetadataSearchResult? Match,
        MetadataLinkPreview? Preview,
        // Carried so a provider that could not answer can say which one it was
        // and why, instead of the caller inventing a sentence for it (#338).
        string Provider = "",
        Deluno.Contracts.IntegrationFailure? Failure = null);

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
                ? "Nothing can be refreshed right now â€” everything stale was tried recently and is waiting out its cooldown."
                : "Nothing needs refreshing.";
        }

        var queued = $"Queued {enqueued:N0} title{(enqueued == 1 ? string.Empty : "s")}";

        return remaining > 0
            ? $"{queued}. Another {remaining:N0} still to go â€” Deluno keeps working through them in the background."
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

    private sealed record BulkMinimumAvailabilityRequest(
        IReadOnlyList<string>? MovieIds,
        string? MinimumAvailability);

    private sealed record BulkSubtitleRescanRequest(IReadOnlyList<string>? MovieIds);

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

    private sealed record MovieCollectionExclusionRequest(bool Excluded);

}
