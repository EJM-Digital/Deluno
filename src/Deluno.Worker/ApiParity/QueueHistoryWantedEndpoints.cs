using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Jobs.Data;
using Deluno.Media;
using Deluno.Security;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker.ApiParity;

/// <summary>One download in the queue, named the way the Queue screen names it: the client it is in and the client's own id for it.</summary>
public sealed record QueueRemovalTarget(string? ClientId, string? QueueItemId);

public sealed record QueueBulkRemovalRequest(IReadOnlyList<QueueRemovalTarget>? Items);

/// <summary>One download's history (#1022): the furthest it got, in Radarr's event names.</summary>
public sealed record HistoryRecord(
    string Id,
    string EventType,
    DateTimeOffset Date,
    string MediaType,
    string TitleId,
    string SourceTitle,
    string? DownloadId,
    string Indexer,
    string DownloadClient,
    string? ImportedFilePath,
    string? Message);

public sealed record HistoryPage(IReadOnlyList<HistoryRecord> Items, string? NextPageToken, bool HasMore);

/// <summary>A film, or one episode, that is missing or below its cutoff.</summary>
public sealed record WantedRecord(
    string MediaType,
    string Id,
    string TitleId,
    string Title,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    string? EpisodeTitle,
    DateTimeOffset? AirDateUtc,
    string WantedStatus,
    string WantedReason,
    string? CurrentQuality,
    string? TargetQuality,
    DateTimeOffset? LastSearchUtc,
    DateTimeOffset? NextEligibleSearchUtc);

public sealed record WantedPage(int Page, int PageSize, int TotalRecords, IReadOnlyList<WantedRecord> Records);

/// <summary>
/// The three single addresses Radarr and Sonarr have where Deluno had them
/// split (#1022): <c>DELETE /queue/{id}</c> with its removeFromClient and
/// blocklist flags, <c>GET /history</c> with an event type, and
/// <c>GET /wanted/missing</c> and <c>/wanted/cutoff</c>.
///
/// <para>Checked against Radarr's <c>QueueController</c> (<c>RemoveAction</c>
/// takes <c>removeFromClient = true</c>, <c>blocklist = false</c>,
/// <c>skipRedownload = false</c>), its <c>HistoryController</c> and its
/// <c>MissingController</c> / <c>CutoffController</c> (paged lists, with
/// <c>monitored</c> defaulting to true). Every one reads or acts through the
/// service the screens use, so the API and the screen cannot disagree.</para>
/// </summary>
public static class QueueHistoryWantedEndpoints
{
    public static IEndpointRouteBuilder MapDelunoQueueHistoryWantedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var queue = endpoints.MapGroup("/api/queue")
            .RequireAuthorization(DelunoAuthorizationPolicies.Queue);

