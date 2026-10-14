using Deluno.Contracts;
using Microsoft.Extensions.DependencyInjection;
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
using Deluno.Integrations.Subtitles;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Movies.Services;
using Deluno.Notifications;
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
            IMovieCollectionService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var updated = await service.UpdateManyAsync([id], request, cancellationToken);
                return updated.Count == 0 ? Results.NotFound() : Results.Ok(updated[0]);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["collection"] = [ex.Message] });
            }
        });

        // #998: Radarr's collection editor changes several collections at once
        // (monitor, add missing, quality profile, root folder, minimum
        // availability, search on add); this is that call.
        collections.MapPut("/bulk", async (
            HttpContext httpContext,
            [FromBody] BulkUpdateMovieCollectionsRequest request,
            IMovieCollectionService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var updated = await service.UpdateManyAsync(
                    request.CollectionIds ?? [],
                    new UpdateMovieCollectionRequest(
                        request.Monitored,
                        request.MonitorMovies,
                        request.QualityProfileId,
                        request.MinimumAvailability,
                        request.SearchOnAdd,
                        null,
                        request.LibraryId),
                    cancellationToken);
                return Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["collections"] = [ex.Message] });
            }
        });

        // #1056: stop following collections. The movies they added are not touched.
        collections.MapDelete("/{id}", async (
            string id,
            HttpContext httpContext,
            IMovieCollectionService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var removed = await service.RemoveManyAsync([id], cancellationToken);
            return removed.Count == 0 ? Results.NotFound() : Results.Ok(new { removed = removed.Count });
        });

        collections.MapPost("/remove", async (
            HttpContext httpContext,
            [FromBody] RemoveMovieCollectionsRequest request,
            IMovieCollectionService service,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            try
            {
                var removed = await service.RemoveManyAsync(request.CollectionIds ?? [], cancellationToken);
                return Results.Ok(new { removed = removed.Count });
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["collections"] = [ex.Message] });
            }
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

        // Posters for the titles on the Dashboard's Live board (#1096): only the ids that still exist are answered, so a deleted title is simply absent (no failing request per id).
        movies.MapGet("/posters", async (
            string? ids,
            IMovieCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var posters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(48))
            {
                var item = await repository.GetByIdAsync(id, cancellationToken);
                if (item is not null) posters[id] = item.PosterUrl;
            }

            return Results.Ok(posters);
        });

        // The name, year and poster of the titles on the Dashboard's Live board (#1096), one request for all of them. A title that no longer exists is simply absent.
        movies.MapGet("/titles", async (
            string? ids,
            IMovieCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var titles = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in (ids ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(48))
            {
                var item = await repository.GetByIdAsync(id, cancellationToken);
                if (item is not null) titles[id] = new { title = item.Title, year = item.ReleaseYear, posterUrl = item.PosterUrl };
            }

            return Results.Ok(titles);
        });

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
            // false leaves the quick-filter counts out and returns only totalCount, for a
            // caller that wants the number and not the chips.
            bool? facets,
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
                    Filters: filters,
                    IncludeFacets: facets != false),
                cancellationToken);

            return Results.Ok(page);
        });

        movies.MapGet("/import-recovery", async (
            IMovieCatalogRepository repository,
            // The counts always cover every open case. The list is the newest 50 without
            // their details unless asked: a screen that lists them all passes limit=500.
            int? limit,
            bool? includeDetails,
            CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetImportRecoverySummaryAsync(cancellationToken, limit ?? 50, includeDetails == true);
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
                includeUnmonitored: true,
                tagNames: null,
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

        movies.MapPost("/import-recovery/{id}/dismiss", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IDispatchProblemLog problemLog,
            IDownloadDispatchesRepository dispatchesRepository,
            Deluno.Platform.Data.IProcessorRepository processors,
            Deluno.Jobs.Data.IActivityFeedRepository activityFeed,
            IHealthCheckRepository healthChecks,
            IRealtimeEventPublisher realtime,
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

            // The alert recorded beside this case settles with it (#537).
            await RecoveryCaseProblems.AcknowledgeAsync(updated.DetailsJson, problemLog, cancellationToken, dispatchesRepository);

            await repository.AddImportRecoveryEventAsync(id, "case_dismissed", "Case dismissed by user.", null, cancellationToken);

            // Its twins go with it (#1228): the hand-off it is about, and its row on System.
            await Deluno.Platform.Data.ProblemTwins.AfterIssueDismissedAsync(id, updated.DetailsJson, processors, activityFeed, healthChecks, realtime, cancellationToken);
            return Results.Ok(updated);
        });

        // Try again on a recovery case (#558): the case kind's own action, or
        // why there is none yet. The button posted here for as long as it had
        // been on screen, and until now nothing answered.
        movies.MapPost("/import-recovery/{id}/retry", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IDispatchProblemLog problemLog,
            IDownloadDispatchesRepository dispatchesRepository,
            IJobQueueRepository jobQueueRepository,
            IJobScheduler jobScheduler,
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository librariesRepository,
            IProcessorRepository processorRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var recoveryCase = await repository.GetImportRecoveryCaseAsync(id, cancellationToken);
            if (recoveryCase is null || !string.Equals(recoveryCase.Status, "open", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var outcome = await RecoveryCaseRetry.RetryAsync(
                recoveryCase.FailureKind,
                recoveryCase.DetailsJson,
                dispatchesRepository,
                jobQueueRepository,
                jobScheduler,
                mediaStateRepository,
                librariesRepository,
                processorRepository,
                timeProvider,
                cancellationToken);
            if (!outcome.Retried)
            {
                return Results.Conflict(new { message = outcome.Message });
            }

            await repository.ResolveImportRecoveryCaseAsync(id, "resolved", cancellationToken);
            // Its alerts settle with it (#537). The download itself is left as the
            // retry left it: an import being run again must still have its record.
            await RecoveryCaseProblems.AcknowledgeAsync(recoveryCase.DetailsJson, problemLog, cancellationToken);
            await repository.AddImportRecoveryEventAsync(id, "case_retried", outcome.Message, null, cancellationToken);
            return Results.Ok(new { message = outcome.Message });
        });

        // Stop wanting it (#927): offered beside Search for another on a
        // vanished download's own case - ends the download and unmonitors
        // the title, rather than searching for a different copy of it.
        movies.MapPost("/import-recovery/{id}/stop-wanting", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IDispatchProblemLog problemLog,
            IDownloadDispatchesRepository dispatchesRepository,
            IDispatchTitleUnmonitorService titleUnmonitorService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var recoveryCase = await repository.GetImportRecoveryCaseAsync(id, cancellationToken);
            if (recoveryCase is null || !string.Equals(recoveryCase.Status, "open", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var outcome = await RecoveryCaseRetry.StopWantingAsync(
                recoveryCase.DetailsJson,
                dispatchesRepository,
                titleUnmonitorService,
                cancellationToken);
            if (!outcome.Retried)
            {
                return Results.Conflict(new { message = outcome.Message });
            }

            await repository.ResolveImportRecoveryCaseAsync(id, "resolved", cancellationToken);
            await RecoveryCaseProblems.AcknowledgeAsync(recoveryCase.DetailsJson, problemLog, cancellationToken, dispatchesRepository);
            await repository.AddImportRecoveryEventAsync(id, "case_stopped_wanting", outcome.Message, null, cancellationToken);
            return Results.Ok(new { message = outcome.Message });
        });

        // #919: a processed file Checkpoint B held back offers three real
        // actions, not just words in its recommendation -- this is the first
        // of them, alongside /retry (run the processor again) and
        // /search-again below.
        movies.MapPost("/import-recovery/{id}/import-original", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IDispatchProblemLog problemLog,
            IDownloadDispatchesRepository dispatchesRepository,
            IProcessorRepository processorRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var recoveryCase = await repository.GetImportRecoveryCaseAsync(id, cancellationToken);
            if (recoveryCase is null || !string.Equals(recoveryCase.Status, "open", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var outcome = await RecoveryCaseRetry.ImportOriginalAsync(
                recoveryCase.FailureKind, recoveryCase.DetailsJson, dispatchesRepository, processorRepository, cancellationToken);
            if (!outcome.Retried)
            {
                return Results.Conflict(new { message = outcome.Message });
            }

            await repository.ResolveImportRecoveryCaseAsync(id, "resolved", cancellationToken);
            await RecoveryCaseProblems.AcknowledgeAsync(recoveryCase.DetailsJson, problemLog, cancellationToken, dispatchesRepository);
            await repository.AddImportRecoveryEventAsync(id, "case_import_original", outcome.Message, null, cancellationToken);
            return Results.Ok(new { message = outcome.Message });
        });

        // #919's third action: a different release entirely, without
        // blocklisting this one -- Checkpoint A already showed it is good.
        movies.MapPost("/import-recovery/{id}/search-again", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IDispatchProblemLog problemLog,
            IDownloadDispatchesRepository dispatchesRepository,
            IJobScheduler jobScheduler,
            IMediaStateRepository mediaStateRepository,
            ILibrariesRepository librariesRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var recoveryCase = await repository.GetImportRecoveryCaseAsync(id, cancellationToken);
            if (recoveryCase is null || !string.Equals(recoveryCase.Status, "open", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var outcome = await RecoveryCaseRetry.SearchAgainWithoutBlocklistingAsync(
                recoveryCase.FailureKind,
                recoveryCase.DetailsJson,
                dispatchesRepository,
                jobScheduler,
                mediaStateRepository,
                librariesRepository,
                timeProvider,
                cancellationToken);
            if (!outcome.Retried)
            {
                return Results.Conflict(new { message = outcome.Message });
            }

            await repository.ResolveImportRecoveryCaseAsync(id, "resolved", cancellationToken);
            await RecoveryCaseProblems.AcknowledgeAsync(recoveryCase.DetailsJson, problemLog, cancellationToken, dispatchesRepository);
            await repository.AddImportRecoveryEventAsync(id, "case_search_again", outcome.Message, null, cancellationToken);
            return Results.Ok(new { message = outcome.Message });
        });

        movies.MapDelete("/import-recovery/{id}", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IDispatchProblemLog problemLog,
            IDownloadDispatchesRepository dispatchesRepository,
            Deluno.Platform.Data.IProcessorRepository processors,
            Deluno.Jobs.Data.IActivityFeedRepository activityFeed,
            IHealthCheckRepository healthChecks,
            IRealtimeEventPublisher realtime,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // Read the case's details before the row goes, so its alert settles too (#537).
            if (await repository.ResolveImportRecoveryCaseAsync(id, "dismissed", cancellationToken) is { } dismissed)
            {
                await RecoveryCaseProblems.AcknowledgeAsync(dismissed.DetailsJson, problemLog, cancellationToken, dispatchesRepository);
                await Deluno.Platform.Data.ProblemTwins.AfterIssueDismissedAsync(id, dismissed.DetailsJson, processors, activityFeed, healthChecks, realtime, cancellationToken);
            }

            var removed = await repository.DeleteImportRecoveryCaseAsync(id, cancellationToken);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // What the copy on disk is, in words: the release it came from, what it
        // matched, and where it went. The file's name is a preference and may
        // say none of this, so Deluno answers from its own record.
        movies.MapGet("/{id}/copy", async (
            string id,
            IMovieCatalogRepository repository,
            IMediaStateRepository mediaStateRepository,
            IQualityRepository qualityRepository,
            IDownloadDispatchesRepository dispatchesRepository,
            CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            var snapshot = await mediaStateRepository.GetLatestPreferenceEvaluationSnapshotAsync(
                MediaKind.Movie,
                id,
                movie.LibraryId,
                fileIdentity: null,
                cancellationToken,
                movie.FilePath);
            var rules = await qualityRepository.ListCustomFormatsAsync(cancellationToken);
            var matched = (snapshot?.MatchedRuleIds ?? [])
                .Select(ruleId => rules.FirstOrDefault(rule => string.Equals(rule.Id, ruleId, StringComparison.OrdinalIgnoreCase)))
                .Where(rule => rule is not null)
                .Select(rule => new { id = rule!.Id, name = rule.Name, trashId = rule.TrashId })
                .ToArray();
            // The file's own record answers first. A dispatch is consulted only
            // for a copy imported before provenance was kept with the file -
            // and it must ask for archived rows, because a successful import
            // archives its dispatch immediately.
            var acquisition = snapshot?.Acquisition;
            var dispatch = acquisition is not null
                ? null
                : (await dispatchesRepository.QueryDispatchesAsync(
                        new DispatchQueryFilter { MediaType = "movies", EntityId = id, IncludeArchived = true },
                        new DispatchPaginationOptions { PageSize = 5 },
                        cancellationToken))
                    .Items
                    .OrderByDescending(item => item.CreatedUtc)
                    .FirstOrDefault();

            return Results.Ok(new
            {
                movieId = movie.Id,
                filePath = movie.FilePath,
                fileSizeBytes = movie.FileSizeBytes,
                quality = movie.CurrentQuality,
                releaseName = snapshot?.ReleaseNameEvidence ?? dispatch?.ReleaseName,
                releaseGroup = movie.ReleaseGroup,
                indexerName = acquisition?.IndexerName ?? dispatch?.IndexerName,
                downloadClientName = acquisition?.DownloadClientName ?? dispatch?.DownloadClientName,
                grabbedUtc = acquisition?.GrabbedUtc ?? dispatch?.CreatedUtc,
                importedUtc = snapshot?.EvaluatedUtc,
                source = snapshot?.Source,
                formats = matched,
                planId = snapshot?.PlanId,
                planVersion = snapshot?.PlanVersion,
                planHash = snapshot?.PlanHash
            });
        });

        movies.MapGet("/{id}", async (
            string id,
            IMovieCatalogRepository repository,
            IMediaSubtitleOverrideRepository overrides,
            CancellationToken cancellationToken) =>
        {
            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null)
            {
                return Results.NotFound();
            }

            // The per-title quality profile override was write-only before
            // #784 - GetByIdAsync's two branches never projected it, so an
            // edit surface asking "what is this title's profile right now"
            // had nothing to read.
            var qualityProfileId = await repository.GetQualityProfileIdAsync(id, cancellationToken);
            movie = movie with { QualityProfileId = qualityProfileId };

            // #843: a single title's own fetch carries its subtitle-language
            // override, when it has set one. The paged grid does not - same
            // as the file's own codec facts above, which is why this is one
            // extra lookup here rather than a join the grid also pays for.
            var @override = await overrides.GetAsync(MediaKind.Movie, id, cancellationToken);
            if (@override is not null)
            {
                movie = movie with
                {
                    SubtitleLanguagesOverride = @override.Languages,
                    SubtitleModeOverride = @override.Mode
                };
            }

            return Results.Ok(movie);
        });

        /*
          Every subtitle this file has, and which of the three types each one
          is — plain, hearing-impaired or forced (#923). The rank a fetched
          subtitle sits on already decides the bar under a poster; this reads
          the same table for a single title's own page, where "which one did
          you actually get" is the question.
        */
        movies.MapGet("/{id}/subtitles", async (
            string id,
            IMediaSubtitleRepository subtitles,
            CancellationToken cancellationToken) =>
        {
            var rows = await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, cancellationToken);
            return Results.Ok(rows);
        });

        /*
          #840: the same append-only history the Wanted screen's status
          reads from, shown from this one title's own subtitle panel -
          "what has Deluno already tried", not just what it currently holds.
        */
        movies.MapGet("/{id}/subtitles/history", async (
            string id,
            IMediaSubtitleRepository subtitles,
            CancellationToken cancellationToken) =>
        {
            var rows = await subtitles.ListAttemptHistoryAsync(MediaKind.Movie, id, cancellationToken);
            return Results.Ok(rows);
        });

        movies.MapPut("/monitoring", async (
            HttpContext httpContext,
            [FromBody] UpdateMovieMonitoringRequest request,
            IMovieCatalogRepository repository,
            TitleRemovalService titleRemoval,
            IRealtimeEventPublisher realtimeEventPublisher,
            IAutoTaggingService autoTagging,
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

            var movieIds = request.MovieIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            // #900: unmonitoring is "I don't want this any more", not just "stop
            // searching later" - an in-flight download for it is cancelled right
            // now, with its data, rather than left to finish and import anyway.
            if (!request.Monitored)
            {
                foreach (var movieId in movieIds)
                {
                    var movie = await repository.GetByIdAsync(movieId, cancellationToken);
                    if (movie is not null)
                    {
                        await titleRemoval.CancelDownloadForUnmonitoredTitleAsync("movie", movieId, movie.Title, cancellationToken);
                    }
                }
            }

            var updated = await repository.UpdateMonitoredAsync(
                movieIds,
                request.Monitored,
                cancellationToken);

            foreach (var movieId in movieIds)
                await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movieId, cancellationToken);

            // #1024: a rule can depend on whether a title is monitored.
            await autoTagging.ApplyEditedAsync(MediaKind.Movie, movieIds, cancellationToken);

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
        // is its wanted status" — it is "why is nothing happening". Every media
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
            IBlockedReleaseRepository blockedReleases,
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
            var blocked = await blockedReleases.ListForAsync("movies", id, cancellationToken);

            return Results.Ok(await gatherer.GatherAsync(
                MediaKind.Movie,
                id,
                item.Title,
                held.DownloadClientName,
                held.ProcessorName,
                excluded,
                cancellationToken,
                fetchedBefore?.ClientName,
                fetchedBefore?.WhenUtc,
                blocked.Count));
        });

        // Clear what is standing in the way, deliberately and on the record.
        //
        // Destructive across systems Deluno does not own — it removes a
        // download and its files, and restarts a processor hand-off — so it is
        // a POST that reports every step, rather than something that happens
        // quietly on the way to a search.
        movies.MapPost("/{id}/force-redownload", async (
            string id,
            string? libraryId,
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
            IBlockedReleaseRepository blockedReleaseRepository,
            IPlatformSettingsRepository platformSettings,
            IOutboundNotificationService outboundNotificationService,
            INotificationService notificationPreferences,
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

            var result = await overrides.ForceAsync(
                await AcquisitionBlockerSources.OverrideRequestAsync(
                    "movies", id, item.Title, item.ImdbId, dispatches, processors, exclusions, blockedReleaseRepository, cancellationToken),
                cancellationToken);

            // And then actually look for it. Clearing the obstacles and stopping
            // there would leave the person exactly where they were, one button
            // press poorer — "force a re-download" has to end in a download
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
                    libraryId,
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
                    platformSettings,
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

            var forceRedownloadMessage = $"Someone forced a re-download of {item.Title}. {answer.Summary}";
            var forceRedownloadDetails = JsonSerializer.Serialize(new { answer.Cleared, answer.CouldNotClear, answer.SearchStarted });
            await activityFeed.RecordActivityAsync(
                "acquisition.override",
                forceRedownloadMessage,
                forceRedownloadDetails,
                null,
                "movies",
                id,
                cancellationToken);
            // #966: forced import is one of the recovery/cleanup actions
            // that already landed in Activity but never reached the
            // outbound path. Gated by its own preference, off by default.
            var forcedImportPreferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
            if (forcedImportPreferences.ForcedImportEnabled)
            {
                var forcedTitleTags = (await tagStore.ListAsync(MediaKind.Movie, id, cancellationToken)).Select(assignment => assignment.Name).ToArray();
                await outboundNotificationService.DispatchAsync(
                    "acquisition.override",
                    $"Forced re-download: {item.Title}",
                    forceRedownloadMessage,
                    forceRedownloadDetails,
                    cancellationToken,
                    forcedTitleTags);
            }

            return Results.Ok(answer);
        });

        movies.MapPost("/{id}/search", async (
            string id,
            string? libraryId,
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
            IPlatformSettingsRepository platformSettings,
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
                libraryId,
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
                platformSettings,
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
                result.DownloadClientName,
                failures = result.Failures ?? [],
                candidates = MediaSearchCandidateView.ForAll(result.Candidates)
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
            [FromServices] TitleRemovalService titleRemoval,
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
                            var removalMetadata = await titleRemoval.RemoveAsync(
                                MovieRemoval.Target(movie, repository),
                                new TitleRemovalOptions(request.DeleteFiles, request.AddImportListExclusion),
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
                                    JobType: TitleSearchJobs.Movie,
                                    Source: "bulk",
                                    PayloadJson: TitleSearchJobs.Payload(movie.Id, movie.Title, null, TitleSearchJobs.FromBulkEditor),
                                    RelatedEntityType: "movie",
                                    RelatedEntityId: movie.Id,
                                    DedupeKey: TitleSearchJobs.DedupeKey(MediaKind.Movie, movie.Id, null)),
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
            string? libraryId,
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
            IMediaTagStore tagStore,
            Deluno.Integrations.DownloadClients.IDownloadClientTelemetryService downloadClientTelemetry,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // #1175: sending by hand what is already downloading asks first.
            var grabbedMovie = await mediaStateRepository.GetByIdAsync(MediaKind.Movie, id, cancellationToken);
            var activeDownloadCheck = grabbedMovie is null
                ? null
                : ActiveDownloadChecks.ForWholeTitle(id, grabbedMovie.Title, downloadClientTelemetry);

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
                    request.OverrideReason,
                    request.DuplicateAction),
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
                releasePreferencePlanRepository,
                libraryId,
                tagStore,
                activeDownloadCheck);

            if (result.ActiveDownload is not null)
            {
                return ActiveDownloadResponse.Conflict(result.ActiveDownload);
            }

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
            if (library is not null)
            {
                // The film's own profile judges it when it has one (#1157).
                library = await TitleQualityProfile.ApplyAsync(mediaStateRepository, qualityRepository, MediaKind.Movie, id, library, cancellationToken);
            }

            QualityProfileItem? profile = null;
            if (library?.QualityProfileId is not null)
            {
                var profiles = await qualityRepository.ListQualityProfilesAsync(cancellationToken);
                profile = profiles.FirstOrDefault(item => item.Id == library.QualityProfileId);
            }

            var decision = workflowService.EvaluateWantedStatus(
                wantedItem.HasFile,
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
                    VoteCount: null,
                    ReplaceWithEmpty: true),
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
            IMediaStateRepository mediaState,
            IOutboundNotificationService notificationService,
            INotificationService notificationPreferences,
            ITitleTagService titleTagService,
            IAutoTaggingService autoTagging,
            IActivityFeedRepository activityFeed,
            IServiceProvider services,
            IQualityRepository qualityRepository,
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

            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(request.LibraryId) &&
                LibraryMembership.Targets(libraries, "movies", request.LibraryId, []).Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["libraryId"] = ["Choose one of your movie libraries."] });
            }

            // The profile picked in the add dialog (#1145) must be one made for films.
            QualityProfileItem? chosenProfile = null;
            if (!string.IsNullOrWhiteSpace(request.QualityProfileId))
            {
                chosenProfile = (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
                    .FirstOrDefault(profile => string.Equals(profile.Id, request.QualityProfileId.Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(profile.MediaType, "movies", StringComparison.OrdinalIgnoreCase));
                if (chosenProfile is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["qualityProfileId"] = ["Choose one of your movie quality profiles."] });
                }
            }

            var requestStartedUtc = DateTimeOffset.UtcNow;
            var movie = await repository.AddAsync(request, cancellationToken);
            if (chosenProfile is not null)
            {
                await repository.UpdateQualityProfileAsync(movie.Id, chosenProfile.Id, cancellationToken);
            }

            // The film's own profile, if it has one, judges it from the first search (#1157).
            var ownProfile = chosenProfile ?? await new TitleProfileLookup(mediaState, qualityRepository)
                .ForTitleAsync(MediaKind.Movie, movie.Id, cancellationToken);
            // #1038: the tags picked in the add dialog go on the title now, before
            // any routing, search or the "added" notification reads them. A title
            // the catalogue already held keeps its tags and gains these.
            var addedTags = TagNameList.Split(string.Join(',', request.Tags ?? []));
            if (addedTags.Count > 0)
            {
                await titleTagService.GiveAsync(MediaKind.Movie, movie.Id, addedTags, cancellationToken);
            }
            // Only the library the person chose, or the one it is already in (#530).
            var already = (await mediaState.ListWantedByIdsAsync(MediaKind.Movie, [movie.Id], cancellationToken))
                .Select(row => row.LibraryId)
                .ToArray();
            // Carried into the catalogue refresh below rather than enqueued
            // here directly (series' #646 follow-up applies here too). A
            // freshly-added film can still be missing metadata (IMDb id,
            // runtime) the search needs, and that refresh job races this same
            // request's own search job exactly the way the series add did.
            // Handing the request to the refresh handler means it runs once
            // the metadata it needs is on record, not whenever its own worker
            // happens to pick it up.
            var pendingSearches = new List<object>();
            foreach (var library in LibraryMembership.Targets(libraries, "movies", request.LibraryId, already))
            {
                // #1161: a film whose folder is already in this library is
                // matched to that file before anything else decides it is
                // missing. Without this, adding back a title that was removed
                // searched straight away and downloaded the same release again.
                if (!movie.HasFile && services.GetService<IExistingLibraryImportService>() is { } folderImport)
                {
                    var found = await folderImport.ImportTitleFolderAsync(library.Id, movie.Id, movie.Title, movie.ReleaseYear, cancellationToken);
                    if (found.Matched && await repository.GetByIdAsync(movie.Id, cancellationToken) is { } matched)
                    {
                        movie = matched;
                        await activityFeed.RecordActivityAsync(
                            "movie.file-matched",
                            $"{movie.Title} was added and Deluno found its file already in {library.Name}, so it matched that file and did not search or download it again.",
                            JsonSerializer.Serialize(new { libraryId = library.Id, path = found.Path }),
                            null, "movie", movie.Id, cancellationToken);
                    }
                }

                // The film's own state, not a fresh install's.
                //
                // AddAsync dedupes: adding a title the catalogue already holds
                // returns the row it already has. Passing false here then went
                // on to overwrite it — EnsureWantedStateAsync upserts with
                // `has_file = excluded.has_file` — so re-adding a film you
                // already had wiped the record that its file existed, while
                // leaving the file and its path on the entry untouched.
                //
                // That is the hasFile=false-with-a-filePath state seen on the
                // lab, and the reason reconciliation then called that same file
                // an orphan: the tracked-file query selects on has_file = 1, so
                // a file whose flag has been cleared is a file nothing owns.
                // Worse than cosmetic — the title goes back on the wanted list
                // and Deluno re-downloads what it is already holding.
                var judge = TitleQualityProfile.Apply(library, ownProfile);
                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: judge.MediaType,
                    HasFile: movie.HasFile,
                    CurrentQuality: movie.CurrentQuality,
                    CutoffQuality: judge.CutoffQuality,
                    UpgradeUntilCutoff: judge.UpgradeUntilCutoff,
                    UpgradeUnknownItems: judge.UpgradeUnknownItems));

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

                // Whether this add searches at once is one explicit choice on the
                // add dialog (#1145, which settled #645 and #793): "Search for it as
                // soon as it is added". Monitoring alone no longer starts a search -
                // a ticked Monitor box with the search box unticked used to search
                // anyway, before the title's quality profile could be changed. With
                // the choice off, the next scheduled search finds it. Targeted at
                // this one film, the same shape as a recovery retry or an external
                // redownload: real work for one id, not a fresh sweep of the library.
                var searchOnAddRequested =
                    (request.SearchForMissingNow && decision.WantedStatus == WantedStatuses.Missing) ||
                    (request.SearchForCutoffUnmetNow && decision.WantedStatus == WantedStatuses.Upgrade);
                if (searchOnAddRequested)
                {
                    pendingSearches.Add(new
                    {
                        libraryId = library.Id,
                        libraryName = library.Name,
                        mediaType = library.MediaType,
                        retryDelayHours = library.RetryDelayHours,
                        triggeredBy = "movie-added",
                        targetEntityId = movie.Id
                    });
                }
            }

            // #1024: auto-tagging rules run now, before the "added" notification and before
            // anything routes by tag. The libraries are passed because the wanted state
            // that would say where the film is going is only just written.
            var isNewTitle = movie.CreatedUtc >= requestStartedUtc;
            await autoTagging.ApplyAsync(
                MediaKind.Movie,
                movie.Id,
                isNewTitle ? AutoTagTrigger.Added : AutoTagTrigger.Edited,
                isNewTitle ? new AutoTagAddFacts(false, false, null) : null,
                LibraryMembership.Targets(libraries, "movies", request.LibraryId, already).Select(library => library.Id).ToArray(),
                cancellationToken);

            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "movies.catalog.refresh",
                    Source: "movies",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        movie.Id,
                        movie.Title,
                        movie.ImdbId,
                        pendingSearches
                    }),
                    RelatedEntityType: "movie",
                    RelatedEntityId: movie.Id),
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movie.Id, cancellationToken);

            // On Title Added (#856): AddAsync dedupes, so a title already in
            // the catalogue returns its existing row with its original
            // CreatedUtc -- only a row created within this request is a
            // genuinely new title.
            if (movie.CreatedUtc >= requestStartedUtc &&
                (await notificationPreferences.GetPreferencesAsync(cancellationToken)).AddedEnabled)
            {
                await notificationService.DispatchAsync(
                    "title.added",
                    $"Added: {movie.Title}",
                    $"{movie.Title} was added to the library.",
                    JsonSerializer.Serialize(new { movie.Id, movie.Title, mediaType = "movies" }),
                    cancellationToken,
                    titleTags: await titleTagService.ListNamesAsync(MediaKind.Movie, movie.Id, cancellationToken));
            }

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
            [FromServices] TitleRemovalService titleRemoval,
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

            await titleRemoval.RemoveAsync(
                MovieRemoval.Target(movie, repository),
                new TitleRemovalOptions(deleteFiles ?? false, addImportListExclusion ?? false),
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
        /*
          The point a film becomes worth searching for, set across a selection.

          It has always been settable one film at a time and never in bulk,
          which is the wrong way round: minimum availability is the setting you
          change after realising a whole shelf is wrong — every film you added
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
        /*
          #922's one-click "This is out of sync": a person's own judgment,
          fed through #897's mechanism. Deletes the file, blocklists it so it
          is never offered again, and drops the held row so the language is
          missing again - which is what puts it back in front of the
          library's own next subtitle search.
        */
        movies.MapPost("/{id}/subtitles/{language}/reject", async (
            HttpContext httpContext,
            string id,
            string language,
            IMovieCatalogRepository repository,
            IMediaSubtitleRepository subtitles,
            IActivityFeedRepository activity,
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

            var held = (await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, cancellationToken))
                .FirstOrDefault(row => string.Equals(row.Language, language, StringComparison.OrdinalIgnoreCase) && !row.Forced);
            if (held is null || held.FilePath is null)
            {
                return Results.NotFound(new { message = $"There is no {language} subtitle held for this movie." });
            }

            await SubtitleRejection.RejectAsync(
                subtitles,
                activity,
                MediaKind.Movie,
                id,
                movie.Title,
                language,
                forced: false,
                held.HearingImpaired,
                held.FilePath,
                held.SourceKey,
                held.Provider,
                releaseName: null,
                "You marked this subtitle as out of sync.",
                cancellationToken);

            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);

            return Results.Ok(new
            {
                rejected = true,
                message = $"The {language} subtitle was removed and blocklisted; the next library subtitle search will fetch a replacement."
            });
        });

        /*
          #842: a per-file, run-now resync - not the bulk button, which only
          forgets the scan record and answers "queued". This runs the actual
          timing logic synchronously and answers "done" with what it found,
          the same as Bazarr's per-file force-sync.
        */
        movies.MapPost("/{id}/subtitles/{language}/resync", async (
            HttpContext httpContext,
            string id,
            string language,
            IMovieCatalogRepository repository,
            ILibrariesRepository librariesRepository,
            IMediaSubtitleRepository subtitles,
            IActivityFeedRepository activity,
            ISubtitleTimingSync timingSync,
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

            if (string.IsNullOrWhiteSpace(movie.FilePath))
            {
                return Results.NotFound(new { message = "This movie has no file on disk to resync a subtitle against." });
            }

            var held = (await subtitles.ListSubtitlesAsync(MediaKind.Movie, id, cancellationToken))
                .FirstOrDefault(row => string.Equals(row.Language, language, StringComparison.OrdinalIgnoreCase) && !row.Forced);
            if (held is null || held.FilePath is null)
            {
                return Results.NotFound(new { message = $"There is no {language} subtitle held for this movie." });
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == movie.LibraryId);
            var policy = SubtitleTimingPolicyCodec.Normalize(library?.SubtitleTimingPolicy) ?? new SubtitleTimingPolicy();
            // A run-now request is a person asking right now. Honouring a
            // disabled policy here would make the button lie about doing
            // something when it silently did nothing, so this one override
            // is deliberate: enabled for this one call, whatever the library
            // otherwise defaults to.
            policy = policy with { Enabled = true };

            var result = await timingSync.SyncAsync(
                movie.FilePath, held.FilePath, originalLanguage: null, cancellationToken, policy);

            if (result.Outcome == SubtitleTimingOutcome.Mismatched)
            {
                await SubtitleRejection.RejectAsync(
                    subtitles, activity, MediaKind.Movie, id, movie.Title, language,
                    forced: false, held.HearingImpaired, held.FilePath, held.SourceKey, held.Provider,
                    releaseName: null, result.Reason, cancellationToken);
            }
            else
            {
                await subtitles.RecordTimingStatusAsync(
                    MediaKind.Movie, id, language, forced: false, held.HearingImpaired,
                    result.Outcome switch
                    {
                        SubtitleTimingOutcome.Moved => "moved",
                        SubtitleTimingOutcome.AlreadyInTime => "already_in_time",
                        _ => "not_checked"
                    },
                    result.Reason,
                    cancellationToken);
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);

            return Results.Ok(new
            {
                done = true,
                outcome = result.Outcome.ToString(),
                moved = result.Adjusted,
                message = result.Reason
            });
        });

        /*
          #843: one movie's own subtitle-language override, replacing the
          library's Languages/Mode for this title only. Unset (never
          called, or cleared) means it follows the library exactly as
          before - nothing here changes that default path.
        */
        movies.MapPut("/{id}/subtitles/override", async (
            HttpContext httpContext,
            string id,
            [FromBody] SetSubtitleLanguageOverrideRequest request,
            IMovieCatalogRepository repository,
            IMediaSubtitleOverrideRepository overrides,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (await repository.GetByIdAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            if (request.Languages is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["languages"] = ["Choose at least one language, or delete the override to remove it."]
                });
            }

            var mode = string.Equals(request.Mode, "first", StringComparison.OrdinalIgnoreCase) ? "first" : "all";
            await overrides.SetAsync(MediaKind.Movie, id, new SubtitleLanguageOverride(request.Languages, mode), cancellationToken);

            return Results.Ok(new { languages = request.Languages, mode });
        });

        movies.MapDelete("/{id}/subtitles/override", async (
            HttpContext httpContext,
            string id,
            IMovieCatalogRepository repository,
            IMediaSubtitleOverrideRepository overrides,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (await repository.GetByIdAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            await overrides.ClearAsync(MediaKind.Movie, id, cancellationToken);

            return Results.Ok(new { message = "This movie now follows its library's subtitle languages again." });
        });

        /*
          #841: the manual search screen's own list - every ranked candidate
          from every provider, shown rather than acted on, so a person can
          pick differently from what the automatic fetch would have.
        */
        movies.MapGet("/{id}/subtitles/{language}/candidates", async (
            HttpContext httpContext,
            string id,
            string language,
            IMovieCatalogRepository repository,
            ILibrariesRepository librariesRepository,
            IMediaSubtitleRepository subtitles,
            ISubtitleFetchService fetchService,
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

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == movie.LibraryId);
            var blockedKeys = await subtitles.ListBlockedKeysAsync(MediaKind.Movie, id, cancellationToken);

            var request = new SubtitleSearchRequest(
                movie.Title, movie.ReleaseYear, null, null, null, movie.ReleaseGroup, movie.ImdbId, [language], IsEpisode: false);
            var candidates = await fetchService.SearchCandidatesAsync(
                request, language, isEpisodeMedia: false, movie.FilePath, excludeHearingImpaired: false,
                cancellationToken, library?.SubtitleNamePolicy, blockedKeys);

            return Results.Ok(candidates);
        });

        /*
          #841: picking one candidate from the manual search list above and
          writing it, through the same content-policy and naming path a
          fetch uses.
        */
        movies.MapPost("/{id}/subtitles/{language}/candidates/pick", async (
            HttpContext httpContext,
            string id,
            string language,
            [FromBody] PickSubtitleCandidateRequest request,
            IMovieCatalogRepository repository,
            ILibrariesRepository librariesRepository,
            IMediaSubtitleRepository subtitles,
            ISubtitleFetchService fetchService,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null || string.IsNullOrWhiteSpace(movie.FilePath))
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.ProviderKey) || string.IsNullOrWhiteSpace(request.DownloadToken))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["providerKey"] = ["Choose a candidate from the search results first."]
                });
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == movie.LibraryId);
            var searchRequest = new SubtitleSearchRequest(
                movie.Title, movie.ReleaseYear, null, null, null, movie.ReleaseGroup, movie.ImdbId, [language], IsEpisode: false);

            var outcome = await fetchService.DownloadCandidateAsync(
                searchRequest, request.ProviderKey, request.DownloadToken, language, isEpisodeMedia: false,
                movie.FilePath, cancellationToken, library?.SubtitleContentPolicy, library?.SubtitleNamePolicy,
                library?.SubtitleOmitLanguageCode ?? false);

            if (!outcome.Found || outcome.WrittenPath is null)
            {
                return Results.UnprocessableEntity(new { message = outcome.Reason });
            }

            await subtitles.RecordFetchedAsync(
                MediaKind.Movie, id,
                new MediaSubtitleRow(
                    Language: language,
                    Source: "manual",
                    Forced: false,
                    HearingImpaired: outcome.HearingImpaired,
                    FilePath: outcome.WrittenPath,
                    StreamIndex: null,
                    Codec: "srt",
                    Provider: outcome.ProviderKey,
                    MatchRung: (int)outcome.Match,
                    SourceKey: outcome.SourceKey),
                cancellationToken);

            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);

            return Results.Ok(new { written = true, message = outcome.Reason });
        });

        /*
          #841: a blacklist action on a candidate a person saw in manual
          search but never downloaded - stores its identity so it is never
          offered again, without requiring it to already be on disk (that
          case is the /reject endpoint above).
        */
        movies.MapPost("/{id}/subtitles/{language}/blacklist", async (
            HttpContext httpContext,
            string id,
            string language,
            [FromBody] BlacklistSubtitleCandidateRequest request,
            IMovieCatalogRepository repository,
            IMediaSubtitleRepository subtitles,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (await repository.GetByIdAsync(id, cancellationToken) is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.ProviderKey) || string.IsNullOrWhiteSpace(request.DownloadToken))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["providerKey"] = ["Choose a candidate from the search results first."]
                });
            }

            var blockKey = SubtitleBlockKeys.For(request.ProviderKey, request.DownloadToken);
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "You blacklisted this candidate." : request.Reason;
            await subtitles.BlockSubtitleAsync(
                MediaKind.Movie, id, language, blockKey, request.ProviderKey, request.ReleaseName, reason, cancellationToken);

            return Results.Ok(new { blacklisted = true });
        });

        /*
          #841: a person's own .srt/.ass file, written through the same
          UTF-8/style-tag policy a fetched subtitle already gets - a manual
          upload is not a second, looser write path.
        */
        movies.MapPost("/{id}/subtitles/{language}/upload", async (
            HttpContext httpContext,
            string id,
            string language,
            IFormFile file,
            IMovieCatalogRepository repository,
            ILibrariesRepository librariesRepository,
            IMediaSubtitleRepository subtitles,
            ISubtitleFileWriter fileWriter,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var movie = await repository.GetByIdAsync(id, cancellationToken);
            if (movie is null || string.IsNullOrWhiteSpace(movie.FilePath))
            {
                return Results.NotFound();
            }

            if (file.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = ["Choose a .srt or .ass file to upload."]
                });
            }

            byte[] uploaded;
            await using (var stream = file.OpenReadStream())
            await using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, cancellationToken);
                uploaded = buffer.ToArray();
            }

            if (!SubtitleArchive.LooksLikeSubtitle(uploaded))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["file"] = ["That file does not look like a subtitle."]
                });
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == movie.LibraryId);
            var modification = SubtitleContentModifier.Apply(uploaded, library?.SubtitleContentPolicy);
            var written = await fileWriter.WriteAsync(
                movie.FilePath, language, hearingImpaired: false, modification.Content, cancellationToken,
                library?.SubtitleOmitLanguageCode ?? false);

            await subtitles.RecordFetchedAsync(
                MediaKind.Movie, id,
                new MediaSubtitleRow(
                    Language: language,
                    Source: "uploaded",
                    Forced: false,
                    HearingImpaired: false,
                    FilePath: written,
                    StreamIndex: null,
                    Codec: "srt",
                    Provider: null,
                    MatchRung: (int)SubtitleCutoff.Rung),
                cancellationToken);

            await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);

            return Results.Ok(new
            {
                written = true,
                appliedModifications = modification.AppliedRules,
                message = $"Your {language} subtitle was written and is now held for this movie."
            });
        }).DisableAntiforgery();

        movies.MapPost("/bulk/quality-profile", async (
            HttpContext httpContext,
            [FromBody] BulkQualityProfileRequest request,
            IMovieCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            IAutoTaggingService autoTagging,
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

            // No profile means the title goes back to following its library's (#1157).
            var profileId = string.IsNullOrWhiteSpace(request.QualityProfileId) ? null : request.QualityProfileId.Trim();

            var updated = 0;
            foreach (var id in request.MovieIds)
            {
                if (await repository.UpdateQualityProfileAsync(id, profileId, cancellationToken))
                {
                    updated++;
                    await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
                    // #1024: a rule can depend on the quality profile.
                    await autoTagging.ApplyAsync(MediaKind.Movie, id, AutoTagTrigger.Edited, null, null, cancellationToken);
                }
            }

            return Results.Ok(new { updated, qualityProfileId = profileId });
        });

        movies.MapPost("/bulk/search", async (
            HttpContext httpContext,
            [FromBody] BulkSearchRequest request,
            IMediaStateRepository mediaStateRepository,
            IJobScheduler jobScheduler,
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
                jobScheduler,
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
            ILibrariesRepository librariesRepository,
            IPlatformSettingsRepository platformSettingsRepository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            IAutoTaggingService autoTagging,
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

            // "Move the files" is opt in. Left off, the record is reassigned
            // and nothing on disk changes - the acceptance for #787 requires
            // that to be stated plainly rather than left for the user to
            // discover as an orphaned folder under the old root.
            if (!request.MoveFiles)
            {
                var reassignedOnly = await repository.ReassignLibraryAsync(
                    request.MovieIds, request.FromLibraryId, request.ToLibraryId, cancellationToken);

                if (reassignedOnly > 0)
                {
                    foreach (var movieId in request.MovieIds.Distinct(StringComparer.OrdinalIgnoreCase))
                        await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movieId, cancellationToken);

                    // #1024: a rule can depend on the library or its folder.
                    await autoTagging.ApplyEditedAsync(MediaKind.Movie, request.MovieIds, cancellationToken);
                }

                return Results.Ok(new { reassigned = reassignedOnly, filesMoved = 0, moveFailures = Array.Empty<string>() });
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var toLibrary = libraries.FirstOrDefault(item => item.Id == request.ToLibraryId);
            var fromLibrary = libraries.FirstOrDefault(item => item.Id == request.FromLibraryId);
            if (toLibrary is null || fromLibrary is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["libraryId"] = ["Both libraries must exist to move files between them."]
                });
            }

            var movedFilePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var failedMovieIds = new HashSet<string>(StringComparer.Ordinal);
            var moveFailureTitles = new List<string>();
            var moveFailureReasons = new List<string>();
            var untrackedTravelled = new List<string>();
            var movedForUndo = new List<(string MovieId, string Title, bool WholeFolder, string FilePath, string Source, string Destination)>();
            var movieSettings = await platformSettingsRepository.GetAsync(cancellationToken);
            foreach (var movieId in request.MovieIds)
            {
                var movie = await repository.GetByIdAsync(movieId, cancellationToken);
                if (movie?.FilePath is not { Length: > 0 } filePath)
                {
                    // Nothing on disk to move; the library reassignment below
                    // still applies to it, same as any title with no file.
                    continue;
                }

                string? attemptSource = null;
                string? attemptDestination = null;
                try
                {
                    // The whole title folder moves, not only the tracked file -
                    // extras and subtitles sitting beside it would otherwise be
                    // orphaned under the old root, exactly the failure mode
                    // #787 exists to close. The decision itself lives in
                    // MovieFileRelocationPlanner so it can be tested without a
                    // filesystem; only the actual move happens here.
                    // #1078: only the title's own folder, or a flat file on its
                    // own into a new title folder, ever moves. The shared
                    // guard decides, at this moment, so it re-checks.
                    var movieRoots = libraries.Select(library => library.RootPath).ToArray();
                    var flatFolderName = NamingTemplateRenderer.RenderSegment(
                        movieSettings.MovieFolderFormat,
                        movie.Title,
                        movie.ReleaseYear,
                        movie.ImdbId,
                        genre: movie.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                        decision: new NamingDecision(OriginalTitle: movie.OriginalTitle),
                        replaceIllegalCharacters: movieSettings.ReplaceIllegalCharacters,
                        colonReplacementMode: movieSettings.ColonReplacementMode);
                    var outcome = await MovieFileRelocationPlanner.PlanAsync(
                        filePath,
                        fromLibrary.RootPath,
                        toLibrary.RootPath,
                        movieRoots,
                        (folder, ct) => repository.OtherMoviesHaveFilesUnderAsync(movie.Id, folder, ct),
                        flatFolderName,
                        cancellationToken);
                    var plan = outcome.Plan;
                    if (plan is null)
                    {
                        failedMovieIds.Add(movieId);
                        moveFailureTitles.Add(movie.Title);
                        moveFailureReasons.Add($"{movie.Title}: {outcome.RefusalReason}");
                        continue;
                    }

                    attemptSource = plan.SourceDirectory;
                    attemptDestination = plan.DestinationDirectory;
                    if (plan.MovesWholeFolder && Directory.Exists(plan.DestinationDirectory))
                    {
                        failedMovieIds.Add(movieId);
                        moveFailureTitles.Add(movie.Title);
                        moveFailureReasons.Add($"{movie.Title}: a folder with that name already exists in {toLibrary.Name}, so nothing was moved.");
                        continue;
                    }

                    if (plan.MovesWholeFolder)
                    {
                        var destinationParent = Path.GetDirectoryName(plan.DestinationDirectory);
                        if (destinationParent is { Length: > 0 })
                        {
                            Directory.CreateDirectory(destinationParent);
                        }

                        // #1078: what Deluno tracks moves with the folder and every tracked path
                        // is rewritten; anything else in the folder travels too, and is noted.
                        var trackedBefore = (await repository.ListTrackedFilePathsAsync(movie.Id, cancellationToken))
                            .Select(path => Path.GetFullPath(path))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var untrackedHere = Directory
                            .EnumerateFiles(plan.SourceDirectory, "*", SearchOption.AllDirectories)
                            .Where(path => !trackedBefore.Contains(Path.GetFullPath(path)))
                            .Select(path => Path.GetRelativePath(plan.SourceDirectory, path))
                            .ToArray();

                        Directory.Move(plan.SourceDirectory, plan.DestinationDirectory);
                        try
                        {
                            await repository.RelocateTrackedFilePathsAsync(
                                movie.Id, plan.SourceDirectory, plan.DestinationDirectory, cancellationToken);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            // The folder has already moved but the database could not follow. The
                            // batch carries on with the next film, and this one is put back where it
                            // was if it can be; otherwise the message says exactly where it is.
                            var putBack = FolderMoveRecovery.TryPutBack(plan.SourceDirectory, plan.DestinationDirectory);
                            var failure = FolderMoveRecovery.Describe(
                                movie.Title, plan.SourceDirectory, plan.DestinationDirectory, putBack, exception.Message,
                                $"run Rescan folder on {toLibrary.Name}");
                            failedMovieIds.Add(movieId);
                            moveFailureTitles.Add(movie.Title);
                            moveFailureReasons.Add(failure);
                            await activityFeedRepository.RecordActivityAsync(
                                "library.movie.move-records-failed",
                                failure,
                                JsonSerializer.Serialize(new { movieId, source = plan.SourceDirectory, destination = plan.DestinationDirectory, putBack }),
                                null,
                                "movie",
                                movieId,
                                cancellationToken);
                            continue;
                        }

                        if (untrackedHere.Length > 0)
                        {
                            untrackedTravelled.Add($"{movie.Title}: {string.Join(", ", untrackedHere.Take(5))}{(untrackedHere.Length > 5 ? $" and {untrackedHere.Length - 5} more" : "")}");
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(plan.DestinationDirectory);
                        File.Move(filePath, Path.Combine(plan.DestinationDirectory, Path.GetFileName(filePath)), overwrite: false);
                    }

                    movedFilePaths[movieId] = Path.Combine(plan.DestinationDirectory, Path.GetFileName(filePath));
                    movedForUndo.Add((movieId, movie.Title, plan.MovesWholeFolder, filePath, plan.SourceDirectory, plan.DestinationDirectory));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failedMovieIds.Add(movieId);
                    moveFailureTitles.Add(movie.Title);
                    moveFailureReasons.Add(FolderMoveRecovery.DescribeDiskFailure(
                        movie.Title,
                        attemptSource ?? Path.GetDirectoryName(filePath) ?? filePath,
                        attemptDestination ?? toLibrary.RootPath,
                        exception));
                }
            }

            // A movie whose move failed keeps its record where it was - the
            // file did not move, so the database must not say it did.
            var idsToReassign = request.MovieIds.Where(id => !failedMovieIds.Contains(id)).ToArray();

            var count = 0;
            if (idsToReassign.Length > 0)
            {
                try
                {
                    count = await repository.ReassignLibraryAsync(
                        idsToReassign, request.FromLibraryId, request.ToLibraryId, movedFilePaths, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Nothing was reassigned, so every folder that moved must go back or be named,
                    // otherwise the records say one place and the disk says another.
                    var reasons = new List<string>();
                    foreach (var moved in movedForUndo.Where(item => idsToReassign.Contains(item.MovieId)))
                    {
                        string? note = null;
                        if (moved.WholeFolder)
                        {
                            if (FolderMoveRecovery.TryPutBack(moved.Source, moved.Destination))
                            {
                                try
                                {
                                    await repository.RelocateTrackedFilePathsAsync(
                                        moved.MovieId, moved.Destination, moved.Source, CancellationToken.None);
                                }
                                catch (Exception)
                                {
                                    note = $"{moved.Title}: the folder is back at {moved.Source}, but Deluno could not update its records to match. Run Rescan folder on {fromLibrary.Name}.";
                                }
                            }
                            else
                            {
                                note = FolderMoveRecovery.Describe(moved.Title, moved.Source, moved.Destination, false, exception.Message, $"run Rescan folder on {toLibrary.Name}");
                            }
                        }
                        else
                        {
                            try
                            {
                                File.Move(Path.Combine(moved.Destination, Path.GetFileName(moved.FilePath)), moved.FilePath, overwrite: false);
                            }
                            catch (Exception moveBack) when (moveBack is IOException or UnauthorizedAccessException)
                            {
                                note = $"{moved.Title}: the file is now at {Path.Combine(moved.Destination, Path.GetFileName(moved.FilePath))}, but Deluno could not update its records ({exception.Message}). Move it back to {moved.FilePath}, or run Rescan folder on {toLibrary.Name}.";
                            }
                        }

                        if (note is not null)
                        {
                            reasons.Add(note);
                        }
                    }

                    var summary = $"Deluno could not move the records of {idsToReassign.Length} movie{(idsToReassign.Length == 1 ? "" : "s")} to {toLibrary.Name} ({exception.Message}), so they stayed in {fromLibrary.Name}."
                        + (reasons.Count == 0 ? " Any folders that had been moved were put back." : $" {string.Join(" ", reasons)}");
                    await activityFeedRepository.RecordActivityAsync(
                        "library.movie.move-records-failed",
                        summary,
                        JsonSerializer.Serialize(new { movieIds = idsToReassign, reasons }),
                        null,
                        "library",
                        request.ToLibraryId,
                        cancellationToken);
                    return Results.Ok(new
                    {
                        reassigned = 0,
                        filesMoved = 0,
                        moveFailures = movedForUndo.Where(item => idsToReassign.Contains(item.MovieId)).Select(item => item.Title).Concat(moveFailureTitles).ToArray(),
                        moveFailureReasons = moveFailureReasons.Concat(new[] { summary }).ToArray()
                    });
                }
            }

            if (count > 0)
            {
                foreach (var movieId in idsToReassign.Distinct(StringComparer.OrdinalIgnoreCase))
                    await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movieId, cancellationToken);

                // #1024: a rule can depend on the library or its folder.
                await autoTagging.ApplyEditedAsync(MediaKind.Movie, idsToReassign, cancellationToken);
            }

            if (untrackedTravelled.Count > 0)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "library.movie.untracked-files-moved",
                    $"Files Deluno was not tracking moved with their folder to {toLibrary.Name}: {string.Join("; ", untrackedTravelled)}.",
                    JsonSerializer.Serialize(new { untrackedTravelled }),
                    null,
                    "library",
                    request.ToLibraryId,
                    cancellationToken);
            }

            await activityFeedRepository.RecordActivityAsync(
                "library.movie.reassigned",
                moveFailureTitles.Count == 0
                    ? $"{count} movie{(count == 1 ? "" : "s")} moved from {fromLibrary.Name} to {toLibrary.Name}."
                    : $"{count} movie{(count == 1 ? "" : "s")} moved to {toLibrary.Name}; {moveFailureTitles.Count} file{(moveFailureTitles.Count == 1 ? "" : "s")} could not be moved and stayed in {fromLibrary.Name}.",
                JsonSerializer.Serialize(new { filesMoved = movedFilePaths.Count, moveFailureTitles, moveFailureReasons }),
                null,
                "library",
                request.ToLibraryId,
                cancellationToken);

            return Results.Ok(new { reassigned = count, filesMoved = movedFilePaths.Count, moveFailures = moveFailureTitles, moveFailureReasons });
        });

        movies.MapPost("/bulk/tags", async (
            HttpContext httpContext,
            [FromBody] BulkTagsRequest request,
            IMovieCatalogRepository repository,
            IMediaTagStore tagStore,
            IPlatformSettingsRepository platformSettingsRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            IAutoTaggingService autoTagging,
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

            if (!MediaTagBulkOperations.TryParseMode(request.Mode, out var mode))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["mode"] = ["Mode must be add, remove or replace."]
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

                var currentAssignments = await tagStore.ListAsync(MediaKind.Movie, movie.Id, cancellationToken);
                var finalAssignments = MediaTagBulkOperations.Apply(mode, currentAssignments, assignments);
                await tagStore.ReplaceAsync(MediaKind.Movie, movie.Id, finalAssignments, cancellationToken);
                updated++;
                // #1024: a rule can look for a tag the title now carries (or no longer carries).
                await autoTagging.ApplyAsync(MediaKind.Movie, movie.Id, AutoTagTrigger.Edited, null, null, cancellationToken);
                await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movie.Id, cancellationToken);
            }

            return Results.Ok(new { updated, tags = normalizedTags, mode = mode.ToString().ToLowerInvariant() });
        });

        /*
          One title's own rename preview (#997). Read-only: it says which
          folder Deluno would rename and to what, or why it would leave it
          alone, and touches nothing. Applying is the same explicit action the
          bulk tools use (/bulk/rename-apply), so what is shown here is what
          that will do.
        */
        movies.MapGet("/{id}/rename-preview", async (
            string id,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            ILibrariesRepository librariesRepository,
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

            var settings = await platformSettingsRepository.GetAsync(cancellationToken);
            var proposedName = NamingTemplateRenderer.RenderSegment(
                settings.MovieFolderFormat,
                movie.Title,
                movie.ReleaseYear,
                movie.ImdbId,
                genre: movie.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                decision: new NamingDecision(OriginalTitle: movie.OriginalTitle),
                replaceIllegalCharacters: settings.ReplaceIllegalCharacters,
                colonReplacementMode: settings.ColonReplacementMode);

            var target = await ResolveOwnFolderAsync(repository, librariesRepository, movie, cancellationToken);
            if (!target.IsSafe)
            {
                return Results.Ok(RenamePreviewOutcome.Refused(target, proposedName));
            }

            var sourceDir = target.Folder;
            var parentDir = sourceDir is null ? null : Path.GetDirectoryName(sourceDir);
            return Results.Ok(RenamePreviewOutcome.Describe(
                sourceDir is null ? null : Path.GetFileName(sourceDir),
                proposedName,
                hasFolder: sourceDir is not null && parentDir is not null,
                destinationExists: parentDir is not null && Directory.Exists(Path.Combine(parentDir, proposedName))));
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
                        genre: movie.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                        decision: new NamingDecision(OriginalTitle: movie.OriginalTitle),
                        replaceIllegalCharacters: settings.ReplaceIllegalCharacters,
                        colonReplacementMode: settings.ColonReplacementMode)
                });
            }

            return Results.Ok(new { count = previews.Count, previews });
        });

        /*
          Preview and apply were one continuous step in every renamed tool
          this replaces; here they were two disconnected ones, because only
          the first had ever been built. This is the second (#788): it
          renders the same proposed folder name the preview did, for the same
          selection, and then actually renames each title's folder on disk -
          reporting per movie which renamed and which did not, rather than
          promising a single all-or-nothing outcome for a batch.
        */
        movies.MapPost("/bulk/rename-apply", async (
            HttpContext httpContext,
            [FromBody] BulkRenamePreviewRequest request,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            ILibrariesRepository librariesRepository,
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
                    ["movieIds"] = ["Choose at least one movie to rename."]
                });
            }

            var settings = await platformSettingsRepository.GetAsync(cancellationToken);
            var template = string.IsNullOrWhiteSpace(request.Template)
                ? settings.MovieFolderFormat
                : request.Template.Trim();

            var renamed = new List<object>();
            var failures = new List<object>();
            foreach (var id in request.MovieIds)
            {
                var movie = await repository.GetByIdAsync(id, cancellationToken);
                if (movie is null)
                {
                    failures.Add(new { movieId = id, title = (string?)null, reason = "This title no longer exists." });
                    continue;
                }

                if (movie.FilePath is not { Length: > 0 } filePath)
                {
                    // Nothing on disk for this title - there is no folder to
                    // rename, so it is neither a success nor a failure.
                    continue;
                }

                // The same guard the title page's preview uses, re-run now at
                // apply time against the current rows (#1067).
                var target = await ResolveOwnFolderAsync(repository, librariesRepository, movie, cancellationToken);
                if (!target.IsSafe)
                {
                    failures.Add(new { movieId = id, movie.Title, reason = target.Message });
                    continue;
                }

                var sourceDir = target.Folder!;
                var parentDir = Path.GetDirectoryName(sourceDir);
                if (parentDir is null)
                {
                    failures.Add(new { movieId = id, movie.Title, reason = "The stored file path has no folder to rename." });
                    continue;
                }

                var proposedName = NamingTemplateRenderer.RenderSegment(
                    template,
                    movie.Title,
                    movie.ReleaseYear,
                    movie.ImdbId,
                    genre: movie.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                    decision: new NamingDecision(OriginalTitle: movie.OriginalTitle),
                    replaceIllegalCharacters: settings.ReplaceIllegalCharacters,
                    colonReplacementMode: settings.ColonReplacementMode);

                var destinationDir = Path.Combine(parentDir, proposedName);
                if (string.Equals(destinationDir, sourceDir, StringComparison.OrdinalIgnoreCase))
                {
                    // Already named correctly - nothing to do, and not a failure.
                    continue;
                }

                if (Directory.Exists(destinationDir))
                {
                    failures.Add(new { movieId = id, movie.Title, reason = $"A folder named \"{proposedName}\" already exists." });
                    continue;
                }

                try
                {
                    Directory.Move(sourceDir, destinationDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new { movieId = id, movie.Title, reason = "The folder could not be renamed on disk." });
                    continue;
                }

                try
                {
                    // Every tracked path under the folder follows (file, subtitles, scan rows), in one
                    // transaction, exactly as a reassign does (#1067).
                    await repository.RelocateTrackedFilePathsAsync(id, sourceDir, destinationDir, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The folder has already been renamed but the database could not follow. Put it
                    // back, or say exactly where it is, record it, and carry on with the next title.
                    var putBack = FolderMoveRecovery.TryPutBack(sourceDir, destinationDir);
                    var reason = FolderMoveRecovery.Describe(movie.Title, sourceDir, destinationDir, putBack, ex.Message);
                    failures.Add(new { movieId = id, movie.Title, reason });
                    await activityFeedRepository.RecordActivityAsync(
                        "library.movie.rename-records-failed",
                        reason,
                        JsonSerializer.Serialize(new { movieId = id, source = sourceDir, destination = destinationDir, putBack }),
                        null,
                        "movie",
                        id,
                        cancellationToken);
                    continue;
                }

                await realtimeEventPublisher.PublishEntityChangedAsync("Movie", id, cancellationToken);
                renamed.Add(new { movieId = id, movie.Title, from = Path.GetFileName(sourceDir), to = proposedName });
            }

            if (renamed.Count > 0 || failures.Count > 0)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "library.movie.renamed",
                    failures.Count == 0
                        ? $"{renamed.Count} movie folder{(renamed.Count == 1 ? "" : "s")} renamed."
                        : $"{renamed.Count} movie folder{(renamed.Count == 1 ? "" : "s")} renamed; {failures.Count} could not be renamed.",
                    JsonSerializer.Serialize(new { renamed, failures }),
                    null,
                    "movie",
                    null,
                    cancellationToken);
            }

            return Results.Ok(new { renamedCount = renamed.Count, renamed, failures });
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
        string? libraryId,
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
        IPlatformSettingsRepository platformSettings,
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
            releasePreferencePlanRepository,
            libraryId,
            platformSettings);

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

    /// <summary>
    /// The one place a movie's own folder is decided for "rename folder", used
    /// by the single-title preview and by rename-apply so they cannot disagree
    /// (#1067). Never renames anything.
    /// </summary>
    private static async Task<TitleFolderTarget> ResolveOwnFolderAsync(
        IMovieCatalogRepository repository,
        ILibrariesRepository librariesRepository,
        MovieListItem movie,
        CancellationToken cancellationToken)
    {
        var roots = (await librariesRepository.ListLibrariesAsync(cancellationToken))
            .Select(library => library.RootPath)
            .ToArray();
        return await TitleFolderGuard.ForMovieAsync(
            movie.FilePath,
            roots,
            (folder, ct) => repository.OtherMoviesHaveFilesUnderAsync(movie.Id, folder, ct),
            cancellationToken);
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
        CancellationToken cancellationToken,
        bool replaceIdentity = false)
        => repository.UpdateMetadataAsync(
            CatalogueMetadata.ToUpdate(movieId, result, result.Studio) with
            {
                Title = replaceIdentity ? result.Title : null,
                Year = replaceIdentity ? result.Year : null,
                // A person's Fix match re-links to a different title: what the
                // new one lacks must not keep the old one's artwork.
                ReplaceWithEmpty = replaceIdentity
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
        string? OverrideReason,
        string? DuplicateAction = null);

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

    private sealed record BulkReassignLibraryRequest(
        IReadOnlyList<string>? MovieIds,
        string? FromLibraryId,
        string? ToLibraryId,
        bool MoveFiles = false);

    private sealed record BulkMinimumAvailabilityRequest(
        IReadOnlyList<string>? MovieIds,
        string? MinimumAvailability);

    private sealed record SetSubtitleLanguageOverrideRequest(IReadOnlyList<string>? Languages, string? Mode);

    private sealed record PickSubtitleCandidateRequest(string? ProviderKey, string? DownloadToken);

    private sealed record BlacklistSubtitleCandidateRequest(string? ProviderKey, string? DownloadToken, string? ReleaseName, string? Reason);

    private sealed record BulkQualityProfileRequest(
        IReadOnlyList<string>? MovieIds,
        string? QualityProfileId);

    private sealed record BulkTagsRequest(
        IReadOnlyList<string>? MovieIds,
        string? Tags,
        string? Mode = null);

    private sealed record BulkRenamePreviewRequest(
        IReadOnlyList<string>? MovieIds,
        string? Template);

    private sealed record BulkSearchRequest(IReadOnlyList<string>? MovieIds);

    private sealed record MovieCollectionExclusionRequest(bool Excluded);

}
