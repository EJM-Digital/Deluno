using Deluno.Contracts;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Integrations.Metadata;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Quality;
using Deluno.Security;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Deluno.Api.Discover;

/// <summary>
/// Discover (#785): a browsable grid of TMDb's own "popular" list plus every
/// configured import list's entries, with an add action inline. Radarr's
/// Discover page is the model — a person has never been able to browse what
/// to add next inside Deluno itself, only search for something they already
/// had in mind.
///
/// <para>The TMDb grid and each list's entries are two separate sections
/// rather than one merged, deduplicated feed: <see cref="IIntakeListPreviewService"/>
/// already answers "is this in the library, is it excluded, would it be
/// added" for a list entry the same way <see cref="IMetadataLibraryPresence"/>
/// and <see cref="MediaExclusionMatching"/> answer it for a TMDb card — merging
/// the two shapes into one is not needed to satisfy either question, so this
/// keeps the two answers next to their own evidence instead of inventing a
/// third, unified representation nothing else in Deluno uses.</para>
/// </summary>
public static class DiscoverEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDelunoDiscoverEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var discover = endpoints.MapGroup("/api/discover");

        discover.MapGet(string.Empty, async (
            HttpContext httpContext,
            string? mediaType,
            int? page,
            IMetadataProvider metadataProvider,
            IMetadataLibraryPresence libraryPresence,
            IUnifiedExclusionRepository exclusionRepository,
            IIntakeRepository intakeRepository,
            IIntakeListPreviewService intakeListPreviewService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            var normalized = NormalizeMediaType(mediaType);
            var tmdbMediaType = normalized == "series" ? "tv" : "movies";

            var popularRaw = await metadataProvider.GetPopularAsync(tmdbMediaType, Math.Max(page ?? 1, 1), cancellationToken);
            var popularMarked = await libraryPresence.MarkHeldTitlesAsync(popularRaw, cancellationToken);
            var exclusions = await exclusionRepository.ListActiveAsync(normalized, null, null, cancellationToken);

            var popular = popularMarked
                .Select(result => new DiscoverItem(
                    Provider: result.Provider,
                    ProviderId: result.ProviderId,
                    Title: result.Title,
                    Year: result.Year,
                    Overview: result.Overview,
                    PosterUrl: result.PosterUrl,
                    BackdropUrl: result.BackdropUrl,
                    Rating: result.Rating,
                    ImdbId: result.ImdbId,
                    ExternalUrl: result.ExternalUrl,
                    InLibrary: result.LibraryEntryId is not null,
                    LibraryEntryId: result.LibraryEntryId,
                    Excluded: exclusions.Any(exclusion => MediaExclusionMatching.Names(exclusion, result.ImdbId, result.ProviderId, result.Title, result.Year))))
                .ToArray();

            var sources = (await intakeRepository.ListIntakeSourcesAsync(cancellationToken))
                .Where(source => source.IsEnabled && NormalizeMediaType(source.MediaType) == normalized)
                .ToArray();

            var lists = new List<DiscoverListSection>(sources.Length);
            foreach (var source in sources)
            {
                try
                {
                    var preview = await intakeListPreviewService.PreviewAsync(source.Id, cancellationToken);
                    lists.Add(new DiscoverListSection(
                        source.Id,
                        source.Name,
                        preview.Items
                            .Select(item => new DiscoverListEntry(
                                item.Title,
                                item.Year,
                                item.ImdbId,
                                Status: item.Action,
                                Reason: item.Reason,
                                InLibrary: item.Action == "already in library",
                                Excluded: item.Action == "excluded"))
                            .ToArray()));
                }
                catch (InvalidOperationException)
                {
                    // A source that cannot be previewed right now (a bad feed,
                    // a removed list) is missing from Discover rather than
                    // failing the whole page for every other list and the
                    // TMDb grid beside it.
                }
            }

            return Results.Ok(new DiscoverResponse(popular, lists));
        });

        discover.MapPost("/add", async (
            HttpContext httpContext,
            [FromBody] DiscoverAddRequest request,
            IMovieCatalogRepository movieRepository,
            ISeriesCatalogRepository seriesRepository,
            ILibrariesRepository librariesRepository,
            IMediaDecisionService mediaDecisionService,
            CancellationToken cancellationToken) =>
        {
            var denied = await UserAuthorization.RequireAuthenticatedAsync(httpContext, cancellationToken);
            if (denied is not null)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["A title is required."] });
            }

            var normalized = NormalizeMediaType(request.MediaType);
            var libraries = await librariesRepository.ListLibrariesAsync(cancellationToken);
            var target = LibraryMembership.Targets(libraries, normalized == "series" ? "tv" : "movies", request.LibraryId, [])
                .FirstOrDefault();

            if (normalized == "series")
            {
                var series = await seriesRepository.AddAsync(
                    new CreateSeriesRequest(
                        Title: request.Title,
                        StartYear: request.Year,
                        ImdbId: request.ImdbId,
                        Monitored: request.Monitored,
                        MetadataProvider: request.Provider,
                        MetadataProviderId: request.ProviderId,
                        Overview: request.Overview,
                        PosterUrl: request.PosterUrl,
                        BackdropUrl: request.BackdropUrl,
                        LibraryId: target?.Id),
                    cancellationToken);

                if (target is not null)
                {
                    var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                        MediaType: "tv",
                        HasFile: series.HasFile,
                        CurrentQuality: null,
                        CutoffQuality: target.CutoffQuality,
                        UpgradeUntilCutoff: target.UpgradeUntilCutoff,
                        UpgradeUnknownItems: target.UpgradeUnknownItems));

                    await seriesRepository.EnsureWantedStateAsync(
                        series.Id,
                        target.Id,
                        decision.WantedStatus,
                        decision.WantedReason,
                        series.HasFile,
                        decision.CurrentQuality,
                        decision.TargetQuality,
                        decision.QualityCutoffMet,
                        cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(request.QualityProfileId))
                {
                    await seriesRepository.UpdateQualityProfileAsync(series.Id, request.QualityProfileId, cancellationToken);
                }

                return Results.Created($"/api/series/{series.Id}", new DiscoverAddResponse(series.Id, "series", target?.Id));
            }

            var movie = await movieRepository.AddAsync(
                new CreateMovieRequest(
                    Title: request.Title,
                    ReleaseYear: request.Year,
                    ImdbId: request.ImdbId,
                    Monitored: request.Monitored,
                    MetadataProvider: request.Provider,
                    MetadataProviderId: request.ProviderId,
                    OriginalTitle: null,
                    Overview: request.Overview,
                    PosterUrl: request.PosterUrl,
                    BackdropUrl: request.BackdropUrl,
                    LibraryId: target?.Id),
                cancellationToken);

            if (target is not null)
            {
                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: "movie",
                    HasFile: movie.HasFile,
                    CurrentQuality: movie.CurrentQuality,
                    CutoffQuality: target.CutoffQuality,
                    UpgradeUntilCutoff: target.UpgradeUntilCutoff,
                    UpgradeUnknownItems: target.UpgradeUnknownItems));

                await movieRepository.EnsureWantedStateAsync(
                    movie.Id,
                    target.Id,
                    decision.WantedStatus,
                    decision.WantedReason,
                    movie.HasFile,
                    decision.CurrentQuality,
                    decision.TargetQuality,
                    decision.QualityCutoffMet,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(request.QualityProfileId))
            {
                await movieRepository.UpdateQualityProfileAsync(movie.Id, request.QualityProfileId, cancellationToken);
            }

            return Results.Created($"/api/movies/{movie.Id}", new DiscoverAddResponse(movie.Id, "movies", target?.Id));
        });

        return endpoints;
    }

    private static string NormalizeMediaType(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "shows" or "series" ? "series" : "movies";
}

public sealed record DiscoverItem(
    string Provider,
    string ProviderId,
    string Title,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    double? Rating,
    string? ImdbId,
    string? ExternalUrl,
    bool InLibrary,
    string? LibraryEntryId,
    bool Excluded);

/// <summary>One list entry, with whatever <see cref="IIntakeListPreviewService"/> already knows about it.</summary>
public sealed record DiscoverListEntry(
    string Title,
    int? Year,
    string? ImdbId,
    string Status,
    string Reason,
    bool InLibrary,
    bool Excluded);

public sealed record DiscoverListSection(string SourceId, string SourceName, IReadOnlyList<DiscoverListEntry> Items);

public sealed record DiscoverResponse(IReadOnlyList<DiscoverItem> Popular, IReadOnlyList<DiscoverListSection> Lists);

public sealed record DiscoverAddRequest(
    string MediaType,
    string Title,
    int? Year,
    string? ImdbId,
    string? Provider,
    string? ProviderId,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    string? LibraryId,
    string? QualityProfileId,
    bool Monitored);

public sealed record DiscoverAddResponse(string Id, string MediaType, string? LibraryId);
