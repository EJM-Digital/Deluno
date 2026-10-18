using Deluno.Security;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Worker.Testing;

/// <summary>
/// Lets a browser test put a show with a few episodes, and the searches that were made for some of them, into the real
/// databases, so the show's page can be walked without a metadata service to supply the episodes. What the page then
/// reads, and every menu it offers, go through the real routes and the real repositories; only the starting records are
/// written here, with the same repository calls the catalogue and the search planner make. The show is taken out again by
/// the ordinary bulk remove.
///
/// <para><b>Never reachable outside a smoke run.</b> Mapped only when <c>Testing:EnableSeriesSeedEndpoint</c> is true, which
/// the shipped app's configuration never sets: in a real install the route is absent from the endpoint table, not merely
/// unauthorized (the same guard as the other test-only routes).</para>
/// </summary>
public static class SeriesSeedTestEndpoints
{
    public static IEndpointRouteBuilder MapDelunoSeriesSeedTestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/testing/series/seed", async (
            HttpContext httpContext,
            [FromBody] SeedSeriesRequest request,
            ISeriesCatalogRepository series,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var count = Math.Clamp(request.EpisodeCount, 1, 30);
            await series.ImportExistingAsync(
                request.LibraryId, request.Title, request.Year, "missing", "Seeded for a browser test.", null, null, false, null, null,
                Enumerable.Range(1, count).Select(number => new ImportedEpisodeItem(1, number, HasFile: false)).ToArray(),
                cancellationToken);

            var seriesId = (await series.ListAsync(cancellationToken)).Single(item => item.Title == request.Title).Id;
            var episodes = (await series.GetInventoryDetailAsync(seriesId, cancellationToken))!.Episodes
                .OrderBy(item => item.SeasonNumber).ThenBy(item => item.EpisodeNumber).ToList();

            var now = timeProvider.GetUtcNow();
            foreach (var search in request.Searches ?? [])
            {
                var episode = episodes.Single(item => item.EpisodeNumber == search.EpisodeNumber);
                await series.RecordSearchAttemptAsync(
                    seriesId, episode.EpisodeId, request.LibraryId, "manual", search.Outcome ?? "no-results", now, null,
                    search.Outcome ?? "no-results", null, null, null, cancellationToken);
                now = now.AddSeconds(1);
            }

            return Results.Ok(new { seriesId, episodes = episodes.Select(item => new { item.EpisodeId, item.SeasonNumber, item.EpisodeNumber }) });
        });

        return endpoints;
    }

    public sealed record SeedSeriesRequest(string LibraryId, string Title, int? Year, int EpisodeCount, IReadOnlyList<SeedSearch>? Searches);

    public sealed record SeedSearch(int EpisodeNumber, string? Outcome);
}
