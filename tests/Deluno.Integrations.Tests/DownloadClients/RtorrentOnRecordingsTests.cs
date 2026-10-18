using System.Net;
using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// Deluno's rTorrent client against rTorrent 0.16.25's own replies, recorded from the real client in Docker (tests/fixtures/recorded/rtorrent,
/// #1237): the published image with its XML-RPC mount, downloading a film and a three-episode pack from a second rTorrent through a
/// small tracker. Each recorded state is replayed here in the order it happened, and what Deluno sent is asserted beside what it made of
/// the answers. A hash in a call is the test's own; the replies are what rTorrent said.
/// </summary>
public sealed class RtorrentOnRecordingsTests
{
    private static readonly Regex MethodName = new("<methodName>([^<]+)</methodName>", RegexOptions.Compiled);

    private static DownloadClientItem Item(string? username = "user", string? secret = "secret")
        => Client("rtorrent", username, secret) with { Name = "rTorrent" };

    /// <summary>
    /// Answers each call with the next recorded reply to the same method, taken from the given exchanges in the order listed, and
    /// once they are used up with the last one. The calls Deluno made are kept, with their bodies.
    /// </summary>
    private sealed class Scripted : HttpMessageHandler
    {
        private readonly Dictionary<string, List<RecordedReply>> _replies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _used = new(StringComparer.Ordinal);

        public List<string> Methods { get; } = [];

        public List<string> Bodies { get; } = [];

        public List<System.Net.Http.Headers.AuthenticationHeaderValue?> Authorization { get; } = [];

        public Scripted Add(Recording recording)
        {
            foreach (var exchange in recording.Exchanges)
            {
                var method = MethodOf(exchange.Request.Body);
                if (!_replies.TryGetValue(method, out var list))
                {
                    _replies[method] = list = [];
                }

                list.Add(exchange.Reply);
            }

            return this;
        }

        /// <summary>Puts the replies of one recorded call in front of the rest for that call, so a test can choose which answer comes first.</summary>
        public Scripted Add(Recording recording, params int[] exchanges)
        {
            foreach (var index in exchanges)
            {
                var exchange = recording.Exchanges[index];
                var method = MethodOf(exchange.Request.Body);
                if (!_replies.TryGetValue(method, out var list))
                {
                    _replies[method] = list = [];
                }

                list.Add(exchange.Reply);
            }

            return this;
        }

        private RecordedReply? _fallback;

        /// <summary>Answers every call no recording was added for with this recorded reply (a mount that refuses the login refuses every call).</summary>
        public Scripted Fallback(RecordedReply reply)
        {
            _fallback = reply;
            return this;
        }

        public bool Holds(string method) => _replies.ContainsKey(method);

        public Scripted Reset()
        {
            _replies.Clear();
            _used.Clear();
            return this;
        }

        public int Count(string method) => Methods.Count(m => m == method);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var method = MethodOf(body);
            Methods.Add(method);
            Bodies.Add(body);
            Authorization.Add(request.Headers.Authorization);
            if (!_replies.TryGetValue(method, out var list))
            {
                if (_fallback is not null)
                {
                    return _fallback.ToResponse();
                }

                throw new InvalidOperationException($"No rTorrent recording answers {method}. Calls so far: {string.Join(", ", Methods)}.");
            }

            var at = _used.GetValueOrDefault(method);
            _used[method] = at + 1;
            var chosen = list[Math.Min(at, list.Count - 1)];
            if (chosen.Status == 0)
            {
                throw new HttpRequestException(chosen.Error ?? "The connection was refused.");
            }

