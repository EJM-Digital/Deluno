using Deluno.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Series.Data;

namespace Deluno.Retention;

/// <summary>What Deluno knows about a season or an episode that its show does not say.</summary>
public sealed record RetentionPartFacts(
    bool LatestSeason,
    bool HasUnaired,
    int EpisodesHeld,
    DateTimeOffset? AirDate,
    DateTimeOffset? Imported,
    string? Quality,
    bool? CutoffMet,
    bool? Monitored,
    string? Path,
    IReadOnlyList<string> EpisodeIds);

/// <summary>
/// One thing a rule can act on: a movie, a show, a season or an episode that
/// Deluno holds a file for (#494).
/// </summary>
/// <param name="EntityId">
/// The movie or show id; <c>{showId}:s{n}</c> for a season; the episode id for an episode.
/// </param>
/// <param name="ShowId">The show a season or episode belongs to, whose catalogue fields it carries.</param>
/// <param name="Paths">The files Deluno tracks for it.</param>
/// <param name="LibraryRoots">Where its libraries live, for the drive's free space.</param>
public sealed record RetentionUnit(
    string Level,
    string EntityId,
    string? ShowId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string Title,
    string ShowTitle,
    int? Year,
    string? ImdbId,
    string? TmdbId,
    string? TvdbId,
    long? SizeBytes,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> LibraryRoots,
    RetentionPartFacts? Part = null,
    /// <summary>
    /// When it joined Deluno (a season or episode carries its show's date, the
    /// same inheritance every other show-level field uses). Feeds the
    /// *SinceAdded server fields (#834): activity only counts once it is set
    /// and falls after this.
    /// </summary>
    DateTimeOffset? AddedUtc = null)
{
    /// <summary>The movie or show the catalogue answers for.</summary>
    public string CatalogueId => ShowId ?? EntityId;

    public static string SeasonId(string showId, int season) => $"{showId}:s{season}";
}

