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
    private const string Hash = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";

    private static DownloadClientItem Item() => Client("transmission", "user", "pw");

    private static TransmissionDownloadClient ClientAnswering(Func<HttpRequestMessage, string, HttpResponseMessage?> answer, out Calls calls)
    {
        var made = new Calls("transmission", [RecordedReplies.Transmission("settings")], answer);
        calls = made;
        return new TransmissionDownloadClient(new Factory(made.Build()));
    }

    private static readonly string[] OddReplies =
    [
        "null",
        "{\"result\":\"success\"}",
        "{\"result\":\"success\",\"arguments\":{}}",
        "{\"result\":\"success\",\"arguments\":{\"torrents\":[]}}",
        "{\"result\":\"success\",\"arguments\":{\"torrents\":[{}]}}",
        "{\"result\":\"success\",\"arguments\":{\"torrent-added\":{},\"torrent-duplicate\":{},\"torrents\":[{\"id\":7,\"hashString\":\"d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c\",\"status\":4,\"percentDone\":0.5,\"labels\":[\"\",\"movies\"],\"files\":[{\"name\":\"a/b.mkv\"},{}],\"trackerStats\":[{}],\"error\":3}]}}",
        "{\"result\":\"success\",\"arguments\":{\"rpc-version\":18,\"version\":\"4.1.3 (abc)\",\"download-dir\":\"/downloads\",\"size-bytes\":500,\"alt-speed-enabled\":true,\"torrents\":[{\"id\":7,\"hashString\":\"d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c\",\"name\":\"x\",\"status\":0,\"labels\":[],\"files\":[]}]}}",
        "{\"result\":\"success\",\"arguments\":{\"rpc-version\":18,\"torrents\":[{\"id\":7,\"hashString\":\"other\",\"status\":6,\"percentDone\":1}]}}",
        "{\"result\":\"no such torrent\"}"
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
    public async Task A_client_that_drops_the_connection_answers_a_snapshot_free_space_and_name_lookup_with_nothing()
    {
        var client = ClientAnswering((_, body) => body.Contains("session-get", StringComparison.Ordinal) || body.Contains("free-space", StringComparison.Ordinal) || body.Contains("\"torrent-get\"", StringComparison.Ordinal)
            ? throw new HttpRequestException("The connection dropped.") : RecordedReplies.Json("{\"result\":\"success\"}"), out _);

        await EveryCallOnAnOddReply.AskAsync(client, Item(), usenet: false, Hash);
    }
}
