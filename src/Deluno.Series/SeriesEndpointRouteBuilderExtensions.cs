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
using Deluno.Integrations.Subtitles;
using Deluno.Integrations.Metadata;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Notifications;
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
            IAutoTaggingService autoTagging,
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
                if (numbering is not null)
                {
                    // #1024: a rule can depend on the series type (anime, daily, standard).
                    await autoTagging.ApplyAsync(MediaKind.Series, id, AutoTagTrigger.Edited, null, null, cancellationToken);
                }

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
                    Filters: filters,
                    IncludeFacets: facets != false),
                cancellationToken);

            return Results.Ok(page);
        });

        series.MapGet("/import-recovery", async (
            ISeriesCatalogRepository repository,
            // The counts always cover every open case. The list is the newest 50 without
            // their details unless asked: a screen that lists them all passes limit=500.
            int? limit,
            bool? includeDetails,
            CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetImportRecoverySummaryAsync(cancellationToken, limit ?? 50, includeDetails == true);
            return Results.Ok(summary);
        });

        series.MapGet("/wanted", async (ISeriesCatalogRepository repository, CancellationToken cancellationToken) =>
        {
            var summary = await repository.GetWantedSummaryAsync(cancellationToken);
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

        /*
          Every subtitle a single episode's file has, and which of the three
          types each one is — plain, hearing-impaired or forced (#923). A
          show's subtitle belongs to the episode, not the series (see
          MediaTableMap's SubtitleMediaIdColumn), so this reads the same
          table the movie page reads keyed on the episode instead of the
          title, mirroring GET /api/movies/{id}/subtitles.
        */
        series.MapGet("/episodes/{episodeId}/subtitles", async (
            string episodeId,
            IMediaSubtitleRepository subtitles,
            CancellationToken cancellationToken) =>
        {
            var rows = await subtitles.ListSubtitlesAsync(MediaKind.Series, episodeId, cancellationToken);
            return Results.Ok(rows);
        });

        /*
          #840: the movies twin - one episode's own append-only search
          history, the same rows the Wanted screen's status reads from.
        */
        series.MapGet("/episodes/{episodeId}/subtitles/history", async (
            string episodeId,
            IMediaSubtitleRepository subtitles,
            CancellationToken cancellationToken) =>
        {
            var rows = await subtitles.ListAttemptHistoryAsync(MediaKind.Series, episodeId, cancellationToken);
            return Results.Ok(rows);
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
                includeUnmonitored: true,
                tagNames: null,
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

        series.MapPost("/import-recovery/{id}/dismiss", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
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
        series.MapPost("/import-recovery/{id}/retry", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
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
        // the title (an episode, not its whole series), rather than
        // searching for a different copy of it.
        series.MapPost("/import-recovery/{id}/stop-wanting", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
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
        series.MapPost("/import-recovery/{id}/import-original", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
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
        series.MapPost("/import-recovery/{id}/search-again", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
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

        series.MapDelete("/import-recovery/{id}", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
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

        // Posters for the titles on the Dashboard's Live board (#1096): only the ids that still exist are answered, so a deleted title is simply absent (no failing request per id).
        series.MapGet("/posters", async (
            string? ids,
            ISeriesCatalogRepository repository,
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

        // The name, year and poster of the shows on the Dashboard's Live board, and the season and episode number of the episodes it names (#1096), in one request. Anything that no longer exists is simply absent.
        series.MapGet("/titles", async (
            string? ids,
            string? episodeIds,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            static string[] Split(string? value) => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(48).ToArray();
            var episodes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var showIds = new HashSet<string>(Split(ids), StringComparer.OrdinalIgnoreCase);
            foreach (var row in await repository.ListEpisodeNumbersAsync(Split(episodeIds), cancellationToken))
            {
                episodes[row.EpisodeId] = new { seriesId = row.SeriesId, season = row.SeasonNumber, episode = row.EpisodeNumber };
                showIds.Add(row.SeriesId);
            }

            var shows = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in showIds.Take(96))
            {
                var item = await repository.GetByIdAsync(id, cancellationToken);
                if (item is not null) shows[id] = new { title = item.Title, year = item.StartYear, posterUrl = item.PosterUrl };
            }

            return Results.Ok(new { series = shows, episodes });
        });

        series.MapGet("/{id}", async (
            string id,
            ISeriesCatalogRepository repository,
            IMediaSubtitleOverrideRepository overrides,
            CancellationToken cancellationToken) =>
        {
            var item = await repository.GetByIdAsync(id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound();
            }

            // #843: a show's own subtitle-language override, when it has set
            // one - at the show level, not per episode.
            var @override = await overrides.GetAsync(MediaKind.Series, id, cancellationToken);
            if (@override is not null)
            {
                item = item with
                {
                    SubtitleLanguagesOverride = @override.Languages,
                    SubtitleModeOverride = @override.Mode
                };
            }

            return Results.Ok(item);
        });

        series.MapPut("/monitoring", async (
            HttpContext httpContext,
            [FromBody] UpdateSeriesMonitoringRequest request,
            ISeriesCatalogRepository repository,
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

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Choose at least one series before updating monitoring."]
                });
            }

            var seriesIds = request.SeriesIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            // #900: unmonitoring is "I don't want this any more", not just "stop
            // searching later" - an in-flight download for it is cancelled right
            // now, with its data, rather than left to finish and import anyway.
            if (!request.Monitored)
            {
                foreach (var seriesId in seriesIds)
                {
                    var seriesItem = await repository.GetByIdAsync(seriesId, cancellationToken);
                    if (seriesItem is not null)
                    {
                        // #947: an automatic per-episode search's dispatch is
                        // recorded under the episode's own id, not the
                        // series' - unmonitoring the series has to reach
                        // those too, not just its own season-pack dispatches.
                        var inventory = await repository.GetInventoryDetailAsync(seriesId, cancellationToken);
                        var episodeIds = inventory?.Episodes.Select(episode => episode.EpisodeId).ToArray() ?? [];
                        await titleRemoval.CancelDownloadForUnmonitoredTitleAsync(
                            "series",
                            seriesId,
                            seriesItem.Title,
                            cancellationToken,
                            relatedEpisodeIds: episodeIds);
                    }
                }
            }

            var updated = await repository.UpdateMonitoredAsync(
                seriesIds,
                request.Monitored,
                cancellationToken);
            foreach (var seriesId in seriesIds)
                await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);

            // #1024: a rule can depend on whether a show is monitored.
            await autoTagging.ApplyEditedAsync(MediaKind.Series, seriesIds, cancellationToken);

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
            TitleRemovalService titleRemoval,
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

            var episodeIds = request.EpisodeIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            var updated = await repository.UpdateEpisodeMonitoredAsync(
                episodeIds,
                request.Monitored,
                cancellationToken);

            // #900: unmonitoring is "I don't want this any more", not just "stop
            // searching later" - an in-flight download for it is cancelled right
            // now, with its data, rather than left to finish and import anyway.
            //
            // #947: an episode's own dispatch is not the only one that can be
            // running for it - a season-pack dispatch recorded under the
            // series also covers it. That pack is only cancelled once every
            // episode it covers is unmonitored, which is why monitored state
            // is updated above first: the check below reads it fresh rather
            // than guessing at episodes still being unmonitored in this same
            // request.
            if (!request.Monitored)
            {
                foreach (var episodeId in episodeIds)
                {
                    var seriesId = await repository.GetEpisodeSeriesIdAsync(episodeId, cancellationToken);
                    var seriesTitle = seriesId is null ? null : (await repository.GetByIdAsync(seriesId, cancellationToken))?.Title;
                    var label = seriesTitle is { Length: > 0 } ? $"An episode of {seriesTitle}" : "This episode";
                    var seasonPack = seriesId is null
                        ? null
                        : new TitleRemovalService.SeasonPackUnmonitorContext(
                            seriesId,
                            async (coveredEpisodeIds, ct) =>
                            {
                                var inventory = await repository.GetInventoryDetailAsync(seriesId, ct);
                                if (inventory is null)
                                {
                                    return false;
                                }

                                var monitoredById = inventory.Episodes.ToDictionary(episode => episode.EpisodeId, episode => episode.Monitored, StringComparer.OrdinalIgnoreCase);
                                return coveredEpisodeIds.All(coveredEpisodeId =>
                                    !monitoredById.TryGetValue(coveredEpisodeId, out var monitored) || !monitored);
                            });
                    await titleRemoval.CancelDownloadForUnmonitoredTitleAsync("episode", episodeId, label, cancellationToken, seasonPack: seasonPack);
                }
            }

            if (updated > 0)
            {
                foreach (var seriesId in await repository.ListParentSeriesIdsAsync(episodeIds, cancellationToken))
                {
                    await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);
                }
            }

            return Results.Ok(new { updated });
        });

        // The cross-series Season Pass screen (#791): every series' seasons,
        // episode counts and monitor state, in one query rather than one
        // inventory fetch per series.
        series.MapGet("/season-pass", async (
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            return Results.Ok(await repository.ListSeasonPassAsync(cancellationToken));
        });

        // Bulk-toggles monitoring at the season or series level, across as
        // many series as the Season Pass screen's selection covers.
        series.MapPut("/season-pass/monitoring", async (
            HttpContext httpContext,
            [FromBody] UpdateSeasonPassMonitoringRequest request,
            ISeriesCatalogRepository repository,
            TitleRemovalService titleRemoval,
            IRealtimeEventPublisher realtimeEventPublisher,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.Scopes is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["scopes"] = ["Choose at least one series or season before updating monitoring."]
                });
            }

            // #1028: Season Pass's own scopes are series- or season-wide, but
            // the repository always applies them at episode granularity (a
            // "whole series" scope still updates every episode row) - so
            // which episode ids each scope touches has to be resolved the
            // same way, before the update, to know what to cancel afterwards.
            var episodeIdsByScopeSeries = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (!request.Monitored)
            {
                foreach (var scope in request.Scopes)
                {
                    var inventory = await repository.GetInventoryDetailAsync(scope.SeriesId, cancellationToken);
                    if (inventory is null)
                    {
                        continue;
                    }

                    var episodes = scope.SeasonNumber is { } seasonNumber
                        ? inventory.Episodes.Where(episode => episode.SeasonNumber == seasonNumber)
                        : inventory.Episodes;
                    if (!episodeIdsByScopeSeries.TryGetValue(scope.SeriesId, out var episodeIds))
                    {
                        episodeIds = [];
                        episodeIdsByScopeSeries[scope.SeriesId] = episodeIds;
                    }

                    episodeIds.AddRange(episodes.Select(episode => episode.EpisodeId));
                }
            }

            var updated = await repository.SetSeasonPassMonitoringAsync(request.Scopes, request.Monitored, cancellationToken);

            // #900/#947: unmonitoring from Season Pass is the same "I don't
            // want this any more" as unmonitoring from a series or episode
            // page - an in-flight download is cancelled now, through the
            // same path (including the season-pack rule: a pack recorded
            // under the series is only cancelled once every episode it
            // covers is unmonitored, read fresh here since the update above
            // already landed).
            if (!request.Monitored)
            {
                foreach (var (seriesId, episodeIds) in episodeIdsByScopeSeries)
                {
                    if (episodeIds.Count == 0)
                    {
                        continue;
                    }

                    var seriesTitle = (await repository.GetByIdAsync(seriesId, cancellationToken))?.Title;
                    var label = seriesTitle is { Length: > 0 } ? $"An episode of {seriesTitle}" : "This episode";
                    var seasonPack = new TitleRemovalService.SeasonPackUnmonitorContext(
                        seriesId,
                        async (coveredEpisodeIds, ct) =>
                        {
                            var inventory = await repository.GetInventoryDetailAsync(seriesId, ct);
                            if (inventory is null)
                            {
                                return false;
                            }

                            var monitoredById = inventory.Episodes.ToDictionary(episode => episode.EpisodeId, episode => episode.Monitored, StringComparer.OrdinalIgnoreCase);
                            return coveredEpisodeIds.All(coveredEpisodeId =>
                                !monitoredById.TryGetValue(coveredEpisodeId, out var monitored) || !monitored);
                        });

                    foreach (var episodeId in episodeIds.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        await titleRemoval.CancelDownloadForUnmonitoredTitleAsync("episode", episodeId, label, cancellationToken, seasonPack: seasonPack);
                    }
                }
            }

            if (updated > 0)
            {
                foreach (var seriesId in request.Scopes.Select(scope => scope.SeriesId).Distinct(StringComparer.Ordinal))
                {
                    await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);
                }
            }

            return Results.Ok(new { updated });
        });

        // Why a title will not download, and what could be done about it.
        //
        // The question a person asks of a title that never arrives is not "what
        // is its wanted status" — it is "why is nothing happening". Every media
        // manager accumulates records that quietly answer that and never say
        // so: a client that already holds the release, a processor still
        // holding the file, an exclusion added when it was removed. This says
        // them out loud, and marks the ones a person is allowed to override.
        series.MapGet("/{id}/acquisition-blockers", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
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
                dispatches, processors, "tv", id, cancellationToken);
            var excluded = await AcquisitionBlockerSources.IsExcludedAsync(
                exclusions, "tv", item.Title, item.ImdbId, cancellationToken);
            // What Deluno fetched before and no longer holds — read from its
            // own dispatch record, so it answers with the client switched off.
            var fetchedBefore = await AcquisitionBlockerSources.FindPreviousFetchAsync(
                dispatches, "tv", id, cancellationToken);
            var blocked = await blockedReleases.ListForAsync("tv", id, cancellationToken);

            return Results.Ok(await gatherer.GatherAsync(
                MediaKind.Series,
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
        series.MapPost("/{id}/force-redownload", async (
            string id,
            string? libraryId,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
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
                    "tv", id, item.Title, item.ImdbId, dispatches, processors, exclusions, blockedReleaseRepository, cancellationToken),
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
                "tv",
                id,
                cancellationToken);
            // #966: forced import is one of the recovery/cleanup actions
            // that already landed in Activity but never reached the
            // outbound path. Gated by its own preference, off by default.
            var forcedImportPreferences = await notificationPreferences.GetPreferencesAsync(cancellationToken);
            if (forcedImportPreferences.ForcedImportEnabled)
            {
                var forcedTitleTags = (await tagStore.ListAsync(MediaKind.Series, id, cancellationToken)).Select(assignment => assignment.Name).ToArray();
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

        series.MapPost("/{id}/search", async (
            string id,
            string? libraryId,
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

            // The show-level query takes season packs; each missing episode is
            // asked for on its own, judged against its own file (#593).
            var plannedEpisodes = 0;
            if (!string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(result.Outcome, "blocked", StringComparison.OrdinalIgnoreCase))
            {
                var inventory = await repository.GetInventoryDetailAsync(id, cancellationToken);
                var rows = (await mediaStateRepository.ListWantedByIdsAsync(MediaKind.Series, [id], cancellationToken))
                    .Where(row => row.Id == id)
                    .ToArray();
                var (wanted, _) = MediaLibraryChoice.Pick(
                    rows,
                    libraryId,
                    await platformSettingsRepository.ListLibrariesAsync(cancellationToken),
                    "series");
                if (inventory is not null && wanted?.LibraryId is { Length: > 0 } searchLibraryId)
                {
                    var episodes = MissingEpisodeSearches.For(id, inventory.Episodes, timeProvider.GetUtcNow());
                    await jobQueueRepository.PlanEpisodeSearchesAsync(searchLibraryId, episodes, cancellationToken);
                    plannedEpisodes = episodes.Count;
                }
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
                plannedEpisodes,
                failures = result.Failures ?? [],
                candidates = MediaSearchCandidateView.ForAll(result.Candidates)
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
                    return Results.Json(
                        MetadataProviderResponses.Unavailable(
                            lookup,
                            $"{item.Title} was left exactly as it is."),
                        statusCode: StatusCodes.Status503ServiceUnavailable);
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
                    MetadataProviderResponses.Unavailable(
                        plan.Provider,
                        plan.Failure,
                        "Its episode catalogue could not be read either way, so nothing was changed."),
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
                    MetadataProviderResponses.Unavailable(
                        plan.Provider,
                        plan.Failure,
                        "Its episode catalogue could not be read either way, so nothing was changed."),
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
                cancellationToken,
                deliberateRelink: true);
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
                    VoteCount: null,
                    ReplaceWithEmpty: true),
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

            library = await TitleQualityProfile.ApplyAsync(mediaStateRepository, qualityRepository, MediaKind.Series, id, library, cancellationToken);
            var routing = await platformSettingsRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            var configuredSources = routing?.Sources.Count ?? 0;
            var configuredClients = routing?.DownloadClients.Count ?? 0;
            var now = timeProvider.GetUtcNow();
            var nextEligibleSearchUtc = now.AddHours(Math.Max(1, library.RetryDelayHours));
            var customFormats = await ResolveCustomFormatsAsync(qualityRepository, library.QualityProfileId, cancellationToken);
            var profileRules = await QualityProfileResolver.ResolveSearchRulesAsync(qualityRepository, library.QualityProfileId, cancellationToken);
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
            // What each episode's search actually found. The counts alone told
            // a person nothing: Deluno would compare three releases, choose
            // one, hold it for review, and the screen had no way to say which
            // release, or why. A movie search has returned its candidates all
            // along.
            var episodeResults = new List<object>();
            var comparedCandidates = 0;
            var alreadyOnItsWay = new List<string>();

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
                        TagNames: tagNames,
                        SearchKind: AcquisitionSearchKinds.Interactive,
                        AvailableUtc: wantedItem.AvailableUtc,
                        CurrentFilePresent: !string.IsNullOrWhiteSpace(baseline.FilePath),
                        CurrentReleaseName: baseline.FilePath,
                        NumberingScheme: seriesItem.NumberingScheme,
                        AbsoluteNumber: episode.AbsoluteNumber,
                        AirDate: episode.AirDate,
                        SceneSeasonNumber: episode.SceneSeasonNumber,
                        SceneEpisodeNumber: episode.SceneEpisodeNumber,
                        CurrentPreferenceEvaluation: baseline.PreferenceEvaluation,
                        PreferencePlan: preferencePlan,
                        RuntimeMinutes: seriesItem.RuntimeMinutes,
                        EntityId: seriesItem.Id,
                        ImdbId: seriesItem.ImdbId,
                        Genres: seriesItem.Genres,
                        OtherTitles: CatalogueMetadata.ReadAlternateTitles(seriesItem.MetadataJson),
                        SeasonsWithRemovedEpisodes: inventory.SeasonsWithRemovedEpisodes(),
                        EpisodeTitles: inventory.EpisodeTitlesForSeason(episode.SeasonNumber)).WithProfile(profileRules),
                    cancellationToken);
                var searchPlan = decisionPlan.SearchPlan;
                var bestCandidate = searchPlan.BestCandidate;
                var outcome = decisionPlan.Outcome;
                searchReasons.Add(searchPlan.Reason);
                failures.AddRange(searchPlan.Failures ?? []);
                comparedCandidates += searchPlan.Candidates.Count;
                episodeResults.Add(new
                {
                    episode.EpisodeId,
                    episode.SeasonNumber,
                    episode.EpisodeNumber,
                    episode.Title,
                    outcome,
                    reason = searchPlan.Reason,
                    summary = searchPlan.Summary,
                    releaseName = bestCandidate?.ReleaseName,
                    indexerName = bestCandidate?.IndexerName,
                    comparedCount = searchPlan.Candidates.Count,
                    // One thing per button (#1175): "send" lists nothing to choose from, "choose" sends nothing.
                    candidates = request.ChooseOnly
                        ? MediaSearchCandidateView.ForAll(searchPlan.Candidates)
                        : []
                });

                var willDispatch = !request.ChooseOnly
                    && decisionPlan.ShouldDispatch && decisionPlan.SelectedDownloadClient is not null && decisionPlan.DispatchRequest is not null;
                using var sendGate = willDispatch
                    ? await DispatchSendGate.EnterAsync(bestCandidate!.ReleaseName, [episode.EpisodeId, seriesItem.Id], cancellationToken)
                    : null;
                if (willDispatch)
                {
                    // An episode already downloading is never sent a second release (#1175).
                    var onItsWay = await DispatchCoverage.AlreadyOnItsWayAsync(
                        jobQueueRepository, bestCandidate!.ReleaseName, [episode.EpisodeId, seriesItem.Id], cancellationToken);
                    if (onItsWay is not null)
                    {
                        willDispatch = false;
                        alreadyOnItsWay.Add($"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}: {onItsWay}");
                    }
                }

                if (willDispatch)
                {
                    matchedCount++;
                    queuedCount++;
                    var downloadClient = decisionPlan.SelectedDownloadClient!;
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
                        : await downloadClientGrabService.GrabAsync(
                            downloadClient.DownloadClientId,
                            decisionPlan.DispatchRequest! with
                            {
                                OnDeadRelease = new DeadReleaseFollowUp("episode", episode.EpisodeId, seriesItem.Title, library.Id, library.Name)
                            },
                            cancellationToken);
                    if (grabResult.Failure is { } dispatchFailure)
                    {
                        failures.Add(dispatchFailure);
                    }
                    if (grabResult.Succeeded && grabResult.Status == "sent")
                    {
                        sentCount++;
                    }
                    else if (!grabResult.Succeeded && grabResult.Status != "planned")
                    {
                        // A send the client refused, a paused or open circuit: a failed episode, not a planned one.
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
                        clientExternalId: grabResult.ExternalId,
                        startedBy: DownloadStartedBy.Manual,
                        titleEntityId: seriesItem.Id);
                }

                await repository.RecordSearchAttemptAsync(
                    seriesItem.Id,
                    episode.EpisodeId,
                    library.Id,
                    "manual-episode",
                    request.ChooseOnly ? "checked" : outcome,
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
                    // The third place this claim was made, and the last. Saying
                    // nothing satisfied the rules is only true when nothing was
                    // found: a search that compared releases and is waiting on a
                    // person satisfied them well enough to offer a choice.
                    Reason: matchedCount > 0
                        ? $"{matchedCount} episode search result{(matchedCount == 1 ? string.Empty : "s")} met the active quality and policy rules."
                        : comparedCandidates > 0
                            ? $"{comparedCandidates} release{(comparedCandidates == 1 ? " was" : "s were")} compared and none was sent automatically, so the choice is yours."
                            : "No selected episodes produced a release that satisfied the active quality and policy rules.",
                    Inputs: new Dictionary<string, string?>
                    {
                        ["title"] = seriesItem.Title,
                        ["libraryId"] = library.Id,
                        ["episodeCount"] = targetEpisodes.Count.ToString(),
                        ["candidatesCompared"] = comparedCandidates.ToString(),
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
                // "No results" is about whether anything was found. It was
                // being reported whenever nothing was *dispatched*, so a search
                // that returned three candidates and held the best one for a
                // person said the indexers had returned nothing.
                reason = matchedCount > 0
                    ? MediaSearchReasons.Ok
                    : searchReasons.FirstOrDefault(item => !string.Equals(item, MediaSearchReasons.Ok, StringComparison.OrdinalIgnoreCase))
                        ?? (episodeResults.Count > 0 && comparedCandidates > 0
                            ? MediaSearchReasons.HeldForReview
                            : MediaSearchReasons.NoResults),
                searchedEpisodes = targetEpisodes.Count,
                matchedCount,
                queuedCount,
                sentCount,
                plannedCount,
                failedCount,
                episodes = episodeResults.ToArray(),
                failures = failures.Distinct().ToArray(),
                alreadyDownloading = alreadyOnItsWay.ToArray()
            });
        });

        /*
          Removing one show. The mirror of the movie route, deliberately - a
          media manager should not answer the same question two different ways
          depending on whether the thing has episodes in it.

          This existed only as POST /bulk with an array of one, so a removal that
          failed entirely came back 200 with successCount: 0 (#421).
        */
        series.MapDelete("/{id}", async (
            string id,
            HttpContext httpContext,
            bool? deleteFiles,
            bool? addImportListExclusion,
            ISeriesCatalogRepository repository,
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

            var series = await repository.GetByIdAsync(id, cancellationToken);
            if (series is null)
            {
                return Results.NotFound();
            }

            await titleRemoval.RemoveAsync(
                SeriesRemoval.Target(series, repository),
                new TitleRemovalOptions(deleteFiles ?? false, addImportListExclusion ?? false),
                cancellationToken);

            await realtimeEventPublisher.PublishEntityChangedAsync("Series", id, cancellationToken);
            return Results.NoContent();
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
            [FromServices] TitleRemovalService titleRemoval,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var refErrors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, refErrors, "qualityProfileId", Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile,
                request.QualityProfileId, "tv", cancellationToken);
            if (refErrors.Count > 0)
            {
                return Results.ValidationProblem(refErrors);
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
                            var removalMetadata = await titleRemoval.RemoveAsync(
                                SeriesRemoval.Target(series, repository),
                                new TitleRemovalOptions(request.DeleteFiles, request.AddImportListExclusion),
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
                                    JobType: TitleSearchJobs.Series,
                                    Source: "bulk",
                                    PayloadJson: TitleSearchJobs.Payload(series.Id, series.Title, null, TitleSearchJobs.FromBulkEditor),
                                    RelatedEntityType: "series",
                                    RelatedEntityId: series.Id,
                                    DedupeKey: TitleSearchJobs.DedupeKey(MediaKind.Series, series.Id, null)),
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
        /*
          #922's one-click "This is out of sync": a person's own judgment, fed
          through #897's mechanism. Deletes the file, blocklists it so it is
          never offered again, and drops the held row so the language is
          missing again for that episode - which is what puts it back in
          front of the library's own next subtitle search.

          Addressed by episode, not by series: a subtitle belongs to one
          episode, same as everywhere else this store keeps that distinction.
        */
        series.MapPost("/episodes/{episodeId}/subtitles/{language}/reject", async (
            HttpContext httpContext,
            string episodeId,
            string language,
            ISeriesCatalogRepository repository,
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

            var seriesId = await repository.GetEpisodeSeriesIdAsync(episodeId, cancellationToken);
            if (seriesId is null)
            {
                return Results.NotFound();
            }

            var show = await repository.GetByIdAsync(seriesId, cancellationToken);

            var held = (await subtitles.ListSubtitlesAsync(MediaKind.Series, episodeId, cancellationToken))
                .FirstOrDefault(row => string.Equals(row.Language, language, StringComparison.OrdinalIgnoreCase) && !row.Forced);
            if (held is null || held.FilePath is null)
            {
                return Results.NotFound(new { message = $"There is no {language} subtitle held for this episode." });
            }

            await SubtitleRejection.RejectAsync(
                subtitles,
                activity,
                MediaKind.Series,
                episodeId,
                show?.Title ?? "this show",
                language,
                forced: false,
                held.HearingImpaired,
                held.FilePath,
                held.SourceKey,
                held.Provider,
                releaseName: null,
                "You marked this subtitle as out of sync.",
                cancellationToken);

            await realtimeEventPublisher.PublishEntityChangedAsync("Episode", episodeId, cancellationToken);

            return Results.Ok(new
            {
                rejected = true,
                message = $"The {language} subtitle was removed and blocklisted; the next library subtitle search will fetch a replacement."
            });
        });

        /*
          #842: a per-file, run-now resync for one episode's subtitle - see
          the movies twin for why this answers "done" rather than "queued".
        */
        series.MapPost("/episodes/{episodeId}/subtitles/{language}/resync", async (
            HttpContext httpContext,
            string episodeId,
            string language,
            ISeriesCatalogRepository repository,
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

            var seriesId = await repository.GetEpisodeSeriesIdAsync(episodeId, cancellationToken);
            if (seriesId is null)
            {
                return Results.NotFound();
            }

            var videoPath = await repository.GetEpisodeFilePathAsync(episodeId, cancellationToken);
            if (string.IsNullOrWhiteSpace(videoPath))
            {
                return Results.NotFound(new { message = "This episode has no file on disk to resync a subtitle against." });
            }

            var held = (await subtitles.ListSubtitlesAsync(MediaKind.Series, episodeId, cancellationToken))
                .FirstOrDefault(row => string.Equals(row.Language, language, StringComparison.OrdinalIgnoreCase) && !row.Forced);
            if (held is null || held.FilePath is null)
            {
                return Results.NotFound(new { message = $"There is no {language} subtitle held for this episode." });
            }

            var show = await repository.GetByIdAsync(seriesId, cancellationToken);
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == show!.LibraryId);
            var policy = SubtitleTimingPolicyCodec.Normalize(library?.SubtitleTimingPolicy) ?? new SubtitleTimingPolicy();
            // See the movies twin: a run-now request overrides a disabled
            // library policy for this one call rather than silently doing
            // nothing.
            policy = policy with { Enabled = true };

            var result = await timingSync.SyncAsync(
                videoPath!, held.FilePath, originalLanguage: null, cancellationToken, policy);

            if (result.Outcome == SubtitleTimingOutcome.Mismatched)
            {
                await SubtitleRejection.RejectAsync(
                    subtitles, activity, MediaKind.Series, episodeId, show?.Title ?? "this show", language,
                    forced: false, held.HearingImpaired, held.FilePath, held.SourceKey, held.Provider,
                    releaseName: null, result.Reason, cancellationToken);
            }
            else
            {
                await subtitles.RecordTimingStatusAsync(
                    MediaKind.Series, episodeId, language, forced: false, held.HearingImpaired,
                    result.Outcome switch
                    {
                        SubtitleTimingOutcome.Moved => "moved",
                        SubtitleTimingOutcome.AlreadyInTime => "already_in_time",
                        _ => "not_checked"
                    },
                    result.Reason,
                    cancellationToken);
            }

            await realtimeEventPublisher.PublishEntityChangedAsync("Episode", episodeId, cancellationToken);

            return Results.Ok(new
            {
                done = true,
                outcome = result.Outcome.ToString(),
                moved = result.Adjusted,
                message = result.Reason
            });
        });

        /*
          #843: one show's own subtitle-language override, at the show
          level - not per episode, and replacing the library's Languages/
          Mode for this title only. Unset (never called, or cleared) means
          it follows the library exactly as before.
        */
        series.MapPut("/{id}/subtitles/override", async (
            HttpContext httpContext,
            string id,
            [FromBody] SetSubtitleLanguageOverrideRequest request,
            ISeriesCatalogRepository repository,
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
            await overrides.SetAsync(MediaKind.Series, id, new SubtitleLanguageOverride(request.Languages, mode), cancellationToken);

            return Results.Ok(new { languages = request.Languages, mode });
        });

        series.MapDelete("/{id}/subtitles/override", async (
            HttpContext httpContext,
            string id,
            ISeriesCatalogRepository repository,
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

            await overrides.ClearAsync(MediaKind.Series, id, cancellationToken);

            return Results.Ok(new { message = "This show now follows its library's subtitle languages again." });
        });

        /*
          #841: the movies twins, for one episode - every ranked candidate
          from every provider (manual search), picking one, blacklisting one
          never downloaded, and a person's own upload. The show and episode
          number come from the series inventory rather than a field on the
          episode row itself, the same way LibrarySubtitleSearchJobHandler
          reads them off ListWantedAsync's own item.
        */
        series.MapGet("/episodes/{episodeId}/subtitles/{language}/candidates", async (
            HttpContext httpContext,
            string episodeId,
            string language,
            ISeriesCatalogRepository repository,
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

            var context = await ResolveEpisodeSearchContextAsync(repository, episodeId, cancellationToken);
            if (context is null)
            {
                return Results.NotFound();
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var library = libraries.FirstOrDefault(item => item.Id == context.Show.LibraryId);
            var blockedKeys = await subtitles.ListBlockedKeysAsync(MediaKind.Series, episodeId, cancellationToken);

            var request = new SubtitleSearchRequest(
                context.Show.Title, context.Show.StartYear, context.Episode.SeasonNumber, context.Episode.EpisodeNumber,
                context.Episode.Title, context.Show.ReleaseGroup, context.Show.ImdbId, [language], IsEpisode: true);
            var candidates = await fetchService.SearchCandidatesAsync(
                request, language, isEpisodeMedia: true, context.FilePath, excludeHearingImpaired: false,
                cancellationToken, library?.SubtitleNamePolicy, blockedKeys);

            return Results.Ok(candidates);
        });

        series.MapPost("/episodes/{episodeId}/subtitles/{language}/candidates/pick", async (
            HttpContext httpContext,
            string episodeId,
            string language,
            [FromBody] PickSubtitleCandidateRequest request,
            ISeriesCatalogRepository repository,
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

            var context = await ResolveEpisodeSearchContextAsync(repository, episodeId, cancellationToken);
            if (context is null || string.IsNullOrWhiteSpace(context.FilePath))
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
            var library = libraries.FirstOrDefault(item => item.Id == context.Show.LibraryId);
            var searchRequest = new SubtitleSearchRequest(
                context.Show.Title, context.Show.StartYear, context.Episode.SeasonNumber, context.Episode.EpisodeNumber,
                context.Episode.Title, context.Show.ReleaseGroup, context.Show.ImdbId, [language], IsEpisode: true);

            var outcome = await fetchService.DownloadCandidateAsync(
                searchRequest, request.ProviderKey, request.DownloadToken, language, isEpisodeMedia: true,
                context.FilePath, cancellationToken, library?.SubtitleContentPolicy, library?.SubtitleNamePolicy,
                library?.SubtitleOmitLanguageCode ?? false);

            if (!outcome.Found || outcome.WrittenPath is null)
            {
                return Results.UnprocessableEntity(new { message = outcome.Reason });
            }

            await subtitles.RecordFetchedAsync(
                MediaKind.Series, episodeId,
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

            await realtimeEventPublisher.PublishEntityChangedAsync("Episode", episodeId, cancellationToken);

            return Results.Ok(new { written = true, message = outcome.Reason });
        });

        series.MapPost("/episodes/{episodeId}/subtitles/{language}/blacklist", async (
            HttpContext httpContext,
            string episodeId,
            string language,
            [FromBody] BlacklistSubtitleCandidateRequest request,
            ISeriesCatalogRepository repository,
            IMediaSubtitleRepository subtitles,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (await repository.GetEpisodeSeriesIdAsync(episodeId, cancellationToken) is null)
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
                MediaKind.Series, episodeId, language, blockKey, request.ProviderKey, request.ReleaseName, reason, cancellationToken);

            return Results.Ok(new { blacklisted = true });
        });

        series.MapPost("/episodes/{episodeId}/subtitles/{language}/upload", async (
            HttpContext httpContext,
            string episodeId,
            string language,
            IFormFile file,
            ISeriesCatalogRepository repository,
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

            var context = await ResolveEpisodeSearchContextAsync(repository, episodeId, cancellationToken);
            if (context is null || string.IsNullOrWhiteSpace(context.FilePath))
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
            var library = libraries.FirstOrDefault(item => item.Id == context.Show.LibraryId);
            var modification = SubtitleContentModifier.Apply(uploaded, library?.SubtitleContentPolicy);
            var written = await fileWriter.WriteAsync(
                context.FilePath, language, hearingImpaired: false, modification.Content, cancellationToken,
                library?.SubtitleOmitLanguageCode ?? false);

            await subtitles.RecordFetchedAsync(
                MediaKind.Series, episodeId,
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

            await realtimeEventPublisher.PublishEntityChangedAsync("Episode", episodeId, cancellationToken);

            return Results.Ok(new
            {
                written = true,
                appliedModifications = modification.AppliedRules,
                message = $"Your {language} subtitle was written and is now held for this episode."
            });
        }).DisableAntiforgery();

        series.MapPost("/bulk/quality-profile", async (
            HttpContext httpContext,
            [FromBody] BulkQualityProfileRequest request,
            ISeriesCatalogRepository repository,
            IRealtimeEventPublisher realtimeEventPublisher,
            IAutoTaggingService autoTagging,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
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

            var refErrors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, refErrors, "qualityProfileId", Deluno.Infrastructure.Storage.ReferenceKind.QualityProfile,
                request.QualityProfileId, "tv", cancellationToken);
            if (refErrors.Count > 0)
            {
                return Results.ValidationProblem(refErrors);
            }

            // No profile means the title goes back to following its library's (#1157).
            var profileId = string.IsNullOrWhiteSpace(request.QualityProfileId) ? null : request.QualityProfileId.Trim();

            var updated = 0;
            foreach (var id in request.SeriesIds)
            {
                if (await repository.UpdateQualityProfileAsync(id, profileId, cancellationToken))
                {
                    updated++;
                    await realtimeEventPublisher.PublishEntityChangedAsync("Series", id, cancellationToken);
                    // #1024: a rule can depend on the quality profile.
                    await autoTagging.ApplyAsync(MediaKind.Series, id, AutoTagTrigger.Edited, null, null, cancellationToken);
                }
            }

            return Results.Ok(new { updated, qualityProfileId = profileId });
        });

        series.MapPost("/bulk/search", async (
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
                MediaKind.Series,
                request.SeriesIds,
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

        series.MapPost("/bulk/reassign-library", async (
            HttpContext httpContext,
            [FromBody] BulkReassignLibraryRequest request,
            ISeriesCatalogRepository repository,
            ILibrariesRepository librariesRepository,
            IActivityFeedRepository activityFeedRepository,
            IRealtimeEventPublisher realtimeEventPublisher,
            IAutoTaggingService autoTagging,
            [FromServices] Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory connectionFactory,
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

            var refErrors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, refErrors, "fromLibraryId", Deluno.Infrastructure.Storage.ReferenceKind.Library,
                request.FromLibraryId, "tv", cancellationToken);
            await Deluno.Infrastructure.Storage.ReferenceChecks.AddProblemAsync(
                connectionFactory, refErrors, "toLibraryId", Deluno.Infrastructure.Storage.ReferenceKind.Library,
                request.ToLibraryId, "tv", cancellationToken);
            if (refErrors.Count > 0)
            {
                return Results.ValidationProblem(refErrors);
            }

            // "Move the folder" is opt in, exactly as for films (#1327). Left off, the record is reassigned and
            // nothing on disk changes.
            if (!request.MoveFiles)
            {
                var reassignedOnly = await repository.ReassignLibraryAsync(
                    request.SeriesIds, request.FromLibraryId, request.ToLibraryId, cancellationToken);

                if (reassignedOnly > 0)
                {
                    foreach (var seriesId in request.SeriesIds.Distinct(StringComparer.OrdinalIgnoreCase))
                        await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);

                    // #1024: a rule can depend on the library or its folder.
                    await autoTagging.ApplyEditedAsync(MediaKind.Series, request.SeriesIds, cancellationToken);
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
                    ["libraryId"] = ["Both libraries must exist to move folders between them."]
                });
            }

            var failedSeriesIds = new HashSet<string>(StringComparer.Ordinal);
            var moveFailureTitles = new List<string>();
            var moveFailureReasons = new List<string>();
            var moved = new List<(string SeriesId, string Title, string Source, string Destination, (string EpisodeId, string Previous, string New)[] Files)>();
            var fromPrefix = fromLibrary.RootPath.TrimEnd('/', '\\').Replace('\\', '/') + "/";
            foreach (var seriesId in request.SeriesIds.Distinct(StringComparer.Ordinal))
            {
                var seriesItem = await repository.GetByIdAsync(seriesId, cancellationToken);
                var episodeFiles = await repository.ListEpisodeFilePathsAsync(seriesId, cancellationToken);
                if (seriesItem is null || episodeFiles.Count == 0)
                {
                    // Nothing on disk to move; the library change below still applies.
                    continue;
                }

                void Fail(string reason)
                {
                    failedSeriesIds.Add(seriesId);
                    moveFailureTitles.Add(seriesItem.Title);
                    moveFailureReasons.Add($"{seriesItem.Title}: {reason}");
                }

                // Only the show's own folder ever moves; the shared guard decides, now, against the current rows.
                var target = await ResolveOwnFolderAsync(repository, librariesRepository, seriesId, episodeFiles, cancellationToken);
                if (!target.IsSafe)
                {
                    Fail(target.Message);
                    continue;
                }

                var plan = SeriesFolderRelocationPlanner.Plan(
                    episodeFiles.Select(file => file.FilePath).ToArray(),
                    Path.GetFileName(target.Folder!.TrimEnd('/', '\\')),
                    target.Folder);
                if (plan is null)
                {
                    Fail("its episode files do not share a folder Deluno can move.");
                    continue;
                }

                if (!Deluno.Platform.TitleFolderNeighbours.IsUnder(plan.SourceDirectory + "/", fromPrefix))
                {
                    Fail($"its folder is not inside {fromLibrary.Name}, so nothing was moved.");
                    continue;
                }

                var destination = Path.Combine(toLibrary.RootPath, Path.GetFileName(plan.SourceDirectory));
                var fullSource = Path.GetFullPath(plan.SourceDirectory);
                var fullDestination = Path.GetFullPath(destination);
                if (string.Equals(fullDestination, fullSource, StringComparison.OrdinalIgnoreCase))
                {
                    // Both libraries share the folder's place; the record changes and nothing moves.
                    continue;
                }

                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    Fail($"a folder with that name already exists in {toLibrary.Name}, so nothing was moved.");
                    continue;
                }

                if (Deluno.Platform.TitleFolderNeighbours.IsUnder(
                        fullDestination + "/",
                        fullSource.Replace('\\', '/').TrimEnd('/') + "/"))
                {
                    Fail($"{toLibrary.Name} sits inside the show's own folder, so nothing was moved.");
                    continue;
                }

                try
                {
                    var parent = Path.GetDirectoryName(destination);
                    if (parent is { Length: > 0 })
                    {
                        Directory.CreateDirectory(parent);
                    }

                    Directory.Move(plan.SourceDirectory, destination);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failedSeriesIds.Add(seriesId);
                    moveFailureTitles.Add(seriesItem.Title);
                    moveFailureReasons.Add(FolderMoveRecovery.DescribeDiskFailure(seriesItem.Title, plan.SourceDirectory, destination, exception));
                    continue;
                }

                moved.Add((seriesId, seriesItem.Title, plan.SourceDirectory, destination,
                    episodeFiles
                        .Select(file => (file.EpisodeId, file.FilePath, Path.Combine(destination, Path.GetRelativePath(plan.SourceDirectory, file.FilePath))))
                        .ToArray()));
            }

            // A show whose move failed keeps its record where it was: its folder did not move.
            var idsToReassign = request.SeriesIds.Where(id => !failedSeriesIds.Contains(id)).ToArray();
            var count = 0;
            if (idsToReassign.Length > 0)
            {
                try
                {
                    count = await repository.ReassignLibraryAsync(
                        idsToReassign,
                        request.FromLibraryId,
                        request.ToLibraryId,
                        moved.Where(item => idsToReassign.Contains(item.SeriesId)).SelectMany(item => item.Files).ToArray(),
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One transaction: no record changed. Every folder that moved goes back, or is named.
                    var reasons = new List<string>();
                    foreach (var item in moved.Where(item => idsToReassign.Contains(item.SeriesId)))
                    {
                        var putBack = FolderMoveRecovery.TryPutBack(item.Source, item.Destination);
                        reasons.Add(FolderMoveRecovery.Describe(
                            item.Title, item.Source, item.Destination, putBack, exception.Message,
                            $"run Rescan folder on {toLibrary.Name}"));
                    }

                    var summary = $"Deluno could not move the records of {idsToReassign.Length} show{(idsToReassign.Length == 1 ? "" : "s")} to {toLibrary.Name} ({exception.Message}), so they stayed in {fromLibrary.Name}."
                        + (reasons.Count == 0 ? "" : $" {string.Join(" ", reasons)}");
                    await activityFeedRepository.RecordActivityAsync(
                        "library.series.move-records-failed",
                        summary,
                        JsonSerializer.Serialize(new { seriesIds = idsToReassign, reasons }),
                        null,
                        "library",
                        request.ToLibraryId,
                        cancellationToken);
                    return Results.Ok(new
                    {
                        reassigned = 0,
                        filesMoved = 0,
                        moveFailures = moved.Select(item => item.Title).Concat(moveFailureTitles).ToArray(),
                        moveFailureReasons = moveFailureReasons.Concat(new[] { summary }).ToArray()
                    });
                }
            }

            if (count > 0)
            {
                foreach (var seriesId in idsToReassign.Distinct(StringComparer.OrdinalIgnoreCase))
                    await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesId, cancellationToken);

                // #1024: a rule can depend on the library or its folder.
                await autoTagging.ApplyEditedAsync(MediaKind.Series, idsToReassign, cancellationToken);
            }

            await activityFeedRepository.RecordActivityAsync(
                "library.series.reassigned",
                moveFailureTitles.Count == 0
                    ? $"{count} show{(count == 1 ? "" : "s")} moved from {fromLibrary.Name} to {toLibrary.Name}."
                    : $"{count} show{(count == 1 ? "" : "s")} moved to {toLibrary.Name}; {moveFailureTitles.Count} folder{(moveFailureTitles.Count == 1 ? "" : "s")} could not be moved and stayed in {fromLibrary.Name}.",
                JsonSerializer.Serialize(new { foldersMoved = moved.Count, moveFailureTitles, moveFailureReasons }),
                null,
                "library",
                request.ToLibraryId,
                cancellationToken);

            return Results.Ok(new { reassigned = count, filesMoved = moved.Count, moveFailures = moveFailureTitles, moveFailureReasons });
        });

        series.MapPost("/bulk/tags", async (
            HttpContext httpContext,
            [FromBody] BulkTagsRequest request,
            ISeriesCatalogRepository repository,
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

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Choose at least one series before applying tags."]
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
            foreach (var id in request.SeriesIds)
            {
                var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
                if (seriesItem is null)
                {
                    continue;
                }

                var currentAssignments = await tagStore.ListAsync(MediaKind.Series, seriesItem.Id, cancellationToken);
                var finalAssignments = MediaTagBulkOperations.Apply(mode, currentAssignments, assignments);
                await tagStore.ReplaceAsync(MediaKind.Series, seriesItem.Id, finalAssignments, cancellationToken);
                updated++;
                // #1024: a rule can look for a tag the show now carries (or no longer carries).
                await autoTagging.ApplyAsync(MediaKind.Series, seriesItem.Id, AutoTagTrigger.Edited, null, null, cancellationToken);
                await realtimeEventPublisher.PublishEntityChangedAsync("Series", seriesItem.Id, cancellationToken);
            }

            return Results.Ok(new { updated, tags = normalizedTags, mode = mode.ToString().ToLowerInvariant() });
        });

        // One show's own rename preview (#997). Read-only, and it plans the
        // folder exactly as /bulk/rename-apply will.
        series.MapGet("/{id}/rename-preview", async (
            string id,
            HttpContext httpContext,
            ISeriesCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            ILibrariesRepository librariesRepository,
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

            var settings = await platformSettingsRepository.GetAsync(cancellationToken);
            var proposedName = NamingTemplateRenderer.RenderSegment(
                settings.SeriesFolderFormat,
                seriesItem.Title,
                seriesItem.StartYear,
                seriesItem.ImdbId,
                tvDbId: ReadMetadataText(seriesItem.MetadataJson, "TvDbId", "tvdbId", "tvdb_id"),
                network: ReadMetadataText(seriesItem.MetadataJson, "Network", "network"),
                genre: seriesItem.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                decision: new NamingDecision(OriginalTitle: seriesItem.OriginalTitle),
                replaceIllegalCharacters: settings.ReplaceIllegalCharacters,
                colonReplacementMode: settings.ColonReplacementMode);

            var episodeFiles = await repository.ListEpisodeFilePathsAsync(id, cancellationToken);
            var target = await ResolveOwnFolderAsync(repository, librariesRepository, id, episodeFiles, cancellationToken);
            if (!target.IsSafe)
            {
                return Results.Ok(RenamePreviewOutcome.Refused(target, proposedName));
            }

            var plan = SeriesFolderRelocationPlanner.Plan(
                episodeFiles.Select(file => file.FilePath).ToArray(),
                proposedName,
                target.Folder);
            return Results.Ok(RenamePreviewOutcome.Describe(
                plan is null ? null : Path.GetFileName(plan.SourceDirectory),
                proposedName,
                hasFolder: plan is not null,
                destinationExists: plan is not null
                    && !string.Equals(plan.SourceDirectory, plan.DestinationDirectory, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(plan.DestinationDirectory)));
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
                        genre: seriesItem.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                        decision: new NamingDecision(OriginalTitle: seriesItem.OriginalTitle),
                        replaceIllegalCharacters: settings.ReplaceIllegalCharacters,
                        colonReplacementMode: settings.ColonReplacementMode)
                });
            }

            return Results.Ok(new { count = previews.Count, previews });
        });

        /*
          The Movies twin of this endpoint renames one folder. A series has
          many episode files across however many seasons, so "the series
          folder" here is whichever folder every one of them sits under -
          found by SeriesFolderRelocationPlanner rather than assumed - and
          every affected episode's stored path is rewritten once that folder
          is renamed (#788).
        */
        series.MapPost("/bulk/rename-apply", async (
            HttpContext httpContext,
            [FromBody] BulkRenamePreviewRequest request,
            ISeriesCatalogRepository repository,
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

            if (request.SeriesIds is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seriesIds"] = ["Choose at least one series to rename."]
                });
            }

            var settings = await platformSettingsRepository.GetAsync(cancellationToken);
            var template = string.IsNullOrWhiteSpace(request.Template)
                ? settings.SeriesFolderFormat
                : request.Template.Trim();

            var renamed = new List<object>();
            var failures = new List<object>();
            foreach (var id in request.SeriesIds)
            {
                var seriesItem = await repository.GetByIdAsync(id, cancellationToken);
                if (seriesItem is null)
                {
                    failures.Add(new { seriesId = id, title = (string?)null, reason = "This series no longer exists." });
                    continue;
                }

                var episodeFiles = await repository.ListEpisodeFilePathsAsync(id, cancellationToken);
                if (episodeFiles.Count == 0)
                {
                    // Nothing on disk for this series - no folder to rename.
                    continue;
                }

                var proposedName = NamingTemplateRenderer.RenderSegment(
                    template,
                    seriesItem.Title,
                    seriesItem.StartYear,
                    seriesItem.ImdbId,
                    tvDbId: ReadMetadataText(seriesItem.MetadataJson, "TvDbId", "tvdbId", "tvdb_id"),
                    network: ReadMetadataText(seriesItem.MetadataJson, "Network", "network"),
                    genre: seriesItem.Genres?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                    decision: new NamingDecision(OriginalTitle: seriesItem.OriginalTitle),
                    replaceIllegalCharacters: settings.ReplaceIllegalCharacters,
                    colonReplacementMode: settings.ColonReplacementMode);

                // The same guard the title page's preview uses, re-run now at
                // apply time against the current rows (#1067).
                var target = await ResolveOwnFolderAsync(repository, librariesRepository, id, episodeFiles, cancellationToken);
                if (!target.IsSafe)
                {
                    failures.Add(new { seriesId = id, seriesItem.Title, reason = target.Message });
                    continue;
                }

                var plan = SeriesFolderRelocationPlanner.Plan(
                    episodeFiles.Select(file => file.FilePath).ToArray(),
                    proposedName,
                    target.Folder);
                if (plan is null)
                {
                    failures.Add(new { seriesId = id, seriesItem.Title, reason = "Its episode files do not share a folder Deluno can rename." });
                    continue;
                }

                if (string.Equals(plan.SourceDirectory, plan.DestinationDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    // Already named correctly - nothing to do, and not a failure.
                    continue;
                }

                if (Directory.Exists(plan.DestinationDirectory))
                {
                    failures.Add(new { seriesId = id, seriesItem.Title, reason = $"A folder named \"{proposedName}\" already exists." });
                    continue;
                }

                try
                {
                    Directory.Move(plan.SourceDirectory, plan.DestinationDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new { seriesId = id, seriesItem.Title, reason = "The folder could not be renamed on disk." });
                    continue;
                }

                try
                {
                    await repository.UpdateEpisodeFileLocationsAsync(
                        episodeFiles
                            .Select(file => (file.EpisodeId, file.FilePath,
                                Path.Combine(plan.DestinationDirectory, Path.GetRelativePath(plan.SourceDirectory, file.FilePath))))
                            .ToArray(),
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The folder has already been renamed but the database could not follow (the
                    // update is one transaction, so no episode row changed). Put the folder back, or
                    // say exactly where it is, record it, and carry on with the next show.
                    var putBack = FolderMoveRecovery.TryPutBack(plan.SourceDirectory, plan.DestinationDirectory);
                    var reason = FolderMoveRecovery.Describe(seriesItem.Title, plan.SourceDirectory, plan.DestinationDirectory, putBack, ex.Message);
                    failures.Add(new { seriesId = id, seriesItem.Title, reason });
                    await activityFeedRepository.RecordActivityAsync(
                        "library.series.rename-records-failed",
                        reason,
                        JsonSerializer.Serialize(new { seriesId = id, source = plan.SourceDirectory, destination = plan.DestinationDirectory, putBack }),
                        null,
                        "series",
                        id,
                        cancellationToken);
                    continue;
                }

                await realtimeEventPublisher.PublishEntityChangedAsync("Series", id, cancellationToken);
                renamed.Add(new
                {
                    seriesId = id,
                    seriesItem.Title,
                    from = Path.GetFileName(plan.SourceDirectory),
                    to = proposedName
                });
            }

            if (renamed.Count > 0 || failures.Count > 0)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "library.series.renamed",
                    failures.Count == 0
                        ? $"{renamed.Count} series folder{(renamed.Count == 1 ? "" : "s")} renamed."
                        : $"{renamed.Count} series folder{(renamed.Count == 1 ? "" : "s")} renamed; {failures.Count} could not be renamed.",
                    JsonSerializer.Serialize(new { renamed, failures }),
                    null,
                    "series",
                    null,
                    cancellationToken);
            }

            return Results.Ok(new { renamedCount = renamed.Count, renamed, failures });
        });

        series.MapPost("/{id}/grab", async (
            string id,
            [FromBody] ReleaseGrabRequest request,
            string? libraryId,
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
            IDownloadClientTelemetryService downloadClientTelemetry,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            // #1175: sending by hand what is already downloading asks first.
            var grabInventory = await repository.GetInventoryDetailAsync(id, cancellationToken);
            var activeDownloadCheck = grabInventory is null || string.IsNullOrWhiteSpace(request.ReleaseName)
                ? null
                : SeriesManualSendGuard.Build(
                    id,
                    grabInventory.Title,
                    [.. grabInventory.Episodes.Select(item => (item.EpisodeId, item.SeasonNumber, item.EpisodeNumber))],
                    request.ReleaseName,
                    downloadClientTelemetry);

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

        series.MapPost("/{id}/seasons/{seasonNumber:int}/search", async (
            string id,
            int seasonNumber,
            HttpContext httpContext,
            ISeasonSearchExecutor seasonSearchExecutor,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await seasonSearchExecutor.ExecuteAsync(
                id,
                seasonNumber,
                triggeredBy: "manual-season",
                activityClause: "was searched from the TV workspace.",
                cancellationToken);

            if (result.Status is SeasonSearchStatus.SeriesNotFound)
            {
                return Results.NotFound();
            }

            if (result.Status is SeasonSearchStatus.SeasonNotFound)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["seasonNumber"] = ["Deluno could not find that season in the tracked inventory."]
                });
            }

            return Results.Ok(new
            {
                outcome = result.Outcome,
                reason = result.Reason,
                seasonNumber = result.SeasonNumber,
                searchedEpisodes = result.SearchedEpisodes,
                installedEpisodeCount = result.InstalledEpisodeCount,
                missingEvidenceEpisodeCount = result.MissingEvidenceEpisodeCount,
                matchedCount = result.MatchedCount,
                queuedCount = result.QueuedCount,
                releaseName = result.ReleaseName,
                indexerName = result.IndexerName,
                dispatchStatus = result.DispatchStatus,
                dispatchMessage = result.DispatchMessage,
                replacementComparisons = result.ReplacementComparisons,
                failures = result.Failures
            });
        });

        // The series screen/drawer's own control for the "monitor new
        // episodes" preset (#801) - changeable at any time, not only at add
        // time, and applied the next time this series' episode catalogue is
        // synced (SyncEpisodeCatalogueAsync).
        series.MapPut("/{id}/monitor-new-episodes", async (
            string id,
            [FromBody] UpdateSeriesMonitorNewEpisodesRequest request,
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

            var updated = await repository.UpdateMonitorNewEpisodesAsync(id, request.MonitorNewEpisodes, cancellationToken);
            if (!updated) return Results.NotFound();

            await realtimeEventPublisher.PublishEntityChangedAsync("Series", id, cancellationToken);
            return Results.Ok(new { updated = true, monitorNewEpisodes = request.MonitorNewEpisodes });
        });

        series.MapPost("/", async (
            HttpContext httpContext,
            [FromBody] CreateSeriesRequest request,
            ISeriesCatalogRepository repository,
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
                LibraryMembership.Targets(libraries, "tv", request.LibraryId, []).Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["libraryId"] = ["Choose one of your TV libraries."] });
            }

            // #991: a person picking a monitor preset in the add-show dialog
            // sends it exactly as typed; normalize the same way an intake
            // list's own choice is normalized, so a stray case or an unknown
            // value falls back to "All" (today's fully-monitored behaviour)
            // instead of silently monitoring nothing.
            if (!string.IsNullOrWhiteSpace(request.ListMonitorMode))
            {
                var normalizedMonitorMode = IntakeMonitorModes.Normalize("tv", request.ListMonitorMode);
                request = request with
                {
                    ListMonitorMode = normalizedMonitorMode == IntakeMonitorModes.All ? null : normalizedMonitorMode
                };
            }

            // The profile picked in the add dialog (#1145) must be one made for TV.
            QualityProfileItem? chosenProfile = null;
            if (!string.IsNullOrWhiteSpace(request.QualityProfileId))
            {
                chosenProfile = (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
                    .FirstOrDefault(profile => string.Equals(profile.Id, request.QualityProfileId.Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(profile.MediaType, "tv", StringComparison.OrdinalIgnoreCase));
                if (chosenProfile is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["qualityProfileId"] = ["Choose one of your TV quality profiles."] });
                }
            }

            var requestStartedUtc = DateTimeOffset.UtcNow;
            var item = await repository.AddAsync(request, cancellationToken);
            if (chosenProfile is not null)
            {
                await repository.UpdateQualityProfileAsync(item.Id, chosenProfile.Id, cancellationToken);
            }

            // The show's own profile, if it has one, judges it from the first search (#1157).
            var ownProfile = chosenProfile ?? await new TitleProfileLookup(mediaState, qualityRepository)
                .ForTitleAsync(MediaKind.Series, item.Id, cancellationToken);
            // #1038: the tags picked in the add dialog go on the title now, before
            // any routing, search or the "added" notification reads them. A title
            // the catalogue already held keeps its tags and gains these.
            var addedTags = TagNameList.Split(string.Join(',', request.Tags ?? []));
            if (addedTags.Count > 0)
            {
                await titleTagService.GiveAsync(MediaKind.Series, item.Id, addedTags, cancellationToken);
            }
            // Only the library the person chose, or the one it is already in (#530).
            var already = (await mediaState.ListWantedByIdsAsync(MediaKind.Series, [item.Id], cancellationToken))
                .Select(row => row.LibraryId)
                .ToArray();
            // Carried into the catalogue refresh below rather than enqueued
            // here directly (#646 follow-up). The refresh job learns this
            // show's episode list; a search enqueued in this same request runs
            // whenever its own worker picks it up, which is often *before*
            // the refresh — a targeted show-level search with zero episodes
            // known yet finds nothing to plan and never runs again until the
            // library's own next scheduled cycle. Handing the request to the
            // refresh handler instead means it only runs once the episodes it
            // needs are on record.
            var pendingSearches = new List<object>();
            foreach (var library in LibraryMembership.Targets(libraries, "tv", request.LibraryId, already))
            {
                // #1161: a show whose folder is already in this library is
                // matched to the episodes on disk before anything decides they
                // are missing, so adding a removed show back never downloads
                // an episode Deluno is already holding.
                if (!item.HasFile && services.GetService<IExistingLibraryImportService>() is { } folderImport)
                {
                    var found = await folderImport.ImportTitleFolderAsync(library.Id, item.Id, item.Title, item.StartYear, cancellationToken);
                    if (found.Matched && await repository.GetByIdAsync(item.Id, cancellationToken) is { } matched)
                    {
                        item = matched;
                        await activityFeed.RecordActivityAsync(
                            "series.file-matched",
                            $"{item.Title} was added and Deluno found its episodes already in {library.Name}, so it matched those files and did not search or download them again.",
                            JsonSerializer.Serialize(new { libraryId = library.Id, path = found.Path }),
                            null, "series", item.Id, cancellationToken);
                    }
                }

                // The show's own state, not a fresh install's.
                //
                // AddAsync dedupes: adding a title the catalogue already holds
                // returns the row it already has. Passing false here then went
                // on to overwrite it — EnsureWantedStateAsync upserts with
                // `has_file = excluded.has_file` — so re-adding a show you
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
                    HasFile: item.HasFile,
                    CurrentQuality: item.CurrentQuality,
                    CutoffQuality: judge.CutoffQuality,
                    UpgradeUntilCutoff: judge.UpgradeUntilCutoff,
                    UpgradeUnknownItems: judge.UpgradeUnknownItems));

                await repository.EnsureWantedStateAsync(
                    item.Id,
                    library.Id,
                    decision.WantedStatus,
                    decision.WantedReason,
                    item.HasFile,
                    decision.CurrentQuality,
                    decision.TargetQuality,
                    decision.QualityCutoffMet,
                    cancellationToken);

                // Whether this add searches at once is one explicit choice on the
                // add dialog (#1145, which settled #645 and #793): "Search for it as
                // soon as it is added". Monitoring alone no longer starts a search -
                // with the choice off, the next scheduled search finds the show.
                // Targeted at this one show, the same shape as a recovery retry or an
                // external redownload: real work for one id, not a fresh sweep.
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
                        triggeredBy = "series-added",
                        targetEntityId = item.Id
                    });
                }
            }

            // #1024: auto-tagging rules run now, before the "added" notification and before
            // anything routes by tag. The libraries are passed because the wanted state
            // that would say where the show is going is only just written.
            var isNewTitle = item.CreatedUtc >= requestStartedUtc;
            await autoTagging.ApplyAsync(
                MediaKind.Series,
                item.Id,
                isNewTitle ? AutoTagTrigger.Added : AutoTagTrigger.Edited,
                isNewTitle ? new AutoTagAddFacts(false, false, null) : null,
                LibraryMembership.Targets(libraries, "tv", request.LibraryId, already).Select(library => library.Id).ToArray(),
                cancellationToken);

            await jobScheduler.EnqueueAsync(
                new EnqueueJobRequest(
                    JobType: "series.catalog.refresh",
                    Source: "series",
                    PayloadJson: JsonSerializer.Serialize(new
                    {
                        item.Id,
                        item.Title,
                        item.ImdbId,
                        pendingSearches
                    }),
                    RelatedEntityType: "series",
                    RelatedEntityId: item.Id),
                cancellationToken);
            await realtimeEventPublisher.PublishEntityChangedAsync("Series", item.Id, cancellationToken);

            // On Title Added (#856): AddAsync dedupes, so a title already in
            // the catalogue returns its existing row with its original
            // CreatedUtc -- only a row created within this request is a
            // genuinely new title.
            if (item.CreatedUtc >= requestStartedUtc &&
                (await notificationPreferences.GetPreferencesAsync(cancellationToken)).AddedEnabled)
            {
                await notificationService.DispatchAsync(
                    "title.added",
                    $"Added: {item.Title}",
                    $"{item.Title} was added to the library.",
                    JsonSerializer.Serialize(new { item.Id, item.Title, mediaType = "tv" }),
                    cancellationToken,
                    titleTags: await titleTagService.ListNamesAsync(MediaKind.Series, item.Id, cancellationToken));
            }

            return Results.Created($"/api/series/{item.Id}", item);
        });

        return endpoints;
    }

    /// <summary>Blank means "no override", so it is stored as null rather than kept.</summary>
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
        IPlatformSettingsRepository platformSettings,
        CancellationToken cancellationToken)
        => MediaSearchHandler.ExecuteAsync(
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
            (seriesId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token) =>
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
                    token),
            cancellationToken,
            tagStore,
            releasePreferencePlanRepository,
            libraryId,
            platformSettings,
            async token => (await repository.GetInventoryDetailAsync(id, token))?.EpisodeTitlesBySeason());

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
        CancellationToken cancellationToken,
        bool deliberateRelink = false)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return SeriesCatalogueSyncResult.None;
        }

        IReadOnlyList<MetadataSeason> seasons;
        try
        {
            var inventory = await repository.GetInventoryDetailAsync(seriesId, cancellationToken);
            var choice = await MetadataCatalogueChoice.ChooseAsync(
                metadataProvider,
                providerId,
                SeriesCatalogueChoice.ExistingEpisodes(inventory),
                cancellationToken,
                deliberateRelink);
            if (choice.Hold != MetadataCatalogueHold.None)
            {
                // The show's numbering came from TheTVDB. Its episodes stay as
                // they are and the next refresh tries TheTVDB again.
                await SeriesCatalogueChoice.RecordHoldAsync(
                    activityFeedRepository, null, choice.Hold, seriesId, seriesTitle, null, cancellationToken);
                return SeriesCatalogueSyncResult.None;
            }

            seasons = choice.Seasons;
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
                episode.AirDateUtc,
                episode.AbsoluteNumber))
            .ToArray();

        if (episodes.Length == 0)
        {
            return SeriesCatalogueSyncResult.None;
        }

        var result = await repository.SyncEpisodeCatalogueAsync(
            seriesId,
            episodes,
            MetadataCatalogueChoice.SourceName(seasons),
            cancellationToken);

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
            errors["numberingScheme"] = ["Numbering scheme must be standard, airdate, absolute, dvd, or scene."];
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
            errors["numberingScheme"] = ["Numbering scheme must be standard, airdate, absolute, dvd, or scene."];
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

    /// <summary>
    /// The one place a show's own folder is decided for "rename folder", used
    /// by the single-show preview and by rename-apply so they cannot disagree
    /// (#1067). Never renames anything.
    /// </summary>
    private static async Task<TitleFolderTarget> ResolveOwnFolderAsync(
        ISeriesCatalogRepository repository,
        ILibrariesRepository librariesRepository,
        string seriesId,
        IReadOnlyList<(string EpisodeId, string FilePath)> episodeFiles,
        CancellationToken cancellationToken)
    {
        var roots = (await librariesRepository.ListLibrariesAsync(cancellationToken))
            .Select(library => library.RootPath)
            .ToArray();
        return await TitleFolderGuard.ForSeriesAsync(
            episodeFiles.Select(file => file.FilePath).ToArray(),
            roots,
            (folder, ct) => repository.OtherSeriesHaveFilesUnderAsync(seriesId, folder, ct),
            cancellationToken);
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
            return new SeriesMetadataLinkPlan(lookup.Status, null, null, lookup.Provider, lookup.Failure);
        }

        IReadOnlyList<MetadataSeason> seasons;
        var tvdbDisagrees = false;
        try
        {
            var existing = await repository.GetInventoryDetailAsync(item.Id, cancellationToken);
            // A person choosing this record on purpose (often to correct a wrong
            // TheTVDB link) gets the preview of what changes; only "TheTVDB did not
            // answer" holds it, because then there is no list to preview.
            var choice = await MetadataCatalogueChoice.ChooseAsync(
                metadataProvider,
                lookup.Result.ProviderId,
                SeriesCatalogueChoice.ExistingEpisodes(existing),
                cancellationToken,
                deliberateRelink: true);
            tvdbDisagrees = choice.TvdbDisagreesButChosen;
            if (choice.Hold != MetadataCatalogueHold.None)
            {
                return new SeriesMetadataLinkPlan(
                    MetadataProviderRecordStatus.Unavailable,
                    null,
                    null,
                    lookup.Provider,
                    IntegrationFailureFactory.FromLegacy(
                        "metadata",
                        lookup.Provider,
                        lookup.Provider,
                        "metadata.provider.catalogue",
                        "connectivity",
                        SeriesCatalogueChoice.HoldMessage(choice.Hold, item.Title)));
            }

            seasons = choice.Seasons;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new SeriesMetadataLinkPlan(
                MetadataProviderRecordStatus.Unavailable,
                null,
                null,
                lookup.Provider,
                // The catalogue call threw rather than returning a typed
                // failure, so name the operation that actually failed instead
                // of implying the record lookup did.
                IntegrationFailureFactory.FromLegacy(
                    "metadata",
                    lookup.Provider,
                    lookup.Provider,
                    "metadata.provider.catalogue",
                    "connectivity",
                    "The episode catalogue could not be read."));
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
        if (tvdbDisagrees)
        {
            consequences.Add("This show's episodes are numbered by TheTVDB, and the selected record's episode list does not match them. Applying it switches the show to the selected record's episode list; any episode Deluno already has that the list lacks is counted below.");
        }
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
        return new SeriesMetadataLinkPlan(lookup.Status, match, preview, lookup.Provider, lookup.Failure);
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


    private sealed record UpdateSeriesMonitorNewEpisodesRequest(bool MonitorNewEpisodes);

    private sealed record SetSubtitleLanguageOverrideRequest(IReadOnlyList<string>? Languages, string? Mode);

    private sealed record PickSubtitleCandidateRequest(string? ProviderKey, string? DownloadToken);

    private sealed record BlacklistSubtitleCandidateRequest(string? ProviderKey, string? DownloadToken, string? ReleaseName, string? Reason);

    /// <summary>
    /// Everything a subtitle search for one episode needs and a path alone
    /// cannot say — the show it belongs to, for its title/year/release group/
    /// imdb id, and the season/episode number and episode title, read off the
    /// series inventory the same way <c>LibrarySubtitleSearchJobHandler</c>
    /// reads them off <c>ListWantedAsync</c>'s own item (#841).
    /// </summary>
    private sealed record EpisodeSearchContext(SeriesListItem Show, SeriesEpisodeInventoryItem Episode, string? FilePath);

    private static async Task<EpisodeSearchContext?> ResolveEpisodeSearchContextAsync(
        ISeriesCatalogRepository repository, string episodeId, CancellationToken cancellationToken)
    {
        var seriesId = await repository.GetEpisodeSeriesIdAsync(episodeId, cancellationToken);
        if (seriesId is null)
        {
            return null;
        }

        var show = await repository.GetByIdAsync(seriesId, cancellationToken);
        if (show is null)
        {
            return null;
        }

        var inventory = await repository.GetInventoryDetailAsync(seriesId, cancellationToken);
        var episode = inventory?.Episodes.FirstOrDefault(item => item.EpisodeId == episodeId);
        if (episode is null)
        {
            return null;
        }

        var filePath = await repository.GetEpisodeFilePathAsync(episodeId, cancellationToken);
        return new EpisodeSearchContext(show, episode, filePath);
    }

    private sealed record BulkQualityProfileRequest(
        IReadOnlyList<string>? SeriesIds,
        string? QualityProfileId);

    private sealed record BulkReassignLibraryRequest(
        IReadOnlyList<string>? SeriesIds,
        string? FromLibraryId,
        string? ToLibraryId,
        bool MoveFiles = false);

    private sealed record BulkTagsRequest(
        IReadOnlyList<string>? SeriesIds,
        string? Tags,
        string? Mode = null);

    private sealed record BulkRenamePreviewRequest(
        IReadOnlyList<string>? SeriesIds,
        string? Template);

    private sealed record BulkSearchRequest(IReadOnlyList<string>? SeriesIds);

}
