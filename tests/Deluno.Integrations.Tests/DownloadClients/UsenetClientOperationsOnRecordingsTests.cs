using System.Web;
using Deluno.Connections.Contracts;
using Deluno.Integrations.DownloadClients;
using Deluno.Integrations.DownloadClients.Clients;
using Deluno.Tests.Recorded;
using static Deluno.Integrations.Tests.DownloadClients.RecordedClientCalls;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #1301: the operations of SABnzbd 5.1.3 and NZBGet 26.3 that the register lists as gaps, replayed from their recorded
/// replies: what Deluno sends on a grab (name, category, priority, password), what it does with no key saved, and that a
/// keep-seeding move is refused on a Usenet client without a word sent to it.
/// </summary>
public sealed class UsenetClientOperationsOnRecordingsTests
{
    private const string Url = "http://127.0.0.1:1180/api?t=get&id=golden6925692986&apikey=stub";

    private static DownloadClientGrabRequest Grab(string name = "Recorded.Queued.A", int? priority = null, string? password = null)
        => new(name, Url, "movie", "movies", "Example Indexer") { Priority = priority, Password = password };

    private static Dictionary<string, string?> QueryOf(string logLine)
    {
        var path = logLine.Split(' ', 3)[1];
        var query = HttpUtility.ParseQueryString(path[(path.IndexOf('?') + 1)..]);
        return query.AllKeys.ToDictionary(key => key!, key => query[key]);
    }

    // ---------------------------------------------------------------- SABnzbd

