using Deluno.Jobs.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Contracts;
using Deluno.Series.Data;

namespace Deluno.Worker.Services;

/// <summary>
/// Finds the title a processor hand-off belongs to (#1104): the hand-off's download
/// names its dispatch, the dispatch names a movie, an episode or a show, and the movie
/// or series catalogue answers from what it already stores. Nothing is fetched.
/// </summary>
public sealed class ProcessorHandoffTitleResolver(
    IDownloadDispatchesRepository dispatches,
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series,
    IArtworkRemoteUrls artwork) : IProcessorHandoffTitleResolver
{
    public async Task<ProcessorHandoffTitle?> ResolveAsync(ProcessorHandoffItem handoff, CancellationToken cancellationToken)
    {
        var dispatch = await dispatches.FindAnyDispatchByHashAsync(handoff.ClientId, handoff.QueueItemId, cancellationToken);
        if (dispatch is null || string.IsNullOrWhiteSpace(dispatch.EntityId))
        {
            return null;
        }

        switch (dispatch.EntityType)
        {
            case "movie":
            {
                var movie = await movies.GetByIdAsync(dispatch.EntityId, cancellationToken);
                return movie is null
                    ? null
                    : new ProcessorHandoffTitle(
                        Title: Clean(movie.Title),
                        Year: movie.ReleaseYear,
                        TmdbId: TmdbId(movie.MetadataProvider, movie.MetadataProviderId),
                        ImdbId: Clean(movie.ImdbId),
                        PosterUrl: await PublicPosterAsync(movie.PosterUrl, cancellationToken));
            }
            case "episode":
            {
                var seriesId = await series.GetEpisodeSeriesIdAsync(dispatch.EntityId, cancellationToken);
                if (seriesId is null)
                {
                    return null;
                }

                var numbers = (await series.ListEpisodeNumbersAsync([dispatch.EntityId], cancellationToken)).FirstOrDefault();
                var show = await series.GetByIdAsync(seriesId, cancellationToken);
                return show is null ? null : await ForShowAsync(show, numbers.SeasonNumber, numbers.EpisodeNumber, numbers.EpisodeId is not null, cancellationToken);
            }
            case "series":
            {
                var show = await series.GetByIdAsync(dispatch.EntityId, cancellationToken);
                return show is null ? null : await ForShowAsync(show, 0, 0, false, cancellationToken);
            }
            default:
                return null;
        }
    }

    private async Task<ProcessorHandoffTitle> ForShowAsync(
        Deluno.Series.Contracts.SeriesListItem show, int season, int episode, bool hasEpisode, CancellationToken cancellationToken)
        => new(
            Title: Clean(show.Title),
            Year: show.StartYear,
            TmdbId: TmdbId(show.MetadataProvider, show.MetadataProviderId),
            TvdbId: Number(show.TvDbId),
            ImdbId: Clean(show.ImdbId),
            Season: hasEpisode ? season : null,
            Episode: hasEpisode ? episode : null,
            PosterUrl: await PublicPosterAsync(show.PosterUrl, cancellationToken));

    private const string LocalArtworkPrefix = "/api/metadata/artwork/";

    /// <summary>
    /// An absolute https address a processor can fetch on its own. A stored provider address is
    /// used as it is; a locally cached one is traced back to the address it was downloaded from.
    /// A path that only works with Deluno's own sign-in is never sent.
    /// </summary>
    private async Task<string?> PublicPosterAsync(string? stored, CancellationToken cancellationToken)
    {
        var url = Clean(stored);
        if (url is null)
        {
            return null;
        }

        if (url.StartsWith(LocalArtworkPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var key = url[LocalArtworkPrefix.Length..].TrimEnd('/');
            url = key.Length == 0 ? null : Clean(await artwork.FindRemoteUrlAsync(key, cancellationToken));
        }

        return url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }

    private static int? TmdbId(string? provider, string? providerId)
        => string.Equals(provider?.Trim(), "tmdb", StringComparison.OrdinalIgnoreCase) ? Number(providerId) : null;

    private static int? Number(string? value)
        => int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
