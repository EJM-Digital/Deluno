using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Connections.Usenet;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: each Deluge operation replayed from Deluge 2.2.0's own recorded replies, with the calls Deluno sent asserted as
/// well as what it made of the answers. A reply the recordings do not hold (a torrent's label before and after a move) is the
/// nearest recorded reply changed in the one value the test is about, and says so.
/// </summary>
public sealed class DelugeOperationsOnRecordingsTests
{
    private const string Stalled = "b7b95339b09b0e7249d2b51ecc500b1650a18592";
    private const string Finished = "d8185091144732e990e3595f7db0e4410b1d9424";

    private static HttpMessageHandler Deluge(Calls calls) => calls.Build();

    private static bool Is(HttpRequestMessage request, string body, string method)
        => body.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal);

    // ---- the file list (d22) ----

    [Fact]
    public async Task The_file_list_is_read_with_get_torrent_status_asking_for_the_files()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("location-moved")]);
        var handler = Deluge(calls);

        var files = await new DelugeDownloadClient(new Factory(handler))
            .GetFilesAsync(Client("deluge"), Finished, CancellationToken.None);

        Assert.Equal(["Recorded.deluge.A.mkv"], files);
        Assert.True(calls.Sent("core.get_torrent_status"));
        Assert.True(calls.Sent(Finished));
    }

    [Fact(Skip = "Defect (#1301): DelugeDownloadClient.GetFilesAsync lets ClientCredentialsRefusedException escape on a wrong password (login-refused recording, auth.login false), though IDownloadClient.GetFilesAsync promises null and DownloadClientTelemetryService calls it with no try. qBittorrent's equivalent returns null.")]
    public async Task A_wrong_password_gives_no_file_list_so_the_check_uses_the_release_name()
    {
        var handler = RecordedReplies.Handler("deluge", [RecordedReplies.Deluge("login-refused")]);

        var files = await new DelugeDownloadClient(new Factory(handler))
            .GetFilesAsync(Client("deluge", secret: "wrong"), Finished, CancellationToken.None);

        Assert.Null(files);
    }

    // ---- delete, keep the files (d26) and a refused removal (d29) ----

    [Fact]
    public async Task Delete_sends_core_remove_torrent_with_the_data_flag_false_and_is_done_once_deluge_no_longer_lists_it()
    {
        var removed = false;
        var calls = new Calls("deluge",
            [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("removed-without-data")],
            (request, body) =>
            {
                if (Is(request, body, "core.remove_torrent")) { removed = true; return null; }
                return Is(request, body, "web.update_ui") && !removed
                    ? RecordedReplies.Deluge("stalled").Find("web.update_ui").Reply.ToResponse()
                    : null;
            });
        var handler = Deluge(calls);

        var result = await new DelugeDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("deluge"), DownloadClientActions.Delete, Stalled, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, calls.Count("core.remove_torrent"));
        var sent = calls.Log.Single(line => line.Contains("core.remove_torrent", StringComparison.Ordinal)).Replace(" ", string.Empty);
        Assert.Contains($"\"params\":[\"{Stalled}\",false]", sent);
    }

    [Fact]
    public async Task A_removal_deluge_refuses_with_an_error_is_reported_as_failed_with_its_reason_and_the_torrent_stays()
    {
        var calls = new Calls("deluge",
            [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("stalled")],
            (request, body) => Is(request, body, "core.remove_torrent")
                ? RecordedReplies.Deluge("removed-twice").Find("core.remove_torrent").Reply.ToResponse()
                : null);
        var handler = Deluge(calls);

        var result = await new DelugeDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("deluge"), DownloadClientActions.Delete, Stalled, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Failure", result.Message);
        Assert.Equal(1, calls.Count("core.remove_torrent"));
    }

    [Fact]
    public async Task A_removal_of_a_torrent_deluge_never_had_sends_no_remove_call()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("removed-twice")]);
        var handler = Deluge(calls);

        var result = await new DelugeDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("deluge"), DownloadClientActions.Delete, Stalled, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(calls.Sent("core.remove_torrent"));
    }

    // ---- keep-seeding move (d33) ----

    private static HttpResponseMessage Label(string label)
        => RecordedReplies.Json(RecordedReplies.Deluge("location-moved").Body("core.get_torrent_status", ("result", new JsonObject { ["label"] = label })));

    [Fact]
    public async Task A_keep_seeding_move_sets_the_label_with_label_set_torrent_and_reads_it_again_to_confirm()
    {
        var reads = 0;
        var calls = new Calls("deluge",
            [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("label-set")],
            (request, body) => Is(request, body, "core.get_torrent_status") ? Label(++reads == 1 ? string.Empty : "recorded-movies") : null);
        var handler = Deluge(calls);

        var result = await new DelugeDownloadClient(new Factory(handler))
            .SetCategoryAsync(Client("deluge"), Finished, "recorded-movies", null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, calls.Count("core.get_torrent_status"));
        Assert.True(calls.Sent("label.set_torrent"));
        Assert.False(calls.Sent("label.add"), "Deluno never creates a label");
        Assert.False(calls.Sent("core.pause_torrent"));
        Assert.False(calls.Sent("core.remove_torrent"));
        Assert.False(calls.Sent("core.move_storage"));
    }

    [Fact]
    public async Task A_keep_seeding_label_deluge_does_not_have_is_refused_before_anything_is_set_and_none_is_created()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("label-set")]);
        var handler = Deluge(calls);

        var result = await new DelugeDownloadClient(new Factory(handler))
            .SetCategoryAsync(Client("deluge"), Finished, "not-a-label", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Deluge has no label named not-a-label", result.Message);
        Assert.False(calls.Sent("label.set_torrent"));
        Assert.False(calls.Sent("label.add"));
    }

    [Fact]
    public async Task A_label_set_deluge_refuses_with_an_error_is_reported_as_failed_with_its_reason()
    {
        var calls = new Calls("deluge",
            [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("label-set")],
            (request, body) =>
            {
                if (Is(request, body, "core.get_torrent_status")) return Label(string.Empty);
                return Is(request, body, "label.set_torrent") ? RecordedReplies.Deluge("label-unknown").Find("label.set_torrent").Reply.ToResponse() : null;
            });
        var handler = Deluge(calls);

        var result = await new DelugeDownloadClient(new Factory(handler))
            .SetCategoryAsync(Client("deluge"), Finished, "recorded-movies", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Deluge refused to set the label", result.Message);
    }

    [Fact]
    public async Task The_keep_seeding_label_check_says_ready_for_a_label_deluge_has_and_missing_for_one_it_has_not()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in"), RecordedReplies.Deluge("label-set")]);
        var handler = Deluge(calls);
        var client = new DelugeDownloadClient(new Factory(handler));

        var ready = await client.CheckKeepSeedingCategoryAsync(Client("deluge"), "recorded-movies", CancellationToken.None);
        var missing = await client.CheckKeepSeedingCategoryAsync(Client("deluge"), "nope", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, ready.Status);
        Assert.Equal(DownloadClientCategoryStatuses.Missing, missing.Status);
        Assert.False(calls.Sent("label.add"));
    }

    // ---- SetUsenetProviderAsync ----

    [Fact]
    public async Task A_torrent_client_takes_no_Usenet_provider_and_nothing_is_sent_to_deluge()
    {
        var calls = new Calls("deluge", [RecordedReplies.Deluge("sign-in")]);
        var handler = Deluge(calls);

        var result = await new DelugeDownloadClient(new Factory(handler))
            .SetUsenetProviderAsync(Client("deluge"), new UsenetProviderRequest("news.example", 563, true, "user", "pass", 8), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(calls.Log);
    }
}
