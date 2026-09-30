using Deluno.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Quality.Data;
using Deluno.Series.Data;

namespace Deluno.Retention;

/// <summary>
/// What Deluno itself knows about a title, at any level (#1003 to #1014). A null
/// is "not known", and a rule reading it leaves the title undecided.
/// </summary>
public sealed record RetentionTitleFacts(
    string? QualityProfile = null,
    bool? Monitored = null,
    string? SeriesType = null,
    int? MissingEpisodes = null,
    bool? SeasonFinale = null,
    bool? SeriesFinale = null,
    int? EpisodeRank = null,
    int? SeasonRank = null,
    /// <summary>
    /// Upcoming: an episode with a future air date, or a season or show every one of whose episodes has one (an
    /// undated episode is Missing, not Upcoming). The word movies use. Retention never acts on it.
    /// </summary>
    bool Upcoming = false);

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
    IReadOnlyList<string> EpisodeIds,
    /// <summary>How many of a season's episodes with a file were left out because they have not aired (a future air date). Retention never touches those.</summary>
    int UpcomingLeftAlone = 0);

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
    DateTimeOffset? AddedUtc = null,
    RetentionTitleFacts? Facts = null)
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
    TimeProvider timeProvider,
    IQualityRepository? qualityProfiles = null)
{
    private const int PageSize = 500;

    /// <param name="needs">What the rule's fields need read about each title; nothing extra is read for a rule that uses none of them.</param>
    public async Task<IReadOnlyList<RetentionUnit>> ReadAsync(string level, CancellationToken cancellationToken, RetentionUnitNeeds needs = RetentionUnitNeeds.None)
    {
        var mediaType = RetentionLevels.MediaType(level);
        var allLibraries = (await libraries.ListLibrariesAsync(cancellationToken))
            .Where(library => (library.MediaType == "tv") == (mediaType == "tv"))
            .ToArray();
        var roots = allLibraries.ToDictionary(library => library.Id, library => library.RootPath, StringComparer.OrdinalIgnoreCase);
        var profiles = new ProfileNames(
            allLibraries.ToDictionary(library => library.Id, library => library.QualityProfileName, StringComparer.OrdinalIgnoreCase),
            needs.HasFlag(RetentionUnitNeeds.Profile) && qualityProfiles is not null
                ? (await qualityProfiles.ListQualityProfilesAsync(cancellationToken)).ToDictionary(profile => profile.Id, profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            needs.HasFlag(RetentionUnitNeeds.Profile));

        return level == RetentionLevels.Movie
            ? await ReadMoviesAsync(allLibraries.Select(library => library.Id), roots, profiles, cancellationToken)
            : await ReadShowsAsync(level, allLibraries.Select(library => library.Id), roots, profiles, needs, cancellationToken);
    }

    /// <summary>
    /// The quality profile's name for a title: its own choice if it has one, else
    /// its library's (#1014). Null when neither is known.
    /// </summary>
    private sealed record ProfileNames(
        IReadOnlyDictionary<string, string?> ByLibrary,
        IReadOnlyDictionary<string, string> ById,
        bool Wanted)
    {
        public string? Of(string? overrideId, string? libraryId)
        {
            if (!Wanted)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(overrideId))
            {
                return ById.TryGetValue(overrideId, out var chosen) ? chosen : null;
            }

            return libraryId is not null && ByLibrary.TryGetValue(libraryId, out var name) ? name : null;
        }
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
                var page = await series.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, IncludeUnmonitoredInStatus: true, PageSize: PageSize, PageToken: token, Filters: filters), cancellationToken);
                ids.UnionWith(page.Items.Select(item => item.Id));
                token = page.HasMore ? page.NextPageToken : null;
            }
            else
            {
                var page = await movies.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, IncludeUnmonitoredInStatus: true, PageSize: PageSize, PageToken: token, Filters: filters), cancellationToken);
                ids.UnionWith(page.Items.Select(item => item.Id));
                token = page.HasMore ? page.NextPageToken : null;
            }
        }
        while (token is not null);

        return ids;
    }

    private async Task<IReadOnlyList<RetentionUnit>> ReadMoviesAsync(IEnumerable<string> libraryIds, IReadOnlyDictionary<string, string> roots, ProfileNames profiles, CancellationToken cancellationToken)
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
            var page = await movies.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, IncludeUnmonitoredInStatus: true, PageSize: PageSize, PageToken: token), cancellationToken);
            foreach (var item in page.Items)
            {
                var held = files.TryGetValue(item.Id, out var list) ? list : [];
                units.Add(new RetentionUnit(
                    RetentionLevels.Movie, item.Id, null, null, null, item.Title, item.Title, item.ReleaseYear, item.ImdbId,
                    Tmdb(item.MetadataProvider, item.MetadataProviderId), null, item.FileSizeBytes,
                    held.Select(file => file.Path).ToArray(),
                    RootsOf(held.Select(file => file.LibraryId), roots),
                    AddedUtc: item.CreatedUtc,
                    Facts: new RetentionTitleFacts(
                        QualityProfile: profiles.Wanted ? profiles.Of(await movies.GetQualityProfileIdAsync(item.Id, cancellationToken), held.Select(file => file.LibraryId).FirstOrDefault()) : null,
                        Monitored: item.Monitored)));
            }

            token = page.HasMore ? page.NextPageToken : null;
        }
        while (token is not null);

        return units;
    }

    private async Task<IReadOnlyList<RetentionUnit>> ReadShowsAsync(string level, IEnumerable<string> libraryIds, IReadOnlyDictionary<string, string> roots, ProfileNames profiles, RetentionUnitNeeds needs, CancellationToken cancellationToken)
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

        // Whether a show has ended is asked of the catalogue, once, and only when a
        // rule reads the series finale. A show with no status either way is unknown.
        IReadOnlySet<string>? endedIds = null, runningIds = null;
        if (needs.HasFlag(RetentionUnitNeeds.ShowStatus))
        {
            endedIds = await CatalogueMatchesAsync(MediaKind.Series, CatalogueFilters.Parse(MediaKind.Series, ["seriesStatus:in:Ended|Canceled"], out _), cancellationToken);
            runningIds = await CatalogueMatchesAsync(MediaKind.Series, CatalogueFilters.Parse(MediaKind.Series, ["seriesStatus:in:Returning Series|In Production|Planned|Pilot"], out _), cancellationToken);
        }

        bool? Ended(string showId) => endedIds is null || runningIds is null ? null : endedIds.Contains(showId) ? true : runningIds.Contains(showId) ? false : null;

        string? token = null;
        do
        {
            var page = await series.ListPageAsync(new CatalogueQuery(Status: CatalogueStatusFilters.Downloaded, IncludeUnmonitoredInStatus: true, PageSize: PageSize, PageToken: token), cancellationToken);
            foreach (var show in page.Items)
            {
                var held = files.TryGetValue(show.Id, out var list) ? list : [];
                var tmdb = Tmdb(show.MetadataProvider, show.MetadataProviderId);
                var showRoots = RootsOf(held.Select(file => file.LibraryId), roots);
                var profile = profiles.Wanted ? profiles.Of(await series.GetQualityProfileIdAsync(show.Id, cancellationToken), held.Select(file => file.LibraryId).FirstOrDefault()) : null;
                if (level == RetentionLevels.Show)
                {
                    RetentionEpisodeMath? showMath = null;
                    // Always read: whether the show is Upcoming is asked of every show, whatever the steps need.
                    if (await series.GetInventoryDetailAsync(show.Id, cancellationToken) is { } showInventory)
                    {
                        showMath = new RetentionEpisodeMath(showInventory.Episodes, now);
                    }

                    units.Add(new RetentionUnit(
                        RetentionLevels.Show, show.Id, null, null, null, show.Title, show.Title, show.StartYear, show.ImdbId, tmdb, Tvdb(show.MetadataProvider, show.MetadataProviderId),
                        show.FileSizeBytes, held.Select(file => file.File.FilePath).ToArray(), showRoots,
                        AddedUtc: show.CreatedUtc,
                        Facts: new RetentionTitleFacts(
                            QualityProfile: profile,
                            Monitored: show.Monitored,
                            SeriesType: show.SeriesType,
                            MissingEpisodes: needs.HasFlag(RetentionUnitNeeds.Inventory) ? showMath?.Missing(null) : null,
                            Upcoming: showMath?.ShowUpcoming() == true,
                            SeriesFinale: showMath?.SeriesFinale(null, null, Ended(show.Id)))));
                    continue;
                }

                var withEpisodes = held.Where(file => file.File.SeasonNumber is not null).ToArray();
                if (withEpisodes.Length == 0)
                {
                    continue;
                }

                var inventory = await series.GetInventoryDetailAsync(show.Id, cancellationToken);
                var episodes = inventory?.Episodes ?? [];
                // A show whose episodes could not be read says nothing about finales or ranks.
                var math = inventory is null ? null : new RetentionEpisodeMath(episodes, now);
                var latestSeason = episodes.Where(episode => episode.SeasonNumber > 0).Select(episode => episode.SeasonNumber).DefaultIfEmpty(0).Max();
                bool Unaired(int season) => episodes.Any(episode => episode.SeasonNumber == season && (episode.AirDateUtc is null || episode.AirDateUtc > now));

                if (level == RetentionLevels.Season)
                {
                    var upcomingIds = episodes
                        .Where(episode => RetentionEpisodeMath.EpisodeUpcoming(episode, now))
                        .Select(episode => episode.EpisodeId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var season in withEpisodes.GroupBy(file => file.File.SeasonNumber!.Value).OrderBy(group => group.Key))
                    {
                        // A season in progress is acted on for what has aired only: episodes with a future air date are left out of
                        // what it holds and of what a rule removes (#1072). A season with nothing aired is Upcoming as a whole.
                        var airedFiles = season.Where(file => file.File.EpisodeId is null || !upcomingIds.Contains(file.File.EpisodeId)).ToArray();
                        var leftAlone = season.Count() - airedFiles.Length;
                        var seasonFiles = airedFiles.Length == 0 ? season.ToArray() : airedFiles;
                        var episodeIds = seasonFiles.Select(file => file.File.EpisodeId).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        units.Add(new RetentionUnit(
                            RetentionLevels.Season, RetentionUnit.SeasonId(show.Id, season.Key), show.Id, season.Key, null,
                            season.Key == 0 ? $"{show.Title} — Specials" : $"{show.Title} — Season {season.Key}",
                            show.Title, show.StartYear, show.ImdbId, tmdb, Tvdb(show.MetadataProvider, show.MetadataProviderId),
                            seasonFiles.Sum(file => file.File.FileSizeBytes ?? 0),
                            seasonFiles.Select(file => file.File.FilePath).ToArray(), showRoots,
                            new RetentionPartFacts(
                                season.Key == latestSeason, Unaired(season.Key), episodeIds.Length, null,
                                seasonFiles.Select(file => file.File.ImportedUtc).Where(when => when is not null).Max(),
                                null, null, null, null, episodeIds, leftAlone),
                            AddedUtc: show.CreatedUtc,
                            Facts: new RetentionTitleFacts(
                                QualityProfile: profile,
                                Monitored: math?.SeasonMonitored(season.Key),
                                SeriesType: show.SeriesType,
                                MissingEpisodes: math?.Missing(season.Key),
                                Upcoming: airedFiles.Length == 0 || math?.SeasonUpcoming(season.Key) == true,
                                SeasonFinale: math?.SeasonFinale(season.Key, null),
                                SeriesFinale: math?.SeriesFinale(season.Key, null, Ended(show.Id)),
                                SeasonRank: math?.SeasonRank(season.Key))));
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
                        show.Title, show.StartYear, show.ImdbId, tmdb, Tvdb(show.MetadataProvider, show.MetadataProviderId),
                        file.File.FileSizeBytes, [file.File.FilePath], showRoots,
                        new RetentionPartFacts(
                            season == latestSeason, Unaired(season), 1, episode?.AirDateUtc, file.File.ImportedUtc,
                            episode?.CurrentQuality, episode?.QualityCutoffMet, episode?.Monitored, file.File.FilePath, [file.File.EpisodeId!]),
                        AddedUtc: show.CreatedUtc,
                        Facts: new RetentionTitleFacts(
                            QualityProfile: profile,
                            Monitored: episode?.Monitored,
                            SeriesType: show.SeriesType,
                            SeasonFinale: math?.SeasonFinale(season, file.File.EpisodeId),
                            SeriesFinale: math?.SeriesFinale(season, file.File.EpisodeId, Ended(show.Id)),
                            EpisodeRank: math?.EpisodeRank(file.File.EpisodeId!),
                            Upcoming: episode is not null && RetentionEpisodeMath.EpisodeUpcoming(episode, now))));
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

    private static string? Tvdb(string? provider, string? providerId)
        => string.Equals(provider, "tvdb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(providerId) ? providerId : null;

    private static string? Tmdb(string? provider, string? providerId)
        => string.Equals(provider, "tmdb", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(providerId) ? providerId : null;
}
