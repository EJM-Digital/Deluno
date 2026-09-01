using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deluno.Api.Monitoring;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Realtime;
using Deluno.Security;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Host.Automation;

public static class AutomationEndpointRouteBuilderExtensions
{
    private const string BulkCatalogueOperation = "catalogue.bulk-add";
    private const string BulkEpisodeOperation = "series.episodes.bulk-sync";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapDelunoAutomationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var automation = endpoints.MapGroup("/api/automation");

        automation.MapPost("/catalogue/bulk", async (
            HttpContext httpContext,
            [FromBody] BulkCatalogueAddRequest request,
            IMovieCatalogRepository movieRepository,
            ISeriesCatalogRepository seriesRepository,
            ILibrariesRepository librariesRepository,
            IMediaDecisionService mediaDecisionService,
            IJobScheduler jobScheduler,
            IRealtimeEventPublisher realtimeEventPublisher,
            IAutomationIdempotencyStore idempotencyStore,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var keyResult = ResolveIdempotencyKey(httpContext, request.IdempotencyKey);
            if (keyResult.Error is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["idempotencyKey"] = [keyResult.Error]
                });
            }

            var envelopeErrors = ValidateEnvelope(request.Items, keyResult.Key);
            if (envelopeErrors.Count > 0)
            {
                return Results.ValidationProblem(envelopeErrors);
            }

