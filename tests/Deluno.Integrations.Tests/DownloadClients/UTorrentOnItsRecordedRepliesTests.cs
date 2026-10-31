using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Usenet;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1278 / #1329: what Deluno does with uTorrent 3.6's own replies (tests/fixtures/recorded/utorrent). Each test builds the uTorrent that
/// answered, in the order it answered, from the recordings: the token page, the list before and after, the reply to the action. What is
/// sent to it is kept so a test can say what Deluno asked, and what it did not.
/// </summary>
public sealed class UTorrentOnItsRecordedRepliesTests
{
    // ------------------------------------------------------------------ what the tests are built from

    /// <summary>uTorrent as recorded: the token page, a list that moves on at each ask (the last one repeats), and the reply to each action.</summary>
    private sealed class UTorrent(string actionReply, params string[] lists) : HttpMessageHandler
    {
        private int _asked;
        public List<string> Sent { get; } = [];
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;
        public Dictionary<string, string> Replies { get; } = [];
        public int Lists => _asked;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Sent.Add(uri.PathAndQuery);
            if (uri.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal))
            {
                return Task.FromResult(TokenStatus == HttpStatusCode.OK
                    ? Ok(RecordedReplies.UtorrentTokenPage())
                    : new HttpResponseMessage(TokenStatus));
            }

            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            if (query["list"] is not null)
            {
                return Task.FromResult(Ok(lists[Math.Min(_asked++, lists.Length - 1)]));
            }