    [Fact]
    public async Task A_sabnzbd_grab_sends_addurl_with_the_url_the_release_name_and_the_category_and_keeps_the_job_id()
    {
        var calls = new Calls("sabnzbd", [RecordedReplies.Sabnzbd("queued")]);
        var handler = calls.Build();

        var result = await new SabnzbdDownloadClient(new Factory(handler))
            .GrabAsync(Client("sabnzbd", secret: "key"), Grab(), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("08531720-7308-4867-9ec9-439b73b17d1f", result.ExternalId);
        var sent = QueryOf(Assert.Single(calls.Log));
        Assert.Equal("addurl", sent["mode"]);
        Assert.Equal(Url, sent["name"]);
        Assert.Equal("Recorded.Queued.A", sent["nzbname"]);
        Assert.Equal("movies", sent["cat"]);
        Assert.False(sent.ContainsKey("password"), "no password is sent when Deluno has none");
        Assert.False(sent.ContainsKey("priority"), "no priority is forced when none was asked for");
    }

    [Fact]
    public async Task A_sabnzbd_grab_sends_a_priority_only_when_asked_and_a_password_only_when_the_release_has_one()
    {
        var calls = new Calls("sabnzbd", [RecordedReplies.Sabnzbd("queued")]);
        var handler = calls.Build();
        var client = new SabnzbdDownloadClient(new Factory(handler));

        await client.GrabAsync(Client("sabnzbd", secret: "key"), Grab(priority: 5, password: "pa55"), CancellationToken.None);
        await client.GrabAsync(Client("sabnzbd", secret: "key"), Grab(priority: -3), CancellationToken.None);
        await client.GrabAsync(Client("sabnzbd", secret: "key") with { AddPaused = true }, Grab(priority: 5), CancellationToken.None);

        var high = QueryOf(calls.Log[0]);
        var low = QueryOf(calls.Log[1]);
        var paused = QueryOf(calls.Log[2]);
        Assert.Equal("1", high["priority"]);
        Assert.Equal("pa55", high["password"]);
        Assert.Equal("-1", low["priority"]);
        Assert.False(low.ContainsKey("password"));
        Assert.Equal("-2", paused["priority"]);
    }

    [Fact]
    public async Task With_no_api_key_saved_sabnzbd_is_not_asked_anything_for_a_grab_a_pause_or_a_resume()
    {
        var calls = new Calls("sabnzbd", [RecordedReplies.Sabnzbd("queued"), RecordedReplies.Sabnzbd("paused"), RecordedReplies.Sabnzbd("resumed")]);
        var handler = calls.Build();
        var client = new SabnzbdDownloadClient(new Factory(handler));
        var keyless = Client("sabnzbd", secret: null);

        var grab = await client.GrabAsync(keyless, Grab(), CancellationToken.None);
        var pause = await client.ExecuteActionAsync(keyless, DownloadClientActions.Pause, "08531720-7308-4867-9ec9-439b73b17d1f", CancellationToken.None);
        var resumeWhole = await client.ResumeClientAsync(keyless, CancellationToken.None);

        Assert.False(grab.Succeeded);
        Assert.Contains("API key is missing", grab.Message);
        Assert.False(pause.Succeeded);
        Assert.Contains("API key is missing", pause.Message);
        Assert.False(resumeWhole.Succeeded);
        Assert.Contains("API key is missing", resumeWhole.Message);
        Assert.Empty(calls.Log);
    }

    [Fact]
    public async Task A_keep_seeding_move_is_refused_on_sabnzbd_with_nothing_sent_to_it()
    {
        var calls = new Calls("sabnzbd", [RecordedReplies.Sabnzbd("category-moved")]);
        var handler = calls.Build();

        var result = await new SabnzbdDownloadClient(new Factory(handler))
            .SetCategoryAsync(Client("sabnzbd", secret: "key"), "08531720-7308-4867-9ec9-439b73b17d1f", "keep", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not support this action", result.Message);
        Assert.Empty(calls.Log);
    }

    // ----------------------------------------------------------------- NZBGet

    [Fact]
    public async Task An_nzbget_grab_sends_append_with_the_name_url_category_and_priority_and_the_nzbid_is_the_job_id()
    {
        var calls = new Calls("nzbget", [RecordedReplies.Nzbget("queued")]);
        var handler = calls.Build();

        var result = await new NzbGetDownloadClient(new Factory(handler), removalSettle: TimeSpan.Zero)
            .GrabAsync(Client("nzbget", "nzbget"), Grab(priority: 100), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("24", result.ExternalId);
        var sent = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(calls.Log)["POST /jsonrpc ".Length..])!;
        Assert.Equal("append", (string?)sent["method"]);
        var parameters = sent["params"]!.AsArray();
        Assert.Equal("Recorded.Queued.A.nzb", (string?)parameters[0]);
        Assert.Equal(Url, (string?)parameters[1]);
        Assert.Equal("movies", (string?)parameters[2]);
        Assert.Equal(100, (int?)parameters[3]);
        Assert.False((bool?)parameters[5], "not added paused");
        Assert.Empty(parameters[9]!.AsArray());
    }

    [Fact]
    public async Task An_nzbget_grab_carries_the_archive_password_as_the_unpack_parameter_only_when_there_is_one()
    {
        var calls = new Calls("nzbget", [RecordedReplies.Nzbget("queued")]);
        var handler = calls.Build();
        var client = new NzbGetDownloadClient(new Factory(handler), removalSettle: TimeSpan.Zero);

        await client.GrabAsync(Client("nzbget", "nzbget"), Grab(password: "pa55"), CancellationToken.None);
        await client.GrabAsync(Client("nzbget", "nzbget"), Grab(), CancellationToken.None);

        var withPassword = calls.Log[0].Replace(" ", string.Empty);
        var without = calls.Log[1].Replace(" ", string.Empty);
        Assert.Contains("*Unpack:Password", withPassword);
        Assert.Contains("pa55", withPassword);
        Assert.DoesNotContain("Unpack:Password", without);
    }

    [Fact]
    public async Task A_paused_add_is_sent_to_nzbget_as_the_add_paused_flag()
    {
        var calls = new Calls("nzbget", [RecordedReplies.Nzbget("queued")]);
        var handler = calls.Build();

        await new NzbGetDownloadClient(new Factory(handler), removalSettle: TimeSpan.Zero)
            .GrabAsync(Client("nzbget", "nzbget") with { AddPaused = true }, Grab(), CancellationToken.None);

        Assert.Contains("false,true,\"\",0,\"SCORE\"", Assert.Single(calls.Log).Replace(" ", string.Empty));
    }

    [Fact]
    public async Task A_keep_seeding_move_is_refused_on_nzbget_with_nothing_sent_to_it()
    {
        var calls = new Calls("nzbget", [RecordedReplies.Nzbget("category-moved")]);
        var handler = calls.Build();

        var result = await new NzbGetDownloadClient(new Factory(handler), removalSettle: TimeSpan.Zero)
            .SetCategoryAsync(Client("nzbget", "nzbget"), "24", "keep", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not support this action", result.Message);
        Assert.Empty(calls.Log);
    }
}
