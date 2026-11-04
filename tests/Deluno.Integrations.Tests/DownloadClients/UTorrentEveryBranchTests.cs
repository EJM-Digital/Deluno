using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1268: every branch of the uTorrent adapter. uTorrent 3.6's recorded token page lets the client in; after that every call is
/// answered with one reply that says little (no list, a row with too few columns, no files), because the branches left are what the
/// client does with a reply that is not the full one. The full replies are tested from the recordings in the other uTorrent classes.
/// </summary>
public sealed class UTorrentEveryBranchTests
{
    private const string Hash = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";

    private static DownloadClientItem Item() => Client("utorrent", "user", "pw");

    private static HttpMessageHandler Answering(Func<HttpRequestMessage, string, HttpResponseMessage?> otherwise)
        => new Calls("utorrent", [RecordedReplies.Utorrent("token")], (request, body) =>
            request.RequestUri!.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal) ? RecordedReplies.Json(RecordedReplies.UtorrentTokenPage()) : otherwise(request, body)).Build();

    private static readonly string[] OddReplies =
    [
        "{}",
        "null",
        "{\"build\":1,\"torrents\":[]}",
        "{\"build\":1,\"torrents\":[[]]}",
        "{\"build\":1,\"torrents\":[[\"d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c\"]]}",
        "{\"build\":1,\"torrents\":[[\"d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c\",201,\"Name\"]]}",
        "{\"build\":1,\"torrents\":[[\"d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c\",\"x\",null,\"3\",\"y\",0,0,0,0,0]]}",
        "{\"build\":1,\"files\":[\"d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c\"]}",
        "{\"build\":1,\"files\":[\"d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c\",[[\"a/b.mkv\",100]]]}",
        "{\"build\":1,\"files\":[\"d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c\",null]}",
        "{\"build\":1,\"props\":[{}]}",
        "{\"build\":1,\"props\":[]}",
        "{\"build\":1,\"label\":[]}"
    ];

    [Fact]
    public async Task No_question_throws_when_the_client_answers_every_call_after_the_token_with_a_reply_that_says_little()
    {
        // Side by side: a grab waits a few seconds for the client to list the torrent.
        await Task.WhenAll(OddReplies.Select(reply =>
        {
            var handler = Answering((_, _) => RecordedReplies.Json(reply));
            return EveryCallOnAnOddReply.AskAsync(new UTorrentDownloadClient(() => handler), Item(), usenet: false, Hash);
        }));
    }

    [Fact]
    public async Task No_question_throws_when_the_list_is_the_recorded_one_with_every_status_the_client_has()
    {
        // Side by side: a grab waits a few seconds for the client to list the torrent.
        await Task.WhenAll(new[] { 0, 1, 2, 4, 8, 16, 32, 64, 128, 201, 233, 136 }.Select(bits =>
        {
            var list = RecordedReplies.UtorrentList([Hash], ("torrents.0.1", bits), ("torrents.0.21", bits % 2 == 0 ? "Downloading 5.0 %" : "Error: disk full"));
            var handler = Answering((request, _) => request.RequestUri!.Query.Contains("list=1", StringComparison.Ordinal) ? RecordedReplies.Json(list) : RecordedReplies.Json("{\"build\":1}"));

            return EveryCallOnAnOddReply.AskAsync(new UTorrentDownloadClient(() => handler), Item(), usenet: false, Hash);
        }));
    }

    [Fact]
    public async Task No_question_throws_when_the_connection_drops_after_the_token()
    {
        var handler = Answering((_, _) => throw new HttpRequestException("The connection dropped."));

        await EveryCallOnAnOddReply.AskAsync(new UTorrentDownloadClient(() => handler), Item(), usenet: false, Hash);
    }

    [Fact]
    public async Task Nothing_is_asked_without_an_address_and_a_category_check_needs_a_name()
    {
        var client = new UTorrentDownloadClient(() => Answering((_, _) => RecordedReplies.Json("{}")));
        var noAddress = Item() with { Host = null, EndpointUrl = null };

        var grab = await client.GrabAsync(noAddress, EveryCallOnAnOddReply.GrabRequests(false).First(), CancellationToken.None);
        var snapshot = await client.GetSnapshotAsync(noAddress, DateTimeOffset.UtcNow, CancellationToken.None);
        var action = await client.ExecuteActionAsync(noAddress, DownloadClientActions.Recheck, Hash, CancellationToken.None);
        var set = await client.SetCategoryAsync(noAddress, Hash, "seeding", "movies", CancellationToken.None);
        var files = await client.GetFilesAsync(noAddress, Hash, CancellationToken.None);
        var noId = await client.GetFilesAsync(Item(), " ", CancellationToken.None);
        var check = await client.CheckKeepSeedingCategoryAsync(noAddress, "movies", CancellationToken.None);
        var blank = await client.CheckKeepSeedingCategoryAsync(Item(), " ", CancellationToken.None);

        Assert.False(grab.Succeeded);
        Assert.Null(snapshot);
        Assert.False(action.Succeeded);
        Assert.False(set.Succeeded);
        Assert.Null(files);
        Assert.Null(noId);
        Assert.Contains("address", check.Message);
        Assert.Contains("Enter a category", blank.Message);
    }

    [Fact]
    public async Task The_default_connection_is_a_real_one_and_a_client_nobody_listens_for_is_unreachable()
    {
        var client = new UTorrentDownloadClient();
        var nobody = Item() with { Host = "127.0.0.1", Port = 1, EndpointUrl = null };

        var check = await client.CheckKeepSeedingCategoryAsync(nobody, "movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, check.Status);
    }
}
