using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1240: a struck private torrent is moved to a keep-seeding category by each torrent client's own call, and
/// nothing else about it changes. Every client is asked before and after, so "moved" means the client now reports
/// the new category; a refusal is a plain failure with its reason, and not one of these paths ever removes, deletes
/// or pauses anything.
///
/// <para>TODO(#1237): every reply below is written from each client's documented API (qBittorrent Web API
/// <c>torrents/info</c>, <c>setCategory</c>, <c>setAutoManagement</c>, <c>torrents/categories</c>; Deluge's Label plugin
/// <c>label.get_labels</c>, <c>label.set_torrent</c> and <c>core.get_torrent_status</c>; Transmission's <c>torrent-get</c> and
/// <c>torrent-set</c> with <c>labels</c>; rTorrent's <c>d.custom1</c> and <c>d.custom1.set</c>; the uTorrent WebUI
/// <c>setprops</c> and <c>list=1</c>). Replace each fake reply with the recorded real reply once #1237 lands them
/// under <c>tests/fixtures/recorded/&lt;app&gt;/&lt;version&gt;/</c>. Each one is marked
/// <c>TODO(#1237): replace with the recorded reply</c>.</para>
/// </summary>
public sealed class KeepSeedingMoveOnEveryTorrentClientTests
{
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef01";

    // ---------------------------------------------------------------- qBittorrent

