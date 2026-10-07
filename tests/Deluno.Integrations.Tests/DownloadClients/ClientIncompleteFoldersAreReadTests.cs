using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

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
        var result = await new DelugeDownloadClient(Factory(new Answering(body => body switch
        {
            _ when body.Contains("\"auth.login\"") => """{"result":true,"error":null,"id":1}""",
            _ when body.Contains("\"web.connected\"") => """{"result":true,"error":null,"id":2}""",
            _ when body.Contains("\"core.get_config_values\"") => """{"result":{"download_location":"D:\\Incomplete\\Deluge","move_completed":true,"move_completed_path":"C:\\Downloads\\Completed"},"error":null,"id":10}""",
            _ when body.Contains("\"label.get_labels\"") => """{"result":["movies"],"error":null,"id":11}""",
            _ when body.Contains("\"label.get_options\"") => """{"result":{"apply_move_completed":true,"move_completed":true,"move_completed_path":"C:\\Downloads\\Completed\\Movies"},"error":null,"id":12}""",
            _ => """{"result":null,"error":null,"id":0}"""
        }))).CheckCategoryAsync(Client("deluge"), "Movies", CancellationToken.None);

        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
        Assert.Equal(@"D:\Incomplete\Deluge", result.IncompletePath);
    }

    [Fact]
    public async Task Transmission_downloads_into_its_incomplete_folder_when_that_is_switched_on()
    {
        var result = await new TransmissionDownloadClient(Factory(new Answering(_ => """
            {"result":"success","arguments":{"download-dir":"C:\\Downloads\\Completed","rpc-version":17,"incomplete-dir":"D:\\Incomplete\\Transmission","incomplete-dir-enabled":true}}
            """))).CheckCategoryAsync(Client("transmission"), "TV", CancellationToken.None);

        Assert.Equal(@"C:\Downloads\Completed\TV", result.SavePath);
        Assert.Equal(@"D:\Incomplete\Transmission", result.IncompletePath);
    }

    [Fact]
    public async Task Transmission_with_no_incomplete_folder_downloads_straight_into_the_finished_folder()
    {
        var result = await new TransmissionDownloadClient(Factory(new Answering(_ => """
            {"result":"success","arguments":{"download-dir":"C:\\Downloads\\Completed","rpc-version":17,"incomplete-dir":"D:\\Incomplete\\Transmission","incomplete-dir-enabled":false}}
            """))).CheckCategoryAsync(Client("transmission"), "TV", CancellationToken.None);

        Assert.NotNull(result.SavePath);
        Assert.Equal(result.SavePath, result.IncompletePath);
    }

    [Theory]
    [InlineData(true, @"D:\Incomplete\qBittorrent")]
    [InlineData(false, @"C:\Downloads\Completed\Movies")]
    public async Task QBittorrent_downloads_into_its_temporary_path_only_when_it_keeps_incomplete_torrents_apart(bool enabled, string expected)
    {
        var handler = new QbitHandler($$"""{"temp_path_enabled":{{(enabled ? "true" : "false")}},"temp_path":"D:\\Incomplete\\qBittorrent"}""");

        var result = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(Client("qbittorrent"), "Movies", CancellationToken.None);

        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
        Assert.Equal(expected, result.IncompletePath);
    }

    [Fact]
    public async Task SABnzbd_downloads_into_its_temporary_download_folder()
    {
        var result = await new SabnzbdDownloadClient(Factory(new Answering(_ => """
            {"config":{"misc":{"complete_dir":"C:\\Downloads\\Completed","download_dir":"D:\\Incomplete\\SABnzbd"},"categories":[{"name":"Movies","dir":"Movies"}]}}
            """))).CheckCategoryAsync(Client("sabnzbd"), "Movies", CancellationToken.None);

        Assert.Equal(@"D:\Incomplete\SABnzbd", result.IncompletePath);
    }

    [Fact]
    public async Task NZBGet_downloads_into_its_intermediate_folder()
    {
        var result = await NzbGetCheck("""{"Name":"InterDir","Value":"D:\\Incomplete\\NZBGet"}""");

        Assert.Equal(@"D:\Incomplete\NZBGet", result.IncompletePath);
    }

    [Fact]
    public async Task NZBGet_with_no_intermediate_folder_downloads_straight_into_the_destination()
    {
        var result = await NzbGetCheck("""{"Name":"InterDir","Value":""}""");

        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
        Assert.Equal(result.SavePath, result.IncompletePath);
    }

    private static Task<DownloadClientCategoryCheckResult> NzbGetCheck(string interDirOption)
        => new NzbGetDownloadClient(Factory(new Answering(_ => $$"""
            {"result":[{"Name":"DestDir","Value":"C:\\Downloads\\Completed"},{{interDirOption}},{"Name":"AppendCategoryDir","Value":"yes"},{"Name":"Category1.Name","Value":"Movies"},{"Name":"Category1.DestDir","Value":""}]}
            """))).CheckCategoryAsync(Client("nzbget"), "Movies", CancellationToken.None);

    private static DownloadClientItem Client(string protocol)
        => new("client-1", protocol, protocol, "localhost", 1234, "user", "secret", "http://localhost:1234/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IHttpClientFactory Factory(HttpMessageHandler handler) => new StubFactory(handler);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Answering(Func<string, string> reply) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(body), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class QbitHandler(string preferences) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = path switch
            {
                _ when path.Contains("auth/login", StringComparison.Ordinal) => "Ok.",
                _ when path.Contains("torrents/categories", StringComparison.Ordinal) => """{"Movies":{"name":"Movies","savePath":"C:\\Downloads\\Completed\\Movies"}}""",
                _ when path.Contains("app/preferences", StringComparison.Ordinal) => preferences,
                _ => "{}"
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
