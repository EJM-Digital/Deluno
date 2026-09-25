using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #606: real pages, recorded from the trackers on 24 September 2026, read
/// through the definitions Deluno ships.
/// </summary>
public sealed class TrackerResponseParserTests
{
    private static readonly TrackerDefinitionCatalog Catalog = new(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);

    [Fact]
    public void Yts_lists_every_copy_of_the_film_with_its_quality_in_the_title()
    {
        var releases = Parse("yts", "yts.json", new TrackerQuery("Night of the Living Dead 1968", "movie", ImdbId: "tt0063350", Categories: [2000]));

        Assert.Equal(3, releases.Count);
        Assert.All(releases, release =>
        {
            Assert.StartsWith("Night of the Living Dead (1968) ", release.Title);
            Assert.EndsWith("-YTS", release.Title);
            Assert.StartsWith("https://yts.gg/torrent/download/", release.DownloadUrl);
            Assert.Equal(40, release.InfoHash!.Length);
            Assert.True(release.Size > 100_000_000);
            Assert.NotNull(release.Seeders);
            Assert.Equal("tt0063350", release.ImdbId);
            Assert.NotNull(release.PublishDate);
            Assert.Contains(release.Categories, category => category / 1000 == 2);
        });
        Assert.Contains(releases, release => release.Title!.Contains("1080p", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pirate_bay_api_gives_magnets_sizes_and_seeders()
    {
        var releases = Parse("thepiratebay", "thepiratebay.json", new TrackerQuery("Night of the Living Dead 1968", "movie", Categories: [2000]));

        Assert.Equal(46, releases.Count);
        var first = releases[0];
        Assert.Equal("Night.of.the.Living.Dead.1968.REMASTERED.1080p.BluRay.X264-AMIAB", first.Title);
        Assert.Equal(10575236092L, first.Size);
        Assert.Equal(49, first.Seeders);
        Assert.StartsWith("magnet:?xt=urn:btih:", first.MagnetUrl);
        Assert.NotNull(first.PublishDate);
    }

    /// <summary>
    /// #606: EZTV's JSON API only filters server-side by IMDb id; an episode
    /// search with one reads every row EZTV has for the show, and only the
    /// requested season/episode survive the row-level filter.
    /// </summary>
    [Fact]
    public void Eztv_with_an_imdb_id_keeps_only_the_requested_episode()
    {
        var releases = Parse(
            "eztv",
            "eztv.json",
            new TrackerQuery("Young Sheldon", "tvsearch", Season: 7, Episode: 9, ImdbId: "tt6226232", Categories: [5000]));

        var release = Assert.Single(releases);
        Assert.StartsWith("Young Sheldon S07E09", release.Title);
        Assert.StartsWith("magnet:?xt=urn:btih:", release.MagnetUrl);
        Assert.Equal(40, release.InfoHash!.Length);
        Assert.True(release.Size > 100_000_000);
        Assert.Equal(3, release.Seeders);
        Assert.NotNull(release.PublishDate);
    }

    /// <summary>A season search (no episode number) keeps the whole season.</summary>
    [Fact]
    public void Eztv_with_an_imdb_id_and_no_episode_keeps_the_whole_season()
    {
        var releases = Parse(
            "eztv",
            "eztv.json",
            new TrackerQuery("Young Sheldon", "tvsearch", Season: 7, ImdbId: "tt6226232", Categories: [5000]));

        Assert.Equal(10, releases.Count);
        Assert.All(releases, release => Assert.StartsWith("Young Sheldon S07", release.Title));
    }

    /// <summary>
    /// Without an IMDb id, EZTV's API hands back its newest torrents across
    /// every show on the site. A keywords-only search must not surface them
    /// as if they were an answer for the searched show (#606): the fixture's
    /// rows are all "Young Sheldon", so a search for something else reads
    /// nothing from it, same as the andmatch row filter every other keyword
    /// tracker definition uses.
    /// </summary>
    [Fact]
    public void Eztv_without_an_imdb_id_reads_nothing_for_an_unrelated_keyword_search()
    {
        var releases = Parse(
            "eztv",
            "eztv.json",
            new TrackerQuery("The Simpsons S01E01", "tvsearch", Season: 1, Episode: 1, Categories: [5000]));

        Assert.Empty(releases);
    }

    [Fact]
    public void Limetorrents_html_rows_become_releases_that_point_at_their_details_page()
    {
        var releases = Parse("limetorrents", "limetorrents.html", new TrackerQuery("Night of the Living Dead 1968", "movie", Categories: [2000]));

        Assert.NotEmpty(releases);
        Assert.All(releases, release =>
        {
            Assert.False(string.IsNullOrWhiteSpace(release.Title));
            Assert.True(release.Size > 0, $"{release.Title} has no size.");
            Assert.NotNull(release.Seeders);
            Assert.NotNull(release.PublishDate);
            // The .torrent is on the details page; the download block finds it at grab time.
            Assert.StartsWith("https://www.limetorrents.fun/", release.DownloadUrl);
        });
    }

    [Fact]
    public void The_1337x_rows_point_at_a_details_page_the_magnet_is_fetched_from()
    {
        var releases = Parse("1337x", "1337x.html", new TrackerQuery("Night of the Living Dead 1968", "movie", Categories: [2000]));

        Assert.Equal(2, releases.Count);
        Assert.All(releases, release =>
        {
            Assert.StartsWith("Night of the Living Dead 1968", release.Title);
            Assert.StartsWith("https://1337x.to/torrent/", release.DownloadUrl);
            Assert.NotNull(release.Seeders);
            Assert.NotNull(release.Peers);
            Assert.True(release.Size > 0);
            Assert.Contains(release.Categories, category => category / 1000 == 2);
        });
    }

    [Fact]
    public void Rutor_rows_already_carry_a_magnet_link()
    {
        // Recorded from rutor.info on 25 September 2026: a search for The
        // Wild Robot, 23 results striped across "gai" and "tum" rows.
        var releases = Parse("rutor", "rutor.html", new TrackerQuery("The Wild Robot", "search", Categories: [2000]));

        Assert.Equal(23, releases.Count);
        Assert.All(releases, release =>
        {
            Assert.Contains("The Wild Robot", release.Title);
            Assert.StartsWith("magnet:?xt=urn:btih:", release.MagnetUrl);
            Assert.True(release.Size > 100_000_000);
            Assert.NotNull(release.Seeders);
            Assert.NotNull(release.Peers);
        });
        Assert.Contains(releases, release => release.Title!.Contains("BDRip 720p", StringComparison.Ordinal));
    }

    [Fact]
    public void Torrentdownloads_search_feed_gives_the_info_hash_so_no_second_page_is_needed()
    {
        // Recorded from torrentdownloads.pro's rss.xml?type=search on
        // 25 September 2026: a search for The Wild Robot, 50 items.
        var releases = Parse("torrentdownloads", "torrentdownloads.xml", new TrackerQuery("The Wild Robot", "search", Categories: [2000]));

        Assert.Equal(50, releases.Count);
        Assert.All(releases, release =>
        {
            Assert.False(string.IsNullOrWhiteSpace(release.Title));
            Assert.Equal(40, release.InfoHash!.Length);
            Assert.True(release.Size > 0);
            Assert.NotNull(release.Seeders);
        });
        var first = releases[0];
        Assert.Equal("The Wild Robot (2024) [1080p] [WEBRip] [5 1] [YTS MX]", first.Title);
        Assert.Equal(2014280923, first.Size);
        Assert.Equal(765, first.Seeders);
    }

    [Fact]
    public void Internet_archive_builds_the_torrent_address_from_each_items_identifier()
    {
        var releases = Parse("internetarchive", "internetarchive.json", new TrackerQuery("ubuntu", "search", Categories: [2000]));

        Assert.Equal(2, releases.Count);
        var first = releases[0];
        Assert.Equal("Ubuntu 24.04.1 Release Notes Screencast", first.Title);
        Assert.Equal("https://archive.org/download/ubuntu-24-04-1-release-notes-screencast/ubuntu-24-04-1-release-notes-screencast_archive.torrent", first.DownloadUrl);
        Assert.Equal(828815442L, first.Size);
        Assert.Contains(first.Categories, category => category / 1000 == 2);
    }

    private static IReadOnlyList<TrackerRelease> Parse(string id, string fixture, TrackerQuery query)
    {
        var definition = Catalog.Find(id)!;
        var link = definition.Links[0];
        var connection = new TrackerConnection("test", definition, link, new Dictionary<string, string?>());
        var variables = TrackerVariables.Build(connection, query, link);
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Search", "Trackers", "Fixtures", fixture));
        var result = TrackerResponseParser.Parse(definition, definition.Search.Paths[0], body, new Uri(link), variables, new TrackerContext(new DateTimeOffset(2026, 9, 24, 8, 12, 0, TimeSpan.Zero)));
        Assert.Equal(0, result.RowsSkipped);
        return result.Releases;
    }
}
