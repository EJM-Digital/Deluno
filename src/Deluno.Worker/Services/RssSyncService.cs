using System.Text.Json;
using Deluno.Api.IndexerHistory;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Platform.Data;
using Deluno.Series.Contracts;
using Deluno.Series.Data;
using Microsoft.Extensions.Logging;

namespace Deluno.Worker.Services;

/// <summary>What one RSS sync pass did.</summary>
public sealed record RssSyncResult(int IndexersRead, int NewReleases, IReadOnlyList<string> SearchesQueued);

/// <summary>
/// Reads each indexer's newest releases and searches straight away for any
/// title they are for that is wanted (#587).
///
/// <para><b>Why.</b> Without it a release was found only when its title's turn
/// came round in the search cycle, at a few titles per run. On the soak rig ten
/// episodes of a show with hundreds that will never be released took every
/// slot, and the Breaking Bad episodes the indexer had just published waited
/// behind them. Radarr and Sonarr read the feed every fifteen minutes.</para>
///
/// <para><b>What it does not do.</b> It never grabs. A match queues the same
/// targeted search a person's Search would, so the profile, the blocklist, the
/// refusal hold, the in-flight check and every other rule decide - there is no
/// second set of rules here to drift from the first.</para>
/// </summary>
public sealed class RssSyncService(
    IConnectionsRepository connectionsRepository,
    ILibrariesRepository librariesRepository,
    IMovieCatalogRepository movieCatalogRepository,
    ISeriesCatalogRepository seriesCatalogRepository,
    IIndexerFeedReader feedReader,
    IRssSyncStateRepository stateRepository,
    IJobScheduler jobScheduler,
    IActivityFeedRepository activityFeed,
    IIndexerHistoryEventStore indexerHistory,
    TimeProvider timeProvider,
    ILogger<RssSyncService> logger,
    Deluno.Contracts.IMediaTagStore? mediaTags = null)
{
    public async Task<RssSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var libraries = (await librariesRepository.ListLibrariesAsync(cancellationToken))
            .Where(library => library.AutoSearchEnabled)
            .ToArray();
        var librariesByIndexer = new Dictionary<string, List<LibraryItem>>(StringComparer.OrdinalIgnoreCase);
        // The library's own choice for each indexer (#1354): "Only for titles tagged" and "Never for titles tagged".
        var linkOf = new Dictionary<(string Library, string Indexer), LibrarySourceLinkItem>();
        foreach (var library in libraries)
        {
            var routing = await librariesRepository.GetLibraryRoutingAsync(library.Id, cancellationToken);
            foreach (var source in routing?.Sources ?? [])
            {
                linkOf[(library.Id, source.IndexerId)] = source;
                if (!librariesByIndexer.TryGetValue(source.IndexerId, out var list))
                {
                    librariesByIndexer[source.IndexerId] = list = [];
                }

                list.Add(library);
            }
        }

        var indexers = (await connectionsRepository.ListIndexersAsync(cancellationToken))
            .Where(indexer => indexer.IsEnabled && indexer.RssEnabled && indexer.AutomaticSearchEnabled
                && librariesByIndexer.ContainsKey(indexer.Id))
            .ToArray();
        if (indexers.Length == 0)
        {
            return new RssSyncResult(0, 0, []);
        }

        // What each library still wants, from its wanted state: the catalogue
        // list carries neither the library nor the wanted status. Monitoring
        // comes from the catalogue, because the wanted list read regardless of
        // the retry window does not filter on it.
        var now = timeProvider.GetUtcNow();
        var movieMonitored = libraries.Any(IsMovies)
            ? (await movieCatalogRepository.ListAsync(cancellationToken)).ToDictionary(movie => movie.Id, movie => movie, StringComparer.OrdinalIgnoreCase)
            : [];
        var showMonitored = libraries.Any(library => !IsMovies(library))
            ? (await seriesCatalogRepository.ListAsync(cancellationToken)).ToDictionary(show => show.Id, show => show, StringComparer.OrdinalIgnoreCase)
            : [];
        var moviesWanted = new Dictionary<string, IReadOnlyList<Deluno.Movies.Contracts.MovieWantedItem>>(StringComparer.OrdinalIgnoreCase);
        var showsWanted = new Dictionary<string, IReadOnlyList<SeriesWantedItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            if (IsMovies(library))
            {
                moviesWanted[library.Id] = await movieCatalogRepository.ListEligibleWantedAsync(library.Id, WholeLibrary, now, ignoreRetryWindow: true, cancellationToken);
            }
            else
            {
                showsWanted[library.Id] = await seriesCatalogRepository.ListEligibleWantedAsync(library.Id, WholeLibrary, now, ignoreRetryWindow: true, cancellationToken);
            }
        }
        var needsTags = mediaTags is not null && linkOf.Values.Any(link => !string.IsNullOrWhiteSpace(link.RequiredTags) || !string.IsNullOrWhiteSpace(link.ExcludedTags));
        var movieTags = needsTags && libraries.Any(IsMovies) ? await TagsByIdAsync(Deluno.Contracts.MediaKind.Movie, cancellationToken) : new Dictionary<string, IReadOnlyList<string>>();
        var showTags = needsTags && libraries.Any(library => !IsMovies(library)) ? await TagsByIdAsync(Deluno.Contracts.MediaKind.Series, cancellationToken) : new Dictionary<string, IReadOnlyList<string>>();
        var inventories = new Dictionary<string, SeriesInventoryDetail?>(StringComparer.OrdinalIgnoreCase);
        var lastSeen = new Dictionary<string, DateTimeOffset>(await stateRepository.GetLastSeenAsync(cancellationToken), StringComparer.OrdinalIgnoreCase);
        var queuedThisPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searches = new List<string>();
        var read = 0;
        var fresh = 0;

        foreach (var indexer in indexers)
        {
            var feed = await feedReader.ReadRecentAsync(indexer, cancellationToken);
            if (!feed.Read)
            {
                // Not read is not empty: the mark stays, and the next pass
                // picks up what this one could not see.
                logger.LogInformation("RSS sync skipped {Indexer}: {Problem}", indexer.Name, feed.Problem);
                await indexerHistory.RecordAsync(
                    new RecordIndexerHistoryEventRequest(
                        indexer.Id,
                        indexer.Name,
                        IndexerAuthFailureText.LooksLikeAuthFailure(feed.Problem) ? IndexerHistoryEventTypes.AuthFailure : IndexerHistoryEventTypes.RssSync,
                        "failed",
                        feed.Problem ?? $"{indexer.Name}'s RSS feed could not be read.",
                        FeedRequestDetailsJson(indexer, feed.Problem)),
                    cancellationToken);
                continue;
            }

            read++;
            var mark = lastSeen.TryGetValue(indexer.Id, out var seen) ? seen : (DateTimeOffset?)null;
            // The first read of a feed acts on all of it; after that, only on
            // what was published since. A release with no date can only be
            // told apart from an old one on the first read, so it waits for
            // the search cycle after that.
            var releases = mark is null
                ? feed.Releases
                : feed.Releases.Where(release => release.PublishedUtc > mark).ToArray();
            fresh += releases.Count;
            await indexerHistory.RecordAsync(
                new RecordIndexerHistoryEventRequest(
                    indexer.Id,
                    indexer.Name,
                    IndexerHistoryEventTypes.RssSync,
                    "read",
                    releases.Count == 0
                        ? $"Read {indexer.Name}'s RSS feed. Nothing new."
                        : $"Read {indexer.Name}'s RSS feed. {releases.Count} new release{(releases.Count == 1 ? "" : "s")}.",
                    FeedRequestDetailsJson(indexer, problem: null, releasesRead: releases.Count)),
                cancellationToken);

            foreach (var release in releases)
            {
                var parsed = FeedReleaseNames.Parse(release.ReleaseName);
                if (parsed is null)
                {
                    continue;
                }

                foreach (var library in librariesByIndexer[indexer.Id])
                {
                    var link = linkOf[(library.Id, indexer.Id)];
                    var queued = IsMovies(library)
                        ? await QueueFilmAsync(parsed, release, indexer.Id, library, moviesWanted[library.Id], movieMonitored, queuedThisPass, link, movieTags, cancellationToken)
                        : await QueueEpisodeAsync(parsed, release, indexer.Id, library, showsWanted[library.Id], showMonitored, inventories, queuedThisPass, link, showTags, cancellationToken);
                    if (queued is not null)
                    {
                        searches.Add(queued);
                    }
                }
            }

            var newest = feed.Releases.Select(release => release.PublishedUtc).Where(date => date is not null).Max();
            var moved = newest ?? timeProvider.GetUtcNow();
            lastSeen[indexer.Id] = mark is { } before && before > moved ? before : moved;
        }

        await stateRepository.SaveLastSeenAsync(lastSeen, cancellationToken);
        if (searches.Count > 0)
        {
            await activityFeed.RecordActivityAsync(
                "rss.sync",
                $"New releases for {searches.Count} wanted title{(searches.Count == 1 ? "" : "s")} appeared on your indexers, so Deluno searched for {(searches.Count == 1 ? "it" : "them")} straight away: {string.Join(", ", searches)}.",
                JsonSerializer.Serialize(new { indexersRead = read, newReleases = fresh, searches }),
                null,
                "rss",
                null,
                cancellationToken);
        }

        return new RssSyncResult(read, fresh, searches);
    }

    private async Task<string?> QueueFilmAsync(
        ParsedFeedRelease parsed,
        IndexerFeedRelease release,
        string indexerId,
        LibraryItem library,
        IReadOnlyList<Deluno.Movies.Contracts.MovieWantedItem> wanted,
        IReadOnlyDictionary<string, Deluno.Movies.Contracts.MovieListItem> catalogue,
        HashSet<string> queuedThisPass,
        LibrarySourceLinkItem link,
        IReadOnlyDictionary<string, IReadOnlyList<string>> titleTags,
        CancellationToken cancellationToken)
    {
        if (parsed.IsEpisode || parsed.Year is null)
        {
            return null;
        }

        var title = FeedReleaseNames.NormalizeTitle(parsed.Title);
        var found = wanted.FirstOrDefault(item =>
            (item.ReleaseYear is null || item.ReleaseYear == parsed.Year)
            && (FeedReleaseNames.NormalizeTitle(item.Title) == title
                || (catalogue.TryGetValue(item.MovieId, out var entry) && FeedReleaseNames.NormalizeTitle(entry.OriginalTitle) == title)));
        if (found is null
            || !catalogue.TryGetValue(found.MovieId, out var movie)
            || !movie.Monitored
            || !IsWanted(found.WantedStatus)
            || !TagsAllow(link, titleTags, movie.Id)
            || !queuedThisPass.Add($"movie:{movie.Id}"))
        {
            return null;
        }

        await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                LibrarySearchJobTypes.Movies,
                "rss",
                JsonSerializer.Serialize(new
                {
                    libraryId = library.Id,
                    libraryName = library.Name,
                    mediaType = "movies",
                    checkMissing = true,
                    checkUpgrades = true,
                    maxItems = 1,
                    retryDelayHours = library.RetryDelayHours,
                    triggeredBy = "rss",
                    targetEntityId = movie.Id,
                    // The indexer whose new release started this search: it is waited for, not skipped for pacing (#1184).
                    priorityIndexerIds = new[] { indexerId }
                }),
                "movie",
                movie.Id,
                IdempotencyKey: $"rss:{movie.Id}:{release.ReleaseName}",
                DedupeKey: $"rss:{movie.Id}"),
            cancellationToken);
        return movie.ReleaseYear is { } year ? $"{movie.Title} ({year})" : movie.Title;
    }

    private async Task<string?> QueueEpisodeAsync(
        ParsedFeedRelease parsed,
        IndexerFeedRelease release,
        string indexerId,
        LibraryItem library,
        IReadOnlyList<SeriesWantedItem> wanted,
        IReadOnlyDictionary<string, SeriesListItem> catalogue,
        Dictionary<string, SeriesInventoryDetail?> inventories,
        HashSet<string> queuedThisPass,
        LibrarySourceLinkItem link,
        IReadOnlyDictionary<string, IReadOnlyList<string>> titleTags,
        CancellationToken cancellationToken)
    {
        if (!parsed.IsEpisode)
        {
            return null;
        }

        var title = FeedReleaseNames.NormalizeTitle(parsed.Title);
        var bare = FeedReleaseNames.WithoutTrailingYear(title);
        var found = wanted.FirstOrDefault(item =>
            MatchesShow(item.Title, catalogue.TryGetValue(item.SeriesId, out var entry) ? entry.OriginalTitle : null, item.StartYear, title, bare));
        if (found is null || !catalogue.TryGetValue(found.SeriesId, out var show) || !show.Monitored)
        {
            return null;
        }

        if (!TagsAllow(link, titleTags, show.Id))
        {
            return null;
        }

        if (!inventories.TryGetValue(show.Id, out var inventory))
        {
            inventories[show.Id] = inventory = await seriesCatalogRepository.GetInventoryDetailAsync(show.Id, cancellationToken);
        }

        var episode = inventory?.Episodes.FirstOrDefault(item =>
            item.SeasonNumber == parsed.Season && item.EpisodeNumber == parsed.Episode);
        if (episode is null || !episode.Monitored || !IsWanted(episode.WantedStatus) || !queuedThisPass.Add($"episode:{episode.EpisodeId}"))
        {
            return null;
        }

        await jobScheduler.EnqueueAsync(
            new EnqueueJobRequest(
                "episode.search",
                "rss",
                JsonSerializer.Serialize(new
                {
                    episodeId = episode.EpisodeId,
                    seriesId = show.Id,
                    libraryId = library.Id,
                    seasonNumber = episode.SeasonNumber,
                    episodeNumber = episode.EpisodeNumber,
                    title = episode.Title ?? $"S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}",
                    priorityIndexerIds = new[] { indexerId }
                }),
                "episode",
                episode.EpisodeId,
                IdempotencyKey: $"rss:{episode.EpisodeId}:{release.ReleaseName}",
                // The scheduled search's key: one search of an episode at a time,
                // whichever pass asked for it.
                DedupeKey: $"episode.search:{episode.EpisodeId}"),
            cancellationToken);
        return $"{show.Title} S{episode.SeasonNumber:00}E{episode.EpisodeNumber:00}";
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> TagsByIdAsync(Deluno.Contracts.MediaKind kind, CancellationToken cancellationToken)
        => (await mediaTags!.ListAllTitleTagsAsync(kind, cancellationToken)).ToDictionary(title => title.Id, title => title.Tags, StringComparer.OrdinalIgnoreCase);

    /// <summary>The library's "Only for titles tagged" / "Never for titles tagged" choice for this indexer lets the title through (#1354).</summary>
    private static bool TagsAllow(LibrarySourceLinkItem link, IReadOnlyDictionary<string, IReadOnlyList<string>> titleTags, string titleId)
        => Deluno.Contracts.TagRoutingMatch.LibrarySourceIsEligible(link.RequiredTags, link.ExcludedTags, titleTags.TryGetValue(titleId, out var tags) ? tags : null);

    private static bool MatchesShow(string showTitle, string? originalTitle, int? startYear, string title, string bare)
    {
        var name = FeedReleaseNames.NormalizeTitle(showTitle);
        var original = FeedReleaseNames.NormalizeTitle(originalTitle);
        var withYear = startYear is { } year ? $"{name} {year}" : null;
        return name == title || name == bare || (original.Length > 0 && (original == title || original == bare)) || withYear == title;
    }

    /// <summary>Every wanted title in a library, not a page of them.</summary>
    private const int WholeLibrary = 100_000;

    private static bool IsMovies(LibraryItem library)
        => !string.Equals(DelunoValueNormalizers.NormalizeMediaType(library.MediaType), "tv", StringComparison.OrdinalIgnoreCase);

    private static bool IsWanted(string? wantedStatus)
    {
        try
        {
            return WantedStatuses.IsSearchable(wantedStatus);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A status Deluno does not know is not a reason to download.
            return false;
        }
    }

    /// <summary>
    /// What was asked and what came back, for the combined Indexer History
    /// row's own expandable detail (#754) -- the feed address queried and
    /// either the problem it answered with or how many releases it held.
    /// </summary>
    private static string FeedRequestDetailsJson(IndexerItem indexer, string? problem, int? releasesRead = null)
        => JsonSerializer.Serialize(new
        {
            request = new { indexer = indexer.Name, baseUrl = indexer.BaseUrl, protocol = indexer.Protocol },
            response = problem is null
                ? new { releasesRead }
                : (object)new { problem }
        });
}
