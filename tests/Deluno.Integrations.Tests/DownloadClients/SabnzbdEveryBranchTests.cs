using System.Net;
using System.Text.Json.Nodes;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1268: every branch of the SABnzbd adapter. The working replies are replayed from SABnzbd 5.1.3's recordings in the other SABnzbd
/// test classes; here every call is answered with a reply that says little or has little in it (what a client sends when it is
/// half-working), and the words SABnzbd's history statuses are turned into are checked one by one.
/// </summary>
public sealed class SabnzbdEveryBranchTests
{
    private const string Hash = "SABnzbd_nzo_abc123";

    private static DownloadClientItem Item() => Client("sabnzbd", null, "0123456789abcdef");

    private static SabnzbdDownloadClient Answering(Func<HttpRequestMessage, HttpResponseMessage> answer, out Calls calls)
    {
        var made = new Calls("sabnzbd", [RecordedReplies.Sabnzbd("settings")], (request, _) => answer(request));
        calls = made;
        return new SabnzbdDownloadClient(new Factory(made.Build()), TimeSpan.Zero);
    }

    private static string Mode(HttpRequestMessage request)
        => System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["mode"] ?? string.Empty;

    private static readonly string[] HistoryStatuses =
    [
        "Completed", "Succeeded", "Success", "Failed", "Failed ", "Error", "Verifying", "Repairing", "Extracting", "Running Script", "Import ready", "Weird", ""
    ];

    private static readonly string[] FailMessages =
    [
        "", "Duplicate NZB", "Unpacking failed, disk full", "Script was aborted, please check script_log for more information.", "Aborted, cannot be completed",
        "Warning: script returned an error", "Out of retention", "Encrypted RAR", "Repairing failed, Disk full"
    ];

    private static string HistorySlots()
    {
        var slots = new List<string>();
        foreach (var status in HistoryStatuses)
        {
            foreach (var message in FailMessages)
            {
                slots.Add("{\"nzo_id\":\"SABnzbd_nzo_" + slots.Count + "\",\"name\":\"Example." + slots.Count + "\",\"category\":\"movies\",\"status\":\"" + status + "\",\"bytes\":123456,\"completed\":1790000000,\"fail_message\":\"" + message + "\",\"storage\":\"/complete/Example\",\"script\":\"" + (slots.Count % 3 == 0 ? "none" : slots.Count % 3 == 1 ? "Clean.py" : "") + "\"}");
            }
        }

        slots.Add("{\"nzo_id\":\"SABnzbd_nzo_bytes\",\"name\":\"x\",\"status\":\"Completed\",\"bytes\":\"1234\",\"completed\":1}");
        slots.Add("{\"nzo_id\":\"SABnzbd_nzo_none\"}");
        return "[" + string.Join(",", slots) + "]";
    }

    private static readonly string Populated = "{\"status\":true,\"version\":\"5.1.3\","
        + "\"queue\":{\"speed\":\"1.2 M\",\"paused\":true,\"have_warnings\":\"2\",\"diskspace1\":\"12.5\",\"diskspacetotal1\":\"100\",\"slots\":["
        + "{\"nzo_id\":\"SABnzbd_nzo_abc123\",\"filename\":\"Example.Movie.2026\",\"cat\":\"movies\",\"status\":\"Paused\",\"mb\":\"1000.0\",\"mbleft\":\"500.0\",\"timeleft\":\"0:10:00\"},"
        + "{\"nzo_id\":\"SABnzbd_nzo_2\",\"filename\":\"Almost\",\"cat\":\"*\",\"status\":\"Downloading\",\"mb\":\"1000.0\",\"mbleft\":\"0.5\",\"timeleft\":\"10:00\"},"
        + "{\"filename\":\"No id\",\"status\":\"Failed\",\"mb\":\"x\",\"mbleft\":\"y\",\"timeleft\":\"z\"},"
        + "{\"nzo_id\":\"SABnzbd_nzo_3\",\"status\":\"Queued\",\"mb\":\"0\",\"mbleft\":\"0\"},{}]},"
        + "\"history\":{\"slots\":" + "HISTORY" + "},"
        + "\"files\":[{\"filename\":\"a/b.mkv\"},{\"filename\":\"\"},{}],"
        + "\"warnings\":[\"A warning\",{\"text\":\"Another\"},{\"text\":null},{}],"
        + "\"categories\":[\"*\",\"movies\",\"\",5],"
        + "\"config\":{\"misc\":{\"complete_dir\":\"/complete\",\"download_dir\":\"/incomplete\"},\"categories\":[{\"name\":\"movies\",\"dir\":\"movies\"},{\"name\":\"tv\",\"dir\":\"\"},{\"name\":\"*\",\"dir\":\"/abs\"},{\"name\":5,\"dir\":5},7,{\"name\":\"\"}],\"servers\":[{\"host\":\"news.example\",\"enable\":1}]},"
        + "\"error\":null}";

