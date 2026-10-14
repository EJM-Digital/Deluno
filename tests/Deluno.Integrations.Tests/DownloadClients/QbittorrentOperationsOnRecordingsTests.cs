using System.Net;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Usenet;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: each qBittorrent operation replayed from qBittorrent 5.2.1's own recorded replies, with the call Deluno sent
/// asserted as well as what it made of the answer.
/// </summary>
public sealed class QbittorrentOperationsOnRecordingsTests
{
    private const string Finished = "d725bf5a29bcf10e3f7d2b8ebbb221b1fe0e783c";
    private const string Stalled = "8d878a21c3d4180ae08b4e044904667d9f30bec5";

    private static HttpClient SignInClient(HttpMessageHandler handler)
        => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:8080/") };

    // ---- SignInAsync, the login variants (q6) ----

    [Fact]
    public async Task A_sign_in_qBittorrent_answers_204_with_no_body_is_accepted_because_the_version_call_works_with_the_session()
    {
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("version")]);
        using var http = SignInClient(calls.Build());

        var result = await QbittorrentSignIn.SignInAsync(http, "deluno", "secret", CancellationToken.None);

        Assert.True(result.SignedIn);
        Assert.False(result.Rejected);
        Assert.Equal(HttpStatusCode.NoContent, result.Status);
        Assert.Equal(1, calls.Count("POST /api/v2/auth/login"));
        Assert.Equal(1, calls.Count("GET /api/v2/app/version"));
    }

    [Fact]
    public async Task A_wrong_login_answered_401_is_refused_at_once_and_not_retried()
    {
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("login-refused")]);
        using var http = SignInClient(calls.Build());

        var result = await QbittorrentSignIn.SignInAsync(http, "deluno", "wrong", CancellationToken.None, TimeSpan.Zero);

        Assert.False(result.SignedIn);
        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Equal(1, calls.Count("auth/login"));
    }

    [Fact]
    public async Task A_really_wrong_login_reads_as_the_client_not_answering_not_as_a_crash()
    {
        var handler = RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("login-refused")]);

        var check = await new QbittorrentDownloadClient(() => handler).CheckCategoryAsync(Client("qbittorrent", "deluno", "wrong"), "movies", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, check.Status);
        Assert.Contains("answered the sign-in with 401", check.Message);
    }

    // ---- the Bearer key (q4) ----

    [Fact]
    public async Task With_only_a_secret_set_qBittorrent_gets_the_key_as_a_bearer_token_and_is_never_asked_to_log_in()
    {
        var stopped = false;
        var calls = new Calls("qbittorrent",
            [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("stalled"), RecordedReplies.Qbittorrent("paused")],
            (request, _) =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("api-key-value", request.Headers.Authorization?.Parameter);
                if (request.RequestUri!.AbsolutePath.EndsWith("torrents/stop", StringComparison.Ordinal)) stopped = true;
                return request.RequestUri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
                    ? (stopped ? RecordedReplies.Qbittorrent("paused") : RecordedReplies.Qbittorrent("stalled")).Find("torrents/info").Reply.ToResponse()
                    : null;
            });
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler)
            .ExecuteActionAsync(Client("qbittorrent", null, "api-key-value"), DownloadClientActions.Pause, Stalled, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(calls.Sent("auth/login"), "the key is sent as a header; no login is made");
        Assert.True(calls.Sent("POST /api/v2/torrents/stop"));
    }

    [Fact]
    public async Task A_key_qBittorrent_refuses_with_403_is_said_plainly()
    {
        var refused = RecordedReplies.Qbittorrent("api-key-refused").Find("torrents/stop").Reply;
        var handler = RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("stalled")], (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("torrents/stop", StringComparison.Ordinal) ? refused.ToResponse() : null);

        var result = await new QbittorrentDownloadClient(() => handler)
            .ExecuteActionAsync(Client("qbittorrent", null, "wrong-key"), DownloadClientActions.Pause, Stalled, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("refused", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("403", result.Message);
    }

    // ---- the file list (q31) ----

    [Fact]
    public async Task The_file_list_of_one_torrent_is_read_from_torrents_files_with_its_hash()
    {
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("finished-single-file")]);
        var handler = calls.Build();

        var files = await new QbittorrentDownloadClient(() => handler)
            .GetFilesAsync(Client("qbittorrent", "deluno"), Finished, CancellationToken.None);

        Assert.Equal(["Recorded.qbittorrent.A.mkv"], files);
        Assert.True(calls.Sent($"GET /api/v2/torrents/files?hash={Finished}"));
    }

    [Fact]
    public async Task A_folder_torrents_file_list_names_every_file_inside_its_folder()
    {
        var handler = RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("finished-folder")]);

        var files = await new QbittorrentDownloadClient(() => handler)
            .GetFilesAsync(Client("qbittorrent", "deluno"), "3b1d84012faffbb5c3c8d00d92358d52f2c856eb", CancellationToken.None);

        Assert.NotNull(files);
        Assert.NotEmpty(files);
        Assert.All(files, name => Assert.StartsWith("Recorded.qbittorrent.Folder/", name));
    }

    [Fact]
    public async Task A_refused_login_gives_no_file_list_so_the_check_falls_back_to_the_release_name()
    {
        var handler = RecordedReplies.Handler("qbittorrent", [RecordedReplies.Qbittorrent("login-refused")]);

        var files = await new QbittorrentDownloadClient(() => handler)
            .GetFilesAsync(Client("qbittorrent", "deluno", "wrong"), Finished, CancellationToken.None);

        Assert.Null(files);
    }

    // ---- delete, keep the files (q35) ----

    [Fact]
    public async Task Delete_sends_torrents_delete_with_deleteFiles_false_and_is_done_once_the_client_no_longer_lists_it()
    {
        var deleted = false;
        var calls = new Calls("qbittorrent",
            [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("removed-without-data")],
            (request, body) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("torrents/delete", StringComparison.Ordinal))
                {
                    deleted = true;
                    return null;
                }

                return request.RequestUri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal) && !deleted
                    ? RecordedReplies.Qbittorrent("stalled").Find("torrents/info").Reply.ToResponse()
                    : null;
            });
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler)
            .ExecuteActionAsync(Client("qbittorrent", "deluno"), DownloadClientActions.Delete, Stalled, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, calls.Count("POST /api/v2/torrents/delete"));
        Assert.True(calls.Sent($"hashes={Stalled}&deleteFiles=false"));
        Assert.False(calls.Sent("deleteFiles=true"));
    }

    [Fact]
    public async Task Delete_of_a_torrent_the_client_does_not_have_sends_no_delete_and_says_so()
    {
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("removed-twice")]);
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler)
            .ExecuteActionAsync(Client("qbittorrent", "deluno"), DownloadClientActions.Delete, "0000000000000000000000000000000000000000", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(calls.Sent("torrents/delete"), "nothing is deleted for a torrent the client does not have");
    }

    // ---- keep-seeding move (q42) ----

    [Fact]
    public async Task A_keep_seeding_move_sets_the_category_after_reading_it_and_confirms_it_by_reading_again()
    {
        var reads = 0;
        var calls = new Calls("qbittorrent",
            [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("category-moved")],
            (request, _) =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)) return null;
                // Before the move the torrent is in no category; after it, the recorded reply (category recorded-movies).
                return ++reads == 1
                    ? RecordedReplies.Json(RecordedReplies.Qbittorrent("category-moved").Body("torrents/info", ("0.category", "")))
                    : null;
            });
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler)
            .SetCategoryAsync(Client("qbittorrent", "deluno"), Finished, "recorded-movies", null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, calls.Count("GET /api/v2/torrents/info"));
        Assert.True(calls.Sent($"POST /api/v2/torrents/setCategory hashes={Finished}&category=recorded-movies"));
        Assert.False(calls.Sent("torrents/stop"), "the move never pauses the torrent");
        Assert.False(calls.Sent("torrents/delete"), "the move never removes the torrent");
        Assert.False(calls.Sent("setLocation"), "the move never relocates the files");
    }

    [Fact]
    public async Task A_keep_seeding_category_qBittorrent_does_not_have_is_refused_409_and_Deluno_creates_none()
    {
        var calls = new Calls("qbittorrent",
            [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version"), RecordedReplies.Qbittorrent("category-unknown"), RecordedReplies.Qbittorrent("category-moved")],
            (request, _) => request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
                ? RecordedReplies.Json(RecordedReplies.Qbittorrent("category-moved").Body("torrents/info", ("0.category", "")))
                : null);
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler)
            .SetCategoryAsync(Client("qbittorrent", "deluno"), Finished, "does-not-exist", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not have a category named does-not-exist", result.Message);
        Assert.False(calls.Sent("createCategory"), "Deluno never creates a category");
    }

    // ---- SetUsenetProviderAsync ----

    [Fact]
    public async Task A_torrent_client_takes_no_Usenet_provider_and_nothing_is_sent_to_it()
    {
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")]);
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler)
            .SetUsenetProviderAsync(Client("qbittorrent", "deluno"), new UsenetProviderRequest("news.example", 563, true, "user", "pass", 8), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(calls.Log);
    }
}
