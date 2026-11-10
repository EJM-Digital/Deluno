using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1268: every branch of the Deluge adapter. Deluge 2.2.0's recorded sign-in lets the client in; after that every call is answered
/// with one odd reply (nothing, an empty result, an error with no words), because the branches left are what the client does with
/// a reply that is not the one it hoped for. The working replies are tested from the recordings in the other Deluge test classes.
/// </summary>
public sealed class DelugeEveryBranchTests
{
    private static readonly string Hash = RecordedReplies.Qbittorrent("category-moved").Hash();

    private static DownloadClientItem Item() => Client("deluge", null, "deluge");

    private static DelugeDownloadClient ClientAnswering(Func<string, HttpResponseMessage> otherwise, out Calls calls)
    {
        var made = new Calls("deluge", [RecordedReplies.Deluge("sign-in")], (_, body) =>
            body.Contains("\"auth.login\"", StringComparison.Ordinal) || body.Contains("\"web.connected\"", StringComparison.Ordinal) ? null : otherwise(body));
        calls = made;
        return new DelugeDownloadClient(new Factory(made.Build()));
    }

    public static TheoryData<string> OddReplies() => new()
    {
        "null",
        "{\"id\":2,\"result\":null,\"error\":null}",
        "{\"id\":2,\"result\":null,\"error\":{\"message\":\"KeyError: 'x'\",\"code\":3}}",
        "{\"id\":2,\"result\":null,\"error\":{\"message\":\"Something else broke\",\"code\":3}}",
        "{\"id\":2,\"result\":null,\"error\":{\"code\":3}}"
    };

    [Theory]
    [MemberData(nameof(OddReplies))]
    public async Task No_question_throws_when_the_client_answers_every_call_after_sign_in_with_an_odd_reply(string reply)
    {
        var client = ClientAnswering(_ => RecordedReplies.Json(reply), out _);

        await EveryCallOnAnOddReply.AskAsync(client, Item(), usenet: false, Hash);
    }

