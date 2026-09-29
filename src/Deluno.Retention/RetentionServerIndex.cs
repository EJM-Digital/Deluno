using Deluno.Connections.Contracts;
using Deluno.Integrations.MediaServers;

namespace Deluno.Retention;

/// <summary>A unit as one server holds it, with what sits under it.</summary>
/// <param name="Episodes">For a show or season, its episodes on that server; empty otherwise.</param>
/// <param name="Users">Everyone that server knows, so "everyone watched it" can be asked.</param>
public sealed record RetentionServerMatch(
    MediaServerConnection Server,
    MediaServerItem Item,
    IReadOnlyList<MediaServerItem> Episodes,
    IReadOnlyList<string> Users)
{
    /// <summary>For a season or an episode: every episode of its show on this server (#1007).</summary>
    public IReadOnlyList<MediaServerItem>? ShowEpisodes { get; init; }

    /// <summary>For a film or show in a collection: the newest finished view of anything sharing one with it; null when it is in none or nothing was viewed.</summary>
    public DateTimeOffset? CollectionNewestView { get; init; }

    /// <summary>Whether it is in any (ordinary) collection, so "no view" can be told from "no collection".</summary>
    public bool InCollection => Item.Collections.Count > 0;
}

/// <summary>
/// One server's library, indexed so each unit finds its item (#494).
///
/// <para><b>By path first, then by id.</b> A path match cannot pick the wrong
/// film; an id match covers a server that sees the files through a different
/// mount. A season is found under its matched show by number; an episode by its
/// file, or under its show by season and episode number.</para>
/// </summary>
public sealed class RetentionServerIndex
{
    private readonly MediaServerConnection _server;
    private readonly MediaServerLibrary _library;
    private readonly IReadOnlyList<MediaServerItem> _titles;
    private readonly ILookup<string, MediaServerItem> _episodesByShow;
    private readonly Dictionary<string, MediaServerItem> _episodesByPath;

    public RetentionServerIndex(MediaServerConnection server, MediaServerLibrary library)
    {
        _server = server;
        _library = library;
        _titles = library.Items.Where(item => item.Level is MediaServerItemLevels.Movie or MediaServerItemLevels.Show).ToArray();
        var episodes = library.Items.Where(item => item.Level == MediaServerItemLevels.Episode).ToArray();
        _episodesByShow = episodes.Where(item => item.ShowServerItemId is not null).ToLookup(item => item.ShowServerItemId!, StringComparer.OrdinalIgnoreCase);
        _episodesByPath = new Dictionary<string, MediaServerItem>(StringComparer.Ordinal);
        foreach (var episode in episodes)
        {
            foreach (var path in episode.Paths)
            {
                _episodesByPath.TryAdd(NormalizePath(path), episode);
            }
        }

        _collectionViews = new Lazy<Dictionary<string, DateTimeOffset?>>(() =>
        {
            var views = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
            foreach (var title in _titles.Where(title => title.Collections.Count > 0))
            {
                var viewed = NewestFinishedView(title, title.Level == MediaServerItemLevels.Show ? _episodesByShow[title.ServerItemId] : []);
                foreach (var name in title.Collections)
                {
                    views[name] = views.TryGetValue(name, out var seen) && seen > viewed ? seen : viewed ?? seen;
                }
            }

            return views;
        });
    }

    /// <summary>
    /// Every collection's name to the newest finished view of anything in it
    /// (#1007): a title's own, and for a show, any of its episodes'.
    /// </summary>
    private readonly Lazy<Dictionary<string, DateTimeOffset?>> _collectionViews;

    internal static DateTimeOffset? NewestFinishedView(MediaServerItem item, IEnumerable<MediaServerItem> episodes)
        => item.Viewers.Where(viewer => viewer.Played).Concat(episodes.SelectMany(episode => episode.Viewers.Where(viewer => viewer.Played)))
            .Select(viewer => viewer.LastPlayedUtc)
            .Append(item.Watch.PlayedByAnyone ? item.Watch.LastPlayedUtc : null)
            .Max();

