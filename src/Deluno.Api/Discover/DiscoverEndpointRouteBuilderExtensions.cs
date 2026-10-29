using System.Text.Json;
using Deluno.Contracts;
using Deluno.Intake.Contracts;
using Deluno.Intake.Data;
using Deluno.Integrations.Metadata;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Contracts;
using Deluno.Movies.Data;
using Deluno.Media;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Data;
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
            [AsParameters] DiscoverFilterQuery filterQuery,
            TimeProvider timeProvider,
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

            // #1429: a filter makes the list a filtered discover; with none, the list is the one asked for (popular by default).
            var listQuery = DiscoverFilters.ToListQuery(
                filterQuery, tmdbMediaType, Math.Max(page ?? 1, 1), DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime));
            var popularRaw = await metadataProvider.GetDiscoverListAsync(tmdbMediaType, listQuery, cancellationToken);
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

            return Results.Ok(new DiscoverResponse(popular, lists, listQuery.List, listQuery.Page));
        });

        discover.MapPost("/add", async (
            HttpContext httpContext,
            [FromBody] DiscoverAddRequest request,
            IMovieCatalogRepository movieRepository,
            ISeriesCatalogRepository seriesRepository,
            ILibrariesRepository librariesRepository,
            IMediaDecisionService mediaDecisionService,
            IJobScheduler jobScheduler,
            IAutoTaggingService autoTagging,
            IQualityRepository qualityRepository,
            IMediaStateRepository mediaState,
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

            // The profile picked on the add card must be one made for this kind of title (#1145).
            QualityProfileItem? chosenProfile = null;
            if (!string.IsNullOrWhiteSpace(request.QualityProfileId))
            {
                var profileMediaType = normalized == "series" ? "tv" : "movies";
                chosenProfile = (await qualityRepository.ListQualityProfilesAsync(cancellationToken))
                    .FirstOrDefault(profile => string.Equals(profile.Id, request.QualityProfileId.Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(profile.MediaType, profileMediaType, StringComparison.OrdinalIgnoreCase));
                if (chosenProfile is null)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["qualityProfileId"] = ["Choose a quality profile made for this kind of title."] });
                }
            }

            var addStartedUtc = DateTimeOffset.UtcNow;
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
                        LibraryId: target?.Id,
                        SearchForMissingNow: request.SearchForMissingNow,
                        SearchForCutoffUnmetNow: request.SearchForCutoffUnmetNow),
                    cancellationToken);
                if (chosenProfile is not null)
                {
                    await seriesRepository.UpdateQualityProfileAsync(series.Id, chosenProfile.Id, cancellationToken);
                }

                var seriesOwnProfile = chosenProfile ?? await new TitleProfileLookup(mediaState, qualityRepository)
                    .ForTitleAsync(MediaKind.Series, series.Id, cancellationToken);

                // Same immediate-search wiring the main add-title flow uses
                // (#793): a pending search rides along on the catalogue
                // refresh job rather than racing it, so it runs once the
                // refresh has had a chance to fill in metadata the search
                // decision may need. Discover's own "search-on-add" checkboxes
                // (#785) are that same promise, kept the same way.
                var seriesPendingSearches = new List<object>();
                if (target is not null)
                {
                    var judge = TitleQualityProfile.Apply(target, seriesOwnProfile);
                    var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                        MediaType: "tv",
                        HasFile: series.HasFile,
                        CurrentQuality: null,
                        CutoffQuality: judge.CutoffQuality,
                        UpgradeUntilCutoff: judge.UpgradeUntilCutoff,
                        UpgradeUnknownItems: judge.UpgradeUnknownItems));

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

                    var searchOnAddRequested = request.Monitored &&
                        ((request.SearchForMissingNow && decision.WantedStatus == WantedStatuses.Missing) ||
                        (request.SearchForCutoffUnmetNow && decision.WantedStatus == WantedStatuses.Upgrade));
                    if (searchOnAddRequested)
                    {
                        seriesPendingSearches.Add(new
                        {
                            libraryId = target.Id,
                            libraryName = target.Name,
                            mediaType = target.MediaType,
                            retryDelayHours = target.RetryDelayHours,
                            triggeredBy = "series-added",
                            targetEntityId = series.Id
                        });
                    }
                }

                // #1024: auto-tagging rules, before the catalogue refresh and any search.
                var seriesIsNew = series.CreatedUtc >= addStartedUtc;
                await autoTagging.ApplyAsync(
                    MediaKind.Series,
                    series.Id,
                    seriesIsNew ? AutoTagTrigger.Added : AutoTagTrigger.Edited,
                    seriesIsNew ? new AutoTagAddFacts(false, false, null) : null,
                    target is null ? null : [target.Id],
                    cancellationToken);

                await jobScheduler.EnqueueAsync(
                    new EnqueueJobRequest(
                        JobType: "series.catalog.refresh",
                        Source: "series",
                        PayloadJson: JsonSerializer.Serialize(new
                        {
                            series.Id,
                            series.Title,
                            series.ImdbId,
                            pendingSearches = seriesPendingSearches
                        }),
                        RelatedEntityType: "series",
                        RelatedEntityId: series.Id),
                    cancellationToken);

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
                    LibraryId: target?.Id,
                    SearchForMissingNow: request.SearchForMissingNow,
                    SearchForCutoffUnmetNow: request.SearchForCutoffUnmetNow),
                cancellationToken);
            if (chosenProfile is not null)
            {
                await movieRepository.UpdateQualityProfileAsync(movie.Id, chosenProfile.Id, cancellationToken);
            }

            var movieOwnProfile = chosenProfile ?? await new TitleProfileLookup(mediaState, qualityRepository)
                .ForTitleAsync(MediaKind.Movie, movie.Id, cancellationToken);

            var moviePendingSearches = new List<object>();
            if (target is not null)
            {
                var judge = TitleQualityProfile.Apply(target, movieOwnProfile);
                var decision = mediaDecisionService.DecideWantedState(new MediaWantedDecisionInput(
                    MediaType: "movie",
                    HasFile: movie.HasFile,
                    CurrentQuality: movie.CurrentQuality,
                    CutoffQuality: judge.CutoffQuality,
                    UpgradeUntilCutoff: judge.UpgradeUntilCutoff,
                    UpgradeUnknownItems: judge.UpgradeUnknownItems));

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

                var searchOnAddRequested = request.Monitored &&
                    ((request.SearchForMissingNow && decision.WantedStatus == WantedStatuses.Missing) ||
                    (request.SearchForCutoffUnmetNow && decision.WantedStatus == WantedStatuses.Upgrade));
                if (searchOnAddRequested)
                {
                    moviePendingSearches.Add(new
                    {
                        libraryId = target.Id,
                        libraryName = target.Name,
                        mediaType = target.MediaType,
                        retryDelayHours = target.RetryDelayHours,
                        triggeredBy = "movie-added",
                        targetEntityId = movie.Id
                    });
                }
            }

            // #1024: auto-tagging rules, before the catalogue refresh and any search.
            var movieIsNew = movie.CreatedUtc >= addStartedUtc;
            await autoTagging.ApplyAsync(
                MediaKind.Movie,
                movie.Id,
                movieIsNew ? AutoTagTrigger.Added : AutoTagTrigger.Edited,
                movieIsNew ? new AutoTagAddFacts(false, false, null) : null,
                target is null ? null : [target.Id],
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
                        pendingSearches = moviePendingSearches
                    }),
                    RelatedEntityType: "movie",
                    RelatedEntityId: movie.Id),
                cancellationToken);

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

