using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1164: the folder check reads where each client keeps a download while it is in progress, so
/// "where it downloads" can be set beside "where it finishes".
/// </summary>
public sealed class ClientIncompleteFoldersAreReadTests
{
    [Fact]
    public async Task Deluge_downloads_into_its_download_location_and_finishes_in_the_labels_folder()
    {
        // Deluge 2.2.0's recorded core folders, labels and label options; the download location and the label's folder are the ones under test.
        var result = await new DelugeDownloadClient(Factory(RecordedDelugeRig.FolderCheck(labelMoves: true, @"C:\Downloads\Completed\Movies", @"D:\Incomplete\Deluge")))
            .CheckCategoryAsync(Client("deluge"), "Movies", CancellationToken.None);

        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
        Assert.Equal(@"D:\Incomplete\Deluge", result.IncompletePath);
    }

    [Fact]
    public async Task Transmission_downloads_into_its_incomplete_folder_when_that_is_switched_on()
    {
        var result = await new TransmissionDownloadClient(Factory(TransmissionSession(true)))
            .CheckCategoryAsync(Client("transmission"), "TV", CancellationToken.None);

        Assert.Equal(@"C:\Downloads\Completed\TV", result.SavePath);
        Assert.Equal(@"D:\Incomplete\Transmission", result.IncompletePath);
    }

    [Fact]
    public async Task Transmission_with_no_incomplete_folder_downloads_straight_into_the_finished_folder()
    {
        var result = await new TransmissionDownloadClient(Factory(TransmissionSession(false)))
            .CheckCategoryAsync(Client("transmission"), "TV", CancellationToken.None);

        Assert.NotNull(result.SavePath);
        Assert.Equal(result.SavePath, result.IncompletePath);
    }

    [Fact]
    public async Task QBittorrent_reports_the_folder_Deluno_sends_torrents_to_not_the_categorys_when_the_client_has_one()
    {
        var handler = QbitHandler(tempPathEnabled: false);
        var client = Client("qbittorrent") with { Location = @"E:\Elsewhere\Movies" };

        var result = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(client, "Movies", CancellationToken.None);

        Assert.Equal(@"E:\Elsewhere\Movies", result.SavePath);
        Assert.Contains("whatever its Movies category says", result.Message);
    }

    [Fact]
    public async Task QBittorrent_with_no_folder_of_its_own_reports_the_categorys_folder_because_grabs_use_automatic_management()
    {
        var handler = QbitHandler(tempPathEnabled: false);

        var result = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(Client("qbittorrent"), "Movies", CancellationToken.None);

        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
    }

    [Theory]
    [InlineData(true, @"D:\Incomplete\qBittorrent")]
    [InlineData(false, @"C:\Downloads\Completed\Movies")]
    public async Task QBittorrent_downloads_into_its_temporary_path_only_when_it_keeps_incomplete_torrents_apart(bool enabled, string expected)
    {
        var handler = QbitHandler(tempPathEnabled: enabled);

        var result = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(Client("qbittorrent"), "Movies", CancellationToken.None);

        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
        Assert.Equal(expected, result.IncompletePath);
    }

    [Fact]
    public async Task SABnzbd_downloads_into_its_temporary_download_folder()
    {
        // SABnzbd 5.1.3's recorded configuration and categories, with its temporary download folder as under test.
        var handler = RecordedReplies.Handler("sabnzbd", [RecordedReplies.Sabnzbd("settings")], (request, _) =>
            request.RequestUri!.Query.Contains("mode=get_config", StringComparison.Ordinal)
                ? RecordedReplies.Json(RecordedReplies.Sabnzbd("settings").Body("mode=get_config", ("config.misc.download_dir", @"D:\Incomplete\SABnzbd")))
                : null);
        var result = await new SabnzbdDownloadClient(Factory(handler)).CheckCategoryAsync(Client("sabnzbd"), "Movies", CancellationToken.None);

        Assert.Equal(@"D:\Incomplete\SABnzbd", result.IncompletePath);
    }

    [Fact]
    public async Task NZBGet_downloads_into_its_intermediate_folder()
    {
        var result = await NzbGetCheck(@"D:\Incomplete\NZBGet");

        Assert.Equal(@"D:\Incomplete\NZBGet", result.IncompletePath);
    }

    [Fact]
    public async Task NZBGet_with_no_intermediate_folder_downloads_straight_into_the_destination()
    {
        var result = await NzbGetCheck(string.Empty);

        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
        Assert.Equal(result.SavePath, result.IncompletePath);
    }

    /// <summary>NZBGet 26.3's recorded configuration, with the intermediate folder as under test and the Movies category taking NZBGet's own destination.</summary>
    private static Task<DownloadClientCategoryCheckResult> NzbGetCheck(string interDir)
        => new NzbGetDownloadClient(Factory(new FixedReply(RecordedReplies.Nzbget("settings").Body(
            "\"config\"", ("result.[Name=InterDir].Value", interDir), ("result.[Name=Category1.DestDir].Value", string.Empty)))))
            .CheckCategoryAsync(Client("nzbget"), "Movies", CancellationToken.None);

    /// <summary>Transmission 4.1.3's recorded session settings, with its incomplete folder as under test.</summary>
    private static HttpMessageHandler TransmissionSession(bool incompleteEnabled)
        => new FixedReply(RecordedReplies.Transmission("settings").Body(
            "session-get", ("arguments.incomplete-dir", @"D:\Incomplete\Transmission"), ("arguments.incomplete-dir-enabled", incompleteEnabled)));

    /// <summary>qBittorrent 5.2.1's recorded sign-in, categories and preferences, with its temporary path and the Movies category's folder as under test.</summary>
    private static HttpMessageHandler QbitHandler(bool tempPathEnabled)
        => RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("settings")], (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("torrents/categories", StringComparison.Ordinal))
            {
                return RecordedReplies.Json(RecordedReplies.Qbittorrent("settings").Body(
                    "torrents/categories", ("><scrubbed>-movies", "Movies"), ("Movies.savePath", @"C:\Downloads\Completed\Movies")));
            }

            return path.Contains("app/preferences", StringComparison.Ordinal)
                ? RecordedReplies.Json(RecordedReplies.Qbittorrent("settings").Body(
                    "app/preferences", ("temp_path_enabled", tempPathEnabled), ("temp_path", @"D:\Incomplete\qBittorrent")))
                : null;
        });

    private static DownloadClientItem Client(string protocol)
        => new("client-1", protocol, protocol, "localhost", 1234, "user", "secret", "http://localhost:1234/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IHttpClientFactory Factory(HttpMessageHandler handler) => new StubFactory(handler);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedReply(string reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(RecordedReplies.Json(reply));
    }
}
