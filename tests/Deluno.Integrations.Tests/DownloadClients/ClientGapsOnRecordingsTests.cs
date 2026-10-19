using System.Net;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: the client states that the first recordings left out, recorded from the real clients in Docker with
/// scripts/recorded-replies/docker/clientgaps.mjs (qBittorrent 5.2.4 and 4.6.7, SABnzbd 5.1.3, NZBGet 26.3): the address ban after too many
/// wrong logins, a 4.x qBittorrent that has no stop and start, the queue moves and force start, and a Usenet client paused as a whole.
/// </summary>
public sealed class ClientGapsOnRecordingsTests
{
    private const string QbitHash = "aed3fbe9593f02c2fe022a2fbd16704709e2fd84";

    private static HttpClient SignInClient(HttpMessageHandler handler)
        => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:8080/") };

    // ---- qBittorrent: the address ban (q7) ----

    [Fact]
    public async Task A_banned_address_is_answered_403_by_the_real_qBittorrent_and_the_sign_in_is_not_tried_again()
    {
        var banned = RecordedReplies.Qbittorrent("login-banned");
        Assert.Equal(401, banned.Exchanges[0].Reply.Status);
        var refusal = banned.Exchanges[5].Reply;
        Assert.Equal(403, refusal.Status);
        Assert.Contains("banned", refusal.Body ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")], (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("auth/login", StringComparison.Ordinal) ? refusal.ToResponse() : null);
        using var http = SignInClient(calls.Build());

        var result = await QbittorrentSignIn.SignInAsync(http, "deluno", "secret", CancellationToken.None, TimeSpan.Zero);

        Assert.False(result.SignedIn);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Equal(1, calls.Count("auth/login"));
        Assert.Equal(0, calls.Count("app/version"));
    }

    [Fact]
    public async Task A_banned_address_gives_no_queue_and_no_pause_and_is_asked_once_per_call()
    {
        var refusal = RecordedReplies.Qbittorrent("login-banned").Exchanges[5].Reply;
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")], (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("auth/login", StringComparison.Ordinal) ? refusal.ToResponse() : null);
        var handler = calls.Build();
        var client = new QbittorrentDownloadClient(() => handler);

        var check = await client.CheckCategoryAsync(Client("qbittorrent", "deluno", "secret"), "movies", CancellationToken.None);
        var pause = await client.ExecuteActionAsync(Client("qbittorrent", "deluno", "secret"), DownloadClientActions.Pause, QbitHash, CancellationToken.None);

        Assert.Equal(DownloadClientCategoryStatuses.Unreachable, check.Status);
        Assert.False(pause.Succeeded);
        Assert.False(calls.Sent("torrents/stop"));
        Assert.False(calls.Sent("torrents/pause"));
    }

    // DEFECT (found by #1301, qbittorrent.q7): qBittorrent's real 403 for a banned address carries the words "Your IP address has been banned after too many
    // failed authentication attempts.", but a 403 on the sign-in is turned into "qBittorrent refused Deluno's username and password; check them in the client's
    // settings", which sends a person with the right password to change it. The ban lifts by itself (an hour, or a restart of qBittorrent).
    [Fact(Skip = "DEFECT: a banned address is told to check the username and password; the recorded 403 says it is banned (ClientCredentials / QbittorrentSignIn read every 403 as a refused login).")]
    public async Task A_banned_address_is_said_to_be_banned_not_to_have_a_wrong_password()
    {
        var refusal = RecordedReplies.Qbittorrent("login-banned").Exchanges[5].Reply;
        var calls = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")], (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("auth/login", StringComparison.Ordinal) ? refusal.ToResponse() : null);
        var handler = calls.Build();

        var pause = await new QbittorrentDownloadClient(() => handler).ExecuteActionAsync(Client("qbittorrent", "deluno", "secret"), DownloadClientActions.Pause, QbitHash, CancellationToken.None);

        Assert.False(pause.Succeeded);
        Assert.Contains("banned", pause.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username and password", pause.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- qBittorrent 4.x: no stop and start (q12) ----

    [Fact]
    public async Task A_qBittorrent_4_that_answers_404_to_stop_and_start_is_paused_and_resumed_with_pause_and_resume()
    {
        var four = RecordedReplies.Qbittorrent("stop-start-missing");
        Assert.Equal(404, four.Exchanges.Single(e => e.Request.Path.EndsWith("torrents/stop", StringComparison.Ordinal)).Reply.Status);
        // The list answers as it did: downloading, paused (asked twice), then running again; the handler gives them in order.
        var infoIndex = 0;
        var infos = four.Exchanges.Where(e => e.Request.Path.Contains("torrents/info", StringComparison.Ordinal)).Select(e => e.Reply).ToArray();
        var handler = new Calls("qbittorrent", [four, RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")], (request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal) ? infos[Math.Min(infoIndex++, infos.Length - 1)].ToResponse() : null);
        var built = handler.Build();
        var client = new QbittorrentDownloadClient(() => built);
        var hash = System.Text.RegularExpressions.Regex.Match(four.Exchanges[1].Request.Path, "hashes=([0-9a-f]+)").Groups[1].Value;

        var paused = await client.ExecuteActionAsync(Client("qbittorrent", "deluno", "secret"), DownloadClientActions.Pause, hash, CancellationToken.None);
        var resumed = await client.ExecuteActionAsync(Client("qbittorrent", "deluno", "secret"), DownloadClientActions.Resume, hash, CancellationToken.None);

        Assert.True(paused.Succeeded, paused.Message);
        Assert.True(resumed.Succeeded, resumed.Message);
        Assert.Equal(1, handler.Count("POST /api/v2/torrents/stop"));
        Assert.Equal(1, handler.Count("POST /api/v2/torrents/pause"));
        Assert.Equal(1, handler.Count("POST /api/v2/torrents/start"));
        Assert.Equal(1, handler.Count("POST /api/v2/torrents/resume"));
        Assert.True(handler.Log.FindIndex(l => l.Contains("torrents/stop", StringComparison.Ordinal)) < handler.Log.FindIndex(l => l.Contains("torrents/pause", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_qBittorrent_5_is_never_sent_the_old_pause_names_because_stop_answers_it()
    {
        var old = RecordedReplies.Qbittorrent("old-pause-resume-names");
        Assert.All(old.Exchanges, e => Assert.Equal(404, e.Reply.Status));
        var stalledHash = "8d878a21c3d4180ae08b4e044904667d9f30bec5";
        var stopped = false;
        var handler = new Calls("qbittorrent", [RecordedReplies.Qbittorrent("stalled"), RecordedReplies.Qbittorrent("paused"), RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")], (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("torrents/stop", StringComparison.Ordinal)) stopped = true;
            return request.RequestUri.AbsolutePath.EndsWith("torrents/info", StringComparison.Ordinal)
                ? (stopped ? RecordedReplies.Qbittorrent("paused") : RecordedReplies.Qbittorrent("stalled")).Find("torrents/info").Reply.ToResponse()
                : null;
        });
        var built = handler.Build();

        var result = await new QbittorrentDownloadClient(() => built).ExecuteActionAsync(Client("qbittorrent", "deluno", "secret"), DownloadClientActions.Pause, stalledHash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(handler.Sent("torrents/pause"));
    }

    // ---- qBittorrent: the queue moves (q40) and force start (q41) ----

    [Theory]
    [InlineData(DownloadClientActions.MoveToTop, "topPrio")]
    [InlineData(DownloadClientActions.MoveUp, "increasePrio")]
    [InlineData(DownloadClientActions.MoveDown, "decreasePrio")]
    [InlineData(DownloadClientActions.MoveToBottom, "bottomPrio")]
    public async Task Each_queue_move_is_sent_to_qBittorrent_on_its_own_endpoint_and_taken_as_done_on_the_recorded_200(string action, string endpoint)
    {
        var recording = RecordedReplies.Qbittorrent("queue-moved");
        Assert.Equal(200, recording.Exchanges.Single(e => e.Request.Path.EndsWith("torrents/" + endpoint, StringComparison.Ordinal)).Reply.Status);
        var calls = new Calls("qbittorrent", [recording, RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")]);
        var handler = calls.Build();

        var result = await new QbittorrentDownloadClient(() => handler).ExecuteActionAsync(Client("qbittorrent", "deluno", "secret"), action, QbitHash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var move = Assert.Single(calls.Log, line => line.Contains("/torrents/", StringComparison.Ordinal));
        Assert.StartsWith("POST /api/v2/torrents/" + endpoint, move, StringComparison.Ordinal);
        Assert.Contains("hashes=" + QbitHash, move, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_priority_qBittorrent_lists_after_a_top_move_is_the_first_place()
    {
        var recording = RecordedReplies.Qbittorrent("queue-moved");
        var before = recording.Exchanges[3].Reply.Json().AsArray().ToDictionary(t => t!["hash"]!.GetValue<string>(), t => t!["priority"]!.GetValue<int>());
        var after = recording.Exchanges[5].Reply.Json().AsArray().ToDictionary(t => t!["hash"]!.GetValue<string>(), t => t!["priority"]!.GetValue<int>());
        var moved = recording.Exchanges[4].Request.Body!.Split('=')[1];

        Assert.NotEqual(1, before[moved]);
        Assert.Equal(1, after[moved]);
    }

    [Fact]
    public async Task Force_start_sends_value_true_and_the_torrent_is_then_listed_forced()
    {
        var recording = RecordedReplies.Qbittorrent("force-start-set");
        var calls = new Calls("qbittorrent", [recording, RecordedReplies.Qbittorrent("sign-in"), RecordedReplies.Qbittorrent("version")]);
        var handler = calls.Build();
        var forcedHash = recording.Exchanges[3].Request.Body!.Split('&')[0].Split('=')[1];

        var result = await new QbittorrentDownloadClient(() => handler).ExecuteActionAsync(Client("qbittorrent", "deluno", "secret"), DownloadClientActions.ForceStart, forcedHash, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var sent = Assert.Single(calls.Log, line => line.Contains("setForceStart", StringComparison.Ordinal));
        Assert.Contains("hashes=" + forcedHash, sent, StringComparison.Ordinal);
        Assert.Contains("value=true", sent, StringComparison.Ordinal);
        var listedAfter = recording.Exchanges[4].Reply.Json().AsArray().Single(t => t!["hash"]!.GetValue<string>() == forcedHash)!;
        Assert.Equal("forcedDL", listedAfter["state"]!.GetValue<string>());
    }

    // ---- SABnzbd and NZBGet paused as a whole (s23, n19) and their queue moves (s31, n27) ----

    [Fact]
    public async Task SABnzbd_paused_as_a_whole_reads_paused_and_mode_resume_releases_it()
    {
        var recording = RecordedReplies.Sabnzbd("client-paused-resumed");
        var calls = new Calls("sabnzbd", [recording]);
        var client = new SabnzbdDownloadClient(new Factory(calls.Build()));

        var result = await client.ResumeClientAsync(Client("sabnzbd", null, "e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0"), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(calls.Log, line => line.Contains("mode=resume", StringComparison.Ordinal) && !line.Contains("name=", StringComparison.Ordinal));
        Assert.True(recording.Exchanges[1].Reply.Json()["queue"]!["paused"]!.GetValue<bool>());
        Assert.False(recording.Exchanges[3].Reply.Json()["queue"]!["paused"]!.GetValue<bool>());
    }

    [Fact]
    public async Task NZBGet_paused_as_a_whole_is_released_with_resumedownload_resumepost_and_resumescan_in_that_order()
    {
        var recording = RecordedReplies.Nzbget("client-paused-resumed");
        var calls = new Calls("nzbget", [recording]);
        var client = new NzbGetDownloadClient(new Factory(calls.Build()));

        var result = await client.ResumeClientAsync(Client("nzbget", "nzbget", "secret"), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.True(result.Succeeded, result.Message);
        var methods = calls.Log.Select(line => System.Text.RegularExpressions.Regex.Match(line, "\"method\":\"([a-z]+)\"").Groups[1].Value).ToArray();
        Assert.Equal(["resumedownload", "resumepost", "resumescan"], methods);
        var status = recording.Exchanges[3].Reply.Json()["result"]!;
        Assert.True(status["DownloadPaused"]!.GetValue<bool>());
        Assert.False(recording.Exchanges[7].Reply.Json()["result"]!["DownloadPaused"]!.GetValue<bool>());
    }

    // DEFECT (found by #1301, sabnzbd.s31): SABnzbd's real answer to mode=queue&name=priority is {"position":N} with no "status", and
    // SabnzbdDownloadClient reads an answer as accepted only when status is true, so every queue move on a real SABnzbd is reported
    // "SABnzbd's answer could not be read, so Deluno cannot tell that it did anything" although SABnzbd did it (the recorded queue shows the new place).
    [Fact(Skip = "DEFECT: a queue move on the real SABnzbd is reported as failed because its answer is {\"position\":N}, not {\"status\":true} (SabnzbdDownloadClient.SendAsync / ReadAnswer).")]
    public async Task SABnzbd_takes_its_recorded_position_answer_to_a_priority_change_as_done()
    {
        var recording = RecordedReplies.Sabnzbd("priority-tiers");
        var calls = new Calls("sabnzbd", [recording]);
        var jobId = System.Text.Json.Nodes.JsonNode.Parse(recording.Exchanges[1].Reply.Body!)!["nzo_ids"]![0]!.GetValue<string>();

        var result = await new SabnzbdDownloadClient(new Factory(calls.Build())).ExecuteActionAsync(Client("sabnzbd", null, "e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0"), DownloadClientActions.MoveToTop, jobId, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
    }

    [Theory]
    [InlineData(DownloadClientActions.MoveToTop, "value2=2")]
    [InlineData(DownloadClientActions.MoveUp, "value2=1")]
    [InlineData(DownloadClientActions.MoveDown, "value2=-2")]
    [InlineData(DownloadClientActions.MoveToBottom, "value2=-2")]
    public async Task SABnzbd_is_sent_the_priority_tier_of_each_move_for_the_job_with_its_key(string action, string tier)
    {
        var recording = RecordedReplies.Sabnzbd("priority-tiers");
        var calls = new Calls("sabnzbd", [recording]);
        var jobId = System.Text.Json.Nodes.JsonNode.Parse(recording.Exchanges[1].Reply.Body!)!["nzo_ids"]![0]!.GetValue<string>();

        await new SabnzbdDownloadClient(new Factory(calls.Build())).ExecuteActionAsync(Client("sabnzbd", null, "e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e0"), action, jobId, CancellationToken.None);

        var sent = Assert.Single(calls.Log, line => line.Contains("name=priority", StringComparison.Ordinal));
        Assert.Contains("mode=queue", sent, StringComparison.Ordinal);
        Assert.Contains("apikey=", sent, StringComparison.Ordinal);
        Assert.Contains("value=" + jobId, sent, StringComparison.Ordinal);
        Assert.Contains(tier, sent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DownloadClientActions.MoveToTop, "GroupMoveTop")]
    [InlineData(DownloadClientActions.MoveUp, "GroupMoveOffset")]
    [InlineData(DownloadClientActions.MoveDown, "GroupMoveOffset")]
    [InlineData(DownloadClientActions.MoveToBottom, "GroupMoveBottom")]
    public async Task NZBGet_moves_a_job_with_its_group_move_command_and_takes_the_recorded_true_as_done(string action, string command)
    {
        var recording = RecordedReplies.Nzbget("queue-moved");
        var calls = new Calls("nzbget", [recording]);

        var result = await new NzbGetDownloadClient(new Factory(calls.Build())).ExecuteActionAsync(Client("nzbget", "nzbget", "secret"), action, "5", CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var sent = Assert.Single(calls.Log, line => line.Contains("editqueue", StringComparison.Ordinal));
        Assert.Contains(command, sent, StringComparison.Ordinal);
        Assert.Contains("[5]", sent, StringComparison.Ordinal);
    }
}
