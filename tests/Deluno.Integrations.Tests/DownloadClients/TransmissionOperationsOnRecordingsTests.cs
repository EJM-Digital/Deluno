using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Connections.Usenet;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: each Transmission operation replayed from Transmission 4.1.3's own recorded replies, with the calls Deluno sent
/// asserted as well as what it made of the answers.
/// </summary>
public sealed class TransmissionOperationsOnRecordingsTests
{
    private const string Finished = "04d6d32adaa36ddd9877f0734a7027a4a0055042";
    private const string FolderHash = "6c3e9edfdfed4f074f7a1ea1580dd68fa6bd06d2";
    private const string Removed = "ff7447384425ba526a3efb9efb792f7ba67a77dd";

    private static bool Is(string body, string method) => body.Contains($"\"method\":\"{method}\"", StringComparison.Ordinal);

    // ---- the file list (t24) ----

    [Fact]
    public async Task The_file_list_is_read_with_torrent_get_files_and_names_every_file_of_a_folder_torrent()
    {
        var calls = new Calls("transmission", [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("finished-folder")]);
        var handler = calls.Build();

        var files = await new TransmissionDownloadClient(new Factory(handler)).GetFilesAsync(Client("transmission"), "52", CancellationToken.None);

        Assert.Equal(["Recorded.transmission.Folder/Recorded.transmission.Folder.mkv", "Recorded.transmission.Folder/Recorded.transmission.Folder.nfo"], files);
        Assert.True(calls.Sent("torrent-get"));
        Assert.True(calls.Sent("\"files\""));
    }

    [Fact]
    public async Task A_torrent_transmission_does_not_list_has_no_files_to_check()
    {
        var calls = new Calls("transmission", [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("removed-twice")]);
        var handler = calls.Build();

        var files = await new TransmissionDownloadClient(new Factory(handler)).GetFilesAsync(Client("transmission"), "99", CancellationToken.None);

        Assert.True(files is null || files.Count == 0, "no file names are made up for a torrent the client does not list");
    }

    [Fact]
    public async Task A_wrong_login_gives_no_file_list_so_the_check_uses_the_release_name()
    {
        var refused = RecordedReplies.Transmission("login-refused").Find("session-get").Reply;
        var handler = RecordedReplies.Handler("none", [], (_, _) => refused.ToResponse());

        var files = await new TransmissionDownloadClient(new Factory(handler)).GetFilesAsync(Client("transmission", "transmission", "wrong"), "52", CancellationToken.None);

        Assert.Null(files);
    }

    // ---- delete, keep the files (t28) ----

    [Fact]
    public async Task Delete_sends_torrent_remove_with_delete_local_data_false_and_is_done_once_the_torrent_is_gone()
    {
        var removed = false;
        var calls = new Calls("transmission",
            [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("removed-without-data")],
            (_, body) =>
            {
                if (Is(body, "torrent-remove")) { removed = true; return null; }
                return Is(body, "torrent-get") && !removed
                    ? RecordedReplies.Transmission("finished-single-file").Find("torrent-get").Reply.ToResponse()
                    : null;
            });
        var handler = calls.Build();

        var result = await new TransmissionDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("transmission"), DownloadClientActions.Delete, Finished, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, calls.Count("torrent-remove"));
        var sent = calls.Log.Single(line => line.Contains("torrent-remove", StringComparison.Ordinal)).Replace(" ", string.Empty);
        Assert.Contains("\"delete-local-data\":false", sent);
        Assert.DoesNotContain("torrent-set-location", string.Join(' ', calls.Log));
    }

    [Fact]
    public async Task Delete_of_a_torrent_transmission_does_not_list_sends_no_remove()
    {
        var calls = new Calls("transmission", [RecordedReplies.Transmission("session-handshake"), RecordedReplies.Transmission("removed-twice")]);
        var handler = calls.Build();

        var result = await new TransmissionDownloadClient(new Factory(handler))
            .ExecuteActionAsync(Client("transmission"), DownloadClientActions.Delete, Removed, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(calls.Sent("torrent-remove"));
    }

    // ---- keep-seeding move (t34) ----

    private static HttpResponseMessage Labels(params string[] labels)
    {
        var array = new JsonArray(labels.Select(label => (JsonNode?)JsonValue.Create(label)).ToArray());
        return RecordedReplies.Json(RecordedReplies.Transmission("label-set").Body("torrent-get", ("arguments.torrents.0.labels", array)));
    }

    [Fact]
    public async Task A_keep_seeding_move_puts_the_new_label_first_drops_the_old_category_and_keeps_the_other_labels()
    {
        var reads = 0;
        var calls = new Calls("transmission",
            [RecordedReplies.Transmission("version"), RecordedReplies.Transmission("label-set")],
            (_, body) =>
            {
                if (!Is(body, "torrent-get")) return null;
                // Before: an old category and someone's own label. The read that confirms the move is the recorded one.
                return ++reads <= 2 ? Labels("old-category", "my-own-label") : null;
            });
        var handler = calls.Build();

        var result = await new TransmissionDownloadClient(new Factory(handler))
            .SetCategoryAsync(Client("transmission"), Finished, "recorded-movies", "old-category", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var set = calls.Log.Single(line => line.Contains("torrent-set", StringComparison.Ordinal)).Replace(" ", string.Empty);
        Assert.Contains("\"labels\":[\"recorded-movies\",\"my-own-label\"]", set);
        Assert.DoesNotContain("old-category", set);
        Assert.False(calls.Sent("torrent-remove"));
        Assert.False(calls.Sent("torrent-stop"));
        Assert.False(calls.Sent("torrent-set-location"));
    }

    // ---- SetUsenetProviderAsync ----

    [Fact]
    public async Task A_torrent_client_takes_no_Usenet_provider_and_nothing_is_sent_to_transmission()
    {
        var calls = new Calls("transmission", [RecordedReplies.Transmission("session-handshake")]);
        var handler = calls.Build();

        var result = await new TransmissionDownloadClient(new Factory(handler))
            .SetUsenetProviderAsync(Client("transmission"), new UsenetProviderRequest("news.example", 563, true, "user", "pass", 8), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(calls.Log);
    }
}
