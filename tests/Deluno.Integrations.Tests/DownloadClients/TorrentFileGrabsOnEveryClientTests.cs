using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1062: with the info-hash known before the send, a .torrent grab is known
/// by its id on every torrent client, and "the client already has it" is
/// decided for a file exactly as for a magnet. Each client also takes the file
/// in its own upload shape.
/// </summary>
public sealed class TorrentFileGrabsOnEveryClientTests
{
    private const string Hash = "de4158016fbc49ebf35641903cec65a0ad0d30b5";

    private static readonly byte[] File = Encoding.Latin1.GetBytes("d4:infod6:lengthi5eee");

    private static DownloadClientGrabRequest Request()
        => new("Example.Movie.2026.1080p", "https://indexer.example/dl/1.torrent", "movies", "movies", "Example Indexer")
        {
            TorrentFile = File,
            KnownInfoHash = Hash
        };

    private static DownloadClientItem Client(string protocol, int port = 8080)
        => new(
            "client-1", protocol, protocol, "localhost", port, null, "secret", $"http://localhost:{port}/", false, null,
            "movies", "tv", null, 1, true, "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    // ---------------------------------------------------------------- qBittorrent

    [Fact]
    public async Task qBittorrent_takes_the_file_as_an_upload_and_the_grab_is_known_by_its_hash()
    {
        var handler = new QbitHandler(before: "[]", after: $$"""[{"hash":"{{Hash}}"}]""");

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent"), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
        Assert.False(result.IsDuplicateFollow);
        Assert.Contains("name=torrents", handler.AddBody, StringComparison.Ordinal);
        Assert.Contains("filename=release.torrent", handler.AddBody, StringComparison.Ordinal);
        Assert.DoesNotContain("name=urls", handler.AddBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task qBittorrent_already_holding_a_torrent_file_is_followed_not_failed()
    {
        var handler = new QbitHandler(before: $$"""[{"hash":"{{Hash}}"}]""", after: $$"""[{"hash":"{{Hash}}"}]""");

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent"), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task qBittorrent_never_showing_the_known_hash_is_still_known_by_it_for_the_never_showed_path()
    {
        var handler = new QbitHandler(before: "[]", after: "[]");

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent"), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task qBittorrent_names_the_known_hash_not_an_unrelated_torrent_that_appeared_first()
    {
        var handler = new QbitHandler(
            before: "[]",
            after: $$"""[{"hash":"9999999999999999999999999999999999999999"},{"hash":"{{Hash}}"}]""");

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent"), Request(), CancellationToken.None);

        Assert.Equal(Hash, result.ExternalId);
    }

    private sealed class QbitHandler(string before, string after) : HttpMessageHandler
    {
        private bool added;

        public string AddBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("torrents/add", StringComparison.Ordinal))
            {
                added = true;
                AddBody = Encoding.Latin1.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                return Text("Ok.");
            }

            return path.Contains("torrents/info", StringComparison.Ordinal) ? Json(added ? after : before) : Text("Ok.");
        }
    }

    // --------------------------------------------------------------------- Deluge

    [Fact]
    public async Task Deluge_holding_a_torrent_file_already_is_followed_by_the_files_own_hash()
    {
        var handler = new DelugeHandler(addResult: "null", sessionHashes: [Hash]);

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client("deluge", 8112), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
        Assert.Contains("core.add_torrent_file", handler.AddBody, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(File), handler.AddBody, StringComparison.Ordinal);
    }

    private sealed class DelugeHandler(string addResult, IReadOnlyList<string> sessionHashes) : HttpMessageHandler
    {
        public string AddBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("core.add_torrent_", StringComparison.Ordinal))
            {
                AddBody = body;
                return Json($$"""{"result":{{addResult}},"error":null,"id":2}""");
            }

            return body.Contains("core.get_session_state", StringComparison.Ordinal)
                ? Json($$"""{"result":[{{string.Join(",", sessionHashes.Select(hash => $"\"{hash}\""))}}],"error":null,"id":9}""")
                : Json("""{"result":true,"error":null,"id":1}""");
        }
    }

    // ------------------------------------------------------------------- rTorrent

    [Fact]
    public async Task rTorrent_takes_the_file_as_base64_and_the_grab_is_known_by_its_hash()
    {
        var handler = new RtorrentHandler(before: [], after: [Hash.ToUpperInvariant()]);

        var result = await new RtorrentDownloadClient(new StubFactory(handler)).GrabAsync(Client("rtorrent"), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
        Assert.Contains("load.raw_start", handler.LoadBody, StringComparison.Ordinal);
        Assert.Contains($"<base64>{Convert.ToBase64String(File)}</base64>", handler.LoadBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task rTorrent_already_holding_a_torrent_file_is_followed_not_failed()
    {
        var handler = new RtorrentHandler(before: [Hash.ToUpperInvariant()], after: [Hash.ToUpperInvariant()]);

        var result = await new RtorrentDownloadClient(new StubFactory(handler)).GrabAsync(Client("rtorrent"), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
    }

    private sealed class RtorrentHandler(IReadOnlyList<string> before, IReadOnlyList<string> after) : HttpMessageHandler
    {
        private bool loaded;

        public string LoadBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = Regex.Match(body, "<methodName>([^<]+)</methodName>").Groups[1].Value;
            if (method.StartsWith("load.", StringComparison.Ordinal))
            {
                loaded = true;
                LoadBody = body;
                return Xml("<value><i4>0</i4></value>");
            }

            if (method == "download_list")
            {
                var values = (loaded ? after : before).Select(hash => $"<value><string>{hash}</string></value>");
                return Xml($"<value><array><data>{string.Concat(values)}</data></array></value>");
            }

            return Xml("<value><i4>0</i4></value>");
        }

        private static HttpResponseMessage Xml(string value)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent($"<?xml version=\"1.0\"?><methodResponse><params><param>{value}</param></params></methodResponse>", Encoding.UTF8, "text/xml")
            };
    }

    // ------------------------------------------------------------------ Transmission

    [Fact]
    public async Task Transmission_takes_the_file_as_metainfo_and_the_grab_is_known_by_its_hash()
    {
        var handler = new TransmissionHandler("""{"result":"success","arguments":{"torrent-added":{"id":7,"hashString":"DE4158016FBC49EBF35641903CEC65A0AD0D30B5"}}}""");

        var result = await new TransmissionDownloadClient(new StubFactory(handler)).GrabAsync(Client("transmission", 9091), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
        Assert.Contains($"\"metainfo\":\"{Convert.ToBase64String(File)}\"", handler.AddBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"filename\"", handler.AddBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transmission_that_names_no_hash_for_the_file_is_still_known_by_the_hash_Deluno_worked_out_and_never_by_its_number()
    {
        var handler = new TransmissionHandler("""{"result":"success","arguments":{"torrent-added":{"id":7}}}""");

        var result = await new TransmissionDownloadClient(new StubFactory(handler)).GrabAsync(Client("transmission", 9091), Request(), CancellationToken.None);

        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task Transmission_already_holding_a_torrent_file_is_followed_by_its_hash()
    {
        var handler = new TransmissionHandler("""{"result":"success","arguments":{"torrent-duplicate":{"id":7,"hashString":"de4158016fbc49ebf35641903cec65a0ad0d30b5"}}}""");

        var result = await new TransmissionDownloadClient(new StubFactory(handler)).GrabAsync(Client("transmission", 9091), Request(), CancellationToken.None);

        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
    }

    private sealed class TransmissionHandler(string addReply) : HttpMessageHandler
    {
        public string AddBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("\"torrent-add\"", StringComparison.Ordinal))
            {
                AddBody = body;
                return Json(addReply);
            }

            return Json("""{"result":"success","arguments":{}}""");
        }
    }

    // --------------------------------------------------------------------- uTorrent

    [Fact]
    public async Task uTorrent_takes_the_file_as_an_upload_and_the_grab_is_known_by_its_hash()
    {
        var handler = new UTorrentHandler(before: [], after: [Hash.ToUpperInvariant()]);

        var result = await new UTorrentDownloadClient(() => handler).GrabAsync(Client("utorrent"), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
        Assert.Contains("action=add-file", handler.AddUri, StringComparison.Ordinal);
        Assert.Contains("name=torrent_file", handler.AddBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task uTorrent_already_holding_a_torrent_file_is_followed_not_reported_as_a_send_with_no_id()
    {
        var handler = new UTorrentHandler(before: [Hash.ToUpperInvariant()], after: [Hash.ToUpperInvariant()]);

        var result = await new UTorrentDownloadClient(() => handler).GrabAsync(Client("utorrent"), Request(), CancellationToken.None);

        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
    }

    private sealed class UTorrentHandler(IReadOnlyList<string> before, IReadOnlyList<string> after) : HttpMessageHandler
    {
        private bool added;

        public string AddUri { get; private set; } = string.Empty;

        public string AddBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            if (uri.Contains("token.html", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div id='token'>t</div>", Encoding.UTF8, "text/html") };
            }

            if (uri.Contains("action=add-", StringComparison.Ordinal))
            {
                added = true;
                AddUri = uri;
                AddBody = request.Content is null ? string.Empty : Encoding.Latin1.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                return Text("{}");
            }

            if (uri.Contains("list=1", StringComparison.Ordinal))
            {
                var rows = string.Join(",", (added ? after : before).Select(hash => $"[\"{hash}\"]"));
                return Json($$"""{"torrents":[{{rows}}]}""");
            }

            return Text("{}");
        }
    }
}