            return Task.FromResult(Ok(Replies.TryGetValue(query["action"] ?? string.Empty, out var own) ? own : actionReply));
        }

        public IEnumerable<string> Actions(string action) => Sent.Where(call => call.Contains("action=" + action + "&", StringComparison.Ordinal) || call.EndsWith("action=" + action, StringComparison.Ordinal));
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private static string ListOf(Recording recording, int which = -1)
    {
        var lists = recording.Exchanges.Where(e => e.Request.Path.Contains("list=1", StringComparison.Ordinal)).ToArray();
        return lists[which < 0 ? lists.Length - 1 : which].Reply.Body!;
    }

    private static string HashOf(string list, string name)
    {
        using var rows = JsonDocument.Parse(list);
        return rows.RootElement.GetProperty("torrents").EnumerateArray().First(row => row[2].GetString() == name)[0].GetString()!;
    }

    private static string ActionReplyOf(Recording recording) => recording.Exchanges[0].Reply.Body!;

    private static DownloadClientItem Client(bool addPaused = false)
        => new("utorrent-1", "uTorrent", "utorrent", "localhost", 8081, "user", "secret", "http://localhost:8081/", false, null, "movies", "tv", null, 1, true,
            "healthy", null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, AddPaused: addPaused);

    private static UTorrentDownloadClient On(UTorrent uTorrent) => new(() => uTorrent);

    private static async Task<IReadOnlyList<DownloadQueueItem>> Queue(UTorrent uTorrent)
        => (await On(uTorrent).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None))!.Queue;

    // ------------------------------------------------------------------ the queue read (U12, U13)

    [Fact]
    public async Task A_finished_torrent_reads_ready_to_import_at_the_full_progress_with_its_label_as_the_media_type()
    {
        // The recorded row carries the label the test put on it (recorded-movies); the label is set to the client's own movies category.
        var list = RecordedReplies.Utorrent("label-set").Body("list=1", ("torrents.0.11", "movies"));

        var item = Assert.Single(await Queue(new UTorrent("{}", list)));

        Assert.Equal(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.Equal(100d, item.Progress);
        Assert.Equal("movies", item.Category);
        Assert.Equal("movies", item.MediaType);
        Assert.Equal(6291456, item.SizeBytes);
    }

    [Fact]
    public async Task The_ratio_is_read_per_mille_and_the_peers_are_the_seeds_plus_the_peers()
    {
        // uTorrent's row: ratio at index 7 in thousandths, peers connected at 12 and seeds connected at 14.
        var list = RecordedReplies.Utorrent("finished-single-file").Body("list=1", ("torrents.0.7", 1500), ("torrents.0.12", 2), ("torrents.0.14", 3));

        var item = Assert.Single(await Queue(new UTorrent("{}", list)));

        Assert.Equal(1.5, item.Ratio);
        Assert.Equal(5, item.Peers);
    }

    [Fact]
    public async Task A_complete_torrent_whose_tracker_does_not_answer_is_still_ready_and_is_not_a_setup_problem()
    {
        var list = ListOf(RecordedReplies.Utorrent("finished-tracker-error"));

        var item = Assert.Single(await Queue(new UTorrent("{}", list)));

        Assert.Equal(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.False(item.IsSetupProblem);
        Assert.Null(item.ErrorMessage);
        Assert.Equal(DownloadTrackerStatuses.Unknown, item.TrackerStatus);
    }

    // The real uTorrent shows a tracker's refusal only in the text of the row's status (index 21); that text for a refused torrent was
    // not seen on the VM, so these two put the error bit (0x10) and a message on the recorded row, as the existing snapshot tests do.
    [Theory]
    [InlineData("Error: Tracker: Unregistered torrent", true)]
    [InlineData("Error: Access is denied.", false)]
    public async Task An_errored_row_is_a_tracker_rejection_only_when_the_message_says_the_torrent_is_unregistered(string message, bool rejected)
    {
        var list = RecordedReplies.Utorrent("finished-tracker-error").Body("list=1", ("torrents.0.1", 217), ("torrents.0.21", message));

        var item = Assert.Single(await Queue(new UTorrent("{}", list)));

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
        Assert.Equal(!rejected, item.IsSetupProblem);
        Assert.Equal(rejected ? DownloadTrackerStatuses.Unregistered : DownloadTrackerStatuses.Unknown, item.TrackerStatus);
        Assert.Equal(message, item.ErrorMessage);
    }

    [Theory]
    [InlineData("stalled")]
    [InlineData("magnet-no-metadata")]
    public async Task A_torrent_that_has_found_no_peers_is_read_from_its_row_and_is_not_a_setup_problem(string state)
    {
        var list = ListOf(RecordedReplies.Utorrent(state));

        var queue = await Queue(new UTorrent("{}", list));

        Assert.NotEmpty(queue);
        Assert.All(queue, item =>
        {
            Assert.False(item.IsSetupProblem);
            Assert.Equal(0, item.Peers);
            Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
        });
    }

    // ------------------------------------------------------------------ unexpected replies (U6), version (U8), free space and seeding time (U14)

    [Fact]
    public async Task An_empty_list_is_an_empty_queue_and_a_reply_with_no_torrents_column_is_too()
    {
        var empty = RecordedReplies.Utorrent("idle").Single.Reply.Body!;
        var noColumn = RecordedReplies.Utorrent("files-unknown-hash").Single.Reply.Body!;

        Assert.Empty(await Queue(new UTorrent("{}", empty)));
        Assert.Empty(await Queue(new UTorrent("{}", noColumn)));
    }

    [Fact]
    public async Task Another_apps_reply_to_the_list_is_not_read_as_a_queue()
    {
        var other = RecordedReplies.Qbittorrent("version").Exchanges[0].Reply.Body ?? "qBittorrent v5";

        await Assert.ThrowsAnyAsync<JsonException>(() => Queue(new UTorrent("{}", other)));
    }

    [Fact]
    public async Task The_build_uTorrent_reports_is_not_shown_as_a_version_and_no_free_space_is_invented_beside_its_settings()
    {
        var uTorrent = new UTorrent("{}", ListOf(RecordedReplies.Utorrent("version")));
        uTorrent.Replies["getsettings"] = RecordedReplies.Utorrent("settings").Single.Reply.Body!;

        var snapshot = await On(uTorrent).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Null(snapshot!.ClientVersion);
        Assert.Null(snapshot.FreeSpaceBytes);
        Assert.Empty(uTorrent.Actions("getsettings"));
    }

    [Fact]
    public async Task The_seeding_time_is_read_from_getprops_in_minutes()
    {
        var props = RecordedReplies.Utorrent("props").Single.Reply.Body!;
        using var parsed = JsonDocument.Parse(props);
        var seconds = parsed.RootElement.GetProperty("props")[0].GetProperty("seed_time").GetInt64();
        var uTorrent = new UTorrent("{}", ListOf(RecordedReplies.Utorrent("finished-single-file")));
        uTorrent.Replies["getprops"] = props;

        var item = Assert.Single(await Queue(uTorrent));

        Assert.Equal((int)(seconds / 60), item.SeedingMinutes);
        Assert.Single(uTorrent.Actions("getprops"));
    }

    // ------------------------------------------------------------------ the token and the sign-in (U1, U3), not answering (U4), a timeout (U5), 429 (U7)

    [Fact]
    public async Task Every_call_asks_for_the_token_page_first_and_sends_the_token_it_read_with_each_later_call()
    {
        var uTorrent = new UTorrent("{}", ListOf(RecordedReplies.Utorrent("finished-single-file")));

        await Queue(uTorrent);
        await Queue(uTorrent);

        Assert.StartsWith("/gui/token.html", uTorrent.Sent[0], StringComparison.Ordinal);
        var later = uTorrent.Sent.Where(call => !call.StartsWith("/gui/token.html", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(later);
        Assert.All(later, call => Assert.Contains("token=recorded-token", call, StringComparison.Ordinal));
        Assert.Equal(2, uTorrent.Sent.Count(call => call.StartsWith("/gui/token.html", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_refused_login_on_the_token_page_stops_every_action_in_plain_words_and_sends_nothing_after_it()
    {
        var refused = RecordedReplies.Utorrent("login-refused").Exchanges[0].Reply.Status;
        var uTorrent = new UTorrent("{}", "{}") { TokenStatus = (HttpStatusCode)refused };

        foreach (var action in new[] { DownloadClientActions.Pause, DownloadClientActions.Resume, DownloadClientActions.Delete, DownloadClientActions.DeleteWithData, DownloadClientActions.Recheck })
        {
            var result = await On(uTorrent).ExecuteActionAsync(Client(), action, new string('a', 40), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.StartsWith("uTorrent refused Deluno's username and password", result.Message, StringComparison.Ordinal);
        }

        Assert.All(uTorrent.Sent, call => Assert.StartsWith("/gui/token.html", call, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_uTorrent_that_is_not_answering_is_not_told_to_pause_resume_recheck_or_remove_anything()
    {
        var handler = RecordedReplies.Utorrent("not-answering").NotAnswering();

        foreach (var action in new[] { DownloadClientActions.Pause, DownloadClientActions.Resume, DownloadClientActions.Recheck, DownloadClientActions.Delete, DownloadClientActions.DeleteWithData })
        {
            var outcome = await Record.ExceptionAsync(() => new UTorrentDownloadClient(() => handler).ExecuteActionAsync(Client(), action, new string('a', 40), CancellationToken.None));

            // The token page cannot be read, so the action is not even tried: the connection failure is raised to the caller that reports it.
            Assert.IsType<HttpRequestException>(outcome);
        }
    }

    [Fact]
    public async Task A_snapshot_of_a_uTorrent_that_is_not_answering_raises_the_connection_failure_for_the_poller_to_report()
    {
        var handler = RecordedReplies.Utorrent("not-answering").NotAnswering();

        await Assert.ThrowsAsync<HttpRequestException>(() => new UTorrentDownloadClient(() => handler).GetSnapshotAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task A_pause_that_times_out_while_uTorrent_is_read_is_a_failure_and_sends_no_pause()
    {
        var uTorrent = new TimesOutOnTheList();

        var result = await new UTorrentDownloadClient(() => uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.Pause, new string('a', 40), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(uTorrent.Sent, call => call.Contains("action=pause", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_429_to_an_action_is_a_plain_failure_naming_the_status()
    {
        var uTorrent = new TooManyRequests();

        var result = await new UTorrentDownloadClient(() => uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.Recheck, new string('a', 40), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("429", result.Message, StringComparison.Ordinal);
    }

    private sealed class TimesOutOnTheList : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request.RequestUri!.PathAndQuery);
            return request.RequestUri.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal)
                ? Task.FromResult(Ok(RecordedReplies.UtorrentTokenPage()))
                : throw new TaskCanceledException("The request timed out.");
        }
    }

    private sealed class TooManyRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("token.html", StringComparison.Ordinal)
                ? Ok(RecordedReplies.UtorrentTokenPage())
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests));
    }

    // ------------------------------------------------------------------ pause, resume, recheck (U16, U17)

    [Fact]
    public async Task Pausing_a_running_torrent_sends_pause_for_its_hash_and_is_done_once_uTorrent_lists_it_paused()
    {
        var running = ListOf(RecordedReplies.Utorrent("queued"));
        var paused = ListOf(RecordedReplies.Utorrent("paused"));
        var one = HashOf(running, "Recorded.Stalled.One.utorrent");
        var uTorrent = new UTorrent(ActionReplyOf(RecordedReplies.Utorrent("paused")), running, paused);

        var result = await On(uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.Pause, one, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var sent = Assert.Single(uTorrent.Actions("pause"));
        Assert.Contains("hash=" + one, sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pause_uTorrent_does_not_show_taking_effect_is_not_reported_done()
    {
        var running = ListOf(RecordedReplies.Utorrent("queued"));
        var one = HashOf(running, "Recorded.Stalled.One.utorrent");
        var uTorrent = new UTorrent(ActionReplyOf(RecordedReplies.Utorrent("paused")), running);

        var result = await On(uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.Pause, one, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_recheck_sends_recheck_for_its_hash_and_uTorrent_goes_on_listing_the_torrent()
    {
        var recorded = RecordedReplies.Utorrent("rechecked");
        var list = ListOf(recorded);
        var hash = HashOf(list, "Recorded.utorrent.B.mkv");
        var uTorrent = new UTorrent(ActionReplyOf(recorded), list);

        var result = await On(uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.Recheck, hash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var sent = Assert.Single(uTorrent.Actions("recheck"));
        Assert.Contains("hash=" + hash, sent, StringComparison.Ordinal);
        Assert.Empty(uTorrent.Actions("remove"));
        Assert.Empty(uTorrent.Actions("removedata"));
    }

    // ------------------------------------------------------------------ remove (U18, and the delete rows)

    [Fact]
    public async Task Delete_sends_remove_not_removedata_and_is_done_once_the_torrent_is_gone_from_the_list()
    {
        var removed = RecordedReplies.Utorrent("removed-without-data");
        var before = ListOf(RecordedReplies.Utorrent("paused"));
        var gone = HashOf(before, "Recorded.Stalled.One.utorrent");
        var uTorrent = new UTorrent(ActionReplyOf(removed), before, ListOf(removed));

        var result = await On(uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.Delete, gone, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("hash=" + gone, Assert.Single(uTorrent.Actions("remove")), StringComparison.Ordinal);
        Assert.Empty(uTorrent.Actions("removedata"));
    }

    [Theory]
    [InlineData(DownloadClientActions.DeleteWithData)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy)]
    public async Task Delete_with_data_sends_removedata_and_waits_for_uTorrent_to_stop_listing_the_torrent_it_is_still_deleting(string action)
    {
        var removed = RecordedReplies.Utorrent("removed-with-data");
        var before = ListOf(RecordedReplies.Utorrent("finished-single-file"));
        var hash = ListHash(before);
        // Straight after removedata uTorrent still lists the torrent ("Deleting 100.0 %"); a moment later it is gone.
        var uTorrent = new UTorrent(ActionReplyOf(removed), before, ListOf(removed), RecordedReplies.Utorrent("idle").Single.Reply.Body!);

        var result = await On(uTorrent).ExecuteActionAsync(Client(), action, hash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("hash=" + hash, Assert.Single(uTorrent.Actions("removedata")), StringComparison.Ordinal);
        Assert.Empty(uTorrent.Actions("remove"));
    }

    [Fact]
    public async Task A_removal_uTorrent_goes_on_listing_is_not_reported_done()
    {
        var removed = RecordedReplies.Utorrent("removed-with-data");
        var stays = ListOf(removed);
        var hash = ListHash(stays);
        var uTorrent = new UTorrent(ActionReplyOf(removed), stays);

        var result = await On(uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.DeleteWithData, hash, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy)]
    public async Task A_second_removal_of_what_uTorrent_no_longer_lists_is_a_failure_and_sends_no_second_remove(string action)
    {
        var twice = RecordedReplies.Utorrent("removed-twice");
        var hash = twice.Exchanges[0].Request.Path.Split("hash=")[1];
        var uTorrent = new UTorrent(ActionReplyOf(twice), ListOf(twice));

        var result = await On(uTorrent).ExecuteActionAsync(Client(), action, hash, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message, StringComparison.Ordinal);
        Assert.Empty(uTorrent.Actions("remove"));
        Assert.Empty(uTorrent.Actions("removedata"));
    }

    [Fact]
    public async Task A_removal_of_a_hash_uTorrent_never_had_is_a_failure_that_sends_no_remove()
    {
        var unknown = RecordedReplies.Utorrent("unknown-id");
        var hash = unknown.Exchanges[0].Request.Path.Split("hash=")[1];
        var uTorrent = new UTorrent(ActionReplyOf(unknown), ListOf(unknown));

        var result = await On(uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.Delete, hash, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(uTorrent.Actions("remove"));
    }

    private static string ListHash(string list)
    {
        using var rows = JsonDocument.Parse(list);
        return rows.RootElement.GetProperty("torrents")[0][0].GetString()!;
    }

    // ------------------------------------------------------------------ the files (U15), the label (U20), folders (U22)

    [Fact]
    public async Task The_files_of_a_torrent_are_read_from_getfiles_in_the_order_uTorrent_lists_them()
    {
        var recorded = RecordedReplies.Utorrent("finished-folder");
        var reply = recorded.Exchanges.Last(e => e.Request.Path.Contains("action=getfiles", StringComparison.Ordinal)).Reply.Body!;
        using var parsed = JsonDocument.Parse(reply);
        var expected = parsed.RootElement.GetProperty("files")[1].EnumerateArray().Select(file => file[0].GetString()!).ToArray();
        var uTorrent = new UTorrent("{}", "{}");
        uTorrent.Replies["getfiles"] = reply;

        var files = await On(uTorrent).GetFilesAsync(Client(), new string('a', 40), CancellationToken.None);

        Assert.Equal(expected, files);
        Assert.True(expected.Length >= 2);
    }

    [Fact]
    public async Task The_files_of_a_torrent_uTorrent_never_had_are_none()
    {
        var uTorrent = new UTorrent("{}", "{}");
        uTorrent.Replies["getfiles"] = RecordedReplies.Utorrent("files-unknown-hash").Single.Reply.Body!;

        Assert.Null(await On(uTorrent).GetFilesAsync(Client(), new string('0', 40), CancellationToken.None));
    }

    [Fact]
    public async Task Moving_a_torrent_to_the_keep_seeding_label_sets_it_with_setprops_and_is_done_once_the_list_shows_it()
    {
        var recorded = RecordedReplies.Utorrent("label-set");
        var after = ListOf(recorded);
        var before = RecordedReplies.Utorrent("finished-single-file").Body("list=1");
        var hash = ListHash(before);
        var uTorrent = new UTorrent(ActionReplyOf(recorded), before, after);

        var result = await On(uTorrent).SetCategoryAsync(Client(), hash, "recorded-movies", null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var sent = Assert.Single(uTorrent.Actions("setprops"));
        Assert.Contains("s=label", sent, StringComparison.Ordinal);
        Assert.Contains("v=recorded-movies", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_label_uTorrent_says_ok_to_but_still_lists_as_it_was_is_not_believed()
    {
        var recorded = RecordedReplies.Utorrent("label-set");
        var before = RecordedReplies.Utorrent("finished-single-file").Body("list=1");
        var hash = ListHash(before);
        var uTorrent = new UTorrent(ActionReplyOf(recorded), before);

        var result = await On(uTorrent).SetCategoryAsync(Client(), hash, "recorded-movies", null, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_label_for_a_torrent_uTorrent_does_not_list_is_a_failure_and_no_label_is_sent()
    {
        var uTorrent = new UTorrent(ActionReplyOf(RecordedReplies.Utorrent("label-unknown-hash")), RecordedReplies.Utorrent("idle").Single.Reply.Body!);

        var result = await On(uTorrent).SetCategoryAsync(Client(), new string('0', 40), "movies", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(uTorrent.Actions("setprops"));
    }

    [Fact]
    public async Task A_label_cannot_be_checked_because_uTorrent_lists_none_and_the_check_says_so_after_reading_the_token()
    {
        var uTorrent = new UTorrent("{}", RecordedReplies.Utorrent("idle").Single.Reply.Body!);

        var check = await On(uTorrent).CheckKeepSeedingCategoryAsync(Client(), "keep-seeding", CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Ready, check.Status);
        Assert.False(check.Found);
        Assert.Contains("does not list its labels", check.Message, StringComparison.Ordinal);
        Assert.Equal("/gui/token.html", Assert.Single(uTorrent.Sent));
    }

    // ------------------------------------------------------------------ what uTorrent has no call for (the base answers, and nothing is sent)

    [Fact]
    public async Task The_calls_uTorrent_has_no_equivalent_for_answer_plainly_and_send_it_nothing()
    {
        var uTorrent = new UTorrent("{}", RecordedReplies.Utorrent("idle").Single.Reply.Body!);
        var client = On(uTorrent);

        var folders = await client.CheckCategoryAsync(Client(), "movies", CancellationToken.None);
        Assert.Equal(DownloadClientCategoryStatuses.Unsupported, folders.Status);
        Assert.False(folders.Supported);
        Assert.Empty(await client.GetHistoryAsync(Client(), DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await client.ClearWarningsAsync(Client(), CancellationToken.None));
        Assert.False((await client.ResumeClientAsync(Client(), CancellationToken.None)).Supported);
        Assert.Null(await client.HasUsenetProviderAsync(Client(), CancellationToken.None));
        Assert.Null(await client.SetUsenetProviderAsync(Client(), new UsenetProviderRequest("news.example.com", 563, true, "user", "secret", 8), CancellationToken.None));
        Assert.Empty(uTorrent.Sent);
    }

    // ------------------------------------------------------------------ a release sent (U9, U11), add paused

    private const string Magnet = "magnet:?xt=urn:btih:497891bbd14f5850b06b426aa5a2945725bea7bd&dn=Recorded.Magnet";

    private static DownloadClientGrabRequest Grab(string url) => new("Recorded.Magnet", url, "movies", null, "Lab Torznab", "dispatch-1");

    [Fact]
    public async Task A_magnet_is_sent_with_add_url_followed_by_its_own_hash_and_labelled_with_the_movies_category()
    {
        var recorded = RecordedReplies.Utorrent("magnet-no-metadata");
        var uTorrent = new UTorrent(ActionReplyOf(recorded), RecordedReplies.Utorrent("idle").Single.Reply.Body!, ListOf(recorded));
        uTorrent.Replies["setprops"] = ActionReplyOf(RecordedReplies.Utorrent("label-set"));

        var result = await On(uTorrent).GrabAsync(Client(), Grab(Magnet), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("497891bbd14f5850b06b426aa5a2945725bea7bd", result.ExternalId, ignoreCase: true);
        Assert.Contains("s=magnet", Assert.Single(uTorrent.Actions("add-url")), StringComparison.Ordinal);
        Assert.Contains("v=movies", Assert.Single(uTorrent.Actions("setprops")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_torrent_uTorrent_is_slow_to_list_is_waited_for_and_then_followed()
    {
        var recorded = RecordedReplies.Utorrent("magnet-no-metadata");
        var idle = RecordedReplies.Utorrent("idle").Single.Reply.Body!;
        var uTorrent = new UTorrent(ActionReplyOf(recorded), idle, idle, ListOf(recorded));

        var result = await On(uTorrent).GrabAsync(Client(), Grab(Magnet), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(uTorrent.Lists >= 3);
    }

    [Fact]
    public async Task A_magnet_for_a_torrent_uTorrent_already_holds_follows_the_copy_it_has()
    {
        var duplicate = RecordedReplies.Utorrent("duplicate-add");
        var list = ListOf(duplicate);
        var hash = ListHash(list);
        var uTorrent = new UTorrent(ActionReplyOf(duplicate), list);

        var result = await On(uTorrent).GrabAsync(Client(), Grab($"magnet:?xt=urn:btih:{hash}&dn=Recorded.utorrent.A.mkv"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(hash, result.ExternalId, ignoreCase: true);
        Assert.Empty(uTorrent.Actions("setprops"));
    }

    [Fact]
    public async Task A_release_whose_hash_is_not_known_and_that_never_shows_in_the_list_is_a_failure_and_two_new_torrents_name_none()
    {
        var recorded = RecordedReplies.Utorrent("queued");
        var idle = RecordedReplies.Utorrent("idle").Single.Reply.Body!;
        // Two torrents appeared in the list at once, so neither can be told to be this release.
        var two = new UTorrent(ActionReplyOf(RecordedReplies.Utorrent("add-file-accepted")), idle, ListOf(recorded));

        var result = await On(two).GrabAsync(Client(), Grab("http://indexer.test/download/1"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("could not tell which torrent it became", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_uTorrent_refuses_is_a_failed_send_with_its_own_reason_and_no_label()
    {
        var refused = RecordedReplies.Utorrent("add-refused");
        var uTorrent = new UTorrent(ActionReplyOf(refused), RecordedReplies.Utorrent("idle").Single.Reply.Body!);

        var result = await On(uTorrent).GrabAsync(Client(), Grab("http://indexer.test/download/1"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("uTorrent did not take the release", result.Message, StringComparison.Ordinal);
        Assert.Empty(uTorrent.Actions("setprops"));
    }

    // #1329: "Add downloads paused" is a switch on every client; uTorrent's add has no paused option, so the torrent it listed is stopped
    // straight after, and only a torrent that is new (never one it already held).
    [Fact]
    public async Task A_download_added_to_a_client_set_to_add_paused_is_stopped_once_uTorrent_lists_it()
    {
        var recorded = RecordedReplies.Utorrent("magnet-no-metadata");
        var uTorrent = new UTorrent(ActionReplyOf(recorded), RecordedReplies.Utorrent("idle").Single.Reply.Body!, ListOf(recorded));

        var result = await On(uTorrent).GrabAsync(Client(addPaused: true), Grab(Magnet), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var stop = Assert.Single(uTorrent.Actions("stop"));
        Assert.Contains("hash=497891BBD14F5850B06B426AA5A2945725BEA7BD", stop, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_download_added_to_a_client_not_set_to_add_paused_is_not_stopped_and_one_uTorrent_already_held_is_never_stopped()
    {
        var recorded = RecordedReplies.Utorrent("magnet-no-metadata");
        var running = new UTorrent(ActionReplyOf(recorded), RecordedReplies.Utorrent("idle").Single.Reply.Body!, ListOf(recorded));
        await On(running).GrabAsync(Client(addPaused: false), Grab(Magnet), CancellationToken.None);
        Assert.Empty(running.Actions("stop"));

        var duplicate = RecordedReplies.Utorrent("duplicate-add");
        var held = new UTorrent(ActionReplyOf(duplicate), ListOf(duplicate));
        await On(held).GrabAsync(Client(addPaused: true), Grab($"magnet:?xt=urn:btih:{ListHash(ListOf(duplicate))}"), CancellationToken.None);
        Assert.Empty(held.Actions("stop"));
    }
    // ------------------------------------------------------------------ the hand-off families on uTorrent: resend, try again, vanish

    [Fact]
    public async Task A_release_removed_from_uTorrent_can_be_sent_again_and_is_followed_as_a_new_torrent_not_a_duplicate()
    {
        var removed = RecordedReplies.Utorrent("removed-with-data");
        var gone = RecordedReplies.Utorrent("idle").Single.Reply.Body!;
        var back = ListOf(RecordedReplies.Utorrent("magnet-no-metadata"));
        // The same magnet: the list is empty when it is sent again (the removal finished), then shows the torrent.
        var uTorrent = new UTorrent(ActionReplyOf(removed), gone, back);

        var result = await On(uTorrent).GrabAsync(Client(), Grab(Magnet), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.IsDuplicateFollow);
        Assert.Single(uTorrent.Actions("add-url"));
    }

    [Fact]
    public async Task A_refused_send_can_be_tried_again_and_the_second_send_is_followed()
    {
        var idle = RecordedReplies.Utorrent("idle").Single.Reply.Body!;
        var refused = new UTorrent(ActionReplyOf(RecordedReplies.Utorrent("add-refused")), idle);
        var first = await On(refused).GrabAsync(Client(), Grab(Magnet), CancellationToken.None);
        Assert.False(first.Succeeded);

        var recorded = RecordedReplies.Utorrent("magnet-no-metadata");
        var accepting = new UTorrent(ActionReplyOf(recorded), idle, ListOf(recorded));
        var second = await On(accepting).GrabAsync(Client(), Grab(Magnet), CancellationToken.None);

        Assert.True(second.Succeeded, second.Message);
    }

    [Fact]
    public async Task A_torrent_that_has_vanished_from_uTorrent_before_or_while_it_is_waited_for_is_not_reported_as_held()
    {
        var gone = RecordedReplies.Utorrent("idle").Single.Reply.Body!;
        var uTorrent = new UTorrent(ActionReplyOf(RecordedReplies.Utorrent("paused")), gone);
        var hash = new string('a', 40);

        var queue = await Queue(uTorrent);
        var paused = await On(uTorrent).ExecuteActionAsync(Client(), DownloadClientActions.Pause, hash, CancellationToken.None);
        var files = await On(uTorrent).GetFilesAsync(Client(), hash, CancellationToken.None);

        Assert.Empty(queue);
        Assert.False(paused.Succeeded);
        Assert.Empty(uTorrent.Actions("pause"));
    }
}
