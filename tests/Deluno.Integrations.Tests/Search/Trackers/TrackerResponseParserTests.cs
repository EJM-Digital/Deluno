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