    private static string PopulatedBody() => Populated.Replace("HISTORY", HistorySlots(), StringComparison.Ordinal);

    public static readonly string[] OddReplies =
    [
        "null",
        "{}",
        "[]",
        "ok",
        "{\"status\":true}",
        "{\"status\":false}",
        "{\"status\":false,\"error\":\"API Key Incorrect\"}",
        "{\"status\":false,\"error\":5}",
        "{\"queue\":{}}",
        "{\"queue\":{\"slots\":[{}]}}",
        "{\"history\":{}}",
        "{\"history\":{\"slots\":[{}]}}",
        "{\"version\":\"\"}",
        "{\"files\":[]}",
        "{\"warnings\":5}",
        "{\"categories\":{\"movies\":1}}",
        "{\"categories\":5}",
        "{\"config\":{}}",
        "{\"config\":{\"categories\":5,\"misc\":{}}}",
        "{\"queue\":{\"paused\":false,\"have_warnings\":\"0\",\"diskspace1\":\"-1\",\"slots\":[]}}"
    ];

    [Fact]
    public async Task No_question_throws_when_the_client_answers_every_call_with_a_reply_that_says_little()
        // Side by side: a half-answered removal waits for the client to let go of the job.
        => await Task.WhenAll(OddReplies.Select(reply =>
            EveryCallOnAnOddReply.AskAsync(Answering(_ => RecordedReplies.Json(reply), out _), Item(), usenet: true, Hash)));

    [Fact]
    public async Task No_question_throws_when_the_client_answers_every_call_with_everything_in_it()
    {
        var body = PopulatedBody();
        await Task.WhenAll(new[] { Hash, "SABnzbd_nzo_2", "SABnzbd_nzo_5", "nothing" }.Select(hash =>
            EveryCallOnAnOddReply.AskAsync(Answering(_ => RecordedReplies.Json(body), out _), Item(), usenet: true, hash)));
    }

