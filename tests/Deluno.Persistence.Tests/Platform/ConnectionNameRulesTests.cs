using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Platform;

/// <summary>The naming rules of ConnectionNames on their own: addresses, labels and clashes.</summary>
public sealed class ConnectionNameRulesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("[::1]", true)]
    [InlineData("::1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("media-box", false)]
    public void A_machines_own_address_is_a_loopback(string? host, bool expected)
        => Assert.Equal(expected, ConnectionNames.IsLoopback(host));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_address_has_no_host_and_no_port(string? address)
    {
        Assert.Null(ConnectionNames.HostOf(address));
        Assert.Null(ConnectionNames.PortOf(address));
    }

    [Theory]
    [InlineData("nas:8081", "nas", 8081)]
    [InlineData("https://nas.example.test:9443/x", "nas.example.test", 9443)]
    [InlineData("http://nas.example.test/x", "nas.example.test", null)]
    [InlineData("nas", "nas", null)]
    [InlineData("::1", "THIS", null)]
    [InlineData("http://localhost:7878", "THIS", 7878)]
    public void The_host_and_port_come_from_a_bare_host_or_a_full_address(string address, string host, int? port)
    {
        var expectedHost = host == "THIS" ? DelunoHostName.Current : host;
        Assert.Equal(expectedHost, ConnectionNames.HostOf(address));
        Assert.Equal(port, ConnectionNames.PortOf(address));
    }

    [Fact]
    public void An_app_is_named_with_its_host_when_there_is_one()
    {
        Assert.Equal("qBittorrent on nas", ConnectionNames.AppOnHost("qBittorrent", "nas:8081"));
        Assert.Equal("qBittorrent", ConnectionNames.AppOnHost("qBittorrent", null));
    }

    [Theory]
    [InlineData(null, "Indexer")]
    [InlineData("", "Indexer")]
    [InlineData("https://www.nzbgeek.info/api", "nzbgeek.info")]
    [InlineData("https://api.nzbgeek.info/api", "nzbgeek.info")]
    [InlineData("https://www.com/api", "www.com")]
    [InlineData("https://tracker.example.test:8080/path", "tracker.example.test")]
    [InlineData("http://localhost:9117/api/v2.0/indexers/1337x/results/torznab", "1337x")]
    public void An_indexer_is_named_after_its_site(string? address, string expected)
        => Assert.Equal(expected, ConnectionNames.SiteName(address));

    [Fact]
    public void An_indexer_with_no_address_takes_the_fallback_given()
        => Assert.Equal("Mine", ConnectionNames.SiteName(null, "Mine"));

    [Fact]
    public void Jackett_and_prowlarr_addresses_are_told_apart_and_ordinary_ones_are_not_aggregators()
    {
        var jackett = ConnectionNames.AggregatorOf("http://box:9117/api/v2.0/indexers/1337x/results/torznab");
        Assert.NotNull(jackett);
        Assert.Equal(ConnectionNames.AggregatorKind.Jackett, jackett.Kind);
        Assert.Equal("1337x", jackett.Id);
        Assert.Equal("http://box:9117", jackett.Origin);
        Assert.Equal(string.Empty, jackett.UrlBase);

        var prowlarr = ConnectionNames.AggregatorOf("http://box:9696/12/api");
        Assert.NotNull(prowlarr);
        Assert.Equal(ConnectionNames.AggregatorKind.Prowlarr, prowlarr.Kind);
        Assert.Equal("12", prowlarr.Id);
        Assert.Equal(string.Empty, prowlarr.UrlBase);
        Assert.Null(ConnectionNames.AggregatedTrackerOf("http://box:9696/12/api"));

        var withBase = ConnectionNames.AggregatorOf("http://box/prowlarr/7/api");
        Assert.NotNull(withBase);
        Assert.Equal("/prowlarr", withBase.UrlBase);

        Assert.Null(ConnectionNames.AggregatorOf("http://box/api"));
        Assert.Null(ConnectionNames.AggregatorOf("http://box/api/v2.0/indexers/all/results/torznab"));
        Assert.Null(ConnectionNames.AggregatorOf("http://box/indexers/x"));
        Assert.Null(ConnectionNames.AggregatorOf(null));
    }

    [Theory]
    [InlineData(null, "List")]
    [InlineData("  ", "List")]
    [InlineData("tmdb", "TMDb list")]
    [InlineData("TMDB-PERSON", "TMDb person")]
    [InlineData("tmdb-discover", "TMDb discover")]
    [InlineData("tmdb-collection", "TMDb collection")]
    [InlineData("tmdb-company-credits", "TMDb company")]
    [InlineData("tmdb-popular", "TMDb popular")]
    [InlineData("trakt", "Trakt")]
    [InlineData("plex", "Plex watchlist")]
    [InlineData("plex-watchlist", "Plex watchlist")]
    [InlineData("mdblist", "Mdblist")]
    public void A_provider_has_a_plain_label(string? provider, string expected)
        => Assert.Equal(expected, ConnectionNames.ProviderLabel(provider));

    [Theory]
    [InlineData("trakt", null, "Trakt")]
    [InlineData("trakt", "  ", "Trakt")]
    [InlineData("trakt", "https://trakt.tv/users/a/lists/my-list/", "Trakt: my-list")]
    [InlineData("rss", "https://example.test", "Rss: example.test")]
    [InlineData("rss", "https://example.test/", "Rss: example.test")]
    [InlineData("tmdb", "12345", "TMDb list: 12345")]
    public void An_import_list_is_its_provider_and_list(string provider, string? feed, string expected)
        => Assert.Equal(expected, ConnectionNames.ImportList(provider, feed));

    [Fact]
    public void A_long_list_name_is_cut_short()
        => Assert.Equal("Trakt: " + new string('a', 60), ConnectionNames.ImportList("trakt", new string('a', 90)));

    [Theory]
    [InlineData("qbittorrent", "qBittorrent")]
    [InlineData("SABnzbd", "SABnzbd")]
    [InlineData("nzbget", "NZBGet")]
    [InlineData("transmission", "Transmission")]
    [InlineData("deluge", "Deluge")]
    [InlineData("utorrent", "uTorrent")]
    [InlineData("rtorrent", "rTorrent")]
    [InlineData(null, "Download client")]
    [InlineData("", "Download client")]
    [InlineData("aria2", "Aria2")]
    public void A_download_client_is_spelt_as_its_own_product_spells_it(string? protocol, string expected)
        => Assert.Equal(expected, ConnectionNames.DownloadClientApp(protocol));

    [Fact]
    public void A_free_name_is_kept_and_a_taken_one_takes_the_port_then_a_suffix_then_a_counter()
    {
        Assert.Equal("A", ConnectionNames.Unique("A", 1, "id", new string?[] { null, " ", "B" }));
        Assert.Equal("A:80", ConnectionNames.Unique("A", 80, "id", new string?[] { "a" }));
        Assert.Equal("A (cd12)", ConnectionNames.Unique("A", null, "ab-cd12", new string?[] { "A" }));
        Assert.Equal("A:80 (9d12)", ConnectionNames.Unique("A:80", 80, "x9d12", new string?[] { "A:80" }));
        Assert.Equal("A:80 (x9)", ConnectionNames.Unique("A", 80, "x9", new string?[] { "A", "A:80", "A:80 (9)" }));
        Assert.Equal("A:80 (2)", ConnectionNames.Unique("A", 80, "!!", new string?[] { "A", "A:80" }));
        Assert.Equal("A:80 (3)", ConnectionNames.Unique("A", 80, null!, new string?[] { "A", "A:80", "A:80 (2)" }));
    }

    [Fact]
    public void Naming_a_whole_kind_keeps_unshared_names_and_separates_shared_ones_by_port_then_suffix()
    {
        var result = ConnectionNames.AssignAll(new (string Id, string Name, int? Port)[]
        {
            ("1", "Solo", 1),
            ("2", "Twin", 8081),
            ("3", "Twin", 8082),
            ("4", "Same", null),
            ("5", "same", null),
            ("6", "Port", 5),
            ("7", "Port", 5),
        });

        Assert.Equal("Solo", result["1"]);
        Assert.Equal("Twin:8081", result["2"]);
        Assert.Equal("Twin:8082", result["3"]);
        Assert.Equal("Same", result["4"]);
        Assert.NotEqual(result["4"], result["5"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Port:5", result["6"]);
        Assert.NotEqual(result["6"], result["7"], StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("Port:5", result["7"]);
    }
}