/// <summary>
/// <see cref="Popular"/> holds the titles of the list asked for (the name is older than #1429): <see cref="List"/> says which
/// one it is (popular, trending, top-rated, upcoming, now-playing, or discover for a filtered search) and <see cref="Page"/> its page.
/// </summary>
public sealed record DiscoverResponse(IReadOnlyList<DiscoverItem> Popular, IReadOnlyList<DiscoverListSection> Lists, string List = "popular", int Page = 1);

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
    bool Monitored,
    /// <summary>Search for this title's missing content right away, once it is added from Discover (#785, mirroring #793). Off by default.</summary>
    bool SearchForMissingNow = false,
    /// <summary>Search for this title's cutoff-unmet content right away, once it is added from Discover (#785, mirroring #793). Off by default.</summary>
    bool SearchForCutoffUnmetNow = false);

public sealed record DiscoverAddResponse(string Id, string MediaType, string? LibraryId);

/// <summary>The Discover filters a person can set (#1429). Every one is optional; a value that is not understood is ignored.</summary>
public sealed record DiscoverFilterQuery(
    string? List = null,
    /// <summary>released | upcoming</summary>
    string? Release = null,
    int? YearFrom = null,
    int? YearTo = null,
    /// <summary>Comma-separated TMDb genre ids.</summary>
    string? Genres = null,
    double? RatingMin = null,
    int? VotesMin = null,
    /// <summary>The title's original language, as a two or three letter code.</summary>
    string? Language = null,
    int? RuntimeMin = null,
    int? RuntimeMax = null,
    /// <summary>popular | rating | newest | oldest</summary>
    string? Sort = null,
    /// <summary>day | week, for the trending list.</summary>
    string? Window = null);

