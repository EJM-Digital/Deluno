using System.Net;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1268: every branch of the Transmission adapter. The working replies are replayed from Transmission 4.1.3's recordings in the other
/// Transmission test classes; here every call is answered with a reply that says little or nothing (what a client sends when it is
/// half-working), plus the session handshake and the cases where a caller gives the client nothing to work with.
/// </summary>
public sealed class TransmissionEveryBranchTests
{
    private static readonly string Hash = RecordedReplies.Qbittorrent("category-moved").Hash();

    private static DownloadClientItem Item() => Client("transmission", "user", "pw");

    private static TransmissionDownloadClient ClientAnswering(Func<HttpRequestMessage, string, HttpResponseMessage?> answer, out Calls calls)
    {
        var made = new Calls("transmission", [RecordedReplies.Transmission("settings")], answer);
        calls = made;
        return new TransmissionDownloadClient(new Factory(made.Build()));
    }

    private static readonly string Rich = "{\"result\":\"success\",\"arguments\":{\"rpc-version\":18,\"version\":\"4.1.3 (abc)\",\"download-dir\":\"/downloads\",\"incomplete-dir\":\"/incomplete\",\"incomplete-dir-enabled\":true,\"alt-speed-enabled\":true,\"size-bytes\":500,"
        + "\"torrent-added\":{\"id\":3,\"hashString\":\"" + RecordedReplies.Qbittorrent("category-moved").Hash() + "\"},"
        + "\"torrents\":[{\"id\":7,\"hashString\":\"" + RecordedReplies.Qbittorrent("category-moved").Hash() + "\",\"name\":\"n\",\"status\":4,\"percentDone\":0.5,\"rateDownload\":5,\"rateUpload\":2,\"eta\":100,\"totalSize\":1000,\"downloadedEver\":500,\"peersConnected\":3,"
        + "\"addedDate\":1700000000,\"downloadDir\":\"/d\",\"labels\":[\"movies\",\"seeding\"],\"error\":3,\"errorString\":\"x\",\"peersSendingToUs\":2,\"isStalled\":true,\"uploadRatio\":1.5,\"secondsSeeding\":120,\"isPrivate\":true,"
        + "\"trackerStats\":[{\"lastAnnounceResult\":\"Unregistered\",\"lastAnnounceSucceeded\":false,\"hasAnnounced\":true,\"announce\":\"http://t.example/a\"}],\"activityDate\":1,\"doneDate\":2,\"queuePosition\":1,\"seedRatioMode\":1,\"seedRatioLimit\":2.0,\"seedIdleMode\":1,\"seedIdleLimit\":30,\"files\":[{\"name\":\"a.mkv\"}]},"
        + "{\"id\":8,\"hashString\":\"other\",\"status\":6,\"percentDone\":1,\"error\":1,\"trackerStats\":[{\"hasAnnounced\":true,\"lastAnnounceSucceeded\":true}]},{}]}}";

    private static readonly string[] OddReplies =
    [
        "null",
        "{\"result\":\"success\"}",
        "{\"result\":\"success\",\"arguments\":{}}",
        "{\"result\":\"success\",\"arguments\":{\"torrents\":[]}}",
        "{\"result\":\"success\",\"arguments\":{\"torrents\":[{}]}}",
        "{\"result\":\"success\",\"arguments\":{\"torrent-added\":{},\"torrent-duplicate\":{},\"torrents\":[{\"id\":7,\"hashString\":\"" + RecordedReplies.Qbittorrent("category-moved").Hash() + "\",\"status\":4,\"percentDone\":0.5,\"labels\":[\"\",\"movies\"],\"files\":[{\"name\":\"a/b.mkv\"},{}],\"trackerStats\":[{}],\"error\":3}]}}",
        "{\"result\":\"success\",\"arguments\":{\"rpc-version\":18,\"version\":\"4.1.3 (abc)\",\"download-dir\":\"/downloads\",\"size-bytes\":500,\"alt-speed-enabled\":true,\"torrents\":[{\"id\":7,\"hashString\":\"" + RecordedReplies.Qbittorrent("category-moved").Hash() + "\",\"name\":\"x\",\"status\":0,\"labels\":[],\"files\":[]}]}}",
        "{\"result\":\"success\",\"arguments\":{\"rpc-version\":18,\"torrents\":[{\"id\":7,\"hashString\":\"other\",\"status\":6,\"percentDone\":1}]}}",
        "{\"result\":\"no such torrent\"}",
        "{\"result\":\"success\",\"arguments\":{\"torrent-added\":{\"id\":3,\"hashString\":\" ABCDEF \"}}}",
        "{\"result\":\"success\",\"arguments\":{\"torrent-added\":{\"id\":3}}}",
        "{\"result\":\"success\",\"arguments\":{\"torrent-duplicate\":{\"hashString\":\"abc\"}}}",
        "{\"result\":\"success\",\"arguments\":{\"torrent-added\":{\"id\":3,\"hashString\":\"  \"}}}",
        "{\"result\":5,\"arguments\":{\"rpc-version\":18}}",
        Rich,
        Rich.Replace("\"result\":\"success\"", "\"result\":\"too many\"", StringComparison.Ordinal)
    ];


