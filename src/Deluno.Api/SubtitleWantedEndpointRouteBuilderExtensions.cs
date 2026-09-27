using System.Text.Json;
using Deluno.Contracts;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Data;
using Deluno.Media;
using Deluno.Security;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api;

/// <summary>
/// #840's cross-library subtitle Wanted screen: every title or episode
/// across every library still short of a wanted subtitle language, and the
/// bulk "search all" action that queues #840's own per-title job for each
/// one listed.
///
/// <para>Lives here rather than in Movies or Series because it has to read
/// across both engines the same way <c>DashboardMetricsEndpointRouteBuilderExtensions</c>
/// does, and neither catalogue project references the other.</para>
/// </summary>
public static class SubtitleWantedEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Bounded the same way a library's own subtitle search job is: a
    /// screen showing thousands of rows is not a screen anybody reads, and
    /// the bulk action below queues a job per row shown.
    /// </summary>
    private const int PerLibraryLimit = 100;

    public static IEndpointRouteBuilder MapDelunoSubtitleWantedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/subtitles/wanted", async (
            HttpContext httpContext,
            ILibrariesRepository librariesRepository,
            IMediaSubtitleRepository mediaSubtitleRepository,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var items = new List<SubtitleWantedRow>();

            foreach (var library in libraries)
            {
                if (library.SubtitleLanguages is not { Count: > 0 })
                {
                    continue;
                }

                var kind = string.Equals(library.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                    ? MediaKind.Series
                    : MediaKind.Movie;

                var wanted = await mediaSubtitleRepository.ListWantedAsync(
                    kind, library.Id, library.SubtitleLanguages, PerLibraryLimit, library.SubtitleEmbeddedCounts, cancellationToken);

                items.AddRange(wanted.Select(item => new SubtitleWantedRow(
                    Kind: kind.ToString(),
                    MediaId: item.MediaId,
                    LibraryId: library.Id,
                    LibraryName: library.Name,
                    Title: item.Title,
                    Year: item.Year,
                    SeasonNumber: item.SeasonNumber,
                    EpisodeNumber: item.EpisodeNumber,
                    EpisodeTitle: item.EpisodeTitle,
                    LanguagesMissing: item.LanguagesToFetch)));
            }

            return Results.Ok(items);
        });

        /*
          #840: one job per listed item, on subtitle.title.search's own lane
          — never the library's own automatic pass, so a person's bulk
          re-search from this screen is never held up behind, or holds up,
          the scheduled slice.
        */
        endpoints.MapPost("/api/subtitles/wanted/search-all", async (
            HttpContext httpContext,
            [FromBody] SearchAllWantedRequest request,
            ISeriesCatalogRepository seriesCatalogRepository,
            IJobScheduler jobScheduler,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (request.Items is not { Count: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["items"] = ["Choose at least one title before searching."]
                });
            }

            var queued = 0;
            foreach (var row in request.Items)
            {
                if (!Enum.TryParse<MediaKind>(row.Kind, ignoreCase: true, out var kind) ||
                    string.IsNullOrWhiteSpace(row.MediaId) || string.IsNullOrWhiteSpace(row.LibraryId))
                {
                    continue;
                }

                // An episode's own subtitle-language override lives on its
                // show, never the episode (#843), so the job payload has to
                // carry the show's id separately from the episode it is
                // actually searching.
                string? overrideMediaId = null;
                if (kind == MediaKind.Series)
                {
                    overrideMediaId = await seriesCatalogRepository.GetEpisodeSeriesIdAsync(row.MediaId, cancellationToken);
                }

                await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "subtitle.title.search",
                        Source: kind == MediaKind.Series ? "tv" : "movies",
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            Kind = kind,
                            LibraryId = row.LibraryId,
                            MediaId = row.MediaId,
                            OverrideMediaId = overrideMediaId
                        }),
                        RelatedEntityType: kind == MediaKind.Series ? "episode" : "movie",
                        RelatedEntityId: row.MediaId,
                        // One search per title in flight at a time - a second
                        // click before the first finishes replaces it rather
                        // than queueing a duplicate.
                        DedupeKey: $"subtitle.title.search:{row.MediaId}"),
                    cancellationToken);
                queued++;
            }

            return Results.Ok(new { queued });
        });

        return endpoints;
    }

    private sealed record SubtitleWantedRow(
        string Kind,
        string MediaId,
        string LibraryId,
        string LibraryName,
        string Title,
        int? Year,
        int? SeasonNumber,
        int? EpisodeNumber,
        string? EpisodeTitle,
        IReadOnlyList<string> LanguagesMissing);

    private sealed record SearchAllWantedRequestItem(string Kind, string MediaId, string LibraryId);

    private sealed record SearchAllWantedRequest(IReadOnlyList<SearchAllWantedRequestItem>? Items);
}
