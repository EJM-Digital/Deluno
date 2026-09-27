using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Where each download client really puts a category, and when it has
/// finished (#616, #635).
///
/// <para><b>Why this file exists.</b> Only SABnzbd and qBittorrent could say
/// where a category saves, and SABnzbd said it relative ("Movies"). NZBGet,
/// Deluge and Transmission said nothing. And NZBGet's finished downloads were
/// never seen as finished: its history reads "SUCCESS/UNPACK".</para>
/// </summary>
public sealed class ClientFoldersAreReadTests
{
    [Theory]
    [InlineData("SUCCESS/UNPACK", "completed")]
    [InlineData("SUCCESS/ALL", "completed")]
    [InlineData("WARNING/SCRIPT", "completed")]
    [InlineData("FAILURE/PAR", "failed")]
    [InlineData("WARNING/DAMAGED", "failed")]
    [InlineData("DELETED/MANUAL", "failed")]
    public async Task An_NZBGet_history_row_is_finished_or_failed_by_its_main_status(string status, string outcome)
    {
        var history = await NzbGetHistory($$"""
            {"result":[{"NZBID":2,"NZBName":"Pioneer.One.S01E01.1080p","Name":"Pioneer.One.S01E01.1080p","Category":"TV","Status":"{{status}}","FileSizeHi":0,"HistoryTime":1790000000,"DestDir":"C:\\Downloads\\Completed\\TV\\Pioneer.One.S01E01.1080p"}]}
            """);

        Assert.Equal(outcome, Assert.Single(history).Outcome);
    }

    [Fact]
    public async Task An_NZBGet_download_is_in_its_own_folder_not_one_with_its_name_added_again()
    {
        var history = await NzbGetHistory("""
            {"result":[{"NZBID":2,"NZBName":"Pioneer.One.S01E01.1080p","Name":"Pioneer.One.S01E01.1080p","Category":"TV","Status":"SUCCESS/UNPACK","FileSizeHi":0,"HistoryTime":1790000000,"DestDir":"C:\\Downloads\\Completed\\TV\\Pioneer.One.S01E01.1080p"}]}
            """);

        Assert.Equal(@"C:\Downloads\Completed\TV\Pioneer.One.S01E01.1080p", Assert.Single(history).SourcePath);
    }

    [Fact]
    public async Task NZBGet_names_a_categorys_folder_with_its_variables_filled_in()
    {
        var result = await new NzbGetDownloadClient(Factory(Json("""
            {"result":[
              {"Name":"MainDir","Value":"C:\\Deluno\\clients\\nzbget"},
              {"Name":"DestDir","Value":"C:\\Downloads\\Completed"},
              {"Name":"AppendCategoryDir","Value":"yes"},
              {"Name":"Category1.Name","Value":"Movies"},
              {"Name":"Category1.DestDir","Value":"C:\\Downloads\\Completed\\Movies"},
              {"Name":"Category2.Name","Value":"TV"},
              {"Name":"Category2.DestDir","Value":""},
              {"Name":"Category3.Name","Value":"Software"},
              {"Name":"Category3.DestDir","Value":"${MainDir}\\complete"}]}
            """))).CheckCategoryAsync(Client("nzbget"), "TV", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
        Assert.Equal(@"C:\Downloads\Completed\TV", result.SavePath);
    }

    [Fact]
    public async Task NZBGet_without_the_category_says_where_its_downloads_go_instead()
    {
        var result = await new NzbGetDownloadClient(Factory(Json("""
            {"result":[{"Name":"DestDir","Value":"C:\\Downloads\\Completed"},{"Name":"Category1.Name","Value":"Movies"}]}
            """))).CheckCategoryAsync(Client("nzbget"), "TV", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Missing, result.Status);
        Assert.Contains(@"C:\Downloads\Completed", result.Message);
    }

    [Fact]
    public async Task Transmission_saves_a_category_inside_its_download_folder()
    {
        var result = await new TransmissionDownloadClient(Factory(Json("""
            {"result":"success","arguments":{"download-dir":"C:\\Downloads\\Completed"}}
            """))).CheckCategoryAsync(Client("transmission"), "TV", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
        Assert.Equal(@"C:\Downloads\Completed\TV", result.SavePath);
    }

    [Fact]
    public async Task A_Deluge_label_that_moves_finished_downloads_saves_to_its_own_folder()
    {
        var result = await new DelugeDownloadClient(Factory(Deluge(labelMoves: true))).CheckCategoryAsync(Client("deluge"), "Movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, result.Status);
        Assert.Equal(@"C:\Downloads\Completed\Movies", result.SavePath);
    }

    [Fact]
    public async Task A_Deluge_label_with_no_folder_of_its_own_is_named_with_where_it_goes()
    {
        var result = await new DelugeDownloadClient(Factory(Deluge(labelMoves: false))).CheckCategoryAsync(Client("deluge"), "Movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Configuration, result.Status);
        Assert.Equal(@"C:\Downloads\Completed", result.SavePath);
        Assert.Contains("Move completed to", result.Message);
    }

    [Theory]
    [InlineData(@"C:\Downloads\Completed", "Movies", @"C:\Downloads\Completed\Movies")]
    [InlineData("/downloads/complete", "tv", "/downloads/complete/tv")]
    [InlineData(@"C:\Downloads\Completed", @"D:\Elsewhere\TV", @"D:\Elsewhere\TV")]
    [InlineData("/downloads", "/mnt/tv/", "/mnt/tv")]
    [InlineData(@"C:\Downloads\Completed\", null, @"C:\Downloads\Completed")]
    public void A_clients_folder_is_joined_the_way_the_client_writes_paths(string root, string? folder, string expected)
        => Assert.Equal(expected, ClientPaths.ClientFolder(root, folder));

    private static async Task<IReadOnlyList<DownloadClientHistoryItem>> NzbGetHistory(string historyJson)
        => await new NzbGetDownloadClient(Factory(Json(historyJson))).GetHistoryAsync(Client("nzbget"), DateTimeOffset.UtcNow, CancellationToken.None);

    private static DownloadClientItem Client(string protocol)
        => new("client-1", protocol, protocol, "localhost", 1234, "user", "secret", "http://localhost:1234/", false, null,
            "Movies", "TV", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IHttpClientFactory Factory(HttpMessageHandler handler) => new StubFactory(handler);

    private static HttpMessageHandler Json(string reply) => new Answering(_ => reply);

    /// <summary>Deluge's web JSON-RPC: sign-in, core folders, labels and one label's options.</summary>
    private static HttpMessageHandler Deluge(bool labelMoves) => new Answering(body => body switch
    {
        _ when body.Contains("\"auth.login\"") => """{"result":true,"error":null,"id":1}""",
        _ when body.Contains("\"web.connected\"") => """{"result":true,"error":null,"id":2}""",
        _ when body.Contains("\"core.get_config_values\"") => """{"result":{"download_location":"D:\\Incomplete\\Deluge","move_completed":true,"move_completed_path":"C:\\Downloads\\Completed"},"error":null,"id":10}""",
        _ when body.Contains("\"label.get_labels\"") => """{"result":["movies","tv"],"error":null,"id":11}""",
        _ when body.Contains("\"label.get_options\"") => labelMoves
            ? """{"result":{"apply_move_completed":true,"move_completed":true,"move_completed_path":"C:\\Downloads\\Completed\\Movies"},"error":null,"id":12}"""
            : """{"result":{"apply_move_completed":false,"move_completed":false,"move_completed_path":""},"error":null,"id":12}""",
        _ => """{"result":null,"error":null,"id":0}"""
    });

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
}
