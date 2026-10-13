using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1240, from the recorded real replies of qBittorrent 5.2.1 and Deluge 2.2.0 (#1237): a struck private torrent is moved
/// to a keep-seeding category by the client's own call, and the move counts as done only when the client reports the new
/// category afterwards. A refusal is a plain failure with the client's reason, and nothing here removes, pauses or relocates anything.
///
/// <para>Every reply is the recorded one. The torrent list is the recorded list changed in the one value a test is about (its
/// category or label); the "unknown category" and "unknown label" replies are the recorded refusals. Replies that were not
/// recorded and are answered with a recorded reply of the same shape: qBittorrent's <c>setAutoManagement</c> (answered with the
/// recorded <c>setCategory</c> success, an empty 200), Deluge's <c>core.get_torrent_status</c> for one torrent (the recorded
/// torrent entry of <c>web.update_ui</c>, which carries the same fields), and Deluge's reply to an id it has never heard of
/// (the recorded one for another call). Not recorded at all, so still tested in
/// <see cref="KeepSeedingMoveOnEveryTorrentClientTests"/>: <c>label.get_labels</c> with the Label plugin off.</para>
/// </summary>
public sealed class KeepSeedingMoveOnQbittorrentAndDelugeTests
{
    private const string QbitHash = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";
    private const string DelugeHash = "d8185091144732e990e3595f7db0e4410b1d9424";
    private const string KeepSeeding = "recorded-movies";
    private const string OldCategory = "<scrubbed>-movies";

    // ---------------------------------------------------------------- qBittorrent

    [Fact]
    public async Task Qbittorrent_moves_a_manually_managed_torrent_with_set_category_and_changes_nothing_else()
    {
        var qbit = new QbitRig { Category = OldCategory, AutoManaged = false };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), QbitHash, KeepSeeding, OldCategory, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(KeepSeeding, qbit.Category);
        var call = Assert.Single(qbit.Calls, c => c.Path.EndsWith("torrents/setCategory", StringComparison.Ordinal));
        Assert.Contains($"hashes={QbitHash}", call.Body);
        Assert.Contains($"category={KeepSeeding}", call.Body);
        // A torrent already on manual management is not touched, and nothing is paused, stopped or removed.
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("setAutoManagement", StringComparison.Ordinal));
        qbit.AssertNothingWasRemovedPausedOrRelocated();
    }

    [Fact]
    public async Task Qbittorrent_takes_an_automatically_managed_torrent_off_it_first_so_the_move_cannot_relocate_the_data()
    {
        var qbit = new QbitRig { Category = OldCategory, AutoManaged = true };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), QbitHash, KeepSeeding, OldCategory, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var order = qbit.Calls.Select(c => c.Path.Split('/').Last()).Where(name => name is "setAutoManagement" or "setCategory").ToArray();
        Assert.Equal(["setAutoManagement", "setCategory"], order);
        Assert.Contains("enable=false", qbit.Calls.Single(c => c.Path.EndsWith("setAutoManagement", StringComparison.Ordinal)).Body);
        qbit.AssertNothingWasRemovedPausedOrRelocated();
    }

    [Fact]
    public async Task Qbittorrent_refusing_a_category_it_does_not_have_is_a_plain_failure_and_the_torrent_is_left_as_it_was()
    {
        var qbit = new QbitRig { Category = OldCategory };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), QbitHash, "does-not-exist", OldCategory, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not have a category named does-not-exist", result.Message, StringComparison.Ordinal);
        Assert.Contains("Create it in qBittorrent first", result.Message, StringComparison.Ordinal);
        Assert.Equal(OldCategory, qbit.Category);
        qbit.AssertNothingWasRemovedPausedOrRelocated();
    }

    [Fact]
    public async Task Qbittorrent_that_says_ok_but_still_reports_the_old_category_is_not_believed()
    {
        var qbit = new QbitRig { Category = OldCategory, IgnoreSetCategory = true };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), QbitHash, KeepSeeding, OldCategory, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains($"still has this download in {OldCategory}", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Qbittorrent_without_the_torrent_is_a_failure_and_nothing_is_set()
    {
        var qbit = new QbitRig { Has = false };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), QbitHash, KeepSeeding, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("setCategory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Qbittorrent_that_cannot_be_reached_is_a_failure_that_changes_nothing()
    {
        var qbit = new QbitRig { InfoStatus = HttpStatusCode.ServiceUnavailable };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), QbitHash, KeepSeeding, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("setCategory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Qbittorrent_already_in_the_category_asks_for_no_change()
    {
        var qbit = new QbitRig { Category = KeepSeeding };

        var result = await qbit.Client().SetCategoryAsync(Item("qbittorrent"), QbitHash, KeepSeeding, KeepSeeding, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("setCategory", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(KeepSeeding, DownloadClientCategoryStatuses.Ready)]
    [InlineData("nope", DownloadClientCategoryStatuses.Missing)]
    public async Task Qbittorrent_checks_a_keep_seeding_category_against_its_own_list_and_never_creates_it(string category, string status)
    {
        var qbit = new QbitRig();

        var result = await qbit.Client().CheckKeepSeedingCategoryAsync(Item("qbittorrent"), category, CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.DoesNotContain(qbit.Calls, c => c.Path.EndsWith("createCategory", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Deluge

    [Fact]
    public async Task Deluge_sets_the_label_with_label_set_torrent_and_changes_nothing_else()
    {
        var deluge = new DelugeRig { Label = OldCategory };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), DelugeHash, KeepSeeding, OldCategory, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(KeepSeeding, deluge.Label);
        var call = Assert.Single(deluge.Methods, m => m.Name == "label.set_torrent");
        Assert.Equal([DelugeHash, KeepSeeding], call.Params);
        Assert.DoesNotContain(deluge.Methods, m => m.Name is "core.remove_torrent" or "core.pause_torrent" or "core.move_storage" or "core.set_torrent_options" or "label.add");
    }

    [Fact]
    public async Task Deluge_never_creates_a_label_it_does_not_have()
    {
        var deluge = new DelugeRig { Label = OldCategory };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), DelugeHash, "does-not-exist", OldCategory, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("no label named does-not-exist", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(deluge.Methods, m => m.Name is "label.add" or "label.set_torrent");
        var check = await new DelugeDownloadClient(deluge.Factory()).CheckKeepSeedingCategoryAsync(Item("deluge"), "does-not-exist", CancellationToken.None);
        Assert.Equal(DownloadClientCategoryStatuses.Missing, check.Status);
        var ready = await new DelugeDownloadClient(deluge.Factory()).CheckKeepSeedingCategoryAsync(Item("deluge"), KeepSeeding, CancellationToken.None);
        Assert.Equal(DownloadClientCategoryStatuses.Ready, ready.Status);
    }

    [Fact]
    public async Task Deluge_refusing_the_label_in_its_reply_is_a_failure_with_its_reason()
    {
        // The label is in the list when the move is checked and Deluge refuses it when asked: the recorded refusal.
        var deluge = new DelugeRig { Label = OldCategory, ListsAlso = "does-not-exist" };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), DelugeHash, "does-not-exist", OldCategory, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Unknown Label", result.Message, StringComparison.Ordinal);
        Assert.Equal(OldCategory, deluge.Label);
    }

    [Fact]
    public async Task Deluge_that_does_not_hold_the_torrent_is_a_failure_and_nothing_is_set()
    {
        var deluge = new DelugeRig { Has = false };

        var result = await new DelugeDownloadClient(deluge.Factory()).SetCategoryAsync(Item("deluge"), DelugeHash, KeepSeeding, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(deluge.Methods, m => m.Name == "label.set_torrent");
    }

    // ---------------------------------------------------------------- rigs

    private static DownloadClientItem Item(string protocol)
        => new($"{protocol}-1", protocol, protocol, "localhost", 8080, null, null, "http://localhost:8080/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>qBittorrent 5.2.1 as recorded, holding one torrent whose category the rig tracks.</summary>
    private sealed class QbitRig
    {
        public string Category { get; set; } = OldCategory;
        public bool AutoManaged { get; set; }
        public bool Has { get; set; } = true;
        public bool IgnoreSetCategory { get; set; }
        public HttpStatusCode InfoStatus { get; set; } = HttpStatusCode.OK;
        public List<(string Path, string Body)> Calls { get; } = [];

        public QbittorrentDownloadClient Client() => new(() => RecordedReplies.Handler("qbittorrent",
            [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("category-moved")], Intercept));

        public void AssertNothingWasRemovedPausedOrRelocated()
            => Assert.DoesNotContain(Calls, c => Regex.IsMatch(c.Path, "torrents/(delete|pause|stop|setLocation|rename|recheck)"));

        private HttpResponseMessage? Intercept(HttpRequestMessage request, string body)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add((path + request.RequestUri.Query, body));
            if (path.EndsWith("torrents/info", StringComparison.Ordinal))
            {
                if (InfoStatus != HttpStatusCode.OK) return new HttpResponseMessage(InfoStatus);
                if (!Has) return RecordedReplies.Json(RecordedReplies.Qbittorrent("removed-twice").Find("torrents/info").Reply.Body!);
                return RecordedReplies.Json(RecordedReplies.Qbittorrent("category-moved").Find("torrents/info").Reply
                    .BodyWith(("0.category", Category), ("0.auto_tmm", AutoManaged)));
            }

            if (path.EndsWith("setAutoManagement", StringComparison.Ordinal))
            {
                AutoManaged = !body.Contains("enable=false", StringComparison.Ordinal);
                return RecordedReplies.Qbittorrent("category-moved").Find("torrents/setCategory").Reply.ToResponse();
            }

            if (path.EndsWith("torrents/setCategory", StringComparison.Ordinal))
            {
                var wanted = Uri.UnescapeDataString(Regex.Match(body, "category=([^&]*)").Groups[1].Value);
                var known = RecordedReplies.Qbittorrent("category-moved").Find("torrents/categories").Reply.Json().AsObject().Select(p => p.Key);
                if (!known.Contains(wanted, StringComparer.OrdinalIgnoreCase))
                {
                    return RecordedReplies.Qbittorrent("category-unknown").Find("torrents/setCategory").Reply.ToResponse();
                }

                if (!IgnoreSetCategory) Category = wanted;
                return RecordedReplies.Qbittorrent("category-moved").Find("torrents/setCategory").Reply.ToResponse();
            }

            return null;
        }
    }

    /// <summary>Deluge 2.2.0 as recorded, holding one torrent whose label the rig tracks.</summary>
    private sealed class DelugeRig
    {
        public string Label { get; set; } = "";
        public bool Has { get; set; } = true;
        public string? ListsAlso { get; set; }
        public List<(string Name, string[] Params)> Methods { get; } = [];

        public IHttpClientFactory Factory() => new Factory(RecordedReplies.Handler("deluge",
            [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("label-set")], Intercept));

        private HttpResponseMessage? Intercept(HttpRequestMessage request, string body)
        {
            using var document = JsonDocument.Parse(body);
            var method = document.RootElement.GetProperty("method").GetString()!;
            var parameters = document.RootElement.GetProperty("params").EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ToString()).ToArray();
            Methods.Add((method, parameters));
            switch (method)
            {
                case "label.get_labels" when ListsAlso is not null:
                    return RecordedReplies.Json(RecordedReplies.Deluge("label-set").Find("label.get_labels").Reply.BodyWith(("+result", 4), ("result.3", ListsAlso)));
                case "core.get_torrent_status":
                    if (!Has)
                    {
                        return RecordedReplies.Json(RecordedReplies.Deluge("unknown-id").Exchanges[0].Reply.Body!);
                    }

                    var torrent = RecordedReplies.Deluge("label-set").Find("web.update_ui").Reply.Json()["result"]!["torrents"]!.AsObject().First().Value!.DeepClone();
                    torrent["label"] = Label;
                    return RecordedReplies.Json(new JsonObject { ["result"] = torrent, ["error"] = null, ["id"] = 31 }.ToJsonString());
                case "label.set_torrent":
                    var wanted = parameters[1];
                    var labels = RecordedReplies.Deluge("label-set").Find("label.get_labels").Reply.Json()["result"]!.AsArray().Select(n => n!.GetValue<string>());
                    if (wanted == ListsAlso || !labels.Contains(wanted, StringComparer.OrdinalIgnoreCase))
                    {
                        return RecordedReplies.Json(RecordedReplies.Deluge("label-unknown").Find("label.set_torrent").Reply.Body!);
                    }

                    Label = wanted;
                    return RecordedReplies.Json(RecordedReplies.Deluge("label-set").Find("label.set_torrent").Reply.Body!);
                default:
                    return null;
            }
        }
    }
}