/// <summary>Turns the filters on the Discover page into the broker's list ask.</summary>
public static class DiscoverFilters
{
    public static DiscoverListQuery ToListQuery(DiscoverFilterQuery query, string tmdbMediaType, int page, DateOnly today)
    {
        var isTv = tmdbMediaType == "tv";
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        var dateField = isTv ? "first_air_date" : "primary_release_date";

        var from = Year(query.YearFrom) is { } yearFrom ? new DateOnly(yearFrom, 1, 1) : (DateOnly?)null;
        var to = Year(query.YearTo) is { } yearTo ? new DateOnly(yearTo, 12, 31) : (DateOnly?)null;
        switch (query.Release?.Trim().ToLowerInvariant())
        {
            case "released":
                to = to is { } limit && limit < today ? limit : today;
                break;
            case "upcoming":
                var tomorrow = today.AddDays(1);
                from = from is { } start && start > tomorrow ? start : tomorrow;
                break;
        }

        if (from is { } gte) filters[$"{dateField}.gte"] = gte.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (to is { } lte) filters[$"{dateField}.lte"] = lte.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var genres = (query.Genres ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.Length is > 0 and <= 8 && id.All(char.IsAsciiDigit))
            .Distinct()
            .ToArray();
        if (genres.Length > 0) filters["with_genres"] = string.Join(',', genres);

        if (query.RatingMin is { } rating and > 0 and <= 10) filters["vote_average.gte"] = rating.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (query.VotesMin is { } votes and > 0) filters["vote_count.gte"] = votes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (query.Language?.Trim().ToLowerInvariant() is { Length: >= 2 and <= 3 } language && language.All(char.IsAsciiLetter)) filters["with_original_language"] = language;
        if (query.RuntimeMin is { } runtimeMin and > 0) filters["with_runtime.gte"] = runtimeMin.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (query.RuntimeMax is { } runtimeMax and > 0) filters["with_runtime.lte"] = runtimeMax.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var sort = query.Sort?.Trim().ToLowerInvariant() switch
        {
            "rating" => "vote_average.desc",
            "newest" => $"{dateField}.desc",
            "oldest" => $"{dateField}.asc",
            _ => null
        };
        if (sort is not null) filters["sort_by"] = sort;
        // TMDb advises a vote floor when sorting by rating, or a title one person rated 10 tops the list.
        if (sort == "vote_average.desc" && !filters.ContainsKey("vote_count.gte")) filters["vote_count.gte"] = "200";

        if (filters.Count > 0)
        {
            return new DiscoverListQuery(DiscoverListQuery.Discover, page, filters);
        }

        var list = query.List?.Trim().ToLowerInvariant();
        if (list is null || !DiscoverListQuery.Offers(list, tmdbMediaType) || list == DiscoverListQuery.Discover)
        {
            list = DiscoverListQuery.Popular;
        }

        return new DiscoverListQuery(list, page, null, query.Window?.Trim().ToLowerInvariant() == "day" ? "day" : "week");
    }

    private static int? Year(int? year) => year is >= 1888 and <= 2100 ? year : null;
}