/// <summary>Reads every movie, show, season or episode Deluno holds, at one level.</summary>
public sealed class RetentionUnitReader(
    IMovieCatalogRepository movies,
    ISeriesCatalogRepository series,
    ILibrariesRepository libraries,
    TimeProvider timeProvider)
{
    private const int PageSize = 500;

    public async Task<IReadOnlyList<RetentionUnit>> ReadAsync(string level, CancellationToken cancellationToken)
    {
        var mediaType = RetentionLevels.MediaType(level);
        var allLibraries = (await libraries.ListLibrariesAsync(cancellationToken))
            .Where(library => (library.MediaType == "tv") == (mediaType == "tv"))
            .ToArray();
        var roots = allLibraries.ToDictionary(library => library.Id, library => library.RootPath, StringComparer.OrdinalIgnoreCase);

        return level == RetentionLevels.Movie
            ? await ReadMoviesAsync(allLibraries.Select(library => library.Id), roots, cancellationToken)
            : await ReadShowsAsync(level, allLibraries.Select(library => library.Id), roots, cancellationToken);
    }

    /// <summary>The ids a single catalogue condition holds for, among titles with files.</summary>
    public async Task<IReadOnlySet<string>> CatalogueMatchesAsync(MediaKind kind, CatalogueFilters filters, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? token = null;
        do
        {
            if (kind == MediaKind.Series)
            {
                var page = await series.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, PageSize: PageSize, PageToken: token, Filters: filters), cancellationToken);
                ids.UnionWith(page.Items.Select(item => item.Id));
                token = page.HasMore ? page.NextPageToken : null;
            }
            else
            {
                var page = await movies.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, PageSize: PageSize, PageToken: token, Filters: filters), cancellationToken);
                ids.UnionWith(page.Items.Select(item => item.Id));
                token = page.HasMore ? page.NextPageToken : null;
            }
        }
        while (token is not null);

        return ids;
    }

    private async Task<IReadOnlyList<RetentionUnit>> ReadMoviesAsync(IEnumerable<string> libraryIds, IReadOnlyDictionary<string, string> roots, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, List<(string Path, string LibraryId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var libraryId in libraryIds)
        {
            await foreach (var file in movies.StreamTrackedFilesAsync(libraryId, cancellationToken))
            {
                if (!files.TryGetValue(file.MovieId, out var list)) files[file.MovieId] = list = [];
                list.Add((file.FilePath, libraryId));
            }
        }

        var units = new List<RetentionUnit>();
        string? token = null;
        do
        {
            var page = await movies.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, PageSize: PageSize, PageToken: token), cancellationToken);
            foreach (var item in page.Items)
            {
                var held = files.TryGetValue(item.Id, out var list) ? list : [];
                units.Add(new RetentionUnit(
                    RetentionLevels.Movie, item.Id, null, null, null, item.Title, item.Title, item.ReleaseYear, item.ImdbId,
                    Tmdb(item.MetadataProvider, item.MetadataProviderId), null, item.FileSizeBytes,
                    held.Select(file => file.Path).ToArray(),
                    RootsOf(held.Select(file => file.LibraryId), roots),
                    AddedUtc: item.CreatedUtc));
            }

            token = page.HasMore ? page.NextPageToken : null;
        }
        while (token is not null);

        return units;
    }

    private async Task<IReadOnlyList<RetentionUnit>> ReadShowsAsync(string level, IEnumerable<string> libraryIds, IReadOnlyDictionary<string, string> roots, CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, List<(Deluno.Series.Contracts.SeriesTrackedFileItem File, string LibraryId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var libraryId in libraryIds)
        {
            await foreach (var file in series.StreamTrackedFilesAsync(libraryId, cancellationToken))
            {
                if (!files.TryGetValue(file.SeriesId, out var list)) files[file.SeriesId] = list = [];
                list.Add((file, libraryId));
            }
        }

        var units = new List<RetentionUnit>();
        var now = timeProvider.GetUtcNow();
        string? token = null;
        do
        {
            var page = await series.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, PageSize: PageSize, PageToken: token), cancellationToken);
            foreach (var show in page.Items)
            {
                var held = files.TryGetValue(show.Id, out var list) ? list : [];
                var tmdb = Tmdb(show.MetadataProvider, show.MetadataProviderId);
                var showRoots = RootsOf(held.Select(file => file.LibraryId), roots);
                if (level == RetentionLevels.Show)
                {
                    units.Add(new RetentionUnit(
                        RetentionLevels.Show, show.Id, null, null, null, show.Title, show.Title, show.StartYear, show.ImdbId, tmdb, null,
                        show.FileSizeBytes, held.Select(file => file.File.FilePath).ToArray(), showRoots,
                        AddedUtc: show.CreatedUtc));
                    continue;
                }

                var withEpisodes = held.Where(file => file.File.SeasonNumber is not null).ToArray();
                if (withEpisodes.Length == 0)
                {
                    continue;
                }

                var inventory = await series.GetInventoryDetailAsync(show.Id, cancellationToken);
                var episodes = inventory?.Episodes ?? [];
                var latestSeason = episodes.Where(episode => episode.SeasonNumber > 0).Select(episode => episode.SeasonNumber).DefaultIfEmpty(0).Max();
                bool Unaired(int season) => episodes.Any(episode => episode.SeasonNumber == season && (episode.AirDateUtc is null || episode.AirDateUtc > now));

                if (level == RetentionLevels.Season)
                {
                    foreach (var season in withEpisodes.GroupBy(file => file.File.SeasonNumber!.Value).OrderBy(group => group.Key))
                    {
                        var episodeIds = season.Select(file => file.File.EpisodeId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        units.Add(new RetentionUnit(
                            RetentionLevels.Season, RetentionUnit.SeasonId(show.Id, season.Key), show.Id, season.Key, null,
                            season.Key == 0 ? $"{show.Title} — Specials" : $"{show.Title} — Season {season.Key}",
                            show.Title, show.StartYear, show.ImdbId, tmdb, null,
                            season.Sum(file => file.File.FileSizeBytes ?? 0),
                            season.Select(file => file.File.FilePath).ToArray(), showRoots,
                            new RetentionPartFacts(
                                season.Key == latestSeason, Unaired(season.Key), episodeIds.Length, null,
                                season.Select(file => file.File.ImportedUtc).Where(when => when is not null).Max(),
                                null, null, null, null, episodeIds),
                            AddedUtc: show.CreatedUtc));
                    }

                    continue;
                }

                foreach (var file in withEpisodes.Where(file => file.File.EpisodeId is not null))
                {
                    var episode = episodes.FirstOrDefault(entry => string.Equals(entry.EpisodeId, file.File.EpisodeId, StringComparison.OrdinalIgnoreCase));
                    var number = file.File.EpisodeNumber ?? episode?.EpisodeNumber;
                    var season = file.File.SeasonNumber!.Value;
                    units.Add(new RetentionUnit(
                        RetentionLevels.Episode, file.File.EpisodeId!, show.Id, season, number,
                        $"{show.Title} — S{season:00}E{number ?? 0:00}{(string.IsNullOrWhiteSpace(episode?.Title) ? string.Empty : $" {episode!.Title}")}",
                        show.Title, show.StartYear, show.ImdbId, tmdb, null,
                        file.File.FileSizeBytes, [file.File.FilePath], showRoots,
                        new RetentionPartFacts(
                            season == latestSeason, Unaired(season), 1, episode?.AirDateUtc, file.File.ImportedUtc,
                            episode?.CurrentQuality, episode?.QualityCutoffMet, episode?.Monitored, file.File.FilePath, [file.File.EpisodeId!]),
                        AddedUtc: show.CreatedUtc));
                }
            }

            token = page.HasMore ? page.NextPageToken : null;
        }
        while (token is not null);

        return units;
    }

    private static IReadOnlyList<string> RootsOf(IEnumerable<string> libraryIds, IReadOnlyDictionary<string, string> roots)
        => libraryIds.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => roots.TryGetValue(id, out var root) ? root : null)
            .OfType<string>()
            .ToArray();

    private static string? Tmdb(string? provider, string? providerId)
        => string.Equals(provider, "tmdb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(providerId) ? providerId : null;
}