    [Fact]
    public async Task No_question_throws_when_the_client_answers_every_call_with_a_reply_that_says_little()
        => await Task.WhenAll(OddReplies.Select(AskWith));

    private static async Task AskWith(string reply)
    {
        // Each way of asking has its own client (and its own call list), and they ask side by side: a half-answered pause or removal waits out its settle window.
        await Task.WhenAll(
            EveryCallOnAnOddReply.AskAsync(ClientAnswering((_, _) => RecordedReplies.Json(reply), out _), Item(), usenet: false, Hash),
            EveryCallOnAnOddReply.AskAsync(ClientAnswering((_, _) => RecordedReplies.Json(reply), out _), Item(), usenet: false, "7"),
            EveryCallOnAnOddReply.AskAsync(ClientAnswering((_, _) => RecordedReplies.Json(reply), out _), Item() with { MoviesCategory = "", TvCategory = "" }, usenet: false, " "));
    }

    [Fact]
    public async Task The_session_handshake_is_followed_and_a_reply_with_no_body_after_it_is_empty()
    {
        var asked = 0;
        var client = ClientAnswering((request, _) =>
        {
            if (asked++ > 0) return RecordedReplies.Json("null");
            var refusal = new HttpResponseMessage(HttpStatusCode.Conflict);
            refusal.Headers.Add("X-Transmission-Session-Id", "abc123");
            return refusal;
        }, out var calls);

        var snapshot = await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(calls.Count("transmission/rpc") >= 2);
    }