    public MediaServerConnection Server => _server;

    public IReadOnlyList<string> Users => _library.Users;

    /// <summary>Movies or shows on this server that no Deluno title matched.</summary>
    public IEnumerable<MediaServerItem> Unmatched(IReadOnlySet<string> matchedItemIds)
        => _titles.Where(item => !matchedItemIds.Contains(item.ServerItemId));

    public RetentionServerMatch? Find(RetentionUnit unit)
    {
        switch (unit.Level)
        {
            case RetentionLevels.Movie:
            case RetentionLevels.Show:
            {
                var title = FindTitle(unit);
                return title is null
                    ? null
                    : new RetentionServerMatch(_server, title, unit.Level == RetentionLevels.Show ? _episodesByShow[title.ServerItemId].ToArray() : [], _library.Users)
                    {
                        CollectionNewestView = title.Collections.Count == 0 ? null : title.Collections.Select(name => _collectionViews.Value.GetValueOrDefault(name)).Max()
                    };
            }

            case RetentionLevels.Season:
            {
                var show = FindTitle(unit);
                if (show is null)
                {
                    return null;
                }

                var season = _library.Items.FirstOrDefault(item =>
                    item.Level == MediaServerItemLevels.Season &&
                    string.Equals(item.ShowServerItemId, show.ServerItemId, StringComparison.OrdinalIgnoreCase) &&
                    item.SeasonNumber == unit.SeasonNumber);
                if (season is null)
                {
                    return null;
                }

                var episodes = _episodesByShow[show.ServerItemId]
                    .Where(item => string.Equals(item.SeasonServerItemId, season.ServerItemId, StringComparison.OrdinalIgnoreCase) || item.SeasonNumber == unit.SeasonNumber)
                    .ToArray();
                return new RetentionServerMatch(_server, season, episodes, _library.Users) { ShowEpisodes = _episodesByShow[show.ServerItemId].ToArray() };
            }

            default:
            {
                foreach (var path in unit.Paths)
                {
                    if (_episodesByPath.TryGetValue(NormalizePath(path), out var byPath))
                    {
                        return new RetentionServerMatch(_server, byPath, [], _library.Users)
                        {
                            ShowEpisodes = byPath.ShowServerItemId is null ? null : _episodesByShow[byPath.ShowServerItemId].ToArray()
                        };
                    }
                }

                var show = FindTitle(unit);
                var episode = show is null
                    ? null
                    : _episodesByShow[show.ServerItemId].FirstOrDefault(item => item.SeasonNumber == unit.SeasonNumber && item.EpisodeNumber == unit.EpisodeNumber);
                return episode is null ? null : new RetentionServerMatch(_server, episode, [], _library.Users) { ShowEpisodes = _episodesByShow[show!.ServerItemId].ToArray() };
            }
        }
    }

    /// <summary>The movie or show a unit belongs to.</summary>
    private MediaServerItem? FindTitle(RetentionUnit unit)
    {
        var wantShow = unit.Level != RetentionLevels.Movie;
        var candidates = _titles.Where(item => (item.Level == MediaServerItemLevels.Show) == wantShow);
        foreach (var item in candidates)
        {
            foreach (var serverPath in item.Paths.Select(NormalizePath))
            {
                foreach (var titlePath in unit.Paths.Select(NormalizePath))
                {
                    if (titlePath == serverPath) return item;
                    if (wantShow && titlePath.StartsWith(serverPath + "/", StringComparison.Ordinal)) return item;
                }
            }
        }

        return candidates.FirstOrDefault(item =>
            (unit.TmdbId is not null && string.Equals(unit.TmdbId, item.TmdbId, StringComparison.OrdinalIgnoreCase)) ||
            (unit.ImdbId is not null && string.Equals(unit.ImdbId, item.ImdbId, StringComparison.OrdinalIgnoreCase)));
    }

    internal static string NormalizePath(string path)
        => path.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
