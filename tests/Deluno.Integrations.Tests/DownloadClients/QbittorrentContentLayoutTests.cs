using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #999: qBittorrent's per-client content layout, sent as the add call's
/// <c>contentLayout</c> field (Radarr and Sonarr's QBittorrentProxyV2 send
/// "Original" and "Subfolder" the same way; the API also takes "NoSubfolder").
/// </summary>
public sealed class QbittorrentContentLayoutTests
{
    private const string Release = "Big.Buck.Bunny.2008.1080p.WEB-DL.x264-DELUNO";

    [Theory]
    [InlineData("original", "contentLayout=Original")]
    [InlineData("subfolder", "contentLayout=Subfolder")]
    [InlineData("no-subfolder", "contentLayout=NoSubfolder")]
    public async Task Qbittorrent_grab_sends_the_chosen_content_layout(string layout, string expected)
    {
        var bodies = new List<string>();

        await new QbittorrentDownloadClient(() => new QbitAddHandler(bodies))
            .GrabAsync(QbitClient() with { ContentLayout = layout }, TorrentGrab(), CancellationToken.None);

        Assert.Contains(expected, Assert.Single(bodies), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("")]
    [InlineData("something-unknown")]
    public async Task Qbittorrent_grab_sends_no_content_layout_when_none_is_chosen(string layout)
    {
        var bodies = new List<string>();

        await new QbittorrentDownloadClient(() => new QbitAddHandler(bodies))
            .GrabAsync(QbitClient() with { ContentLayout = layout }, TorrentGrab(), CancellationToken.None);

        Assert.DoesNotContain("contentLayout", Assert.Single(bodies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_torrent_file_upload_carries_the_content_layout_too()
    {
        var bodies = new List<string>();
        var request = TorrentGrab() with { TorrentFile = [1, 2, 3] };

        await new QbittorrentDownloadClient(() => new QbitAddHandler(bodies))
            .GrabAsync(QbitClient() with { ContentLayout = "subfolder" }, request, CancellationToken.None);

        var body = Assert.Single(bodies);
        Assert.Contains("name=contentLayout", body, StringComparison.Ordinal);
        Assert.Contains("Subfolder", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Original", "original")]
    [InlineData("create_subfolder", "subfolder")]
    [InlineData("NoSubfolder", "no-subfolder")]
    [InlineData(null, "default")]
    [InlineData("nonsense", "default")]
    public void Layout_names_are_normalised_and_unknown_means_the_clients_own(string? given, string expected)
        => Assert.Equal(expected, ContentLayouts.Normalize(given));

    [Fact]
    public void A_new_client_keeps_the_clients_own_layout()
        => Assert.Equal(ContentLayouts.Default, QbitClient().ContentLayout);

    [Fact]
    public void The_folder_check_flags_no_subfolder_and_says_why()
    {
        var link = FolderChainService.ContentLayoutLink(QbitClient() with { ContentLayout = "no-subfolder" }, "movies");

        Assert.NotNull(link);
        Assert.Equal("layout", link!.Step);
        Assert.Equal(FolderChainStatuses.Problem, link.Status);
        Assert.Contains("season pack", link.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("original")]
    [InlineData("subfolder")]
    public void The_folder_check_says_nothing_about_layouts_that_keep_downloads_apart(string layout)
        => Assert.Null(FolderChainService.ContentLayoutLink(QbitClient() with { ContentLayout = layout }, "movies"));

    [Fact]
    public void The_folder_check_ignores_the_layout_of_a_client_that_is_not_qbittorrent()
        => Assert.Null(FolderChainService.ContentLayoutLink(QbitClient() with { Protocol = "transmission", ContentLayout = "no-subfolder" }, "movies"));

    private static DownloadClientGrabRequest TorrentGrab()
        => new(Release, "http://indexer.test/download/1", "movies", "movies", "Fixture indexer");

    private static DownloadClientItem QbitClient()
        => Client("qbittorrent", "qBittorrent", 8080);

    private static DownloadClientItem Client(string protocol, string name, int port)
        => new(
            Id: protocol + "-1", Name: name, Protocol: protocol, Host: "localhost", Port: port,
            Username: null, Secret: null, EndpointUrl: $"http://localhost:{port}/", UseSsl: false, UrlBase: null,
            MoviesCategory: "movies", TvCategory: "tv", CategoryTemplate: null, Priority: 1, IsEnabled: true,
            HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow);

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class QbitAddHandler(List<string> addBodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("torrents/add", StringComparison.Ordinal))
            {
                addBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
                return Json("Ok.");
            }

            if (path.Contains("auth/login", StringComparison.Ordinal)) return Json("Ok.");
            if (path.Contains("torrents/info", StringComparison.Ordinal)) return Json("[]");
            return Json("{}");
        }
    }
}
