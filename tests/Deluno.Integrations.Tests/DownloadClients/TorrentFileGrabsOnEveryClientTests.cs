using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

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
        var handler = new RecordedQbitHandler(before: RecordedListing(), after: RecordedListing(Hash));

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
        var handler = new RecordedQbitHandler(before: RecordedListing(Hash), after: RecordedListing(Hash), addReply: RecordedReplies.Qbittorrent("duplicate-add").Response("torrents/add"));

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent"), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task qBittorrent_never_showing_the_known_hash_is_still_known_by_it_for_the_never_showed_path()
    {
        var handler = new RecordedQbitHandler(before: RecordedListing(), after: RecordedListing());

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent"), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task qBittorrent_names_the_known_hash_not_an_unrelated_torrent_that_appeared_first()
    {
        var handler = new RecordedQbitHandler(
            before: RecordedListing(),
            after: RecordedListing("9999999999999999999999999999999999999999", Hash));

        var result = await new QbittorrentDownloadClient(() => handler).GrabAsync(Client("qbittorrent"), Request(), CancellationToken.None);

        Assert.Equal(Hash, result.ExternalId);
    }

    /// <summary>qBittorrent 5.2.1's recorded list of torrents, made to hold the given hashes (none: its recorded answer to a hash it never had).</summary>
    private static string RecordedListing(params string[] hashes)
        => hashes.Length == 0
            ? RecordedReplies.Qbittorrent("unknown-id").Body("torrents/info")
            : RecordedReplies.Qbittorrent("finished-single-file").Body(
                "torrents/info",
                new[] { ("+", (object?)hashes.Length) }.Concat(hashes.Select((hash, index) => ($"{index}.hash", (object?)hash))).ToArray());

    /// <summary>qBittorrent 5.2.1 as recorded: sign-in, the list before and after the add, and the add's own reply (the recorded success unless the test gives one).</summary>
    private sealed class RecordedQbitHandler(string before, string after, HttpResponseMessage? addReply = null) : HttpMessageHandler
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
                return addReply ?? RecordedReplies.Qbittorrent("finished-single-file").Response("torrents/add");
            }

            if (path.Contains("auth/login", StringComparison.Ordinal)) return RecordedReplies.Qbittorrent("sign-in").Response("auth/login");
            if (path.Contains("app/version", StringComparison.Ordinal)) return RecordedReplies.Qbittorrent("version").Response("app/version");
            // The category the grab puts on the torrent: the recorded creation (qBittorrent said it was there already) and move.
            if (path.Contains("torrents/createCategory", StringComparison.Ordinal)) return RecordedReplies.Qbittorrent("category-moved").Response("torrents/createCategory");
            if (path.Contains("torrents/setCategory", StringComparison.Ordinal)) return RecordedReplies.Qbittorrent("category-moved").Response("torrents/setCategory");
            // qBittorrent 5.2.4's own answer to setAutoManagement, recorded.
            if (path.Contains("torrents/setAutoManagement", StringComparison.Ordinal)) return RecordedReplies.Qbittorrent("auto-management-set").Response("torrents/setAutoManagement");
            return path.Contains("torrents/info", StringComparison.Ordinal)
                ? RecordedReplies.Json(added ? after : before)
                : throw new InvalidOperationException("qBittorrent was asked something no recording answers: " + path);
        }
    }

    // --------------------------------------------------------------------- Deluge

    [Fact]
    public async Task Deluge_holding_a_torrent_file_already_is_followed_by_the_files_own_hash()
    {
        var handler = new RecordedDelugeHandler(addReply: RecordedReplies.Deluge("duplicate-add").Response("core.add_torrent_file"), sessionHashes: [Hash]);

        var result = await new DelugeDownloadClient(new StubFactory(handler)).GrabAsync(Client("deluge", 8112), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
        Assert.Contains("core.add_torrent_file", handler.AddBody, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(File), handler.AddBody, StringComparison.Ordinal);
    }

    /// <summary>Deluge 2.2.0 as recorded: sign-in, the add's own reply, and its session state holding the given hashes.</summary>
    private sealed class RecordedDelugeHandler(HttpResponseMessage addReply, IReadOnlyList<string> sessionHashes) : HttpMessageHandler
    {
        public string AddBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("core.add_torrent_", StringComparison.Ordinal))
            {
                AddBody = body;
                return addReply;
            }

            if (body.Contains("core.get_session_state", StringComparison.Ordinal))
            {
                return RecordedReplies.Json(RecordedReplies.Deluge("settings").Body(
                    "core.get_session_state", ("result", new System.Text.Json.Nodes.JsonArray(sessionHashes.Select(hash => (System.Text.Json.Nodes.JsonNode?)hash).ToArray()))));
            }

            return body.Contains("auth.login", StringComparison.Ordinal) ? RecordedReplies.Deluge("sign-in").Response("auth.login")
                : body.Contains("web.connected", StringComparison.Ordinal) ? RecordedReplies.Deluge("sign-in").Response("web.connected")
                : throw new InvalidOperationException("Deluge was asked something no recording answers: " + body);
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
        var handler = new RecordedTransmissionHandler(RecordedReplies.Transmission("finished-single-file").Body("torrent-add", ("arguments.torrent-added.hashString", Hash.ToUpperInvariant())));

        var result = await new TransmissionDownloadClient(new StubFactory(handler)).GrabAsync(Client("transmission", 9091), Request(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Hash, result.ExternalId);
        Assert.Contains($"\"metainfo\":\"{Convert.ToBase64String(File)}\"", handler.AddBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"filename\"", handler.AddBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transmission_that_names_no_hash_for_the_file_is_still_known_by_the_hash_Deluno_worked_out_and_never_by_its_number()
    {
        var handler = new RecordedTransmissionHandler(RecordedReplies.Transmission("finished-single-file").Body("torrent-add", ("arguments.torrent-added.hashString", null)));

        var result = await new TransmissionDownloadClient(new StubFactory(handler)).GrabAsync(Client("transmission", 9091), Request(), CancellationToken.None);

        Assert.Equal(Hash, result.ExternalId);
    }

    [Fact]
    public async Task Transmission_already_holding_a_torrent_file_is_followed_by_its_hash()
    {
        var handler = new RecordedTransmissionHandler(RecordedReplies.Transmission("duplicate-add").Body("torrent-add", ("arguments.torrent-duplicate.hashString", Hash)));

        var result = await new TransmissionDownloadClient(new StubFactory(handler)).GrabAsync(Client("transmission", 9091), Request(), CancellationToken.None);

        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(Hash, result.ExternalId);
    }

    private sealed class RecordedTransmissionHandler(string addReply) : HttpMessageHandler
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

            // Its category lookup and label: Transmission 4.1.3's recorded session settings and label change.
            return body.Contains("\"session-get\"", StringComparison.Ordinal)
                ? RecordedReplies.Transmission("settings").Response("session-get")
                : RecordedReplies.Transmission("label-set").Response("torrent-set");
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
