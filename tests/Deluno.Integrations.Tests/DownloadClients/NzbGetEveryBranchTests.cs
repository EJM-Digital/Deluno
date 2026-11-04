using System.Text.RegularExpressions;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1268: every branch of the NZBGet adapter. The working replies are replayed from NZBGet 26.3's recordings in the other NZBGet
/// test classes; here every call is answered with a reply that says little or has little in it (what a client sends when it is
/// half-working), and the words NZBGet's history statuses are turned into are checked one by one.
/// </summary>
public sealed class NzbGetEveryBranchTests
{
    private const string Hash = "7";

    private static DownloadClientItem Item() => Client("nzbget", "nzbget", "tegbzn6789");

    private static NzbGetDownloadClient Answering(Func<string, string> resultOf, out Calls calls, Func<string, string?>? whole = null)
    {
        var made = new Calls("nzbget", [RecordedReplies.Nzbget("settings")], (_, body) =>
        {
            var method = Regex.Match(body, "\"method\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            return RecordedReplies.Json(whole?.Invoke(method) ?? "{\"version\":\"1.1\",\"result\":" + resultOf(method) + "}");
        });
        calls = made;
        return new NzbGetDownloadClient(new Factory(made.Build()), TimeSpan.Zero, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
    }

    private const string Queued = "{\"NZBID\":7,\"NZBName\":\"Example.Movie.2026\",\"Status\":\"PAUSED\",\"Category\":\"movies\",\"DestDir\":\"/downloads/Example\",\"FileSizeLo\":1000,\"FileSizeHi\":0,\"RemainingSizeLo\":500,\"RemainingSizeHi\":0,\"PausedSizeLo\":500,\"PausedSizeHi\":0}";
    private const string QueuedMegabytes = "{\"NZBID\":8,\"NZBName\":\"Example.Show\",\"Status\":\"DOWNLOADING\",\"FileSizeMB\":4,\"RemainingSizeMB\":2,\"PausedSizeMB\":0}";
    private const string NearlyDone = "{\"NZBID\":9,\"NZBName\":\"Almost\",\"Status\":\"DOWNLOADING\",\"FileSizeLo\":1000,\"FileSizeHi\":0,\"RemainingSizeLo\":1,\"RemainingSizeHi\":0}";

    private static string History(params string[] statuses)
        => "[" + string.Join(",", statuses.Select((status, index) =>
            "{\"NZBID\":" + (7 + index) + ",\"NZBName\":\"Example." + index + "\",\"Name\":\"Example." + index + "\",\"Category\":\"movies\",\"Status\":\"" + status + "\",\"FileSizeMB\":4,\"HistoryTime\":1790000000,\"DestDir\":\"/d\",\"FinalDir\":\"/f\",\"ScriptStatuses\":[{\"Name\":\"Clean.py\",\"Status\":\"FAILURE\"},{\"Name\":\"\",\"Status\":\"WARNING\"},{\"Name\":\"Ok.py\",\"Status\":\"SUCCESS\"}]}")) + "]";

    private static readonly string[] AllStatuses =
    [
        "SUCCESS/ALL", "SUCCESS/UNPACK", "COMPLETED", "SUCCEEDED", "FAILURE/SCRIPT", "WARNING/SCRIPT", "WARNING/SPACE", "WARNING/HEALTH", "FAILURE/HEALTH",
        "FAILURE/PAR", "FAILURE/UNPACK", "FAILURE/MOVE", "FAILURE/BAD", "FAILURE/", "FAILURE/NONE", "DELETED/DUPE", "DELETED/COPY", "DELETED/GOOD",
        "DELETED/HEALTH", "DELETED/SCAN", "DELETED/MANUAL", "DELETED/NONE", "DELETED/", "DELETED/ODD", "Download failed", "disk error", "Import ready", "Weird", ""
    ];

    private static string Populated(string method, int variant = 0) => variant == 0 ? Original(method) : Variant(method, variant);

    private static string Original(string method) => method switch
    {
        "listgroups" => "[" + Queued + "," + QueuedMegabytes + "," + NearlyDone + ",{}]",
        "history" => History(AllStatuses),
        "status" => "{\"DownloadRate\":1000,\"FreeDiskSpaceMB\":5000,\"ServerStandBy\":false,\"DownloadPaused\":true,\"PostPaused\":true,\"ScanPaused\":true}",
        "config" => "[{\"Name\":\"DestDir\",\"Value\":\"${MainDir}/dst\"},{\"Name\":\"MainDir\",\"Value\":\"/downloads\"},{\"Name\":\"InterDir\",\"Value\":\"\"},{\"Name\":\"Category1.Name\",\"Value\":\"movies\"},{\"Name\":\"Category1.DestDir\",\"Value\":\"${MainDir}/movies\"},{\"Name\":\"Category2.Name\",\"Value\":\"tv\"},{\"Name\":\"Category2.DestDir\",\"Value\":\"\"},{\"Value\":\"orphan\"}]",
        "log" => "[{\"ID\":1,\"Kind\":\"ERROR\",\"Text\":\"Paused due to errors in configuration\",\"Time\":10},{\"ID\":2,\"Kind\":\"INFO\",\"Text\":\"Resumed\",\"Time\":20},{\"ID\":3,\"Kind\":\"ERROR\",\"Text\":\"errors in configuration\",\"Time\":5},{\"ID\":4,\"Kind\":\"ERROR\",\"Text\":\"Other error\",\"Time\":30}]",
        "listfiles" => "[{\"Filename\":\"a/b.mkv\"},{\"Filename\":\"\"},{}]",
        "append" => "7",
        "version" => "\"26.3\"",
        "editqueue" or "saveconfig" or "reload" or "scan" or "pausedownload" or "resumedownload" => "true",
        _ => "null"
    };

    private static string Variant(string method, int variant) => (method, variant) switch
    {
        ("listgroups", 1) => "[{\"NZBID\":1,\"NZBName\":\"Huge\",\"Status\":\"DOWNLOADING\",\"FileSizeHi\":2000000000,\"FileSizeLo\":0,\"RemainingSizeHi\":0,\"RemainingSizeLo\":1},"
            + "{\"NZBID\":2,\"Status\":\"PAUSED\"},{\"NZBID\":3,\"NZBName\":\"MB\",\"Status\":\"QUEUED\",\"FileSizeMB\":10,\"RemainingSizeMB\":5,\"PausedSizeMB\":5},"
            + "{\"NZBID\":4,\"FileSizeLo\":100,\"FileSizeHi\":0,\"RemainingSizeLo\":50,\"RemainingSizeHi\":0,\"PausedSizeLo\":50},"
            + "{\"NZBID\":5,\"Status\":\"QUEUED\",\"FileSizeLo\":100,\"RemainingSizeLo\":40,\"PausedSizeLo\":10,\"PausedSizeHi\":1},{\"NZBID\":7,\"Status\":\"Failed\",\"FileSizeMB\":10,\"RemainingSizeMB\":5},"
            + "{\"NZBID\":8,\"Status\":\"QUEUED\",\"FileSizeMB\":10,\"RemainingSizeMB\":5,\"PausedSizeMB\":1},{\"NZBID\":9,\"Status\":\"DOWNLOADING\",\"RemainingSizeMB\":0}]",
        ("history", 1) => "[{},{\"NZBID\":2,\"NZBName\":\"x\",\"Status\":\"SUCCESS/ALL\",\"DestDir\":\"\",\"FinalDir\":\"\"},{\"NZBID\":3,\"Name\":\"y\",\"DestDir\":\"/d\"},{\"NZBID\":7,\"NZBName\":\"z\",\"Status\":\"SUCCESS/ALL\",\"FinalDir\":\"/\"},{\"NZBID\":8,\"Status\":\"FAILURE/BAD\",\"ScriptStatuses\":null}]",
        ("config", 1) => "[{\"Name\":\"DestDir\",\"Value\":\"${Unknown}/dst\"},{\"Name\":\"AppendCategoryDir\",\"Value\":\"no\"},{\"Name\":\"Category1.Name\",\"Value\":\"movies\"},{\"Name\":\"Category2.Name\",\"Value\":\"tv\"},{\"Name\":\"Category2.DestDir\",\"Value\":\"tv\"},{\"Name\":\"Category3.Name\"}]",
        ("config", 2) => "[{\"Name\":\"DestDir\"},{\"Name\":\"Category1.Name\",\"Value\":\"movies\"},{\"Name\":\"Category1.DestDir\",\"Value\":\"/abs/movies\"}]",
        ("log", 1) => "[{\"ID\":1,\"Kind\":\"INFO\",\"Text\":\"Paused due to errors in configuration\",\"Time\":10},{\"ID\":2,\"Kind\":\"INFO\",\"Time\":11},{\"ID\":3,\"Kind\":\"ERROR\",\"Text\":\"  \",\"Time\":12}]",
        ("log", 2) => "[{\"ID\":1,\"Text\":\"errors in configuration\",\"Time\":10},{\"ID\":2,\"Text\":\"nzbget 26.3 server-mode\",\"Time\":20},{\"ID\":3,\"Text\":\"using x nzbget.conf\",\"Time\":21},{\"ID\":4,\"Text\":\"other\",\"Time\":22},{\"ID\":5}]",
        ("status", 4) => "{\"DownloadRate\":0,\"FreeDiskSpaceMB\":0,\"DownloadPaused\":false,\"PostPaused\":false,\"ScanPaused\":false}",
        ("status", 5) => "{\"DownloadPaused\":true,\"PostPaused\":false}",
        ("resumedownload" or "resumepost" or "resumescan", 3) => "false",
        ("append", 3) => "-1",
        _ => Original(method)
    };

    private static string Empty(string method) => method switch
    {
        "listgroups" or "history" or "config" or "log" or "listfiles" => "[]",
        "status" => "{}",
        "append" => "0",
        "editqueue" or "saveconfig" or "reload" => "false",
        _ => "null"
    };

    private static async Task AskAll(Func<string, string> resultOf, Func<string, string?>? whole = null, Func<DownloadClientItem, DownloadClientItem>? shape = null)
    {
        foreach (var hash in new[] { Hash, "x" })
        {
            var client = Answering(resultOf, out _, whole);
            await EveryCallOnAnOddReply.AskAsync(client, (shape ?? (item => item))(Item()), usenet: true, hash);
        }
    }

    [Fact]
    public async Task No_question_throws_when_the_client_answers_every_call_with_nothing_in_it()
        => await AskAll(Empty);

    [Fact]
    public async Task No_question_throws_when_the_client_answers_every_call_with_something_in_it_of_every_kind()
        => await AskAll(method => Populated(method));

    [Fact]
    public async Task No_question_throws_when_the_client_answers_every_call_with_the_odd_things_a_real_one_can_hold()
    {
        foreach (var variant in new[] { 1, 2, 3, 4, 5 })
        {
            await AskAll(method => Populated(method, variant));
        }
    }

    [Fact]
    public async Task A_category_check_needs_a_name_and_an_address()
    {
        var client = Answering(method => Populated(method), out _);

        var blank = await client.CheckCategoryAsync(Item(), " ", CancellationToken.None);
        var noAddress = await client.CheckCategoryAsync(Item() with { Host = null, EndpointUrl = null }, "movies", CancellationToken.None);
        var folders = new List<DownloadClientCategoryCheckResult>();
        foreach (var variant in new[] { 1, 2 })
        {
            var configured = Answering(method => Populated(method, variant), out _);
            foreach (var name in new[] { "movies", "tv", "none" })
            {
                folders.Add(await configured.CheckCategoryAsync(Item(), name, CancellationToken.None));
            }
        }

        Assert.Contains("Enter a category", blank.Message);
        Assert.Contains("address", noAddress.Message);
        Assert.Equal(6, folders.Count);
    }

    [Fact]
    public async Task No_question_throws_when_the_client_answers_every_call_with_no_result_an_error_or_a_bare_null()
    {
        await AskAll(_ => "null", whole: _ => "null");
        await AskAll(_ => "null", whole: _ => "{\"version\":\"1.1\",\"error\":{\"code\":1,\"message\":\"Invalid procedure\"}}");
        await AskAll(_ => "null", whole: _ => "{\"version\":\"1.1\",\"error\":{\"code\":1}}");
    }

    [Fact]
    public async Task No_question_throws_when_the_client_drops_the_connection_for_each_call_in_turn()
    {
        // Side by side: a removal waits for the client to let go of the job.
        await Task.WhenAll(new[] { "listgroups", "history", "status", "config", "log", "listfiles", "append", "version", "editqueue", "saveconfig", "reload" }.Select(dropped =>
        {
            var made = new Calls("nzbget", [RecordedReplies.Nzbget("settings")], (_, body) =>
            {
                var method = Regex.Match(body, "\"method\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                return method == dropped ? throw new HttpRequestException("The connection dropped.") : RecordedReplies.Json("{\"version\":\"1.1\",\"result\":" + Populated(method) + "}");
            });

            return EveryCallOnAnOddReply.AskAsync(new NzbGetDownloadClient(new Factory(made.Build()), TimeSpan.Zero, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10)), Item(), usenet: true, Hash);
        }));
    }

    [Fact]
    public async Task Nothing_is_asked_without_an_address()
    {
        var client = Answering(method => Populated(method), out var calls);
        var noAddress = Item() with { Host = null, EndpointUrl = null };

        var grab = await client.GrabAsync(noAddress, EveryCallOnAnOddReply.GrabRequests(true).First(), CancellationToken.None);
        var snapshot = await client.GetSnapshotAsync(noAddress, DateTimeOffset.UtcNow, CancellationToken.None);
        var action = await client.ExecuteActionAsync(noAddress, DownloadClientActions.Pause, Hash, CancellationToken.None);
        var resume = await client.ResumeClientAsync(noAddress, CancellationToken.None);
        var history = await client.GetHistoryAsync(noAddress, DateTimeOffset.UtcNow, CancellationToken.None);
        var provider = await client.SetUsenetProviderAsync(noAddress, new Deluno.Connections.Usenet.UsenetProviderRequest("news.example", 563, true, "u", "p", 8), CancellationToken.None);
        var has = await client.HasUsenetProviderAsync(noAddress, CancellationToken.None);
        var check = await client.CheckKeepSeedingCategoryAsync(noAddress, "seeding", CancellationToken.None);
        var blank = await client.CheckCategoryAsync(Item(), " ", CancellationToken.None);

        Assert.False(grab.Succeeded);
        Assert.Contains("address", snapshot!.LastHealthMessage);
        Assert.False(action.Succeeded);
        Assert.False(resume.Succeeded);
        Assert.Empty(history);
        Assert.NotNull(provider);
        Assert.Null(has);
        Assert.NotNull(check);
        Assert.NotNull(blank);
        Assert.Empty(calls.Log);
    }

    [Theory]
    [InlineData("SUCCESS/ALL", "completed")]
    [InlineData("COMPLETED", "completed")]
    [InlineData("FAILURE/SCRIPT", null)]
    [InlineData("WARNING/SCRIPT", null)]
    [InlineData("WARNING/SPACE", null)]
    [InlineData("DELETED/DUPE", null)]
    [InlineData("DELETED/GOOD", null)]
    [InlineData("FAILURE/HEALTH", "failed")]
    [InlineData("DELETED/MANUAL", "failed")]
    [InlineData("Download failed", "failed")]
    [InlineData("disk error", "failed")]
    [InlineData("Import ready", "importready")]
    [InlineData("Weird", "weird")]
    [InlineData("", "unknown")]
    public void A_history_status_is_read_as_one_outcome(string status, string? expected)
    {
        var outcome = NzbGetDownloadClient.NormalizeOutcome(status);

        Assert.False(string.IsNullOrWhiteSpace(outcome));
        if (expected is not null) Assert.Equal(expected, outcome, ignoreCase: true);
    }

    [Fact]
    public void Every_reason_a_history_status_can_carry_is_said_in_words()
    {
        foreach (var status in AllStatuses)
        {
            Assert.False(string.IsNullOrWhiteSpace(NzbGetDownloadClient.FailureReason(status)));
            Assert.False(string.IsNullOrWhiteSpace(NzbGetDownloadClient.DuplicateReason(status)));
            Assert.False(string.IsNullOrWhiteSpace(NzbGetDownloadClient.AttentionReason(status)));
        }

        foreach (var detail in new[] { "HEALTH", "PAR", "UNPACK", "MOVE", "SCRIPT", "", "NONE", "ODD", "DUPE", "COPY", "GOOD", "SCAN", "MANUAL", "SPACE" })
        {
            Assert.Contains("NZBGet", NzbGetDownloadClient.FailureReason("FAILURE/" + detail));
            Assert.Contains("NZBGet", NzbGetDownloadClient.FailureReason("WARNING/" + detail));
            Assert.Contains("NZBGet", NzbGetDownloadClient.FailureReason("DELETED/" + detail));
        }
    }

    [Fact]
    public void A_script_that_failed_or_warned_is_named_and_none_at_all_is_said_plainly()
    {
        Assert.Contains("Clean.py", NzbGetDownloadClient.ScriptReason([new("Ok.py", "SUCCESS"), new("Clean.py", "FAILURE")]));
        Assert.Contains("warned", NzbGetDownloadClient.ScriptReason([new("Clean.py", "WARNING")]));
        Assert.Contains("post-processing script", NzbGetDownloadClient.ScriptReason([new("", "FAILURE")]));
        Assert.Contains("post-processing script", NzbGetDownloadClient.ScriptReason([new("Ok.py", "SUCCESS")]));
        Assert.Contains("post-processing script", NzbGetDownloadClient.ScriptReason(null));
    }

    [Fact]
    public void A_size_comes_from_the_halves_or_whole_megabytes_or_the_high_half_alone()
    {
        Assert.Equal((1L << 32) + 5, NzbGetDownloadClient.SizeBytes(1, 5, null));
        Assert.Equal(3 * 1_048_576L, NzbGetDownloadClient.SizeBytes(0, null, 3));
        Assert.Equal(2L << 32, NzbGetDownloadClient.SizeBytes(2, null, null));
    }

    private static string Degraded(string how, string method) => how switch
    {
        "null" => "null",
        "nullresult" => "{\"version\":\"1.1\",\"result\":null}",
        "empty" => "{\"version\":\"1.1\",\"result\":" + Empty(method) + "}",
        "false" => "{\"version\":\"1.1\",\"result\":false}",
        "error" => "{\"version\":\"1.1\",\"error\":{\"code\":1,\"message\":\"Invalid procedure\"}}",
        "nomessage" => "{\"version\":\"1.1\",\"error\":{\"code\":1}}",
        _ => "{\"version\":\"1.1\",\"result\":" + Populated(method) + "}"
    };

    [Fact]
    public async Task No_question_throws_when_one_call_in_turn_is_answered_with_less_than_the_rest()
    {
        var asks = new List<Task>();
        foreach (var target in new[] { "listgroups", "history", "status", "config", "log", "listfiles", "append", "version", "editqueue", "saveconfig", "reload" })
        {
            foreach (var how in new[] { "null", "nullresult", "empty", "false", "error", "nomessage", "drop" })
            {
                foreach (var hash in new[] { Hash, "9", "x" })
                {
                    var made = new Calls("nzbget", [RecordedReplies.Nzbget("settings")], (_, body) =>
                    {
                        var method = Regex.Match(body, "\"method\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                        if (method != target) return RecordedReplies.Json(Degraded("full", method));
                        return how == "drop" ? throw new HttpRequestException("The connection dropped.") : RecordedReplies.Json(Degraded(how, method));
                    });
                    asks.Add(EveryCallOnAnOddReply.AskAsync(new NzbGetDownloadClient(new Factory(made.Build()), TimeSpan.Zero, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10)), Item(), usenet: true, hash));
                }
            }
        }

        await Task.WhenAll(asks);
    }

    [Theory]
    [InlineData(DownloadClientActions.Delete, true, true)]
    [InlineData(DownloadClientActions.DeleteWithData, true, true)]
    [InlineData(DownloadClientActions.DeleteWithData, false, true)]
    [InlineData(DownloadClientActions.DeleteWithData, true, false)]
    [InlineData(DownloadClientActions.RemoveRefusedCopy, false, true)]
    public async Task A_finished_job_is_removed_with_its_own_folder_or_the_folder_left_is_said(string action, bool queuedFirst, bool folderNamed)
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-nzbget-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "Example.Movie");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "film.mkv"), "x");
        try
        {
            var deleted = false;
            var finalDeleted = false;
            var made = new Calls("nzbget", [RecordedReplies.Nzbget("settings")], (_, body) =>
            {
                var method = EveryCallOnAnOddReply.MethodOf(body);
                if (method == "editqueue")
                {
                    if (body.Contains("FinalDelete", StringComparison.Ordinal)) finalDeleted = true;
                    else deleted = true;
                    return RecordedReplies.Json("{\"version\":\"1.1\",\"result\":true}");
                }

                var job = "{\"NZBID\":7,\"NZBName\":\"Example.Movie\",\"Name\":\"Example.Movie\",\"Status\":\"SUCCESS/ALL\",\"DestDir\":" + System.Text.Json.JsonSerializer.Serialize(folderNamed ? folder : string.Empty) + ",\"FinalDir\":\"\"}";
                return RecordedReplies.Json("{\"version\":\"1.1\",\"result\":" + method switch
                {
                    "listgroups" => queuedFirst && !deleted ? "[" + Queued + "]" : "[]",
                    "history" => finalDeleted || (queuedFirst && !deleted) ? "[]" : "[" + job + "]",
                    _ => Populated(method)
                } + "}");
            });
            var client = new NzbGetDownloadClient(new Factory(made.Build()), TimeSpan.Zero, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));

            var result = await client.ExecuteActionAsync(Item(), action, Hash, CancellationToken.None);

            Assert.NotNull(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