            // The idempotency key identifies this operation; it is transport
            // metadata, not part of the operation body. Hashing it made a
            // retry conflict when the first call supplied the key in JSON and
            // the second supplied the same key only as the standard header.
            var requestHash = ComputeRequestHash(request with { IdempotencyKey = null });
            var replay = await TryReplayAsync(
                idempotencyStore,
                keyResult.Key,
                BulkCatalogueOperation,
                requestHash,
                cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var results = new List<BulkCatalogueItemResult>(request.Items!.Count);

            for (var index = 0; index < request.Items.Count; index++)
            {
                var item = request.Items[index];
                var clientItemId = ClientItemId(item.ClientItemId, index);
                var mediaType = NormalizeMediaType(item.MediaType);
                var itemErrors = ValidateItem(item, mediaType, libraries);
                if (itemErrors.Count > 0)
                {
                    results.Add(new BulkCatalogueItemResult(
                        clientItemId,
                        mediaType ?? "unknown",
                        item.Title?.Trim(),
                        "invalid",
                        Error: string.Join(" ", itemErrors)));
                    continue;
                }

                try
                {
                    var title = item.Title!.Trim();
                    if (mediaType == "movies")
                    {
                        var existingId = await movieRepository.FindExistingIdAsync(
                            title,
                            item.Year,
                            item.ImdbId,
                            item.MetadataProvider,
                            item.MetadataProviderId,
                            cancellationToken);

                        if (request.DryRun)
                        {
                            results.Add(new BulkCatalogueItemResult(
                                clientItemId,
                                mediaType,
                                title,
                                existingId is null ? "would-create" : "already-exists",
                                EntityId: existingId,
                                EpisodeCount: 0));
                            continue;
                        }

                        var movie = await movieRepository.AddAsync(
                            new CreateMovieRequest(
                                title,
                                item.Year,
                                item.ImdbId,
                                item.Monitored,
                                item.MetadataProvider,
                                item.MetadataProviderId,
                                item.OriginalTitle,
                                item.Overview,
                                item.PosterUrl,
                                item.BackdropUrl,
                                item.Rating,
                                item.Genres,
                                item.ExternalUrl,
                                item.MetadataJson),
                            cancellationToken);
                        await EnsureMovieWantedStateAsync(
                            movie.Id,
                            item,
                            libraries,
                            movieRepository,
                            mediaDecisionService,
                            cancellationToken);

                        string? refreshJobId = null;
                        if (existingId is null)
                        {
                            var refreshJob = await EnqueueRefreshAsync(
                                jobScheduler,
                                "movies.catalog.refresh",
                                "movie",
                                movie.Id,
                                movie.Title,
                                movie.ImdbId,
                                ItemIdempotencyKey(keyResult.Key, clientItemId),
                                cancellationToken);
                            refreshJobId = refreshJob.Id;
                        }

                        await realtimeEventPublisher.PublishEntityChangedAsync("Movie", movie.Id, cancellationToken);
                        results.Add(new BulkCatalogueItemResult(
                            clientItemId,
                            mediaType,
                            movie.Title,
                            existingId is null ? "created" : "already-exists",
                            EntityId: movie.Id,
                            RefreshJobId: refreshJobId));
                    }
                    else
                    {
                        var existingId = await seriesRepository.FindExistingIdAsync(
                            title,
                            item.Year,
                            item.ImdbId,
                            item.MetadataProvider,
                            item.MetadataProviderId,
                            cancellationToken);

                        if (request.DryRun)
                        {
                            results.Add(new BulkCatalogueItemResult(
                                clientItemId,
                                mediaType!,
                                title,
                                existingId is null ? "would-create" : "already-exists",
                                EntityId: existingId,
                                EpisodeCount: item.Episodes?.Count ?? 0));
                            continue;
                        }

                        var series = await seriesRepository.AddAsync(
                            new CreateSeriesRequest(
                                title,
                                item.Year,
                                item.ImdbId,
                                item.Monitored,
                                item.MetadataProvider,
                                item.MetadataProviderId,
                                item.OriginalTitle,
                                item.Overview,
                                item.PosterUrl,
                                item.BackdropUrl,
                                item.Rating,
                                item.Genres,
                                item.ExternalUrl,
                                item.MetadataJson,
                                SeriesType: item.SeriesType,
                                NumberingScheme: item.NumberingScheme,
                                NumberingSource: item.NumberingSource),
                            cancellationToken);
                        if (item.SeriesType is not null ||
                            item.NumberingScheme is not null ||
                            item.NumberingSource is not null)
                        {
                            await seriesRepository.UpdateNumberingAsync(
                                series.Id,
                                new UpdateSeriesNumberingRequest(
                                    item.SeriesType,
                                    item.NumberingScheme,
                                    item.NumberingSource),
                                cancellationToken);
                        }
                        await EnsureSeriesWantedStateAsync(
                            series.Id,
                            item,
                            libraries,
                            seriesRepository,
                            mediaDecisionService,
                            cancellationToken);

                        var episodesAdded = 0;
                        var episodesUpdated = 0;
                        if (item.Episodes is { Count: > 0 })
                        {
                            var sync = await seriesRepository.SyncEpisodeCatalogueAsync(
                                series.Id,
                                item.Episodes.Select(episode => new CatalogueEpisodeItem(
                                    episode.SeasonNumber,
                                    episode.EpisodeNumber,
                                    episode.Title,
                                    episode.Overview,
                                    episode.AirDateUtc,
                                    episode.AbsoluteNumber,
                                    episode.SceneSeasonNumber,
                                    episode.SceneEpisodeNumber,
                                    episode.NumberingSource)).ToArray(),
                                "automation",
                                cancellationToken);
                            episodesAdded = sync.AddedCount;
                            episodesUpdated = sync.UpdatedCount;
                        }

                        string? refreshJobId = null;
                        if (existingId is null)
                        {
                            var refreshJob = await EnqueueRefreshAsync(
                                jobScheduler,
                                "series.catalog.refresh",
                                "series",
                                series.Id,
                                series.Title,
                                series.ImdbId,
                                ItemIdempotencyKey(keyResult.Key, clientItemId),
                                cancellationToken);
                            refreshJobId = refreshJob.Id;
                        }

                        await realtimeEventPublisher.PublishEntityChangedAsync("Series", series.Id, cancellationToken);
                        results.Add(new BulkCatalogueItemResult(
                            clientItemId,
                            mediaType!,
                            series.Title,
                            existingId is null ? "created" : "already-exists",
                            EntityId: series.Id,
                            EpisodeCount: item.Episodes?.Count ?? 0,
                            EpisodesAdded: episodesAdded,
                            EpisodesUpdated: episodesUpdated,
                            RefreshJobId: refreshJobId));
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    results.Add(new BulkCatalogueItemResult(
                        clientItemId,
                        mediaType!,
                        item.Title?.Trim(),
                        "failed",
                        Error: exception.Message));
                }
            }

            var response = BuildResponse(request.DryRun, keyResult.Key, results);
            return await PersistAndReturnAsync(
                idempotencyStore,
                keyResult.Key,
                BulkCatalogueOperation,
                requestHash,
                response,
                cancellationToken);
        });