        queue.MapDelete("/{clientId}/{queueItemId}", async (
            string clientId,
            string queueItemId,
            HttpContext httpContext,
            IDownloadClientTelemetryService telemetry,
            CancellationToken cancellationToken,
            bool removeFromClient = true,
            bool blocklist = false,
            bool skipRedownload = false) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var result = await telemetry.RemoveWithOptionsAsync(
                clientId, queueItemId, removeFromClient, blocklist, skipRedownload, cancellationToken);
            return result.Succeeded ? Results.Ok(result) : Results.BadRequest(result);
        });

        queue.MapDelete("/bulk", async (
            HttpContext httpContext,
            [FromBody] QueueBulkRemovalRequest request,
            IDownloadClientTelemetryService telemetry,
            CancellationToken cancellationToken,
            bool removeFromClient = true,
            bool blocklist = false,
            bool skipRedownload = false) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var items = request.Items ?? [];
            if (items.Count == 0 || items.Any(item => string.IsNullOrWhiteSpace(item.ClientId) || string.IsNullOrWhiteSpace(item.QueueItemId)))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["items"] = ["Name at least one download, each with its clientId and queueItemId."]
                });
            }

            if (items.Count > 200)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["items"] = ["Remove at most 200 downloads in one call."]
                });
            }

            var results = new List<QueueRemovalResult>(items.Count);
            foreach (var item in items)
            {
                results.Add(await telemetry.RemoveWithOptionsAsync(
                    item.ClientId!, item.QueueItemId!, removeFromClient, blocklist, skipRedownload, cancellationToken));
            }

            return Results.Ok(results);
        });

        endpoints.MapGet("/api/history", async (
            string? eventType,
            string? mediaType,
            string? titleId,
            int? pageSize,
            string? pageToken,
            IDownloadDispatchesRepository dispatches,
            CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(eventType) && !DispatchHistoryEvents.IsKnown(eventType))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["eventType"] = [$"Deluno's history has these events: {DispatchHistoryEvents.Grabbed}, {DispatchHistoryEvents.Imported}, {DispatchHistoryEvents.Failed}."]
                });
            }

            var media = NormalizeMediaType(mediaType);
            if (!string.IsNullOrWhiteSpace(mediaType) && media is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["mediaType"] = ["Use movies or tv."]
                });
            }

            var (items, next) = await dispatches.QueryDispatchesAsync(
                new DispatchQueryFilter
                {
                    HistoryEvent = eventType,
                    MediaType = media,
                    EntityId = string.IsNullOrWhiteSpace(titleId) ? null : titleId,
                    // A successful import archives its dispatch at once; a history that
                    // hid archived rows would show an empty past.
                    IncludeArchived = true
                },
                new DispatchPaginationOptions { PageSize = new PageRequest(pageSize ?? 50, pageToken).BoundedPageSize, PageToken = pageToken },
                cancellationToken);

            var records = items.Select(item =>
            {
                var kind = DispatchHistoryEvents.For(item.GrabStatus, item.ImportStatus);
                return new HistoryRecord(
                    item.Id,
                    kind,
                    kind == DispatchHistoryEvents.Imported
                        ? item.ImportCompletedUtc ?? item.CreatedUtc
                        : item.GrabAttemptedUtc ?? item.CreatedUtc,
                    item.MediaType,
                    item.EntityId,
                    item.ReleaseName,
                    item.TorrentHashOrItemId,
                    item.IndexerName,
                    item.DownloadClientName,
                    item.ImportedFilePath,
                    kind == DispatchHistoryEvents.Failed
                        ? item.ImportFailureMessage ?? item.GrabMessage ?? item.Failure?.Message
                        : null);
            }).ToArray();

            return Results.Ok(new HistoryPage(records, next, !string.IsNullOrEmpty(next)));
        }).RequireAuthorization(DelunoAuthorizationPolicies.Read);

        var wanted = endpoints.MapGroup("/api/wanted")
            .RequireAuthorization(DelunoAuthorizationPolicies.Read);

        wanted.MapGet("/missing", (
            string? mediaType,
            bool? monitored,
            int? page,
            int? pageSize,
            IMediaStateRepository media,
            ISeriesCatalogRepository series,
            CancellationToken cancellationToken)
            => ListWantedAsync("missing", mediaType, monitored, page, pageSize, media, series, cancellationToken));

        wanted.MapGet("/cutoff", (
            string? mediaType,
            bool? monitored,
            int? page,
            int? pageSize,
            IMediaStateRepository media,
            ISeriesCatalogRepository series,
            CancellationToken cancellationToken)
            => ListWantedAsync("upgrade", mediaType, monitored, page, pageSize, media, series, cancellationToken));

        return endpoints;
    }

    /// <summary>
    /// Films first, then episodes, as one list: the window
    /// <c>[skip, skip + take)</c> is cut from the films' list and then from the
    /// episodes', so a page can straddle both and every record appears exactly
    /// once. <c>monitored</c> defaults to true, as Radarr's does.
    /// </summary>
    private static async Task<IResult> ListWantedAsync(
        string wantedStatus,
        string? mediaType,
        bool? monitored,
        int? page,
        int? pageSize,
        IMediaStateRepository media,
        ISeriesCatalogRepository series,
        CancellationToken cancellationToken)
    {
        var kind = NormalizeMediaType(mediaType);
        if (!string.IsNullOrWhiteSpace(mediaType) && kind is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Use movies or tv."] });
        }

        var pageNumber = Math.Max(1, page ?? 1);
        var take = Math.Clamp(pageSize ?? 50, 1, 200);
        var skip = (pageNumber - 1) * take;
        var monitoredOnly = monitored ?? true;

        var records = new List<WantedRecord>();
        var total = 0;

        var movieTotal = 0;
        if (kind is null or "movies")
        {
            var (movies, count) = await media.ListWantedPageAsync(
                MediaKind.Movie, wantedStatus, monitoredOnly, skip, take, cancellationToken);
            movieTotal = count;
            total += count;
            records.AddRange(movies.Select(item => new WantedRecord(
                "movies", item.Id, item.Id, item.Title, item.Year, null, null, null, item.AvailableUtc,
                item.WantedStatus, item.WantedReason, item.CurrentQuality, item.TargetQuality,
                item.LastSearchUtc, item.NextEligibleSearchUtc)));
        }

        if (kind is null or "tv")
        {
            // Whatever of the window the films did not fill.
            var episodeSkip = Math.Max(0, skip - movieTotal);
            var episodeTake = take - records.Count;
            var (episodes, count) = await series.ListWantedEpisodesPageAsync(
                wantedStatus, monitoredOnly, episodeSkip, Math.Max(0, episodeTake), cancellationToken);
            total += count;
            if (episodeTake > 0)
            {
                records.AddRange(episodes.Select(item => new WantedRecord(
                    "tv", item.EpisodeId, item.SeriesId, item.SeriesTitle, null, item.SeasonNumber, item.EpisodeNumber,
                    item.Title, item.AirDateUtc, item.WantedStatus, item.WantedReason, null, null,
                    item.LastSearchUtc, item.NextEligibleSearchUtc)));
            }
        }

        return Results.Ok(new WantedPage(pageNumber, take, total, records));
    }

    private static string? NormalizeMediaType(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "movies" or "movie" => "movies",
            "tv" or "series" or "shows" or "episodes" => "tv",
            _ => null
        };
}