    [Fact]
    public async Task No_question_throws_when_the_client_fails_or_drops_the_connection_for_each_mode_in_turn()
    {
        var body = PopulatedBody();
        var modes = new[] { "queue", "history", "version", "get_config", "get_files", "warnings", "resume", "pause", "set_config", "addurl", "delete", "change_cat", "get_cats" };
        await Task.WhenAll(modes.SelectMany(mode => new[] { true, false }.Select(drop =>
            EveryCallOnAnOddReply.AskAsync(Answering(request =>
                Mode(request) != mode ? RecordedReplies.Json(body)
                : drop ? throw new HttpRequestException("The connection dropped.")
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("Server error") }, out _), Item(), usenet: true, Hash))));
    }

    [Fact]
    public async Task Nothing_is_asked_without_an_address_or_a_key()
    {
        var client = Answering(_ => RecordedReplies.Json(PopulatedBody()), out var calls);
        var noAddress = Item() with { Host = null, EndpointUrl = null };
        var noKey = Item() with { Secret = null, Username = null };
        var request = new Deluno.Connections.Usenet.UsenetProviderRequest("news.example", 563, true, "u", "p", 8);

        var grab = await client.GrabAsync(noAddress, EveryCallOnAnOddReply.GrabRequests(true).First(), CancellationToken.None);
        var snapshot = await client.GetSnapshotAsync(noAddress, DateTimeOffset.UtcNow, CancellationToken.None);
        var keyless = await client.GetSnapshotAsync(noKey, DateTimeOffset.UtcNow, CancellationToken.None);
        var action = await client.ExecuteActionAsync(noAddress, DownloadClientActions.Pause, Hash, CancellationToken.None);
        var provider = await client.SetUsenetProviderAsync(noAddress, request, CancellationToken.None);
        var keylessProvider = await client.SetUsenetProviderAsync(noKey, request, CancellationToken.None);
        var has = await client.HasUsenetProviderAsync(noKey, CancellationToken.None);
        var resume = await client.ResumeClientAsync(noKey, CancellationToken.None);
        var history = await client.GetHistoryAsync(noKey, DateTimeOffset.UtcNow, CancellationToken.None);
        var warnings = await client.ClearWarningsAsync(noKey, CancellationToken.None);
        var files = await client.GetFilesAsync(noKey, Hash, CancellationToken.None);
        var noId = await client.GetFilesAsync(Item(), " ", CancellationToken.None);
        var blank = await client.CheckCategoryAsync(Item(), " ", CancellationToken.None);
        var check = await client.CheckCategoryAsync(noKey, "movies", CancellationToken.None);

        Assert.False(grab.Succeeded);
        Assert.Contains("address", snapshot!.LastHealthMessage);
        Assert.Contains("API key", keyless!.LastHealthMessage);
        Assert.False(action.Succeeded);
        Assert.False(provider!.Saved);
        Assert.False(keylessProvider!.Saved);
        Assert.Null(has);
        Assert.False(resume.Succeeded);
        Assert.Empty(history);
        Assert.False(warnings);
        Assert.Null(files);
        Assert.Null(noId);
        Assert.Equal(DownloadClientCategoryStatuses.Configuration, blank.Status);
        Assert.Equal(DownloadClientCategoryStatuses.Configuration, check.Status);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public async Task A_news_server_the_client_does_not_keep_is_said_and_one_that_takes_a_moment_is_asked_for_again()
    {
        var shown = 0;
        var slow = Answering(request =>
        {
            if (Mode(request) == "get_config" && ++shown < 3) return RecordedReplies.Json("{\"config\":{\"servers\":[]}}");
            return RecordedReplies.Json(PopulatedBody());
        }, out _);
        var never = Answering(request => Mode(request) == "get_config" ? RecordedReplies.Json("{\"config\":{\"servers\":[]}}") : RecordedReplies.Json("{\"status\":true}"), out _);
        var refused = Answering(request => Mode(request) == "set_config" ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("") } : RecordedReplies.Json("{}"), out _);
        var request = new Deluno.Connections.Usenet.UsenetProviderRequest("news.example", 563, true, "u", "p", 8);

        var kept = await slow.SetUsenetProviderAsync(Item(), request, CancellationToken.None);
        var dropped = await never.SetUsenetProviderAsync(Item(), request, CancellationToken.None);
        var turnedDown = await refused.SetUsenetProviderAsync(Item(), request, CancellationToken.None);

        Assert.NotNull(kept);
        Assert.False(dropped!.Saved);
        Assert.False(turnedDown!.Saved);
    }

    [Theory]
    [InlineData("Completed", "")]
    [InlineData("Completed", "Script returned a warning")]
    [InlineData("Failed", "Duplicate NZB")]
    [InlineData("Failed", "disk full")]
    [InlineData("Failed", "Script was aborted")]
    [InlineData("Failed", "")]
    [InlineData("Running", "")]
    [InlineData("Import ready", "")]
    [InlineData("Weird", "")]
    [InlineData("", "")]
    public void A_history_status_is_read_as_one_outcome_and_a_reason_in_words(string status, string message)
    {
        var outcome = SabnzbdDownloadClient.NormalizeHistoryOutcome(status, message);

        Assert.False(string.IsNullOrWhiteSpace(outcome));
    }

    [Fact]
    public async Task A_history_of_every_status_is_read_without_a_throw()
    {
        var client = Answering(_ => RecordedReplies.Json(PopulatedBody()), out _);

        var history = await client.GetHistoryAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(history.Count > 50);
        Assert.Contains(history, item => !string.IsNullOrWhiteSpace(item.ErrorMessage));
    }

    [Fact]
    public void A_status_nobody_gave_is_read_as_a_word()
    {
        Assert.False(string.IsNullOrWhiteSpace(new SabnzbdDownloadClient(new Factory(new HttpClientHandler())).NormalizeStatus(null, null)));
        _ = JsonNode.Parse("{}");
    }

    [Fact]
    public async Task No_question_throws_when_one_mode_in_turn_is_answered_with_less_than_the_rest()
    {
        var body = PopulatedBody();
        var degraded = new[]
        {
            "null", "{}", "{\"status\":false}", "{\"status\":false,\"error\":\"boom\"}", "{\"queue\":null,\"history\":null}", "{\"queue\":{\"slots\":null}}",
            "{\"history\":{\"slots\":null}}", "{\"history\":{\"slots\":[{\"nzo_id\":\"SABnzbd_nzo_abc123\",\"status\":\"Failed\",\"fail_message\":\"x\"}]},\"queue\":{\"slots\":[]}}",
            "{\"queue\":{\"slots\":[{\"nzo_id\":\"SABnzbd_nzo_abc123\",\"status\":\"Paused\"}]},\"history\":{\"slots\":[]}}", "{\"version\":\"5.1.3\"}", "{\"version\":null}",
            "{\"config\":{\"misc\":{\"complete_dir\":5},\"categories\":[]},\"categories\":[]}", "{\"config\":null}", "{\"config\":{\"misc\":{},\"categories\":[{\"name\":\"movies\"}]}}"
        };
        var asks = new List<Task>();
        foreach (var mode in new[] { "queue", "history", "version", "get_config", "get_files", "warnings", "resume", "pause", "set_config", "addurl", "delete", "change_cat", "get_cats" })
        {
            foreach (var reply in degraded)
            {
                foreach (var hash in new[] { Hash, "SABnzbd_nzo_2", "nothing" })
                {
                    asks.Add(EveryCallOnAnOddReply.AskAsync(Answering(request => RecordedReplies.Json(Mode(request) == mode ? reply : body), out _), Item(), usenet: true, hash));
                }
            }
        }

        await Task.WhenAll(asks);
    }

    [Theory]
    [InlineData("1.5 GB", 1500d)]
    [InlineData("2G", 2000d)]
    [InlineData("300KB", 0.3d)]
    [InlineData("4K", 0.004d)]
    [InlineData("512B", 0.000512d)]
    [InlineData("7MB", 7d)]
    [InlineData("8M", 8d)]
    [InlineData("1.2 M/s", 1.2d)]
    [InlineData("9", 9d)]
    [InlineData("x", 0d)]
    [InlineData("-1", 0d)]
    [InlineData("", 0d)]
    [InlineData(null, 0d)]
    public void A_size_or_speed_is_read_as_decimal_megabytes(string? text, double expected)
        => Assert.Equal(expected, SabnzbdDownloadClient.ParseMegabytes(text), 6);

    [Fact]
    public void The_reasons_a_history_row_carries_are_said_with_or_without_the_clients_own_words()
    {
        Assert.Contains("Clean.py", SabnzbdDownloadClient.ScriptReason("Clean.py", isFailure: true));
        Assert.Contains("warned", SabnzbdDownloadClient.ScriptReason("Clean.py", isFailure: false));
        Assert.Contains("post-processing script", SabnzbdDownloadClient.ScriptReason("None", isFailure: true));
        Assert.Contains("post-processing script", SabnzbdDownloadClient.ScriptReason("", isFailure: true));
        Assert.Contains("post-processing script", SabnzbdDownloadClient.ScriptReason(null, isFailure: false));
        Assert.Contains("it was a duplicate", SabnzbdDownloadClient.DuplicateReason(null));
        Assert.Contains("Duplicate NZB", SabnzbdDownloadClient.DuplicateReason("Duplicate NZB"));
        Assert.Contains("free space, then retry", SabnzbdDownloadClient.AttentionReason(" "));
        Assert.Contains("Unpacking failed", SabnzbdDownloadClient.AttentionReason("Unpacking failed, disk full"));
        Assert.Contains("could not download it.", SabnzbdDownloadClient.FailureReason(null));
        Assert.Contains("because", SabnzbdDownloadClient.FailureReason("because"));
    }

    [Fact]
    public async Task A_job_with_a_sliver_left_never_reads_one_hundred_percent_and_odd_answers_are_read_kindly()
    {
        var queue = "{\"queue\":{\"slots\":[{\"nzo_id\":\"a\",\"filename\":\"Huge\",\"status\":\"Downloading\",\"mb\":\"10000000000\",\"mbleft\":\"0.000001\"}]}}";
        var client = Answering(request => RecordedReplies.Json(Mode(request) == "queue" ? queue : "{\"status\":\"yes\",\"error\":null}"), out _);

        var snapshot = await client.GetSnapshotAsync(Item(), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(snapshot!.Queue.Single().Progress < 100d);
        await EveryCallOnAnOddReply.AskAsync(client, Item(), usenet: true, Hash);
    }

    [Fact]
    public async Task A_category_check_reads_every_kind_of_folder_the_client_can_hold()
    {
        var shapes = new[]
        {
            "{\"config\":{\"misc\":{\"complete_dir\":\"/c\",\"download_dir\":\"/i\"},\"categories\":[{\"name\":\"movies\",\"dir\":\"\"},{\"name\":\"tv\",\"dir\":\"/abs/tv\"},{\"name\":5},{\"dir\":\"x\"}]}}",
            "{\"config\":{\"misc\":{},\"categories\":[{\"name\":\"movies\",\"dir\":\"\"},{\"name\":\"tv\",\"dir\":\"abs\"}]}}",
            "{\"config\":{\"misc\":5,\"categories\":[{\"name\":\"movies\"}]}}",
            "{\"config\":5}",
            "[]"
        };
        foreach (var shape in shapes)
        {
            var client = Answering(_ => RecordedReplies.Json(shape), out _);
            foreach (var name in new[] { "movies", "tv", "other" })
            {
                Assert.NotNull(await client.CheckCategoryAsync(Item(), name, CancellationToken.None));
            }
        }
    }

    [Fact]
    public async Task A_removal_the_queue_would_not_do_and_the_history_could_not_be_asked_about_is_said()
    {
        var body = PopulatedBody();
        var queueNo = Answering(request => Mode(request) == "history" ? throw new HttpRequestException("The connection dropped.")
            : Mode(request) == "queue" && request.RequestUri!.Query.Contains("name=delete", StringComparison.Ordinal) ? RecordedReplies.Json("{\"status\":false}") : RecordedReplies.Json(body), out _);
        var neither = Answering(request => request.RequestUri!.Query.Contains("name=delete", StringComparison.Ordinal) ? throw new HttpRequestException("The connection dropped.") : RecordedReplies.Json(body), out _);
        var teapot = Answering(request => request.RequestUri!.Query.Contains("name=delete", StringComparison.Ordinal) ? new HttpResponseMessage((HttpStatusCode)199) { Content = new StringContent("") } : RecordedReplies.Json(body), out _);
        var historyDown = Answering(request => Mode(request) == "history" && !request.RequestUri!.Query.Contains("name=delete", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : request.RequestUri!.Query.Contains("name=delete", StringComparison.Ordinal) ? RecordedReplies.Json("{\"status\":false}") : RecordedReplies.Json(body), out _);

        foreach (var client in new[] { queueNo, neither, teapot, historyDown })
        {
            foreach (var action in new[] { DownloadClientActions.Delete, DownloadClientActions.DeleteWithData, DownloadClientActions.RemoveRefusedCopy })
            {
                Assert.NotNull(await client.ExecuteActionAsync(Item(), action, "SABnzbd_nzo_2", CancellationToken.None));
            }
        }
    }

    [Fact]
    public async Task A_news_server_given_with_no_user_or_password_is_written_as_it_is()
    {
        var client = Answering(_ => RecordedReplies.Json(PopulatedBody()), out var calls);

        await client.SetUsenetProviderAsync(Item(), new Deluno.Connections.Usenet.UsenetProviderRequest("news.example", 119, false, null, null, 4), CancellationToken.None);

        Assert.True(calls.Sent("ssl=0"));
    }
}
