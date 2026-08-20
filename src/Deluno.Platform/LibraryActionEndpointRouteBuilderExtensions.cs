using Microsoft.AspNetCore.Mvc;
using Deluno.Contracts;
using Deluno.Quality.Presets;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Infrastructure.Observability;
using Deluno.Infrastructure.Resilience;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Platform.Migration;
using Deluno.Platform.Processing;
using Deluno.Quality;
using Deluno.Notifications;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Realtime;
using Deluno.Security.Contracts;
using Deluno.Security;
using System.Net.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Deluno.Quality.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;

namespace Deluno.Platform;

public static class LibraryActionEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoLibraryActionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var write = endpoints.MapGroup(string.Empty)
            .RequireAuthorization(DelunoAuthorizationPolicies.Write);

        write.MapPost("/api/destination-rules/resolve", async (
            [FromBody] DestinationResolutionRequest request,
            IPlatformSettingsRepository repository,
            ILibrariesRepository librariesRepository,
            CancellationToken cancellationToken) =>
        {
            var settings = await repository.GetAsync(cancellationToken);
            var rules = await librariesRepository.ListDestinationRulesAsync(cancellationToken);
            var result = ResolveDestination(request, settings, rules);
            return Results.Ok(result);
        });

        write.MapPost("/api/libraries/{id}/search-now", async (
            string id,
            HttpContext httpContext,
            ILibrariesRepository repository,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var library = (await repository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

            if (library is null)
            {
                return Results.NotFound();
            }

            var requested = await jobs.RequestLibrarySearchAsync(ToPlanItem(library), cancellationToken);
            return requested ? Results.Accepted() : Results.NotFound();
        });

        write.MapPost("/api/libraries/{id}/skip-cycle", async (
            string id,
            HttpContext httpContext,
            ILibrariesRepository repository,
            IJobQueueRepository jobs,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var library = (await repository.ListLibrariesAsync(cancellationToken))
                .FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

            if (library is null)
            {
                return Results.NotFound();
            }

            var skipped = await jobs.SkipLibrarySearchCycleAsync(ToPlanItem(library), cancellationToken);
            return skipped ? Results.Accepted() : Results.NotFound();
        });

        // Importing an existing library is a tracked background operation, not
        // a request that returns when the work is finished. At 20,000 items the
        // work runs far longer than any HTTP request should live, so the POST
        // only starts the run and hands back its position; the worker advances
        // it, and the GET below is what a progress display reads.
        write.MapPost("/api/libraries/{id}/import-existing", async (
            string id,
            HttpContext httpContext,
            [FromServices] IExistingLibraryImportService importService,
            [FromServices] IJobScheduler jobScheduler,
            [FromServices] IActivityFeedRepository activityFeedRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var progress = await importService.StartAsync(id, cancellationToken);
            if (progress is null)
            {
                return Results.NotFound();
            }

            await EnqueueImportSliceAsync(jobScheduler, progress.Run, cancellationToken);

            if (progress.Run.ProcessedCount == 0)
            {
                await activityFeedRepository.RecordActivityAsync(
                    "library.import.existing",
                    $"Deluno started bringing in what is already in {progress.Run.LibraryName}.",
                    null,
                    null,
                    "library",
                    progress.Run.LibraryId,
                    cancellationToken);
            }

            return Results.Accepted($"/api/libraries/{id}/import-existing", progress);
        });

        write.MapGet("/api/libraries/{id}/import-existing", async (
            string id,
            HttpContext httpContext,
            [FromServices] IExistingLibraryImportService importService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var progress = await importService.GetProgressAsync(id, cancellationToken);
            return progress is null ? Results.NotFound() : Results.Ok(progress);
        });

        write.MapPost("/api/libraries/{id}/import-existing/pause", async (
            string id,
            HttpContext httpContext,
            [FromServices] IExistingLibraryImportService importService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var progress = await importService.SetStateAsync(id, LibraryImportRunStatuses.Paused, cancellationToken);
            return progress is null ? Results.NotFound() : Results.Accepted(null, progress);
        });

        write.MapPost("/api/libraries/{id}/import-existing/resume", async (
            string id,
            HttpContext httpContext,
            [FromServices] IExistingLibraryImportService importService,
            [FromServices] IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var progress = await importService.SetStateAsync(id, LibraryImportRunStatuses.Running, cancellationToken);
            if (progress is null)
            {
                return Results.NotFound();
            }

            await EnqueueImportSliceAsync(jobScheduler, progress.Run, cancellationToken);
            return Results.Accepted(null, progress);
        });

        write.MapPost("/api/libraries/{id}/import-existing/cancel", async (
            string id,
            HttpContext httpContext,
            [FromServices] IExistingLibraryImportService importService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var progress = await importService.SetStateAsync(id, LibraryImportRunStatuses.Cancelled, cancellationToken);
            return progress is null ? Results.NotFound() : Results.Accepted(null, progress);
        });

        // What the run set aside rather than guessing at. Capped, because a bad
        // library could produce one of these per title.
        write.MapGet("/api/libraries/{id}/import-existing/issues", async (
            string id,
            int? take,
            HttpContext httpContext,
            [FromServices] IExistingLibraryImportService importService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var issues = await importService.ListIssuesAsync(id, Math.Clamp(take ?? 100, 1, 500), cancellationToken);
            return Results.Ok(issues);
        });

        return endpoints;
    }

    private static async Task EnqueueImportSliceAsync(
        IJobScheduler jobScheduler,
        LibraryImportRunItem run,
        CancellationToken cancellationToken)
    {
        await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                JobType: "library.import.existing",
                Source: "library-import",
                PayloadJson: JsonSerializer.Serialize(new { RunId = run.Id, run.LibraryId }),
                RelatedEntityType: "library",
                RelatedEntityId: run.LibraryId,
                DedupeKey: LibraryImportSliceOutcome.ContinuationDedupeKey(run.Id, run.ProcessedCount)),
            cancellationToken);
    }

    private static LibraryAutomationPlanItem ToPlanItem(LibraryItem library)
    {
        return new LibraryAutomationPlanItem(
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
            SearchWindowEndHour: library.SearchWindowEndHour);
    }

    private static DestinationResolutionResult ResolveDestination(
        [FromBody] DestinationResolutionRequest request,
        PlatformSettingsSnapshot settings,
        IReadOnlyList<DestinationRuleItem> rules)
    {
        var mediaType = NormalizeMediaType(request.MediaType);
        var title = string.IsNullOrWhiteSpace(request.Title) ? "Untitled" : request.Title.Trim();
        var rootFallback = mediaType == "tv"
            ? settings.SeriesRootPath ?? settings.MovieRootPath ?? string.Empty
            : settings.MovieRootPath ?? settings.SeriesRootPath ?? string.Empty;

        var match = rules
            .Where(rule => rule.IsEnabled && string.Equals(NormalizeMediaType(rule.MediaType), mediaType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(rule => rule.Priority)
            .FirstOrDefault(rule => MatchesDestinationRule(rule, request));

        var rootPath = match?.RootPath ?? rootFallback;
        var template = match?.FolderTemplate ??
                       (mediaType == "tv" ? settings.SeriesFolderFormat : settings.MovieFolderFormat);
        var folderName = ApplyFolderTemplate(template, title, request.Year);
        var fullPath = string.IsNullOrWhiteSpace(rootPath)
            ? folderName
            : Path.Combine(rootPath, folderName);

        return new DestinationResolutionResult(
            MediaType: mediaType,
            Title: title,
            Year: request.Year,
            RootPath: rootPath,
            FolderName: folderName,
            FullPath: fullPath,
            MatchedRuleId: match?.Id,
            MatchedRuleName: match?.Name,
            Reason: match is null
                ? "No destination rule matched, so Deluno used the default root folder."
                : $"Matched {match.MatchKind} rule '{match.Name}' with priority {match.Priority}.");
    }

    private static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "series" or "shows"
            ? "tv"
            : "movies";

    private static bool MatchesDestinationRule(DestinationRuleItem rule, DestinationResolutionRequest request)
    {
        var expected = rule.MatchValue.Trim();
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return rule.MatchKind.Trim().ToLowerInvariant() switch
        {
            "genre" => ContainsAny(request.Genres, expected),
            "tag" => ContainsAny(request.Tags, expected),
            "studio" => ContainsText(request.Studio, expected),
            "language" or "originallanguage" => ContainsText(request.OriginalLanguage, expected),
            "title" => ContainsText(request.Title, expected),
            _ => ContainsAny(request.Genres, expected) || ContainsAny(request.Tags, expected)
        };
    }

    private static bool ContainsAny(IReadOnlyList<string>? values, string expected)
        => values?.Any(value => ContainsText(value, expected)) == true;

    private static bool ContainsText(string? value, string expected)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static string ApplyFolderTemplate(string? template, string title, int? year)
    {
        var resolved = string.IsNullOrWhiteSpace(template)
            ? "{Title} ({Year})"
            : template;
        var safeTitle = SanitizePathSegment(title);
        var safeYear = year?.ToString(CultureInfo.InvariantCulture) ?? "Unknown Year";

        return SanitizePathSegment(resolved
            .Replace("{Movie Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{MovieTitle}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Series Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{SeriesTitle}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", safeTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{Release Year}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{ReleaseYear}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{Series Year}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{SeriesYear}", safeYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", safeYear, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Untitled" : cleaned.Trim();
    }

}
