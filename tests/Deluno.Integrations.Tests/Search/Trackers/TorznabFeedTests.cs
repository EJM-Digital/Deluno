using System.Xml.Linq;
using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>#606: a tracker's releases reach the planner as the Torznab feed every other indexer sends.</summary>
public sealed class TorznabFeedTests
{
    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";

    [Fact]
    public void Writes_what_the_planner_reads_from_any_torznab_indexer()
    {
        var release = new TrackerRelease
        {
            Title = "Night.of.the.Living.Dead.1968.1080p.BluRay.x264-GRP",
            DownloadUrl = "https://tracker.example/download.php?id=7",
            DetailsUrl = "https://tracker.example/details.php?id=7",
            InfoHash = "0123456789ABCDEF0123456789ABCDEF01234567",
            Size = 8_589_934_592,
            Seeders = 12,
            Peers = 15,
            PublishDate = new DateTimeOffset(2026, 9, 20, 18, 30, 0, TimeSpan.Zero),
            DownloadVolumeFactor = 0,
            UploadVolumeFactor = 1
        };
        release.Categories.Add(2040);

        var item = TorznabFeed.Write([release]).Descendants("item").Single();

        Assert.Equal(release.Title, item.Element("title")!.Value);
        Assert.Equal(release.PublishDate, DateTimeOffset.Parse(item.Element("pubDate")!.Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(release.DownloadUrl, item.Element("enclosure")!.Attribute("url")!.Value);
        string? Attr(string name) => item.Elements(Torznab + "attr").FirstOrDefault(attr => attr.Attribute("name")!.Value == name)?.Attribute("value")!.Value;
        Assert.Equal("8589934592", Attr("size"));
        Assert.Equal("12", Attr("seeders"));
        Assert.Equal("2040", Attr("category"));
        Assert.Equal(release.InfoHash, Attr("infohash"));
        Assert.Equal("freeleech", Attr("flags"));
    }

    [Fact]
    public void A_magnet_only_release_links_to_its_magnet()
    {
        var release = new TrackerRelease { Title = "Big.Buck.Bunny.2008", MagnetUrl = "magnet:?xt=urn:btih:abcdef0123456789abcdef0123456789abcdef01" };

        var item = TorznabFeed.Write([release]).Descendants("item").Single();

        Assert.Equal(release.MagnetUrl, item.Element("enclosure")!.Attribute("url")!.Value);
        Assert.Equal("ABCDEF0123456789ABCDEF0123456789ABCDEF01", item.Elements(Torznab + "attr").Single(attr => attr.Attribute("name")!.Value == "infohash").Attribute("value")!.Value);
    }
}