            return chosen.ToResponse();
        }
    }

    private static string MethodOf(string? body) => MethodName.Match(body ?? string.Empty).Groups[1].Value;

    private static Recording R(string state) => RecordedReplies.Rtorrent(state);

    /// <summary>
    /// One poll of the client as the real rTorrent answered it: the recorded list reply the handler holds, and for the calls the poll makes
    /// beside it the replies rTorrent gave to the same calls (its version; the tracker call, which the real rTorrent faults because it has no
    /// t.message), unless the test scripted them itself.
    /// </summary>
    private static Task<DownloadClientTelemetrySnapshot?> Poll(Scripted handler)
    {
        if (!handler.Holds("t.multicall")) handler.Add(R("downloading"), 4);
        if (!handler.Holds("system.client_version")) handler.Add(R("version"));
        return Over(handler).GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static RtorrentDownloadClient Over(Scripted handler) => new(new Factory(handler));

    /// <summary>The info hash rTorrent listed in the list row of a recorded state (the first row), as rTorrent writes it: upper case.</summary>
    private static string HashIn(string state, int exchange)
        => Regex.Match(R(state).Exchanges[exchange].Reply.Body!, "<string>([0-9A-F]{40})</string>").Groups[1].Value;

    /// <summary>The torrent file Deluno-side tests send: the very bytes the recorder sent in a recorded load.raw call (the real file, so its info hash is the recorded one).</summary>
    private static byte[] TorrentFileIn(string state, int exchange)
        => Convert.FromBase64String(Regex.Match(R(state).Exchanges[exchange].Request.Body!, "<base64>([^<]+)</base64>").Groups[1].Value);

    private static DownloadClientGrabRequest FileRequest(byte[] file)
        => new("Recorded.Rtorrent.Film.2024.1080p.WEB-DL", "http://indexer.test/download/1", "movies", "movies", "Fixture indexer") { TorrentFile = file };

    private static DownloadClientGrabRequest LinkRequest(string url = "http://indexer.test/download/1")
        => new("Recorded.Rtorrent.Film.2024.1080p.WEB-DL", url, "movies", "movies", "Fixture indexer");

    // ---- the version, nothing held, a refused login, not answering ----

    [Fact]
    public async Task The_version_rTorrent_reports_is_read_from_system_client_version_and_a_client_holding_nothing_has_an_empty_queue()
    {
        var handler = new Scripted().Add(R("idle")).Add(R("version"));

        var snapshot = await Poll(handler);

        Assert.NotNull(snapshot);
        Assert.Equal("healthy", snapshot!.HealthStatus);
        Assert.Equal("0.16.25", snapshot.ClientVersion);
        Assert.Empty(snapshot.Queue);
        Assert.Equal(["d.multicall2", "system.client_version"], handler.Methods);
        Assert.Contains("<methodName>d.multicall2</methodName>", handler.Bodies[0]);
        Assert.Contains("<string>d.peers_connected=</string>", handler.Bodies[0]);
        Assert.Equal("Basic", handler.Authorization[0]?.Scheme);
    }

    [Fact]
    public async Task A_wrong_login_is_the_mounts_401_and_is_said_as_a_refused_login_on_a_pause()
    {
        var refused = R("login-refused");
        Assert.Equal(401, refused.Exchanges[0].Reply.Status);
        Assert.Equal(401, refused.Exchanges[1].Reply.Status);
        var handler = new Scripted().Add(refused).Fallback(refused.Exchanges[0].Reply);

        var poll = await Assert.ThrowsAsync<HttpRequestException>(() => Over(handler).GetSnapshotAsync(Item(secret: "wrong"), DateTimeOffset.UtcNow, CancellationToken.None));
        var pause = await Over(new Scripted().Add(refused).Fallback(refused.Exchanges[0].Reply)).ExecuteActionAsync(Item(secret: "wrong"), DownloadClientActions.Pause, "ABCDEF0123456789ABCDEF0123456789ABCDEF01", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, poll.StatusCode);
        Assert.False(pause.Succeeded);
        Assert.True(pause.Message.Contains("refused Deluno's username and password", StringComparison.Ordinal), pause.Message);
    }

    [Fact]
    public async Task An_rTorrent_that_does_not_answer_lets_the_refusal_reach_the_caller_on_a_poll_and_is_a_plain_failure_on_a_pause()
    {
        var down = R("not-answering").NotAnswering();

        await Assert.ThrowsAsync<HttpRequestException>(() => new RtorrentDownloadClient(new Factory(down)).GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None));
        var pause = await new RtorrentDownloadClient(new Factory(down)).ExecuteActionAsync(Item(), DownloadClientActions.Pause, "ABCDEF0123456789ABCDEF0123456789ABCDEF01", CancellationToken.None);

        Assert.False(pause.Succeeded);
    }

    // ---- what the list says of a torrent in every state ----

    [Fact]
    public async Task A_film_downloading_from_a_peer_is_downloading_with_its_progress_rate_and_peers()
    {
        var handler = new Scripted().Add(R("downloading"), 0).Add(R("version"));

        var snapshot = await Poll(handler);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(HashIn("downloading", 0), item.Id);
        Assert.Equal("Recorded.Rtorrent.Film.2024.1080p.WEB-DL.mkv", item.ReleaseName);
        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
        Assert.Equal(41_943_040, item.SizeBytes);
        Assert.InRange(item.DownloadedBytes, 1, 41_943_039);
        Assert.Equal(item.DownloadedBytes / 41_943_040d * 100, item.Progress, 3);
        Assert.True(item.SpeedMbps > 0);
        Assert.Equal(1, item.Peers);
        Assert.False(item.IsSetupProblem);
        Assert.Equal("1", Regex.Match(R("downloading").Exchanges[1].Reply.Body!, "<i8>(\\d)</i8>").Groups[1].Value);
    }

    [Fact]
    public async Task A_paused_download_reads_as_paused_and_a_resumed_one_as_downloading_again()
    {
        var paused = await Poll(new Scripted().Add(R("paused"), 2));
        var resumed = await Poll(new Scripted().Add(R("resumed"), 2));

        Assert.Equal(DownloadQueueStatuses.Paused, Assert.Single(paused!.Queue).Status);
        Assert.Equal(DownloadQueueStatuses.Downloading, Assert.Single(resumed!.Queue).Status);
    }

    [Fact]
    public async Task A_pause_is_done_only_when_rTorrent_shows_the_download_inactive_afterwards()
    {
        // The list before (downloading: d.is_active 1), then the recorded d.pause and the d.is_active 0 that followed it.
        var handler = new Scripted().Add(R("downloading"), 1).Add(R("paused"), 0, 1);

        var result = await Over(handler).ExecuteActionAsync(Item(), DownloadClientActions.Pause, HashIn("downloading", 0), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["d.is_active", "d.pause", "d.is_active"], handler.Methods);
        Assert.Contains($"<string>{HashIn("downloading", 0)}</string>", handler.Bodies[1]);
    }

    [Fact]
    public async Task A_resume_is_done_only_when_rTorrent_shows_the_download_active_afterwards()
    {
        var handler = new Scripted().Add(R("paused"), 1).Add(R("resumed"), 0, 1);

        var result = await Over(handler).ExecuteActionAsync(Item(), DownloadClientActions.Resume, HashIn("paused", 2), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["d.is_active", "d.resume", "d.is_active"], handler.Methods);
    }

    [Fact]
    public async Task A_pause_of_a_download_rTorrent_does_not_hold_is_a_failure_and_nothing_is_paused()
    {
        var fault = R("unknown-hash-fault");
        Assert.Contains("<fault>", fault.Exchanges[0].Reply.Body);
        var handler = new Scripted().Add(fault, 0, 1);

        var result = await Over(handler).ExecuteActionAsync(Item(), DownloadClientActions.Pause, new string('0', 40), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("d.pause", handler.Methods);
    }

    [Fact]
    public async Task A_resume_of_a_download_rTorrent_does_not_hold_is_a_failure_and_nothing_is_resumed()
    {
        var handler = new Scripted().Add(R("unknown-hash-fault"), 0, 2);

        var result = await Over(handler).ExecuteActionAsync(Item(), DownloadClientActions.Resume, new string('0', 40), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("d.resume", handler.Methods);
    }

    [Fact]
    public async Task A_finished_film_is_ready_to_import_with_the_folder_rTorrent_moved_it_to()
    {
        var handler = new Scripted().Add(R("finished-single-file"), 0).Add(R("version"));

        var snapshot = await Poll(handler);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.Equal(100, item.Progress, 3);
        Assert.Equal("/downloads/complete", item.SourcePath);
    }

    [Fact]
    public async Task A_finished_folder_release_is_ready_to_import_and_its_files_are_the_three_episodes()
    {
        var handler = new Scripted().Add(R("finished-folder"));

        var snapshot = await Poll(handler);
        var files = await Over(handler).GetFilesAsync(Item(), HashIn("finished-folder", 0), CancellationToken.None);

        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(snapshot!.Queue).Status);
        Assert.Equal(
            ["Recorded.Rtorrent.Show.S01.1080p.WEB-DL.S01E01.mkv", "Recorded.Rtorrent.Show.S01.1080p.WEB-DL.S01E02.mkv", "Recorded.Rtorrent.Show.S01.1080p.WEB-DL.S01E03.mkv"],
            files);
    }

    [Fact]
    public async Task A_single_file_torrent_lists_its_one_file_by_f_multicall()
    {
        var handler = new Scripted().Add(R("finished-single-file"), 4);

        var files = await Over(handler).GetFilesAsync(Item(), HashIn("finished-single-file", 0), CancellationToken.None);

        Assert.Equal(["Recorded.Rtorrent.Film.2024.1080p.WEB-DL.mkv"], files);
        Assert.Equal(["f.multicall"], handler.Methods);
    }

    [Fact]
    public async Task The_files_of_a_download_rTorrent_does_not_hold_are_unknown_not_empty()
    {
        var handler = new Scripted().Add(R("unknown-hash-fault"), 10);

        var files = await Over(handler).GetFilesAsync(Item(), new string('0', 40), CancellationToken.None);

        Assert.Null(files);
    }

    [Fact]
    public async Task A_download_stalled_with_the_only_seeder_gone_part_way_stays_downloading_with_no_rate_and_progress_below_the_end()
    {
        var handler = new Scripted().Add(R("stalled")).Add(R("version"));

        var first = await Poll(handler);
        var snapshot = await Poll(handler);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(0, item.SpeedMbps);
        Assert.Equal(11_534_336, item.DownloadedBytes);
        Assert.Equal(16_777_216, item.SizeBytes);
        Assert.InRange(item.Progress, 68, 69);
        Assert.NotEqual(DownloadQueueStatuses.ImportReady, item.Status);
        Assert.Equal(first!.Queue[0].DownloadedBytes, item.DownloadedBytes);
        Assert.Equal(2, handler.Count("d.multicall2"));
    }

    [Fact]
    public async Task A_torrent_with_no_peer_and_no_data_is_active_with_nothing_done()
    {
        var handler = new Scripted().Add(R("no-peers"), 0).Add(R("version"));

        var item = Assert.Single((await Poll(handler))!.Queue);

        Assert.Equal(0, item.DownloadedBytes);
        Assert.Equal(0, item.Peers);
        Assert.Equal(0, item.SpeedMbps);
        Assert.False(item.IsSetupProblem);
    }

    [Fact]
    public async Task A_tracker_that_refuses_the_torrent_shows_in_the_row_message_and_is_read_as_a_problem_on_this_machine_not_the_release()
    {
        var handler = new Scripted().Add(R("tracker-error"), 0).Add(R("version"));

        var item = Assert.Single((await Poll(handler))!.Queue);

        Assert.Equal(DownloadQueueStatuses.Stalled, item.Status);
        Assert.Contains("Registered torrents only", item.ErrorMessage, StringComparison.Ordinal);
        Assert.True(item.IsSetupProblem);
        Assert.Contains("Registered torrents only", R("tracker-error").Exchanges[2].Reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recheck_reads_as_not_transferring_while_it_runs_and_as_ready_once_it_is_done()
    {
        var checking = await Poll(new Scripted().Add(R("rechecked"), 1));
        var done = await Poll(new Scripted().Add(R("rechecked"), 2));

        Assert.Equal(DownloadQueueStatuses.NotTransferring, Assert.Single(checking!.Queue).Status);
        Assert.Equal(DownloadQueueStatuses.ImportReady, Assert.Single(done!.Queue).Status);
    }

    [Fact]
    public async Task A_recheck_sends_d_check_hash_for_the_hash_and_is_sent_nothing_else()
    {
        var handler = new Scripted().Add(R("rechecked"), 0);

        var result = await Over(handler).ExecuteActionAsync(Item(), DownloadClientActions.Recheck, HashIn("rechecked", 1), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["d.check_hash"], handler.Methods);
    }

    // ---- adding ----

    [Fact]
    public async Task A_torrent_file_is_sent_with_load_raw_start_and_the_new_hash_is_found_by_comparing_download_list_before_and_after()
    {
        var handler = new Scripted().Add(R("add-file"), 0, 1, 2);

        var result = await Over(handler).GrabAsync(Item(), FileRequest(TorrentFileIn("add-file", 1)), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(HashIn("add-file", 3), result.ExternalId, ignoreCase: true);
        Assert.Equal(["download_list", "load.raw_start", "download_list"], handler.Methods);
        Assert.Contains(Convert.ToBase64String(TorrentFileIn("add-file", 1)), handler.Bodies[1]);
    }

    [Fact]
    public async Task A_torrent_file_added_paused_is_sent_with_load_raw_and_is_listed_not_active()
    {
        var recording = R("add-file-paused");
        var handler = new Scripted().Add(recording, 0, 1, 2);

        var result = await Over(handler).GrabAsync(Item() with { AddPaused = true }, FileRequest(TorrentFileIn("add-file-paused", 1)), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("load.raw", handler.Methods);
        Assert.DoesNotContain("load.raw_start", handler.Methods);
        var row = await Poll(new Scripted().Add(recording, 3));
        Assert.Equal(DownloadQueueStatuses.Paused, Assert.Single(row!.Queue).Status);
    }

    [Fact]
    public async Task The_same_torrent_file_sent_while_rTorrent_holds_it_is_followed_not_failed_or_added_twice()
    {
        var recording = R("duplicate-add");
        var handler = new Scripted().Add(recording);

        var result = await Over(handler).GrabAsync(Item(), FileRequest(TorrentFileIn("duplicate-add", 1)), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.IsDuplicateFollow);
        Assert.Equal(HashIn("add-file", 3), result.ExternalId, ignoreCase: true);
        Assert.Contains("already holds", result.Message, StringComparison.Ordinal);
        Assert.Equal(["download_list", "load.raw_start", "download_list"], handler.Methods);
    }

    [Fact]
    public async Task A_link_is_sent_with_load_start_and_the_hash_that_appears_is_the_grab()
    {
        var recording = R("add-url");
        var handler = new Scripted().Add(R("idle"), 1).Add(recording, 0, 1);

        var result = await Over(handler).GrabAsync(Item(), LinkRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["download_list", "load.start", "download_list"], handler.Methods);
        Assert.Contains("<string>http://indexer.test/download/1</string>", handler.Bodies[1]);
        Assert.Equal(HashIn("add-url", 2), result.ExternalId, ignoreCase: true);
    }

    [Fact]
    public async Task A_link_added_paused_is_sent_with_load_normal_and_the_torrent_is_not_started()
    {
        var recording = R("add-url-paused");
        var handler = new Scripted().Add(R("idle"), 1).Add(recording, 0, 1);

        var result = await Over(handler).GrabAsync(Item() with { AddPaused = true }, LinkRequest(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["download_list", "load.normal", "download_list"], handler.Methods);
        var listed = await Poll(new Scripted().Add(recording, 2));
        Assert.Equal(DownloadQueueStatuses.Paused, Assert.Single(listed!.Queue).Status);
    }

    [Fact]
    public async Task A_link_the_server_answers_404_for_adds_nothing_and_rTorrent_says_nothing_about_it()
    {
        var recording = R("add-url-dead");
        Assert.Empty(Regex.Matches(recording.Exchanges[3].Reply.Body!, "<string>"));
        var handler = new Scripted().Add(recording, 0, 1, 2);

        var result = await Over(handler).GrabAsync(Item(), LinkRequest("http://indexer.test/dl/Recorded.Nothing.Here.torrent"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("added no torrent", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bytes_that_are_not_a_torrent_are_accepted_by_load_and_add_nothing()
    {
        var recording = R("add-refused");
        var handler = new Scripted().Add(recording);

        var result = await Over(handler).GrabAsync(Item(), FileRequest("this is not a torrent"u8.ToArray()), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("added no torrent", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<fault>", recording.Exchanges[1].Reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_magnet_nobody_can_supply_metadata_for_is_listed_by_its_hash_before_any_data_arrives()
    {
        var recording = R("magnet-no-metadata");
        var magnetHash = Regex.Match(recording.Exchanges[0].Request.Body!, "btih:([0-9a-f]{40})").Groups[1].Value;
        var handler = new Scripted().Add(R("idle"), 1).Add(recording, 0, 1);

        var result = await Over(handler).GrabAsync(Item(), LinkRequest($"magnet:?xt=urn:btih:{magnetHash}&dn=Recorded.Magnet"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(magnetHash, result.ExternalId, ignoreCase: true);
        var row = Assert.Single((await Poll(new Scripted().Add(recording, 2)))!.Queue);
        Assert.Equal(0, row.DownloadedBytes);
    }

    // ---- removing ----

    [Theory]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy)]
    public async Task A_removal_is_done_when_rTorrent_held_the_download_and_no_longer_lists_it(string action)
    {
        var handler = new Scripted().Add(R("downloading"), 2).Add(R("removed-without-data"), 0).Add(R("removed-twice"), 1);

        var result = await Over(handler).ExecuteActionAsync(Item(), action, HashIn("downloading", 0), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(handler.Methods, method => method == "d.erase");
    }

    [Theory]
    [InlineData(DownloadClientActions.Delete)]
    [InlineData(DownloadClientActions.DeleteWithData)]
    public async Task A_second_removal_of_the_same_download_is_a_failure_with_no_second_erase(string action)
    {
        var handler = new Scripted().Add(R("removed-twice"), 1);

        var result = await Over(handler).ExecuteActionAsync(Item(), action, new string('0', 40), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("has no download with that id", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("d.erase", handler.Methods);
    }

    [Fact]
    public async Task Removing_with_data_asks_for_the_base_path_then_erases_and_leaves_files_it_cannot_safely_find_on_this_machine()
    {
        var recording = R("finished-single-file");
        var handler = new Scripted().Add(recording, 2).Add(R("downloading"), 2).Add(R("removed-without-data"), 0).Add(R("removed-twice"), 1);

        var result = await Over(handler).ExecuteActionAsync(Item(), DownloadClientActions.DeleteWithData, HashIn("finished-single-file", 0), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("d.base_path", handler.Methods);
        Assert.Contains("d.erase", handler.Methods);
        Assert.True(handler.Methods.ToList().IndexOf("d.base_path") < handler.Methods.ToList().IndexOf("d.erase"));
        Assert.Contains("/downloads/complete/Recorded.Rtorrent.Film.2024.1080p.WEB-DL.mkv", recording.Exchanges[2].Reply.Body, StringComparison.Ordinal);
        Assert.Contains("could not safely find its files", result.Message + result.FilesNote, StringComparison.Ordinal);
    }

    // ---- the label and the settings ----

    [Fact]
    public async Task The_label_is_set_with_d_custom1_set_and_read_back_by_d_custom1()
    {
        var recording = R("label-set");
        var handler = new Scripted().Add(recording);

        var result = await Over(handler).SetCategoryAsync(Item(), HashIn("finished-single-file", 0), "recorded-movies", null, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("d.custom1.set", handler.Methods.Single(m => m == "d.custom1.set"));
        Assert.Contains("<string>recorded-movies</string>", handler.Bodies[handler.Methods.IndexOf("d.custom1.set")]);
        Assert.Contains("recorded-movies", recording.Exchanges[2].Reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_label_asked_of_a_download_rTorrent_does_not_hold_is_a_failure_and_nothing_is_set()
    {
        var handler = new Scripted().Add(R("unknown-hash-fault"), 6, 7);

        var result = await Over(handler).SetCategoryAsync(Item(), new string('0', 40), "recorded-movies", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain("d.custom1.set", handler.Methods);
    }

    [Fact]
    public void The_default_folder_and_the_free_space_are_answered_as_plain_values()
    {
        var settings = R("settings");

        Assert.Equal("directory.default", MethodOf(settings.Exchanges[0].Request.Body));
        Assert.Contains("/downloads/", settings.Exchanges[0].Reply.Body, StringComparison.Ordinal);
        Assert.Equal("d.free_diskspace", MethodOf(settings.Exchanges[1].Request.Body));
        Assert.Matches("<i8>[0-9]+</i8>", settings.Exchanges[1].Reply.Body!);
    }

    // ---- what the tracker calls say ----

    [Fact]
    public void The_tracker_list_with_the_fields_rTorrent_has_answers_where_t_message_is_a_fault()
    {
        var trackers = R("stalled-trackers");

        Assert.Contains("t.failed_counter=", trackers.Exchanges[0].Request.Body, StringComparison.Ordinal);
        Assert.Contains("<string>http://tracker:8080/announce</string>", trackers.Exchanges[0].Reply.Body, StringComparison.Ordinal);
        Assert.Contains("<string>dht://</string>", trackers.Exchanges[0].Reply.Body, StringComparison.Ordinal);
        Assert.Contains("Command \"t.message\" does not exist.", R("downloading").Exchanges[4].Reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_torrent_list_is_read_even_though_rTorrent_has_no_t_message_and_faults_the_tracker_call()
    {
        // The real rTorrent faults t.multicall for t.message; the poll must still give the list (a tracker read that fails says nothing either way).
        var handler = new Scripted().Add(R("downloading"), 0, 4).Add(R("version"));

        var snapshot = await Poll(handler);

        var item = Assert.Single(snapshot!.Queue);
        Assert.Equal(DownloadTrackerStatuses.Unknown, item.TrackerStatus);
        Assert.Equal(DownloadQueueStatuses.Downloading, item.Status);
    }
}