    [Fact]
    public async Task Qbittorrent_moves_a_manually_managed_torrent_with_set_category_and_changes_nothing_else()
    {
        var qbit = new QbitFake { Category = "deluno-movies", AutoManaged = false };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), Hash, "keep-seeding", "deluno-movies", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("keep-seeding", qbit.Category);
        var call = Assert.Single(qbit.Calls, c => c.Path.EndsWith("torrents/setCategory", StringComparison.Ordinal));
        Assert.Contains($"hashes={Hash}", call.Body);
        Assert.Contains("category=keep-seeding", call.Body);
        // A torrent already on manual management is not touched, and nothing is paused, stopped or removed.
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("setAutoManagement", StringComparison.Ordinal));
        qbit.AssertNothingWasRemovedPausedOrRelocated();
    }

    [Fact]
    public async Task Qbittorrent_takes_an_automatically_managed_torrent_off_it_first_so_the_move_cannot_relocate_the_data()
    {
        var qbit = new QbitFake { Category = "deluno-movies", AutoManaged = true };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), Hash, "keep-seeding", "deluno-movies", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var order = qbit.Calls.Select(c => c.Path.Split('/').Last()).Where(name => name is "setAutoManagement" or "setCategory").ToArray();
        Assert.Equal(["setAutoManagement", "setCategory"], order);
        Assert.Contains("enable=false", qbit.Calls.Single(c => c.Path.EndsWith("setAutoManagement", StringComparison.Ordinal)).Body);
        qbit.AssertNothingWasRemovedPausedOrRelocated();
    }

    [Fact]
    public async Task Qbittorrent_refusing_a_category_it_does_not_have_is_a_plain_failure_and_the_torrent_is_left_as_it_was()
    {
        var qbit = new QbitFake { Category = "deluno-movies", KnownCategories = ["deluno-movies"] };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), Hash, "keep-seeding", "deluno-movies", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not have a category named keep-seeding", result.Message, StringComparison.Ordinal);
        Assert.Contains("Create it in qBittorrent first", result.Message, StringComparison.Ordinal);
        Assert.Equal("deluno-movies", qbit.Category);
        qbit.AssertNothingWasRemovedPausedOrRelocated();
    }

    [Fact]
    public async Task Qbittorrent_that_says_ok_but_still_reports_the_old_category_is_not_believed()
    {
        var qbit = new QbitFake { Category = "deluno-movies", IgnoreSetCategory = true };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), Hash, "keep-seeding", "deluno-movies", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still has this download in deluno-movies", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qbittorrent_without_the_torrent_is_a_failure_and_nothing_is_set()
    {
        var qbit = new QbitFake { Has = false };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("setCategory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Qbittorrent_that_cannot_be_reached_is_a_failure_that_changes_nothing()
    {
        var qbit = new QbitFake { InfoStatus = HttpStatusCode.ServiceUnavailable };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("setCategory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Qbittorrent_already_in_the_category_asks_for_no_change()
    {
        var qbit = new QbitFake { Category = "keep-seeding" };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), Hash, "keep-seeding", "keep-seeding", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("setCategory", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("keep-seeding", DownloadClientCategoryStatuses.Ready)]
    [InlineData("nope", DownloadClientCategoryStatuses.Missing)]
    public async Task Qbittorrent_checks_a_keep_seeding_category_against_its_own_list_and_never_creates_it(string category, string status)
    {
        var qbit = new QbitFake { KnownCategories = ["deluno-movies", "keep-seeding"] };

        var result = await qbit.Client().CheckKeepSeedingCategoryAsync(Item("qbittorrent"), category, CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("createCategory", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Deluge

    [Fact]
    public async Task Deluge_sets_the_label_with_label_set_torrent_and_changes_nothing_else()
    {
        var deluge = new DelugeFake { Label = "movies", Labels = ["movies", "keep-seeding"] };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), Hash, "keep-seeding", "movies", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("keep-seeding", deluge.Label);
        var call = Assert.Single(deluge.Methods, m => m.Name == "label.set_torrent");
        Assert.Equal([Hash, "keep-seeding"], call.Params);
        Assert.DoesNotContain(deluge.Methods, m => m.Name is "core.remove_torrent" or "core.pause_torrent" or "core.move_storage" or "core.set_torrent_options" or "label.add");
    }

    [Fact]
    public async Task Deluge_with_the_label_plugin_off_says_so_and_changes_nothing()
    {
        var deluge = new DelugeFake { LabelPluginOn = false };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Label plugin is off", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(deluge.Methods, m => m.Name == "label.set_torrent");
    }

    [Fact]
    public async Task Deluge_checking_with_the_label_plugin_off_says_it_is_unsupported_and_why()
    {
        var deluge = new DelugeFake { LabelPluginOn = false };

        var result = await new DelugeDownloadClient(deluge.Factory()).CheckKeepSeedingCategoryAsync(Item("deluge"), "keep-seeding", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, result.Status);
        Assert.False(result.Supported);
        Assert.Contains("Label plugin is off", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deluge_never_creates_a_label_it_does_not_have()
    {
        var deluge = new DelugeFake { Label = "movies", Labels = ["movies"] };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), Hash, "keep-seeding", "movies", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no label named keep-seeding", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(deluge.Methods, m => m.Name is "label.add" or "label.set_torrent");
        var check = await new DelugeDownloadClient(deluge.Factory()).CheckKeepSeedingCategoryAsync(Item("deluge"), "keep-seeding", CancellationToken.None);
        Assert.Equal(DownloadClientCategoryStatuses.Missing, check.Status);
    }

    [Fact]
    public async Task Deluge_refusing_the_label_in_its_reply_is_a_failure_with_its_reason()
    {
        var deluge = new DelugeFake { Label = "movies", Labels = ["movies", "keep-seeding"], RefuseSetWith = "Unknown Label" };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), Hash, "keep-seeding", "movies", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Unknown Label", result.Message, StringComparison.Ordinal);
        Assert.Equal("movies", deluge.Label);
    }

    [Fact]
    public async Task Deluge_that_does_not_hold_the_torrent_is_a_failure_and_nothing_is_set()
    {
        var deluge = new DelugeFake { Has = false, Labels = ["keep-seeding"] };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(deluge.Methods, m => m.Name == "label.set_torrent");
    }

    // ---------------------------------------------------------------- Transmission

    [Fact]
    public async Task Transmission_puts_the_keep_seeding_label_first_drops_the_old_category_and_keeps_every_other_label()
    {
        var transmission = new TransmissionFake { Labels = ["deluno-movies", "favourite"] };

        var result = await new TransmissionDownloadClient(transmission.Factory()).SetCategoryAsync(Item("transmission"), Hash, "keep-seeding", "deluno-movies", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["keep-seeding", "favourite"], transmission.Labels);
        var set = Assert.Single(transmission.Calls, c => c.Method == "torrent-set");
        Assert.Equal([Hash], set.Ids);
        // Only the labels are sent: the torrent's folder, state and queue are left alone.
        Assert.Equal(["ids", "labels"], set.ArgumentNames.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(transmission.Calls, c => c.Method is "torrent-remove" or "torrent-stop" or "torrent-set-location");
    }

    [Fact]
    public async Task Transmission_that_already_has_the_label_asks_for_no_change()
    {
        var transmission = new TransmissionFake { Labels = ["keep-seeding"] };

        var result = await new TransmissionDownloadClient(transmission.Factory()).SetCategoryAsync(Item("transmission"), Hash, "keep-seeding", "keep-seeding", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.DoesNotContain(transmission.Calls, c => c.Method == "torrent-set");
    }

    [Fact]
    public async Task Transmission_too_old_for_labels_says_so_and_changes_nothing()
    {
        var transmission = new TransmissionFake { RpcVersion = 15, Labels = ["deluno-movies"] };

        var move = await new TransmissionDownloadClient(transmission.Factory()).SetCategoryAsync(Item("transmission"), Hash, "keep-seeding", "deluno-movies", CancellationToken.None);
        var check = await new TransmissionDownloadClient(transmission.Factory()).CheckKeepSeedingCategoryAsync(Item("transmission"), "keep-seeding", CancellationToken.None);

        Assert.False(move.Succeeded);
        Assert.Contains("too old for labels", move.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(transmission.Calls, c => c.Method == "torrent-set");
        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, check.Status);
        Assert.Contains("too old for labels", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transmission_refusing_the_change_in_its_result_is_a_failure_with_the_reason()
    {
        var transmission = new TransmissionFake { Labels = ["deluno-movies"], RefuseSetWith = "invalid argument" };

        var result = await new TransmissionDownloadClient(transmission.Factory()).SetCategoryAsync(Item("transmission"), Hash, "keep-seeding", "deluno-movies", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("invalid argument", result.Message, StringComparison.Ordinal);
        Assert.Equal(["deluno-movies"], transmission.Labels);
    }

    [Fact]
    public async Task Transmission_that_does_not_hold_the_torrent_is_a_failure_and_nothing_is_set()
    {
        var transmission = new TransmissionFake { Has = false };

        var result = await new TransmissionDownloadClient(transmission.Factory()).SetCategoryAsync(Item("transmission"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(transmission.Calls, c => c.Method == "torrent-set");
    }

    // ---------------------------------------------------------------- rTorrent

    [Fact]
    public async Task Rtorrent_sets_the_label_with_d_custom1_set_and_nothing_else()
    {
        var rtorrent = new RtorrentFake { Label = "" };

        var result = await new RtorrentDownloadClient(rtorrent.Factory()).SetCategoryAsync(Item("rtorrent"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("keep-seeding", rtorrent.Label);
        var set = Assert.Single(rtorrent.Requests, r => r.Method == "d.custom1.set");
        Assert.Equal([Hash, "keep-seeding"], set.Params);
        Assert.DoesNotContain(rtorrent.Requests, r => r.Method is "d.erase" or "d.pause" or "d.stop" or "d.directory.set" or "d.close");
    }

    [Fact]
    public async Task Rtorrent_that_does_not_hold_the_torrent_is_a_failure_and_nothing_is_set()
    {
        var rtorrent = new RtorrentFake { Has = false };

        var result = await new RtorrentDownloadClient(rtorrent.Factory()).SetCategoryAsync(Item("rtorrent"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(rtorrent.Requests, r => r.Method == "d.custom1.set");
    }

    [Fact]
    public async Task Rtorrent_that_faults_on_the_label_is_a_failure_with_its_reason()
    {
        var rtorrent = new RtorrentFake { Label = "", FaultOnSet = "Could not set custom1" };

        var result = await new RtorrentDownloadClient(rtorrent.Factory()).SetCategoryAsync(Item("rtorrent"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Could not set custom1", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rtorrent_cannot_check_a_label_and_says_so_plainly()
    {
        var rtorrent = new RtorrentFake();

        var check = await new RtorrentDownloadClient(rtorrent.Factory()).CheckKeepSeedingCategoryAsync(Item("rtorrent"), "keep-seeding", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, check.Status);
        Assert.Contains("can't check this one", check.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- uTorrent

    [Fact]
    public async Task Utorrent_sets_the_label_with_setprops_and_nothing_else()
    {
        var utorrent = new UTorrentFake { Label = "movies" };

        var result = await new UTorrentDownloadClient(() => utorrent).SetCategoryAsync(Item("utorrent"), Hash, "keep-seeding", "movies", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("keep-seeding", utorrent.Label);
        var set = Assert.Single(utorrent.Paths, p => p.Contains("action=setprops", StringComparison.Ordinal));
        Assert.Contains($"hash={Hash}", set);
        Assert.Contains("s=label", set);
        Assert.Contains("v=keep-seeding", set);
        Assert.DoesNotContain(utorrent.Paths, p => p.Contains("action=remove", StringComparison.Ordinal) || p.Contains("action=pause", StringComparison.Ordinal) || p.Contains("action=stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Utorrent_that_does_not_list_the_torrent_is_a_failure_and_nothing_is_set()
    {
        var utorrent = new UTorrentFake { Has = false };

        var result = await new UTorrentDownloadClient(() => utorrent).SetCategoryAsync(Item("utorrent"), Hash, "keep-seeding", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(utorrent.Paths, p => p.Contains("action=setprops", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Utorrent_that_says_ok_but_keeps_the_old_label_is_not_believed()
    {
        var utorrent = new UTorrentFake { Label = "movies", IgnoreSet = true };

        var result = await new UTorrentDownloadClient(() => utorrent).SetCategoryAsync(Item("utorrent"), Hash, "keep-seeding", "movies", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("still has this download in movies", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Utorrent_cannot_check_a_label_and_says_so_plainly()
    {
        var utorrent = new UTorrentFake();

        var check = await new UTorrentDownloadClient(() => utorrent).CheckKeepSeedingCategoryAsync(Item("utorrent"), "keep-seeding", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, check.Status);
        Assert.Contains("can't check this one", check.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- Usenet

    [Fact]
    public async Task A_usenet_client_is_never_offered_a_move()
    {
        var sab = new SabnzbdDownloadClient(new FactoryOf(new Plain()));
        var nzbget = new NzbGetDownloadClient(new FactoryOf(new Plain()));

        foreach (var (client, item) in new (IDownloadClient, DownloadClientItem)[] { (sab, Item("sabnzbd")), (nzbget, Item("nzbget")) })
        {
            var move = await client.SetCategoryAsync(item, "nzo_1", "keep-seeding", null, CancellationToken.None);
            var check = await client.CheckKeepSeedingCategoryAsync(item, "keep-seeding", CancellationToken.None);

            Assert.False(move.Succeeded);
            Assert.Contains("does not support", move.Message, StringComparison.Ordinal);
            Assert.False(check.Supported);
            Assert.Contains("only a torrent can keep seeding", check.Message, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- fakes

    private static DownloadClientItem Item(string protocol)
        => new($"{protocol}-1", protocol, protocol, "localhost", 8080, null, null, "http://localhost:8080/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class FactoryOf(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Plain : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>TODO(#1237): replace with the recorded reply. Documented qBittorrent Web API shapes.</summary>
    private sealed class QbitFake : HttpMessageHandler
    {
        public string? Category { get; set; } = "";
        public bool AutoManaged { get; set; }
        public bool Has { get; set; } = true;
        public bool IgnoreSetCategory { get; set; }
        public HttpStatusCode InfoStatus { get; set; } = HttpStatusCode.OK;
        public string[] KnownCategories { get; set; } = ["deluno-movies", "keep-seeding"];
        public List<(string Path, string Body)> Calls { get; } = [];

        public QbittorrentDownloadClient Client() => new(() => this);

        public void AssertNothingWasRemovedPausedOrRelocated()
            => Assert.DoesNotContain(Calls, c => Regex.IsMatch(c.Path, "torrents/(delete|pause|stop|setLocation|rename|recheck)"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((path + request.RequestUri.Query, body));
            if (path.EndsWith("torrents/info", StringComparison.Ordinal))
            {
                if (InfoStatus != HttpStatusCode.OK) return new HttpResponseMessage(InfoStatus);
                return Json(Has
                    ? JsonSerializer.Serialize(new[] { new { hash = Hash, name = "Example.Movie.2026", category = Category, auto_tmm = AutoManaged } })
                    : "[]");
            }

            if (path.EndsWith("torrents/categories", StringComparison.Ordinal))
            {
                return Json(JsonSerializer.Serialize(KnownCategories.ToDictionary(name => name, name => new { name, savePath = "" })));
            }

            if (path.EndsWith("setAutoManagement", StringComparison.Ordinal))
            {
                AutoManaged = !body.Contains("enable=false", StringComparison.Ordinal);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path.EndsWith("torrents/setCategory", StringComparison.Ordinal))
            {
                var wanted = Uri.UnescapeDataString(Regex.Match(body, "category=([^&]*)").Groups[1].Value);
                if (!KnownCategories.Contains(wanted, StringComparer.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("Category name does not exist") };
                }

                if (!IgnoreSetCategory) Category = wanted;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
        }
    }

    /// <summary>TODO(#1237): replace with the recorded reply. Documented Deluge web JSON-RPC and Label plugin shapes.</summary>
    private sealed class DelugeFake : HttpMessageHandler
    {
        public string Label { get; set; } = "";
        public string[] Labels { get; set; } = ["keep-seeding"];
        public bool LabelPluginOn { get; set; } = true;
        public bool Has { get; set; } = true;
        public string? RefuseSetWith { get; set; }
        public List<(string Name, string[] Params)> Methods { get; } = [];

        public IHttpClientFactory Factory() => new FactoryOf(this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = document.RootElement.GetProperty("method").GetString()!;
            var parameters = document.RootElement.GetProperty("params").EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ToString()).ToArray();
            Methods.Add((method, parameters));
            switch (method)
            {
                case "auth.login":
                case "web.connected":
                    return Json("""{"result":true,"error":null,"id":1}""");
                case "label.get_labels":
                    return LabelPluginOn
                        ? Json($$"""{"result":{{JsonSerializer.Serialize(Labels)}},"error":null,"id":1}""")
                        : Json("""{"result":null,"error":{"message":"Unknown method"},"id":1}""");
                case "core.get_torrent_status":
                    return Json(Has
                        ? $$"""{"result":{"label":{{JsonSerializer.Serialize(Label)}}},"error":null,"id":1}"""
                        : """{"result":{},"error":null,"id":1}""");
                case "label.set_torrent":
                    if (RefuseSetWith is not null)
                    {
                        return Json($$"""{"result":null,"error":{"message":{{JsonSerializer.Serialize(RefuseSetWith)}}},"id":1}""");
                    }

                    Label = parameters[1];
                    return Json("""{"result":null,"error":null,"id":1}""");
                default:
                    return Json("""{"result":null,"error":null,"id":1}""");
            }
        }
    }

    /// <summary>TODO(#1237): replace with the recorded reply. Documented Transmission RPC shapes.</summary>
    private sealed class TransmissionFake : HttpMessageHandler
    {
        public string[] Labels { get; set; } = [];
        public int RpcVersion { get; set; } = 17;
        public bool Has { get; set; } = true;
        public string? RefuseSetWith { get; set; }
        public List<(string Method, string[] Ids, string[] ArgumentNames)> Calls { get; } = [];

        public IHttpClientFactory Factory() => new FactoryOf(this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var method = document.RootElement.GetProperty("method").GetString()!;
            var arguments = document.RootElement.GetProperty("arguments");
            var ids = arguments.TryGetProperty("ids", out var idList)
                ? idList.EnumerateArray().Select(item => item.ToString()).ToArray()
                : [];
            Calls.Add((method, ids, arguments.EnumerateObject().Select(p => p.Name).ToArray()));
            switch (method)
            {
                case "session-get":
                    return Json($$"""{"arguments":{"rpc-version":{{RpcVersion}}},"result":"success"}""");
                case "torrent-get":
                    return Json(Has
                        ? $$"""{"arguments":{"torrents":[{"hashString":"{{Hash}}","labels":{{JsonSerializer.Serialize(Labels)}}}]},"result":"success"}"""
                        : """{"arguments":{"torrents":[]},"result":"success"}""");
                case "torrent-set":
                    if (RefuseSetWith is not null)
                    {
                        return Json($$"""{"arguments":{},"result":{{JsonSerializer.Serialize(RefuseSetWith)}}}""");
                    }

                    Labels = arguments.GetProperty("labels").EnumerateArray().Select(item => item.GetString()!).ToArray();
                    return Json("""{"arguments":{},"result":"success"}""");
                default:
                    return Json("""{"arguments":{},"result":"success"}""");
            }
        }
    }

    /// <summary>TODO(#1237): replace with the recorded reply. Documented rTorrent XML-RPC shapes.</summary>
    private sealed class RtorrentFake : HttpMessageHandler
    {
        public string Label { get; set; } = "";
        public bool Has { get; set; } = true;
        public string? FaultOnSet { get; set; }
        public List<(string Method, string[] Params)> Requests { get; } = [];

        public IHttpClientFactory Factory() => new FactoryOf(this);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var xml = await request.Content!.ReadAsStringAsync(cancellationToken);
            var method = Regex.Match(xml, "<methodName>([^<]*)</methodName>").Groups[1].Value;
            var parameters = Regex.Matches(xml, "<string>([^<]*)</string>").Select(match => System.Net.WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
            Requests.Add((method, parameters));
            static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
            static string Fault(string reason) => $"<?xml version=\"1.0\"?><methodResponse><fault><value><struct><member><name>faultCode</name><value><i4>-501</i4></value></member><member><name>faultString</name><value><string>{reason}</string></value></member></struct></value></fault></methodResponse>";
            switch (method)
            {
                case "d.custom1":
                    return Has
                        ? Xml($"<?xml version=\"1.0\"?><methodResponse><params><param><value><string>{Label}</string></value></param></params></methodResponse>")
                        : Xml(Fault("Could not find info-hash."));
                case "d.custom1.set":
                    if (FaultOnSet is not null) return Xml(Fault(FaultOnSet));
                    Label = parameters[1];
                    return Xml("<?xml version=\"1.0\"?><methodResponse><params><param><value><i4>0</i4></value></param></params></methodResponse>");
                default:
                    return Xml("<?xml version=\"1.0\"?><methodResponse><params><param><value><string>0.9.8</string></value></param></params></methodResponse>");
            }
        }
    }

    /// <summary>TODO(#1237): replace with the recorded reply. Documented uTorrent WebUI shapes (list=1 rows are positional, label at index 11).</summary>
    private sealed class UTorrentFake : HttpMessageHandler
    {
        public string Label { get; set; } = "";
        public bool Has { get; set; } = true;
        public bool IgnoreSet { get; set; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Paths.Add(uri);
            if (uri.Contains("token.html", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<div id='token'>test-token</div>", Encoding.UTF8, "text/html") });
            }

            if (uri.Contains("list=1", StringComparison.Ordinal))
            {
                var row = $"[\"{Hash}\",201,\"Example.Movie.2026\",1000,0,0,0,0,0,0,0,{JsonSerializer.Serialize(Label)}]";
                return Task.FromResult(Json($$"""{"build":1,"torrents":[{{(Has ? row : string.Empty)}}]}"""));
            }

            if (uri.Contains("action=setprops", StringComparison.Ordinal) && !IgnoreSet)
            {
                Label = Uri.UnescapeDataString(Regex.Match(uri, "v=([^&]*)").Groups[1].Value);
            }

            return Task.FromResult(Json("""{"build":1}"""));
        }
    }
}
