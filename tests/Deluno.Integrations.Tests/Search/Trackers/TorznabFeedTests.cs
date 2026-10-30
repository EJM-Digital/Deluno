using System.Xml.Linq;
using Deluno.Integrations.Search.Trackers;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>#606: a tracker's releases reach the planner as the Torznab feed every other indexer sends.</summary>
public sealed class TorznabFeedTests
{
    // The namespace of Jackett 0.24.2850's recorded Torznab feed, which the feed Deluno writes has to share.
    private static readonly XNamespace Torznab = XDocument.Parse(Deluno.Tests.Recorded.RecordedReplies.Jackett("search-feed").Exchanges[0].Reply.Body!).Root!.GetNamespaceOfPrefix("torznab")!;

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

    // #745: the structured flags a media-plan "Indexer flag" custom-format
    // condition and the legacy free-text preference both read (FeedMediaSearchPlanner
    // joins every "flags"/"flag" attr into one comma list) come from the same
    // ratio facts every private tracker already reports, not only Freeleech.

    [Fact]
    public void A_half_leech_release_is_flagged_half_leech()
    {
        var release = new TrackerRelease { Title = "Half Leech Release", MagnetUrl = "magnet:?xt=urn:btih:0000000000000000000000000000000000000000", DownloadVolumeFactor = 0.5, UploadVolumeFactor = 1 };

        var item = TorznabFeed.Write([release]).Descendants("item").Single();

        var flags = item.Elements(Torznab + "attr").Where(attr => attr.Attribute("name")!.Value == "flags").Select(attr => attr.Attribute("value")!.Value).ToArray();
        Assert.Equal(["half leech"], flags);
    }

    [Fact]
    public void A_double_upload_release_is_flagged_double_upload()
    {
        var release = new TrackerRelease { Title = "Double Upload Release", MagnetUrl = "magnet:?xt=urn:btih:1111111111111111111111111111111111111111", DownloadVolumeFactor = 1, UploadVolumeFactor = 2 };

        var item = TorznabFeed.Write([release]).Descendants("item").Single();

        var flags = item.Elements(Torznab + "attr").Where(attr => attr.Attribute("name")!.Value == "flags").Select(attr => attr.Attribute("value")!.Value).ToArray();
        Assert.Equal(["double upload"], flags);
    }

    [Fact]
    public void A_freeleech_and_double_upload_release_carries_both_flags()
    {
        var release = new TrackerRelease { Title = "Both Flags Release", MagnetUrl = "magnet:?xt=urn:btih:2222222222222222222222222222222222222222", DownloadVolumeFactor = 0, UploadVolumeFactor = 2 };

        var item = TorznabFeed.Write([release]).Descendants("item").Single();

        var flags = item.Elements(Torznab + "attr").Where(attr => attr.Attribute("name")!.Value == "flags").Select(attr => attr.Attribute("value")!.Value).ToArray();
        Assert.Equal(["freeleech", "double upload"], flags);
    }

    [Fact]
    public void A_release_with_ordinary_ratios_carries_no_flags()
    {
        var release = new TrackerRelease { Title = "Ordinary Release", MagnetUrl = "magnet:?xt=urn:btih:3333333333333333333333333333333333333333", DownloadVolumeFactor = 1, UploadVolumeFactor = 1 };

        var item = TorznabFeed.Write([release]).Descendants("item").Single();

        Assert.DoesNotContain(item.Elements(Torznab + "attr"), attr => attr.Attribute("name")!.Value == "flags");
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
