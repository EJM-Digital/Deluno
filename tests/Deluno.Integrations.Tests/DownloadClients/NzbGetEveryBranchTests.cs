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

    private static string Populated(string method) => method switch
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
        => await AskAll(Populated);

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
        var client = Answering(Populated, out var calls);
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
}
