using System.Net;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1148: Deluge answers 200 even when it refuses a request, and Deluno said
/// "sent" without reading the answer, so a removal that never happened was
/// reported as done.
/// </summary>
public sealed class DelugeRemovalIsReportedHonestlyTests
{
    [Fact]
    public async Task A_refused_removal_is_reported_as_failed_with_delugess_reason()
    {
        var handler = new Handler("""{"result":null,"error":{"message":"Torrent not in session"},"id":3}""");

        var result = await Remove(handler);

        Assert.False(result.Succeeded);
        Assert.Contains("Torrent not in session", result.Message);
    }

    [Fact]
    public async Task A_removal_that_returns_false_is_reported_as_failed()
    {
        var result = await Remove(new Handler("""{"result":false,"error":null,"id":3}"""));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_removal_is_sent_with_one_torrent_id_and_the_data_flag_and_succeeds_when_deluge_accepts_it()
    {
        var handler = new Handler("""{"result":true,"error":null,"id":3}""");

        var result = await Remove(handler);

        Assert.True(result.Succeeded);
        var removal = Assert.Single(handler.Bodies, body => body.Contains("core.remove_torrent", StringComparison.Ordinal));
        Assert.Contains("\"params\":[\"abc123\",true]", removal.Replace(" ", string.Empty));
    }

    private static Task<DownloadClientActionResult> Remove(Handler handler)
        => new DelugeDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client(), DownloadClientActions.DeleteWithData, "abc123", CancellationToken.None);

    private static DownloadClientItem Client()
        => new("deluge-1", "Deluge", "deluge", "localhost", 8112, null, null, "http://localhost:8112/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(string removeAnswer) : HttpMessageHandler
    {
        private bool _removed;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            // Deluge holds the torrent until it is removed (#1252): its list shows it before, and not after a removal it accepted.
            var removing = body.Contains("core.remove_torrent", StringComparison.Ordinal);
            if (removing && removeAnswer.Contains("\"result\":true", StringComparison.Ordinal)) _removed = true;
            var answer = removing
                ? removeAnswer
                : body.Contains("web.update_ui", StringComparison.Ordinal)
                    ? _removed
                        ? """{"result":{"torrents":{}},"error":null,"id":2}"""
                        : """{"result":{"torrents":{"abc123":{"name":"Some.Film"}}},"error":null,"id":2}"""
                    : """{"result":true,"error":null,"id":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
