using Deluno.Contracts;
using Deluno.Jobs.Data;
using Deluno.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker.Attention;

/// <summary>What the bell lists that no other screen already lists (#1098).</summary>
public static class AttentionEndpoints
{
    /// <summary>One title with no acceptable release, as the bell draws it.</summary>
    public sealed record NoReleaseRow(string MediaType, string Id, string Title, int? Year, string LibraryId, DateTimeOffset LastSearchUtc, DateTimeOffset? MissingSinceUtc, string? Reason);

    /// <summary>A held release as Transfers draws it under Needs action (#1159).</summary>
    public sealed record HeldReleaseRow(
        string HistoryId, string MediaType, string Id, string? EpisodeId, string Title, int? Year, string? EpisodeLabel, string LibraryId,
        string ReleaseName, string? IndexerName, string? IndexerId, string? DownloadUrl, string? Quality, long? SizeBytes, int? Seeders,
        string? Reason, DateTimeOffset HeldUtc);

    /// <summary>
    /// The titles the bell lists as having no acceptable release. The bell reads this through the endpoint and the
    /// tray's count reads it here, so both count the same rows.
    /// </summary>
    public static async Task<NoReleaseRow[]> ListNoReleaseAsync(
        INoAcceptableReleaseReader reader,
        AttentionDismissalStore dismissals,
        CancellationToken cancellationToken)
    {
        var all = await reader.ListAsync(500, cancellationToken);
        var dismissed = await dismissals.ListAsync(cancellationToken);
        // A dismissal is forgotten once its title stops being this problem, so it can come back as a new one.
        await dismissals.ForgetExceptAsync(
            all.Select(item => AttentionDismissalStore.NoReleaseKey(item.MediaType, item.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        return all
            .Where(item => !dismissed.Contains(AttentionDismissalStore.NoReleaseKey(item.MediaType, item.Id)))
            .Select(item => new NoReleaseRow(item.MediaType, item.Id, item.Title, item.Year, item.LibraryId, item.LastSearchUtc, item.MissingSinceUtc, item.Reason))
            .ToArray();
    }

    public static IEndpointRouteBuilder MapDelunoAttentionReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Wanted titles whose newest search found nothing Deluno would take, minus the ones the person dismissed.
        endpoints.MapGet("/api/attention/no-release", async (
            INoAcceptableReleaseReader reader,
            AttentionDismissalStore dismissals,
            CancellationToken cancellationToken) =>
            Results.Ok(await ListNoReleaseAsync(reader, dismissals, cancellationToken)));

        // Searches that ended "held": a usable release that needs a person's yes (#1159), minus the ones already answered.
        endpoints.MapGet("/api/attention/held-releases", async (
            IHeldReleaseReader reader,
            AttentionDismissalStore dismissals,
            CancellationToken cancellationToken) =>
        {
            var all = await reader.ListAsync(500, cancellationToken);
            var dismissed = await dismissals.ListAsync(cancellationToken);
            await dismissals.ForgetExceptAsync(
                all.Select(item => AttentionDismissalStore.HeldReleaseKey(item.HistoryId)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                cancellationToken,
                AttentionDismissalStore.HeldReleasePrefix);
            var rows = all
                .Where(item => !dismissed.Contains(AttentionDismissalStore.HeldReleaseKey(item.HistoryId)))
                .Select(item => new HeldReleaseRow(
                    item.HistoryId, item.MediaType, item.Id, item.EpisodeId, item.Title, item.Year, item.EpisodeLabel, item.LibraryId,
                    item.ReleaseName, item.IndexerName, item.IndexerId, item.DownloadUrl, item.Quality, item.SizeBytes, item.Seeders,
                    item.Reason, item.HeldUtc))
                .ToArray();
            return Results.Ok(rows);
        });

        return endpoints;
    }

    public static IEndpointRouteBuilder MapDelunoAttentionWriteEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Stop showing a title in the bell. Nothing about the title changes and nothing is deleted.
        endpoints.MapPost("/api/attention/no-release/{mediaType}/{id}/dismiss", async (
            string mediaType,
            string id,
            AttentionDismissalStore dismissals,
            CancellationToken cancellationToken) =>
        {
            if (mediaType is not ("movie" or "series"))
            {
                return Results.NotFound();
            }

            await dismissals.DismissAsync(AttentionDismissalStore.NoReleaseKey(mediaType, id), cancellationToken);
            return Results.NoContent();
        });

        // Answer a held release: approved, replaced or dismissed, it stops being listed. The release itself is
        // sent (or refused) by the grab and blocklist routes the screen calls first (#1159).
        endpoints.MapPost("/api/attention/held-releases/{historyId}/dismiss", async (
            string historyId,
            AttentionDismissalStore dismissals,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(historyId))
            {
                return Results.NotFound();
            }

            await dismissals.DismissAsync(AttentionDismissalStore.HeldReleaseKey(historyId), cancellationToken);
            return Results.NoContent();
        });

        // Approve a held release (#1159). It is judged again against today's rules first: a title that has its file
        // since, a newer search, or a release the current title rules refuse is answered 409 with the plain reason.
        // Otherwise it is sent exactly as the title's own grab does, and the held row is answered.
        endpoints.MapPost("/api/attention/held-releases/{historyId}/approve", async (
            string historyId,
            IHeldReleaseReader heldReader,
            AttentionDismissalStore dismissals,
            Deluno.Movies.Data.IMovieCatalogRepository movieRepository,
            Deluno.Series.Data.ISeriesCatalogRepository seriesRepository,
            IMediaStateRepository mediaStateRepository,
            Deluno.Platform.Data.IPlatformSettingsRepository platformSettingsRepository,
            Deluno.Libraries.Data.ILibrariesRepository librariesRepository,
            Deluno.Quality.Data.IQualityRepository qualityRepository,
            Deluno.Jobs.Data.IJobQueueRepository jobQueueRepository,
            Deluno.Integrations.Search.IAcquisitionDecisionPipeline acquisitionPipeline,
            Deluno.Integrations.DownloadClients.IDownloadClientGrabService downloadClientGrabService,
            Deluno.Jobs.Data.IActivityFeedRepository activityFeedRepository,
            TimeProvider timeProvider,
            Deluno.Quality.Guides.IGuidePackageStore guidePackageStore,
            Deluno.Quality.Data.IReleasePreferencePlanRepository releasePreferencePlanRepository,
            IMediaTagStore tagStore,
            Deluno.Integrations.DownloadClients.IDownloadClientTelemetryService downloadClientTelemetry,
            CancellationToken cancellationToken) =>
        {
            var check = await heldReader.CheckAsync(historyId, cancellationToken);
            if (check is null)
            {
                return Results.NotFound();
            }

            if (check.Refusal is not null)
            {
                return Results.Json(new { code = "held-release-refused", message = check.Refusal }, statusCode: StatusCodes.Status409Conflict);
            }

            var item = check.Item;
            var isMovie = item.MediaType == "movie";
            ActiveDownloadCheck? activeDownloadCheck;
            if (isMovie)
            {
                activeDownloadCheck = ActiveDownloadChecks.ForWholeTitle(item.Id, item.Title, downloadClientTelemetry);
            }
            else
            {
                var inventory = await seriesRepository.GetInventoryDetailAsync(item.Id, cancellationToken);
                activeDownloadCheck = inventory is null
                    ? null
                    : Deluno.Series.Services.SeriesManualSendGuard.Build(
                        item.Id,
                        inventory.Title,
                        [.. inventory.Episodes.Select(episode => (episode.EpisodeId, episode.SeasonNumber, episode.EpisodeNumber))],
                        item.ReleaseName,
                        downloadClientTelemetry);
            }

            var result = await MediaGrabHandler.ExecuteAsync(
                isMovie ? MediaKind.Movie : MediaKind.Series,
                item.Id,
                new MediaReleaseGrabRequest(
                    item.ReleaseName, item.IndexerId, item.IndexerName, item.DownloadUrl, item.Quality, item.SizeBytes, item.Seeders,
                    Force: true, OverrideReason: "You approved this release from Needs action."),
                mediaStateRepository,
                platformSettingsRepository,
                librariesRepository,
                qualityRepository,
                jobQueueRepository,
                acquisitionPipeline,
                downloadClientGrabService,
                activityFeedRepository,
                timeProvider,
                (mediaId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token) =>
                    isMovie
                        ? movieRepository.RecordSearchAttemptAsync(mediaId, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token)
                        : seriesRepository.RecordSearchAttemptAsync(mediaId, null, libraryId, triggerKind, outcome, now, nextEligibleUtc, lastSearchResult, releaseName, indexerName, detailsJson, token),
                cancellationToken,
                guidePackageStore,
                releasePreferencePlanRepository,
                item.LibraryId,
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

            await dismissals.DismissAsync(AttentionDismissalStore.HeldReleaseKey(historyId), cancellationToken);
            return Results.Ok(new { result.ReleaseName, result.IndexerName, result.DispatchStatus, result.DispatchMessage });
        });

        return endpoints;
    }
}