    [Fact]
    public async Task Nothing_is_asked_without_an_address()
    {
        var client = ClientAnswering(_ => RecordedReplies.Json("null"), out var calls);
        var noAddress = Item() with { Host = null, EndpointUrl = null };

        var grab = await client.GrabAsync(noAddress, EveryCallOnAnOddReply.GrabRequests(false).First(), CancellationToken.None);
        var snapshot = await client.GetSnapshotAsync(noAddress, DateTimeOffset.UtcNow, CancellationToken.None);
        var action = await client.ExecuteActionAsync(noAddress, DownloadClientActions.Pause, Hash, CancellationToken.None);
        var set = await client.SetCategoryAsync(noAddress, Hash, "seeding", "movies", CancellationToken.None);
        var files = await client.GetFilesAsync(noAddress, Hash, CancellationToken.None);
        var noId = await client.GetFilesAsync(Item(), " ", CancellationToken.None);

        Assert.False(grab.Succeeded);
        Assert.Contains("address", snapshot!.LastHealthMessage);
        Assert.False(action.Succeeded);
        Assert.False(set.Succeeded);
        Assert.Null(files);
        Assert.Null(noId);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public async Task A_sign_in_the_client_does_not_answer_is_refused_and_a_web_ui_that_does_not_say_whether_it_is_connected_is_asked_for_its_hosts()
    {
        foreach (var login in new[] { "null", "{\"id\":1,\"result\":false,\"error\":null}" })
        {
            var refused = new Calls("deluge", [RecordedReplies.Deluge("sign-in")], (_, body) => body.Contains("\"auth.login\"", StringComparison.Ordinal) ? RecordedReplies.Json(login) : null);
            var result = await new DelugeDownloadClient(new Factory(refused.Build())).ExecuteActionAsync(Item(), DownloadClientActions.Recheck, Hash, CancellationToken.None);
            Assert.False(result.Succeeded);
        }

        foreach (var hosts in new[] { "null", "{\"id\":3,\"result\":null,\"error\":null}", "{\"id\":3,\"result\":{},\"error\":null}" })
        {
            var none = new Calls("deluge", [RecordedReplies.Deluge("sign-in")], (_, body) =>
                body.Contains("\"web.connected\"", StringComparison.Ordinal) ? RecordedReplies.Json("null")
                : body.Contains("\"web.get_hosts\"", StringComparison.Ordinal) ? RecordedReplies.Json(hosts) : null);
            Assert.Null(await new DelugeDownloadClient(new Factory(none.Build())).GetFilesAsync(Item(), Hash, CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_grab_with_no_label_to_set_leaves_the_torrent_unlabelled()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in")], (_, body) =>
            body.Contains("\"auth.login\"", StringComparison.Ordinal) || body.Contains("\"web.connected\"", StringComparison.Ordinal) ? null
            : RecordedReplies.Json(Full(System.Text.RegularExpressions.Regex.Match(body, "\"method\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value, body)));
        var unlabelled = Item() with { MoviesCategory = "", TvCategory = "" };

        var result = await new DelugeDownloadClient(new Factory(calls.Build())).GrabAsync(unlabelled, EveryCallOnAnOddReply.GrabRequests(false).ElementAt(2) with { Category = null }, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(calls.Sent("label."));
    }

    [Fact]
    public async Task A_web_ui_with_no_daemon_connected_is_connected_to_the_first_host_or_refused_when_it_has_none()
    {
        var hosts = "{\"id\":3,\"result\":[[\"c0ffee\",\"127.0.0.1\",58846,\"localhost\"]],\"error\":null}";
        var disconnected = RecordedReplies.Deluge("sign-in").Body("web.connected", ("result", false));
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("settings")], (_, body) =>
        {
            if (body.Contains("\"web.connected\"", StringComparison.Ordinal)) return RecordedReplies.Json(disconnected);
            if (body.Contains("\"web.get_hosts\"", StringComparison.Ordinal)) return RecordedReplies.Json(hosts);
            if (body.Contains("\"web.connect\"", StringComparison.Ordinal)) return RecordedReplies.Json("{\"id\":4,\"result\":null,\"error\":null}");
            return body.Contains("\"auth.login\"", StringComparison.Ordinal) ? null : RecordedReplies.Json("{\"id\":5,\"result\":null,\"error\":null}");
        });
        var none = new Calls("deluge", [RecordedReplies.Deluge("sign-in")], (_, body) =>
        {
            if (body.Contains("\"web.connected\"", StringComparison.Ordinal)) return RecordedReplies.Json(disconnected);
            return body.Contains("\"web.get_hosts\"", StringComparison.Ordinal) ? RecordedReplies.Json("{\"id\":3,\"result\":[],\"error\":null}") : null;
        });

        var connected = await new DelugeDownloadClient(new Factory(calls.Build())).ExecuteActionAsync(Item(), DownloadClientActions.Recheck, Hash, CancellationToken.None);
        var refused = await new DelugeDownloadClient(new Factory(none.Build())).GetFilesAsync(Item(), Hash, CancellationToken.None);

        Assert.True(calls.Sent("web.connect\""));
        Assert.NotNull(connected);
        Assert.Null(refused);
    }

    [Fact]
    public async Task A_label_the_client_lacks_is_said_when_checking_a_category()
    {
        var labels = RecordedDelugeRig.Labels();
        var handler = RecordedDelugeRig.Handler(string.Empty, labels: labels);
        var client = new DelugeDownloadClient(new Factory(handler));

        var absent = await client.CheckCategoryAsync(Item(), "an-unknown-label", CancellationToken.None);
        var blank = await client.CheckCategoryAsync(Item(), " ", CancellationToken.None);
        var noAddress = await client.CheckCategoryAsync(Item() with { Host = null, EndpointUrl = null }, "movies", CancellationToken.None);
        var keepBlank = await client.CheckKeepSeedingCategoryAsync(Item(), "", CancellationToken.None);
        var keepNoAddress = await client.CheckKeepSeedingCategoryAsync(Item() with { Host = null, EndpointUrl = null }, "movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Missing, absent.Status);
        Assert.Equal(DownloadClientCategoryStatuses.Configuration, blank.Status);
        Assert.Contains("address", noAddress.Message);
        Assert.Contains("Enter a category", keepBlank.Message);
        Assert.Contains("address", keepNoAddress.Message);
    }

    [Fact]
    public async Task A_torrent_with_nothing_said_about_it_still_makes_a_queue_item_and_the_label_plugin_off_is_said()
    {
        var bare = RecordedDelugeRig.Finished(("name", null), ("state", null), ("progress", null), ("label", null), ("save_path", null), ("time_added", null), ("eta", null));
        var client = new DelugeDownloadClient(new Factory(RecordedDelugeRig.Handler(bare)));
        var off = new DelugeDownloadClient(new Factory(RecordedDelugeRig.Handler(string.Empty, labels: "{\"id\":11,\"result\":null,\"error\":{\"message\":\"Unknown method\"}}")));

        var snapshot = await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);
        var unsupported = await off.CheckCategoryAsync(Item(), "movies", CancellationToken.None);

        Assert.Single(snapshot!.Queue);
        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, unsupported.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Setting_a_label_with_no_name_does_nothing(string name)
    {
        var client = ClientAnswering(_ => RecordedReplies.Json("{\"id\":2,\"result\":null,\"error\":null}"), out var calls);

        var result = await client.SetCategoryAsync(Item(), Hash, name, "movies", CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(calls.Sent("label.set_torrent"));
    }

    // ---- a client that answers fully, then fails one call at a time ----

    private const string FullTorrent = "{\"name\":\"Example.Movie\",\"state\":\"Seeding\",\"progress\":100,\"download_payload_rate\":5,\"upload_payload_rate\":2,\"eta\":10,\"total_size\":1000,\"total_done\":1000,\"num_peers\":3,\"num_seeds\":2,"
        + "\"time_added\":1700000000,\"label\":\"movies\",\"message\":\"Error: disk full\",\"save_path\":\"/incomplete\",\"move_completed\":true,\"move_completed_path\":\"/final\",\"ratio\":1.5,\"seeding_time\":120,"
        + "\"tracker_status\":\"Error: Unregistered torrent\",\"trackers\":[{\"url\":\"http://t.example/a\"},{}],\"private\":true,\"queue\":0,\"stop_at_ratio\":true,\"stop_ratio\":2.0,\"remove_at_ratio\":true}";

    private static string Full(string method, string body) => method switch
    {
        "web.update_ui" => "{\"id\":2,\"result\":{\"torrents\":{\"" + RecordedReplies.Qbittorrent("category-moved").Hash() + "\":" + FullTorrent
            + ",\"f\":{\"state\":\"Seeding\",\"progress\":100,\"label\":\"tv\",\"save_path\":\"/same/\",\"move_completed\":true,\"move_completed_path\":\"/same\"},\"g\":{\"state\":\"Seeding\",\"progress\":100,\"move_completed\":false,\"save_path\":\"/x\",\"move_completed_path\":\"/y\"},\"h\":{\"state\":\"Seeding\",\"progress\":100,\"move_completed\":true,\"move_completed_path\":\"/y\"},\"e\":{\"state\":\"Seeding\",\"progress\":100,\"label\":\"movies\"},\"b\":{\"name\":\"Moving\",\"state\":\"Moving\",\"progress\":100,\"label\":\"tv\",\"save_path\":\"/x\",\"move_completed\":true,\"move_completed_path\":\"/y\"},\"c\":{},\"d\":{\"state\":\"Paused\",\"progress\":3,\"label\":\"movies\",\"message\":\"OK\"}}},\"error\":null}",
        "core.get_free_space" => "{\"id\":8,\"result\":123456789,\"error\":null}",
        "daemon.info" or "daemon.get_version" => "{\"id\":9,\"result\":\"2.2.0\",\"error\":null}",
        "core.get_torrent_status" => body.Contains("\"files\"", StringComparison.Ordinal) && body.Contains("\"label\"", StringComparison.Ordinal) ? "{\"id\":2,\"result\":{\"label\":\"movies\"},\"error\":null}" : "{\"id\":2,\"result\":{\"files\":[{\"path\":\"a/b.mkv\"},{}],\"label\":\"movies\"},\"error\":null}",
        "label.get_labels" => "{\"id\":5,\"result\":[\"movies\",\"tv\",\"seeding\"],\"error\":null}",
        "label.get_options" => "{\"id\":12,\"result\":{\"apply_move_completed\":true,\"move_completed\":true,\"move_completed_path\":\"/labelfolder\"},\"error\":null}",
        "core.get_config_values" => "{\"id\":10,\"result\":{\"download_location\":\"/incomplete\",\"move_completed\":true,\"move_completed_path\":\"/final\"},\"error\":null}",
        "core.get_session_state" => "{\"id\":9,\"result\":[\"" + RecordedReplies.Qbittorrent("category-moved").Hash() + "\"],\"error\":null}",
        "core.add_torrent_file" or "core.add_torrent_magnet" or "core.add_torrent_url" => "{\"id\":2,\"result\":\"" + RecordedReplies.Qbittorrent("category-moved").Hash() + "\",\"error\":null}",
        "core.remove_torrent" => "{\"id\":3,\"result\":true,\"error\":null}",
        _ => "{\"id\":3,\"result\":null,\"error\":null}"
    };

    private static readonly string[] Methods =
    [
        "web.update_ui", "core.get_free_space", "daemon.info", "daemon.get_version", "core.get_torrent_status", "label.get_labels", "label.get_options", "label.add", "label.set_torrent",
        "core.get_config_values", "core.get_session_state", "core.add_torrent_file", "core.add_torrent_magnet", "core.add_torrent_url", "core.remove_torrent", "core.pause_torrent",
        "core.resume_torrent", "core.force_recheck", "core.queue_top", "core.set_torrent_options"
    ];

    private static string Degraded(string how, string method, string body) => how switch
    {
        "nullbody" => "null",
        "string" => "{\"id\":2,\"result\":\"x\",\"error\":null}",
        "odd" => "{\"id\":2,\"result\":{\"move_completed\":\"yes\",\"move_completed_path\":5,\"apply_move_completed\":0,\"label\":5,\"download_location\":5,\"files\":null,\"torrents\":null},\"error\":null}",
        "nomessage" => "{\"id\":2,\"result\":null,\"error\":{\"code\":4}}",
        "blankmessage" => "{\"id\":2,\"result\":null,\"error\":{\"message\":\"\",\"code\":4}}",
        "error" => "{\"id\":2,\"result\":null,\"error\":{\"message\":\"Torrent already in session (" + RecordedReplies.Qbittorrent("category-moved").Hash() + ")\",\"code\":4}}",
        "keyerror" => "{\"id\":2,\"result\":null,\"error\":{\"message\":\"KeyError: 'x'\",\"code\":4}}",
        "refused" => "{\"id\":2,\"result\":null,\"error\":{\"message\":\"Plugin not enabled\",\"code\":4}}",
        "null" => "{\"id\":2,\"result\":null,\"error\":null}",
        "false" => "{\"id\":2,\"result\":false,\"error\":null}",
        "empty" => "{\"id\":2,\"result\":{},\"error\":null}",
        _ => Full(method, body)
    };

    [Fact]
    public async Task No_question_throws_when_one_call_in_turn_fails_in_each_way_and_the_rest_answer_fully()
    {
        // Side by side: a half-answered removal waits for the client to let go of the torrent.
        var asks = new List<Task>();
        foreach (var target in Methods)
        {
            foreach (var how in new[] { "error", "keyerror", "refused", "null", "false", "drop", "empty", "full", "nullbody", "string", "odd", "nomessage", "blankmessage" })
            {
                foreach (var hash in new[] { Hash, "unknown" })
                {
                    var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in")], (_, body) =>
                    {
                        var method = System.Text.RegularExpressions.Regex.Match(body, "\"method\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                        if (method is "auth.login" or "web.connected") return null;
                        if (method != target) return RecordedReplies.Json(Full(method, body));
                        return how == "drop" ? throw new HttpRequestException("The connection dropped.") : RecordedReplies.Json(Degraded(how, method, body));
                    });
                    asks.Add(EveryCallOnAnOddReply.AskAsync(new DelugeDownloadClient(new Factory(calls.Build())), Item(), usenet: false, hash));
                }
            }
        }

        await Task.WhenAll(asks);
    }

    [Fact]
    public async Task A_torrent_file_is_named_after_the_release_with_every_character_a_file_name_cannot_hold_replaced()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in")], (_, body) =>
            body.Contains("\"auth.login\"", StringComparison.Ordinal) || body.Contains("\"web.connected\"", StringComparison.Ordinal) ? null
            : RecordedReplies.Json(Full(System.Text.RegularExpressions.Regex.Match(body, "\"method\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value, body)));
        var request = EveryCallOnAnOddReply.GrabRequests(false).Last() with { ReleaseName = "Example/Movie: 2026" + new string(Path.GetInvalidFileNameChars()[0], 1) };

        var result = await new DelugeDownloadClient(new Factory(calls.Build())).GrabAsync(Item(), request, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(calls.Sent("Example_Movie"));
    }
}
