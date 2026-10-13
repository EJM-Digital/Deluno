using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1194: an ignore entry can name a tag (the client's own on the torrent, or one Deluno holds for the title or
/// indexer) or a tracker domain (the host of the torrent's announce URL, subdomains included). Today's match on
/// the indexer's name stays.
/// </summary>
public sealed class TheIgnoreListMatchesTagsAndTrackerDomainsTests
{
    private static DownloadHealthIgnoreEntry Entry(string? hash = null, string? category = null, string? domain = null, string? tag = null)
        => new("e", hash, category, domain, null, DateTimeOffset.UnixEpoch, tag);

    [Fact]
    public void A_tracker_domain_matches_the_torrents_announce_host_and_its_subdomains()
    {
        var entry = Entry(domain: "tracker.example.org");

        Assert.True(entry.Matches("h", "movies", "Some Indexer", null, ["tracker.example.org"]));
        Assert.True(Entry(domain: "example.org").Matches("h", "movies", "Some Indexer", null, ["tracker.example.org"]));
        Assert.False(entry.Matches("h", "movies", "Some Indexer", null, ["tracker.other.net"]));
        Assert.False(Entry(domain: "ample.org").Matches("h", "movies", "Some Indexer", null, ["tracker.example.org"]));
    }

    [Fact]
    public void A_pasted_announce_url_is_reduced_to_its_host()
        => Assert.True(Entry(domain: "https://tracker.example.org:443/announce?passkey=x").Matches("h", "movies", "Indexer", null, ["tracker.example.org"]));

    [Fact]
    public void The_indexer_name_match_that_was_there_before_still_works()
        => Assert.True(Entry(domain: "pubtracker").Matches("h", "movies", "PubTracker (usenet)"));

    [Fact]
    public void A_tag_matches_the_clients_tag_or_a_tag_deluno_holds_and_nothing_else()
    {
        var entry = Entry(tag: "keep");

        Assert.True(entry.Matches("h", "movies", "Indexer", ["Keep"], null, null));
        Assert.True(entry.Matches("h", "movies", "Indexer", null, null, ["keep"]));
        Assert.False(entry.Matches("h", "movies", "Indexer", ["other"], null, ["another"]));
    }

    [Fact]
    public async Task Qbittorrent_reports_its_tags_and_every_tracker_host_so_a_tracker_domain_entry_exempts_it()
    {
        // qBittorrent 5.2.1's recorded list of a torrent with no peers (state stalledDL) carrying the tags under test, and its recorded
        // tracker list (DHT, PeX, LSD) with the PeX line replaced by a real tracker's announce address.
        var torrents = RecordedQbittorrentRig.Info(
            RecordedQbittorrentRig.Stalled, ("hash", "abc123"), ("category", "movies"), ("tags", "keep, private"), ("tracker", string.Empty));
        var trackers = RecordedReplies.Qbittorrent("trackers").Body("torrents/trackers", ("1.url", "https://tracker.example.org:443/abc/announce"));
        var client = new QbittorrentDownloadClient(() => RecordedQbittorrentRig.Handler(torrents, trackers));

        var snapshot = await client.GetSnapshotAsync(
            new("qbit-1", "qBittorrent", "qbittorrent", "localhost", 8080, null, null, "http://localhost:8080/", false, null, "movies", "tv", null, 1, true,
                "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow, CancellationToken.None);
        var item = Assert.Single(snapshot!.Queue);

        Assert.Equal(["keep", "private"], item.Tags);
        Assert.Equal(["tracker.example.org"], item.TrackerHosts);
        Assert.True(Entry(domain: "tracker.example.org").Matches(item.Id, item.Category, item.IndexerName, item.Tags, item.TrackerHosts));
        Assert.True(Entry(tag: "private").Matches(item.Id, item.Category, item.IndexerName, item.Tags, item.TrackerHosts));
    }
}
