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
    private const string Hash = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";

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
}
