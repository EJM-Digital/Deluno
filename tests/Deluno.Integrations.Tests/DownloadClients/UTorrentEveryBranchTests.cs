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

    internal static DownloadClientItem Item() => Client("utorrent", "user", "pw");

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

    // ---- a grab, step by step ----

    private static string ListHaving() => RecordedReplies.UtorrentList([Hash]);

    private static string AddedReply(string error) => RecordedReplies.Utorrent("add-refused").Body("action=add-file", ("error", error));

    /// <summary>A uTorrent that lists nothing before the add and the torrent from then on, answering the add and the stop and label calls as given.</summary>
    internal static HttpMessageHandler Adding(Func<HttpRequestMessage, string, HttpResponseMessage?> others)
    {
        var lists = 0;
        return Answering((request, body) =>
        {
            if (others(request, body) is { } own) return own;
            if (request.RequestUri!.Query.Contains("list=1", StringComparison.Ordinal)) return RecordedReplies.Json(++lists == 1 ? RecordedReplies.UtorrentList([]) : ListHaving());
            return RecordedReplies.Json(RecordedReplies.Utorrent("label-set").Body("action=setprops"));
        });
    }

    internal static DownloadClientGrabRequest Magnet() => EveryCallOnAnOddReply.GrabRequests(false).First() with { SaveFolder = null };

    private static bool Action(HttpRequestMessage request, string name) => request.RequestUri!.Query.Contains("action=" + name, StringComparison.Ordinal);

    [Fact]
    public async Task A_torrent_added_paused_is_stopped_and_a_stop_that_fails_or_drops_is_said()
    {
        var stopped = await new UTorrentDownloadClient(() => Adding((r, _) => Action(r, "stop") ? RecordedReplies.Json("{\"build\":1}") : null))
            .GrabAsync(Item() with { AddPaused = true }, Magnet(), CancellationToken.None);
        var refused = await new UTorrentDownloadClient(() => Adding((r, _) => Action(r, "stop") ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) : null))
            .GrabAsync(Item() with { AddPaused = true }, Magnet(), CancellationToken.None);
        var dropped = await new UTorrentDownloadClient(() => Adding((r, _) => Action(r, "stop") ? throw new HttpRequestException("The connection dropped.") : null))
            .GrabAsync(Item() with { AddPaused = true }, Magnet(), CancellationToken.None);

        Assert.Contains("added stopped", stopped.Message);
        Assert.Contains("would not stop it", refused.Message);
        Assert.Contains("would not stop it", dropped.Message);
    }

    [Fact]
    public async Task A_label_that_cannot_be_set_never_fails_the_grab()
    {
        var result = await new UTorrentDownloadClient(() => Adding((r, _) => Action(r, "setprops") ? throw new HttpRequestException("The connection dropped.") : null))
            .GrabAsync(Item(), Magnet(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Theory]
    [InlineData("Can't add torrent: ", "Can't add")]
    [InlineData("   ", "no reason")]
    public async Task A_refused_add_is_said_with_its_reason_or_that_it_gave_none(string error, string expected)
    {
        var result = await new UTorrentDownloadClient(() => Adding((r, _) => Action(r, "add-url") ? RecordedReplies.Json(AddedReply(error)) : null))
            .GrabAsync(Item(), Magnet(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task An_add_answered_with_something_that_is_not_json_or_has_no_error_is_taken_as_accepted()
    {
        foreach (var reply in new[] { "OK", "[]", "{\"build\":1,\"error\":5}" })
        {
            var result = await new UTorrentDownloadClient(() => Adding((r, _) => Action(r, "add-url") ? RecordedReplies.Json(reply) : null))
                .GrabAsync(Item(), Magnet(), CancellationToken.None);

            Assert.True(result.Succeeded, reply + ": " + result.Message);
        }
    }

    [Fact]
    public async Task A_torrent_whose_list_cannot_be_read_is_still_followed_by_its_hash()
    {
        var result = await new UTorrentDownloadClient(() => Answering((r, _) =>
                Action(r, "add-url") ? RecordedReplies.Json("{\"build\":1}")
                : r.RequestUri!.Query.Contains("list=1", StringComparison.Ordinal) ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                : RecordedReplies.Json("{\"build\":1}")))
            .GrabAsync(Item(), Magnet(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    // ---- reading ----

    [Fact]
    public async Task A_row_with_numbers_where_text_belongs_and_text_where_numbers_belong_is_still_read()
    {
        var list = RecordedReplies.UtorrentList([Hash], ("torrents.0.0", 12345), ("torrents.0.2", 77), ("torrents.0.3", "4096"), ("torrents.0.4", "x"), ("torrents.0.5", "12"), ("torrents.0.9", "7"), ("torrents.0.11", 5));
        var handler = Answering((r, _) => r.RequestUri!.Query.Contains("list=1", StringComparison.Ordinal) ? RecordedReplies.Json(list)
            : Action(r, "getprops") ? RecordedReplies.Json("{\"build\":1}") : RecordedReplies.Json("{\"build\":1}"));

        var snapshot = await new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(snapshot!.Queue);
    }

    [Fact]
    public async Task A_seeding_time_answer_that_is_nothing_leaves_the_torrent_listed()
    {
        var list = ListHaving();
        var handler = Answering((r, _) => r.RequestUri!.Query.Contains("list=1", StringComparison.Ordinal) ? RecordedReplies.Json(list) : RecordedReplies.Json("null"));

        var snapshot = await new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Single(snapshot!.Queue);
    }

    [Fact]
    public async Task A_seeding_time_that_cannot_be_read_leaves_the_torrent_listed()
    {
        var list = ListHaving();
        var handler = Answering((r, _) => r.RequestUri!.Query.Contains("list=1", StringComparison.Ordinal) ? RecordedReplies.Json(list)
            : throw new HttpRequestException("The connection dropped."));

        var snapshot = await new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task Setting_a_label_the_client_turns_down_is_said_with_its_status()
    {
        var list = ListHaving();
        var handler = Answering((r, _) => Action(r, "setprops") ? new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
            : r.RequestUri!.Query.Contains("list=1", StringComparison.Ordinal) ? RecordedReplies.Json(list) : RecordedReplies.Json("{\"build\":1}"));

        var result = await new UTorrentDownloadClient(() => handler).SetCategoryAsync(Item(), Hash, "seeding", "movies", CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData("201")]
    [InlineData("Seeding")]
    [InlineData("  ")]
    [InlineData(null)]
    public void A_status_is_a_bit_field_or_a_word(string? status)
    {
        var client = new UTorrentDownloadClient();

        Assert.False(string.IsNullOrWhiteSpace(client.NormalizeStatus(status, 0.5)));
        Assert.Equal("utorrent", client.Protocol);
    }
}
