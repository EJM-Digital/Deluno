using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.Search;
using Deluno.Integrations.Metadata;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
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
            CreateMovieImportRecoveryCaseRequest request,
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
            UpdateMovieMonitoringRequest request,
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
            IMediaSearchPlanner mediaSearchPlanner,
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
            var configuredSources = routing?.Sources.Count ?? 0;
            var configuredClients = routing?.DownloadClients.Count ?? 0;
            var now = timeProvider.GetUtcNow();
            var customFormats = await ResolveCustomFormatsAsync(platformSettingsRepository, library.QualityProfileId, cancellationToken);

            var searchPlan = configuredSources == 0 || configuredClients == 0
                ? new MediaSearchPlan(
                    null,
                    [],
                    configuredSources == 0
                        ? "No indexers are linked to this library yet."
                        : "No download client is linked to this library yet.")
                : await mediaSearchPlanner.BuildPlanAsync(
                    movie.Title,
                    movie.ReleaseYear,
                    "movies",
                    wantedItem.CurrentQuality,
                    wantedItem.TargetQuality,
                    routing!.Sources,
                    customFormats);

            var outcome = configuredSources == 0 || configuredClients == 0
                ? "blocked"
                : searchPlan.BestCandidate is null
                    ? "checked"
                    : "matched";
            DownloadClientGrabResult? grabResult = null;

            if (outcome == "matched" && !string.Equals(mode, "preview", StringComparison.OrdinalIgnoreCase))
            {
                var downloadClient = routing!.DownloadClients.OrderBy(item => item.Priority).First();
                var category = library.MediaType == "tv" ? "tv" : "movies";
                grabResult = searchPlan.BestCandidate!.DownloadUrl is null
                    ? new DownloadClientGrabResult(downloadClient.DownloadClientId, searchPlan.BestCandidate.ReleaseName, false, "planned", "No download URL was available.")
                    : await downloadClientGrabService.GrabAsync(
                        downloadClient.DownloadClientId,
                        new DownloadClientGrabRequest(
                            searchPlan.BestCandidate.ReleaseName,
                            searchPlan.BestCandidate.DownloadUrl,
                            "movies",
                            category,
                            searchPlan.BestCandidate.IndexerName),
                        cancellationToken);
                await jobQueueRepository.RecordDownloadDispatchAsync(
                    library.Id,
                    "movies",
                    "movie",
                    movie.Id,
                    searchPlan.BestCandidate!.ReleaseName,
                    searchPlan.BestCandidate.IndexerName,
                    downloadClient.DownloadClientId,
                    downloadClient.DownloadClientName,
                    grabResult.Status,
                    JsonSerializer.Serialize(new { searchPlan, grabResult }),
                    cancellationToken);
            }

            await repository.RecordSearchAttemptAsync(
                movie.Id,
                library.Id,
                "manual",
                outcome,
                now,
                now.AddHours(Math.Max(1, library.RetryDelayHours)),
                BuildSearchResult(searchPlan, configuredClients),
                searchPlan.BestCandidate?.ReleaseName,
                searchPlan.BestCandidate?.IndexerName,
                searchPlan.Candidates.Count == 0 ? null : JsonSerializer.Serialize(searchPlan),
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
                releaseName = searchPlan.BestCandidate?.ReleaseName,
                indexerName = searchPlan.BestCandidate?.IndexerName,
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
                    candidate.Seeders
                }).ToArray()
            });
        });

        movies.MapPost("/{id}/grab", async (
            string id,
            ReleaseGrabRequest request,
            HttpContext httpContext,
            IMovieCatalogRepository repository,
            IPlatformSettingsRepository platformSettingsRepository,
            IJobQueueRepository jobQueueRepository,
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
            var grabResult = await downloadClientGrabService.GrabAsync(
                downloadClient.DownloadClientId,
                new DownloadClientGrabRequest(
                    request.ReleaseName.Trim(),
                    request.DownloadUrl!.Trim(),
                    "movies",
                    category,
                    request.IndexerName?.Trim()),
                cancellationToken);

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
                JsonSerializer.Serialize(new { selectedRelease = request, grabResult }),
                cancellationToken);

            var now = timeProvider.GetUtcNow();
            await repository.RecordSearchAttemptAsync(
                movie.Id,
                library.Id,
                "manual-grab",
                grabResult.Status == "sent" ? "matched" : "checked",
                now,
                now.AddHours(Math.Max(1, library.RetryDelayHours)),
                grabResult.Message,
                request.ReleaseName.Trim(),
                request.IndexerName?.Trim(),
                JsonSerializer.Serialize(new { selectedRelease = request, grabResult }),
                cancellationToken);

            await activityFeedRepository.RecordActivityAsync(
                "movie.release.grabbed",
                $"{movie.Title} release was manually selected and sent to {downloadClient.DownloadClientName}.",
                JsonSerializer.Serialize(new { selectedRelease = request, grabResult }),
                null,
                "movie",
                movie.Id,
                cancellationToken);

            return Results.Ok(new
            {
                releaseName = request.ReleaseName.Trim(),
                indexerName = request.IndexerName?.Trim(),
                dispatchStatus = grabResult.Status,
                dispatchMessage = grabResult.Message
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
            MetadataRefreshJobsRequest request,
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
            CreateMovieRequest request,
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

            var errors = Validate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var movie = await repository.AddAsync(request, cancellationToken);
            var libraries = await platformSettingsRepository.ListLibrariesAsync(cancellationToken);
            foreach (var library in libraries.Where(item => item.MediaType == "movies"))
            {
                var decision = LibraryQualityDecider.Decide(
                    mediaLabel: "movie",
                    hasFile: false,
                    currentQuality: null,
                    cutoffQuality: library.CutoffQuality,
                    upgradeUntilCutoff: library.UpgradeUntilCutoff,
                    upgradeUnknownItems: library.UpgradeUnknownItems);

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

    private static string BuildSearchResult(MediaSearchPlan plan, int configuredClients)
    {
        if (plan.BestCandidate is null)
        {
            return plan.Summary;
        }

        return $"{plan.Summary} Ready to send to {configuredClients} download client{(configuredClients == 1 ? "" : "s")}.";
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
        string? IndexerName,
        string? DownloadUrl);

    private sealed record MetadataRefreshJobsRequest(
        bool ForceAll,
        int? Take);

    private sealed record MetadataRefreshJobsResponse(
        int EnqueuedCount,
        IReadOnlyList<Deluno.Jobs.Contracts.JobQueueItem> Jobs);
}
