using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// A Transmission grab saves to its category's folder (#629).
///
/// <para><b>Why this file exists.</b> Deluno tagged a Transmission grab with a
/// label and nothing else. Transmission has no folder per label, so films and
/// TV all went to one folder that Weir never watched, on the rig.</para>
/// </summary>
public sealed class TransmissionCategoryFolderTests
{
    [Theory]
    [InlineData(@"C:\Downloads\Completed", "TV", @"C:\Downloads\Completed\TV")]
    [InlineData(@"C:\Downloads\Completed\", "Movies", @"C:\Downloads\Completed\Movies")]
    [InlineData("/downloads/completed", "tv", "/downloads/completed/tv")]
    [InlineData(@"C:\Downloads\Completed\TV", "TV", @"C:\Downloads\Completed\TV")]
    public void The_category_folder_sits_inside_Transmissions_own_and_is_written_its_way(string downloadDir, string category, string expected)
        => Assert.Equal(expected, TransmissionDownloadClient.CategoryFolder(downloadDir, category));

    [Theory]
    [InlineData(null, "TV")]
    [InlineData(@"C:\Downloads", "")]
    public void With_nothing_to_build_on_there_is_no_category_folder(string? downloadDir, string category)
        => Assert.Null(TransmissionDownloadClient.CategoryFolder(downloadDir, category));

    [Fact]
    public async Task A_TV_grab_is_added_to_the_TV_folder_inside_Transmissions_download_folder()
    {
        var added = new List<string>();
        var client = new TransmissionDownloadClient(new StubFactory(new FakeTransmission(@"C:\Downloads\Completed", added)));

        var result = await client.GrabAsync(
            Transmission(),
            new DownloadClientGrabRequest("Pioneer.One.S01E01.720p", "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", "tv", "TV", "Fixture"),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var body = Assert.Single(added);
        Assert.Contains("\"download-dir\":\"C:\\\\Downloads\\\\Completed\\\\TV\"", body);
        Assert.Contains("\"labels\":[\"TV\"]", body);
    }

    private static DownloadClientItem Transmission()
        => new(
            "client-1", "Transmission", "transmission", "localhost", 9091, "user", "secret", "http://localhost:9091/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Transmission's RPC: its download folder for session-get, and a new torrent for torrent-add.</summary>
    private sealed class FakeTransmission(string downloadDir, List<string> added) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string reply;
            if (body.Contains("\"session-get\"", StringComparison.Ordinal))
            {
                reply = $"{{\"result\":\"success\",\"arguments\":{{\"download-dir\":{System.Text.Json.JsonSerializer.Serialize(downloadDir)}}}}}";
            }
            else if (body.Contains("\"torrent-add\"", StringComparison.Ordinal))
            {
                added.Add(body);
                reply = "{\"result\":\"success\",\"arguments\":{\"torrent-added\":{\"id\":7,\"name\":\"Pioneer.One.S01E01.720p\"}}}";
            }
            else
            {
                reply = "{\"result\":\"success\",\"arguments\":{}}";
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