    [Fact]
    public async Task Nothing_is_asked_without_an_address()
    {
        var client = ClientAnswering((_, _) => RecordedReplies.Json("null"), out var calls);
        var noAddress = Item() with { Host = null, EndpointUrl = null };

        var grab = await client.GrabAsync(noAddress, EveryCallOnAnOddReply.GrabRequests(false).First(), CancellationToken.None);
        var snapshot = await client.GetSnapshotAsync(noAddress, DateTimeOffset.UtcNow, CancellationToken.None);
        var action = await client.ExecuteActionAsync(noAddress, DownloadClientActions.Recheck, Hash, CancellationToken.None);
        var set = await client.SetCategoryAsync(noAddress, "7", "seeding", "movies", CancellationToken.None);
        var check = await client.CheckCategoryAsync(noAddress, "movies", CancellationToken.None);
        var keep = await client.CheckKeepSeedingCategoryAsync(noAddress, "seeding", CancellationToken.None);
        var blank = await client.CheckKeepSeedingCategoryAsync(Item(), " ", CancellationToken.None);

        Assert.False(grab.Succeeded);
        Assert.Contains("address", snapshot!.LastHealthMessage);
        Assert.False(action.Succeeded);
        Assert.False(set.Succeeded);
        Assert.Contains("address", check.Message);
        Assert.Contains("address", keep.Message);
        Assert.Contains("Enter a category", blank.Message);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public async Task A_torrent_id_that_is_not_a_number_is_looked_up_by_its_hash_or_refused_when_blank()
    {
        var client = ClientAnswering((_, _) => RecordedReplies.Json("{\"result\":\"success\",\"arguments\":{\"rpc-version\":18,\"torrents\":[{\"id\":9,\"hashString\":\"" + Hash + "\"}]}}"), out _);

        var byHash = await client.SetCategoryAsync(Item(), Hash.ToUpperInvariant(), "seeding", "movies", CancellationToken.None);
        var unreadable = await client.SetCategoryAsync(Item(), "   ", "seeding", "movies", CancellationToken.None);

        Assert.NotNull(byHash);
        Assert.False(unreadable.Succeeded);
    }

    [Fact]
    public async Task No_question_throws_when_the_client_drops_the_connection_for_each_call_in_turn()
        => await Task.WhenAll(new[] { "session-get", "free-space", "torrent-get", "torrent-add", "torrent-set", "torrent-remove", "torrent-start", "torrent-stop", "torrent-verify", "queue-move-top" }.Select(dropped =>
            EveryCallOnAnOddReply.AskAsync(ClientAnswering((_, body) => body.Contains("\"" + dropped + "\"", StringComparison.Ordinal)
                ? throw new HttpRequestException("The connection dropped.") : RecordedReplies.Json(Rich), out _), Item(), usenet: false, Hash)));

    [Fact]
    public async Task A_client_on_another_machine_does_not_have_its_free_space_measured_here()
    {
        var local = ClientAnswering((_, body) => RecordedReplies.Json(body.Contains("free-space", StringComparison.Ordinal) ? "{\"result\":\"success\",\"arguments\":{}}" : Rich), out _);

        var onThisMachine = await local.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);
        var elsewhere = await local.GetSnapshotAsync(Item() with { Host = "nas.example.lan", EndpointUrl = null }, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(onThisMachine);
        Assert.Null(elsewhere!.FreeSpaceBytes);
    }

    [Fact]
    public async Task No_question_throws_when_one_call_in_turn_is_answered_with_less_than_the_rest()
    {
        var degraded = new[]
        {
            "null",
            "{\"result\":\"success\"}",
            "{\"result\":\"success\",\"arguments\":{}}",
            "{\"result\":\"success\",\"arguments\":{\"torrents\":[]}}",
            "{\"result\":\"success\",\"arguments\":{\"torrents\":[{}]}}",
            "{\"result\":\"success\",\"arguments\":{\"torrents\":[{\"id\":99,\"hashString\":\"x\"},{\"id\":98,\"hashString\":\"y\"}],\"rpc-version\":3}}",
            "{\"result\":5}",
            "{\"arguments\":{\"rpc-version\":18}}"
        };
        var asks = new List<Task>();
        foreach (var target in new[] { "session-get", "free-space", "torrent-get", "torrent-add", "torrent-set", "torrent-remove", "torrent-stop", "torrent-verify" })
        {
            foreach (var reply in degraded)
            {
                foreach (var hash in new[] { Hash, "7", "unknown" })
                {
                    asks.Add(EveryCallOnAnOddReply.AskAsync(ClientAnswering((_, body) =>
                        RecordedReplies.Json(body.Contains("\"" + target + "\"", StringComparison.Ordinal) ? reply : Rich), out _), Item(), usenet: false, hash));
                }
            }
        }

        await Task.WhenAll(asks);
    }

    [Fact]
    public async Task A_category_check_needs_a_name()
    {
        var client = ClientAnswering((_, _) => RecordedReplies.Json(Rich), out _);

        var blank = await client.CheckCategoryAsync(Item(), " ", CancellationToken.None);
        var noAddress = await client.CheckKeepSeedingCategoryAsync(Item() with { Host = null, EndpointUrl = null }, "seeding", CancellationToken.None);

        Assert.Contains("Enter a category", blank.Message);
        Assert.Contains("address", noAddress.Message);
    }

    [Fact]
    public async Task A_removal_with_data_whose_name_and_folder_cannot_be_read_still_removes_the_torrent()
    {
        foreach (var weak in new[] { "null", "{\"result\":\"success\"}", "{\"result\":\"success\",\"arguments\":{\"torrents\":[]}}", "{\"result\":\"success\",\"arguments\":{\"torrents\":[{}]}}" })
        {
            var client = ClientAnswering((_, body) => RecordedReplies.Json(body.Contains("\"name\",\"downloadDir\"", StringComparison.Ordinal) ? weak : Rich), out _);

            var result = await client.ExecuteActionAsync(Item(), DownloadClientActions.DeleteWithData, "7", CancellationToken.None);

            Assert.NotNull(result);
        }

        var dropped = ClientAnswering((_, body) => body.Contains("\"name\",\"downloadDir\"", StringComparison.Ordinal) ? throw new HttpRequestException("The connection dropped.") : RecordedReplies.Json(Rich), out _);
        Assert.NotNull(await dropped.ExecuteActionAsync(Item(), DownloadClientActions.DeleteWithData, "7", CancellationToken.None));
    }
}