        automation.MapPost("/series/{seriesId}/episodes/bulk", async (
            string seriesId,
            HttpContext httpContext,
            [FromBody] BulkSeriesEpisodeRequest request,
            ISeriesCatalogRepository seriesRepository,
            IAutomationIdempotencyStore idempotencyStore,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var keyResult = ResolveIdempotencyKey(httpContext, request.IdempotencyKey);
            if (keyResult.Error is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["idempotencyKey"] = [keyResult.Error]
                });
            }

            var envelopeErrors = ValidateEpisodeEnvelope(seriesId, request.Episodes, keyResult.Key);
            if (envelopeErrors.Count > 0)
            {
                return Results.ValidationProblem(envelopeErrors);
            }

            var requestHash = ComputeRequestHash(new
            {
                seriesId,
                request = request with { IdempotencyKey = null }
            });
            var replay = await TryReplayAsync(
                idempotencyStore,
                keyResult.Key,
                BulkEpisodeOperation,
                requestHash,
                cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            var series = await seriesRepository.GetByIdAsync(seriesId, cancellationToken);
            if (series is null)
            {
                return Results.NotFound(new { message = "The requested TV show does not exist in Deluno." });
            }

            var validItems = new List<(BulkSeriesEpisodeItem Item, string ClientItemId)>();
            var itemResults = new List<BulkSeriesEpisodeItemResult>(request.Episodes!.Count);
            for (var index = 0; index < request.Episodes.Count; index++)
            {
                var item = request.Episodes[index];
                var clientItemId = ClientItemId(item.ClientItemId, index);
                var errors = ValidateEpisodeItem(item);
                if (errors.Count > 0)
                {
                    itemResults.Add(new BulkSeriesEpisodeItemResult(
                        clientItemId,
                        item.SeasonNumber,
                        item.EpisodeNumber,
                        "invalid",
                        string.Join(" ", errors)));
                }
                else
                {
                    validItems.Add((item, clientItemId));
                }
            }

            var added = 0;
            var updated = 0;
            string? syncError = null;
            if (validItems.Count > 0 && !request.DryRun)
            {
                try
                {
                    var sync = await seriesRepository.SyncEpisodeCatalogueAsync(
                        seriesId,
                        validItems.Select(entry => new CatalogueEpisodeItem(
                            entry.Item.SeasonNumber,
                            entry.Item.EpisodeNumber,
                            entry.Item.Title,
                            entry.Item.Overview,
                            entry.Item.AirDateUtc,
                            entry.Item.AbsoluteNumber,
                            entry.Item.SceneSeasonNumber,
                            entry.Item.SceneEpisodeNumber,
                            entry.Item.NumberingSource)).ToArray(),
                        "automation",
                        cancellationToken);
                    added = sync.AddedCount;
                    updated = sync.UpdatedCount;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The repository applies this batch atomically. Returning
                    // one failed result per valid input keeps the automation
                    // contract honest and lets callers retry the same key
                    // without mistaking an exception for a successful sync.
                    syncError = exception.Message;
                }
            }

            itemResults.AddRange(validItems.Select(entry => new BulkSeriesEpisodeItemResult(
                entry.ClientItemId,
                entry.Item.SeasonNumber,
                entry.Item.EpisodeNumber,
                request.DryRun ? "would-sync" : syncError is null ? "synced" : "failed",
                syncError)));
            itemResults = itemResults
                .OrderBy(result => result.ClientItemId, StringComparer.Ordinal)
                .ToList();

            var response = new BulkSeriesEpisodeResponse(
                request.DryRun,
                keyResult.Key,
                seriesId.Trim(),
                request.Episodes.Count,
                itemResults.Count(result => result.Status is "synced" or "would-sync"),
                itemResults.Count(result => result.Status == "invalid"),
                itemResults.Count(result => result.Status == "failed"),
                added,
                updated,
                itemResults);
            return await PersistAndReturnAsync(
                idempotencyStore,
                keyResult.Key,
                BulkEpisodeOperation,
                requestHash,
                response,
                cancellationToken);
        });

        return endpoints;
    }

    /// <summary>
    /// Read-only summary for dashboards and Home Assistant. It deliberately
    /// selects stable operational facts instead of making an integration scrape
    /// the web UI.
    /// </summary>
    public static IEndpointRouteBuilder MapDelunoAutomationReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/automation/summary", async (
            IMonitoringService monitoringService,
            ILibrariesRepository librariesRepository,
            IExistingLibraryImportService importService,
            CancellationToken cancellationToken) =>
        {
            var dashboard = await monitoringService.GetDashboardAsync(cancellationToken);
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var progress = new List<(LibraryImportRunProgress Run, int Issues)>();
            foreach (var library in libraries)
            {
                var current = await importService.GetProgressAsync(library.Id, cancellationToken);
                if (current is null)
                {
                    continue;
                }

                var issues = await importService.ListIssuesAsync(library.Id, 500, cancellationToken);
                progress.Add((current, issues.Count));
            }

            var importSummary = new AutomationImportSummary(
                Active: progress.Count(item => LibraryImportRunStatuses.IsActive(item.Run.Run.Status)),
                Failed: progress.Count(item => string.Equals(item.Run.Run.Status, LibraryImportRunStatuses.Failed, StringComparison.OrdinalIgnoreCase)),
                Completed: progress.Count(item => string.Equals(item.Run.Run.Status, LibraryImportRunStatuses.Completed, StringComparison.OrdinalIgnoreCase)),
                Issues: progress.Sum(item => item.Issues));

            var attention = dashboard.Alerts
                .Select(alert => new AutomationAttentionItem(
                    alert.Code,
                    alert.Severity,
                    alert.Summary,
                    alert.Details,
                    alert.DetectedUtc))
                .ToList();
            attention.AddRange(progress
                .Where(item => string.Equals(item.Run.Run.Status, LibraryImportRunStatuses.Failed, StringComparison.OrdinalIgnoreCase))
                .Select(item => new AutomationAttentionItem(
                    "library-import.failed",
                    "error",
                    $"Existing-library import failed for {item.Run.Run.LibraryName}.",
                    item.Run.Run.LastError ?? "The import run needs review.",
                    item.Run.Run.UpdatedUtc)));

            return Results.Ok(new AutomationSummaryResponse(
                dashboard.GeneratedUtc,
                new AutomationReadinessSummary(
                    dashboard.Readiness.Status,
                    dashboard.Readiness.Ready,
                    dashboard.Readiness.FailedChecks),
                new AutomationQueueSummary(
                    dashboard.Services.ActiveJobs,
                    dashboard.Services.QueuedJobs,
                    dashboard.Services.FailedJobs,
                    dashboard.Services.OpenDispatchAlerts),
                importSummary,
                attention));
        });

        return endpoints;
    }

    private static async Task EnsureMovieWantedStateAsync(
        string movieId,
        BulkCatalogueAddItem item,
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> libraries,
        IMovieCatalogRepository repository,
        IMediaDecisionService mediaDecisionService,
        CancellationToken cancellationToken)
    {
        foreach (var library in TargetLibraries(item, libraries, "movies"))
        {
            var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                library.MediaType,
                HasFile: false,
                CurrentQuality: null,
                library.CutoffQuality,
                library.UpgradeUntilCutoff,
                library.UpgradeUnknownItems,
                item.IsReleased));
            await repository.EnsureWantedStateAsync(
                movieId,
                library.Id,
                decision.WantedStatus,
                decision.WantedReason,
                false,
                decision.CurrentQuality,
                decision.TargetQuality,
                decision.QualityCutoffMet,
                cancellationToken);
        }
    }

    private static async Task EnsureSeriesWantedStateAsync(
        string seriesId,
        BulkCatalogueAddItem item,
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> libraries,
        ISeriesCatalogRepository repository,
        IMediaDecisionService mediaDecisionService,
        CancellationToken cancellationToken)
    {
        foreach (var library in TargetLibraries(item, libraries, "tv"))
        {
            var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                library.MediaType,
                HasFile: false,
                CurrentQuality: null,
                library.CutoffQuality,
                library.UpgradeUntilCutoff,
                library.UpgradeUnknownItems,
                item.IsReleased));
            await repository.EnsureWantedStateAsync(
                seriesId,
                library.Id,
                decision.WantedStatus,
                decision.WantedReason,
                false,
                decision.CurrentQuality,
                decision.TargetQuality,
                decision.QualityCutoffMet,
                cancellationToken);
        }
    }

    private static IEnumerable<Deluno.Libraries.Contracts.LibraryItem> TargetLibraries(
        BulkCatalogueAddItem item,
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> libraries,
        string mediaType)
        => libraries.Where(library =>
            string.Equals(library.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(item.LibraryId)
                || string.Equals(library.Id, item.LibraryId.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static async Task<JobQueueItem> EnqueueRefreshAsync(
        IJobScheduler jobScheduler,
        string jobType,
        string entityType,
        string entityId,
        string title,
        string? externalId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
        => await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                jobType,
                "automation",
                JsonSerializer.Serialize(new { id = entityId, title, imdbId = externalId }, JsonOptions),
                entityType,
                entityId,
                IdempotencyKey: idempotencyKey,
                DedupeKey: idempotencyKey),
            cancellationToken);

    private static BulkCatalogueAddResponse BuildResponse(
        bool dryRun,
        string? idempotencyKey,
        IReadOnlyList<BulkCatalogueItemResult> results)
        => new(
            dryRun,
            idempotencyKey,
            results.Count,
            results.Count(result => result.Status == "created" || result.Status == "would-create"),
            results.Count(result => result.Status == "already-exists"),
            results.Count(result => result.Status == "invalid"),
            results.Count(result => result.Status == "failed"),
            results);

    private static Dictionary<string, string[]> ValidateEnvelope<T>(
        IReadOnlyList<T>? items,
        string? idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (items is not { Count: > 0 })
        {
            errors["items"] = ["Submit at least one catalogue item."];
        }
        else if (items.Count > AutomationBatchLimits.MaxCatalogueItems)
        {
            errors["items"] = [$"A single request may contain at most {AutomationBatchLimits.MaxCatalogueItems} catalogue items."];
        }

        AddKeyError(errors, idempotencyKey);
        if (items is not null)
        {
            var duplicates = items
                .Select((item, index) => item switch
                {
                    BulkCatalogueAddItem catalogue => catalogue.ClientItemId,
                    _ => null
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .GroupBy(value => value!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicates.Length > 0)
            {
                AddValidationErrors(
                    errors,
                    "items",
                    [$"Client item ids must be unique: {string.Join(", ", duplicates)}."]);
            }
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateEpisodeEnvelope(
        string seriesId,
        IReadOnlyList<BulkSeriesEpisodeItem>? episodes,
        string? idempotencyKey)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            errors["seriesId"] = ["A series id is required."];
        }
        if (episodes is not { Count: > 0 })
        {
            errors["episodes"] = ["Submit at least one episode."];
        }
        else if (episodes.Count > AutomationBatchLimits.MaxEpisodeItems)
        {
            errors["episodes"] = [$"A single request may contain at most {AutomationBatchLimits.MaxEpisodeItems} episodes."];
        }

        AddKeyError(errors, idempotencyKey);
        if (episodes is not null)
        {
            var duplicateClientItemIds = episodes
                .Select(item => item.ClientItemId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .GroupBy(value => value!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToArray();
            if (duplicateClientItemIds.Length > 0)
            {
                AddValidationErrors(
                    errors,
                    "episodes",
                    [$"Client item ids must be unique: {string.Join(", ", duplicateClientItemIds)}."]);
            }

            var duplicateNumbers = episodes
                .GroupBy(item => (item.SeasonNumber, item.EpisodeNumber))
                .Where(group => group.Count() > 1)
                .Select(group => $"S{group.Key.SeasonNumber:00}E{group.Key.EpisodeNumber:00}")
                .ToArray();
            if (duplicateNumbers.Length > 0)
            {
                AddValidationErrors(
                    errors,
                    "episodes",
                    [$"Episode numbers must be unique: {string.Join(", ", duplicateNumbers)}."]);
            }
        }

        return errors;
    }

    private static void AddValidationErrors(
        IDictionary<string, string[]> errors,
        string key,
        IEnumerable<string> additions)
    {
        errors[key] = (errors.TryGetValue(key, out var existing) ? existing : [])
            .Concat(additions)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static List<string> ValidateItem(
        BulkCatalogueAddItem item,
        string? mediaType,
        IReadOnlyList<Deluno.Libraries.Contracts.LibraryItem> libraries)
    {
        var errors = new List<string>();
        if (mediaType is null)
        {
            errors.Add("Media type must be movie/movies or tv/series.");
        }
        if (string.IsNullOrWhiteSpace(item.Title))
        {
            errors.Add("Title is required.");
        }
        if (item.Year is < 1870 or > 2200)
        {
            errors.Add("Year must be between 1870 and 2200.");
        }
        if (!string.IsNullOrWhiteSpace(item.LibraryId)
            && !libraries.Any(library =>
                string.Equals(library.Id, item.LibraryId.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(library.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Library id does not identify a library for this media type.");
        }
        if (item.Episodes is { Count: > AutomationBatchLimits.MaxEpisodeItems })
        {
            errors.Add($"An item may contain at most {AutomationBatchLimits.MaxEpisodeItems} episodes.");
        }
        if (item.Episodes is not null)
        {
            foreach (var episode in item.Episodes)
            {
                errors.AddRange(ValidateEpisodeItem(episode));
            }
        }
        if (mediaType == "tv")
        {
            if (item.SeriesType is not null && !SeriesTypes.IsKnown(item.SeriesType))
            {
                errors.Add("Series type must be standard, daily, or anime.");
            }
            if (item.NumberingScheme is not null && !SeriesNumberingSchemes.IsKnown(item.NumberingScheme))
            {
                errors.Add("Numbering scheme must be standard, airdate, absolute, or scene.");
            }
            if (item.NumberingSource is not null &&
                !string.Equals(item.NumberingSource, SeriesNumberingSources.Provider, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.NumberingSource, SeriesNumberingSources.Owner, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Numbering source must be provider or owner.");
            }
        }
        if (mediaType != "tv" && item.Episodes is { Count: > 0 })
        {
            errors.Add("Explicit episodes can only be supplied for a TV item.");
        }
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> ValidateEpisodeItem(BulkSeriesEpisodeItem item)
    {
        var errors = new List<string>();
        if (item.SeasonNumber < 0)
        {
            errors.Add("Season number cannot be negative.");
        }
        if (item.EpisodeNumber <= 0)
        {
            errors.Add("Episode number must be greater than zero.");
        }
        AddAlternateNumberingErrors(
            errors,
            item.AbsoluteNumber,
            item.SceneSeasonNumber,
            item.SceneEpisodeNumber,
            item.NumberingSource);
        return errors;
    }

    private static List<string> ValidateEpisodeItem(BulkCatalogueEpisode item)
    {
        var errors = new List<string>();
        if (item.SeasonNumber < 0)
        {
            errors.Add("Season number cannot be negative.");
        }
        if (item.EpisodeNumber <= 0)
        {
            errors.Add("Episode number must be greater than zero.");
        }
        AddAlternateNumberingErrors(
            errors,
            item.AbsoluteNumber,
            item.SceneSeasonNumber,
            item.SceneEpisodeNumber,
            item.NumberingSource);
        return errors;
    }

    private static void AddAlternateNumberingErrors(
        ICollection<string> errors,
        int? absoluteNumber,
        int? sceneSeasonNumber,
        int? sceneEpisodeNumber,
        string? numberingSource)
    {
        if (absoluteNumber is <= 0)
        {
            errors.Add("Absolute number must be greater than zero when supplied.");
        }

        if (sceneSeasonNumber is < 0)
        {
            errors.Add("Scene season number cannot be negative.");
        }

        if (sceneEpisodeNumber is <= 0)
        {
            errors.Add("Scene episode number must be greater than zero when supplied.");
        }

        if (sceneSeasonNumber.HasValue != sceneEpisodeNumber.HasValue)
        {
            errors.Add("Scene season and scene episode numbers must be supplied together.");
        }

        if (!string.IsNullOrWhiteSpace(numberingSource)
            && !string.Equals(numberingSource, SeriesNumberingSources.Provider, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(numberingSource, SeriesNumberingSources.Owner, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Numbering source must be provider or owner.");
        }
    }

    private static string? NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() switch
        {
            "movie" or "movies" => "movies",
            "tv" or "series" or "show" or "shows" => "tv",
            _ => null
        };

    private static string ClientItemId(string? value, int index)
        => string.IsNullOrWhiteSpace(value) ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : value.Trim();

    private static void AddKeyError(IDictionary<string, string[]> errors, string? key)
    {
        if (key is not null && key.Length > AutomationBatchLimits.MaxIdempotencyKeyLength)
        {
            errors["idempotencyKey"] = [$"Idempotency keys may not exceed {AutomationBatchLimits.MaxIdempotencyKeyLength} characters."];
        }
    }

    private static (string? Key, string? Error) ResolveIdempotencyKey(HttpContext httpContext, string? bodyKey)
    {
        var headerKey = httpContext.Request.Headers["Idempotency-Key"].ToString().Trim();
        var normalizedBodyKey = bodyKey?.Trim();
        if (!string.IsNullOrWhiteSpace(headerKey)
            && !string.IsNullOrWhiteSpace(normalizedBodyKey)
            && !string.Equals(headerKey, normalizedBodyKey, StringComparison.Ordinal))
        {
            return (null, "The Idempotency-Key header and request body value must match.");
        }

        var key = string.IsNullOrWhiteSpace(headerKey) ? normalizedBodyKey : headerKey;
        return (string.IsNullOrWhiteSpace(key) ? null : key, null);
    }

    private static string? ItemIdempotencyKey(string? batchKey, string clientItemId)
        => string.IsNullOrWhiteSpace(batchKey) ? null : $"{batchKey}:item:{clientItemId}";

    private static string ComputeRequestHash<T>(T request)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions))));

    private static async Task<IResult?> TryReplayAsync(
        IAutomationIdempotencyStore store,
        string? key,
        string operation,
        string requestHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var lookup = await store.GetAsync(key, operation, requestHash, cancellationToken);
        if (!lookup.Found)
        {
            return null;
        }
        if (!lookup.HashMatches)
        {
            return Results.Conflict(new
            {
                message = "This idempotency key was already used for a different operation or request body.",
                idempotencyKey = key
            });
        }

        return Results.Text(lookup.ResponseJson!, "application/json", Encoding.UTF8);
    }

    private static async Task<IResult> PersistAndReturnAsync<T>(
        IAutomationIdempotencyStore store,
        string? key,
        string operation,
        string requestHash,
        T response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        if (string.IsNullOrWhiteSpace(key))
        {
            return Results.Text(json, "application/json", Encoding.UTF8);
        }

        var saved = await store.SaveAsync(key, operation, requestHash, json, cancellationToken);
        if (!saved.HashMatches)
        {
            return Results.Conflict(new
            {
                message = "This idempotency key was already used for a different operation or request body.",
                idempotencyKey = key
            });
        }

        return Results.Text(saved.ResponseJson!, "application/json", Encoding.UTF8);
    }
}
