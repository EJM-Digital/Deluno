using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Resuming a whole Usenet client and giving SABnzbd and NZBGet the person's news server (coverage register, area api), against what the
/// real SABnzbd 5.1.3 and NZBGet 26.3 answered (tests/fixtures/recorded, from the e2e stack's images in Docker; scripts/recorded-replies/docker/usenet-client-controls.mjs).
/// The loopback servers below serve those recordings, so the real adapters, the real routes and the real database run.
/// </summary>
public sealed class UsenetClientsAreResumedAndGivenAProviderOnRecordedAppsTests
{
    private const string Host = "news.example.test";

    private static Task<RecordedAppServer> SabnzbdAsync(params string[] states)
        => RecordedAppServer.StartAsync("sabnzbd", states.Select(RecordedReplies.Sabnzbd));

    private static Task<RecordedAppServer> NzbgetAsync(params string[] states)
        => RecordedAppServer.StartAsync("nzbget", states.Select(RecordedReplies.Nzbget));

    private static async Task<string> AddAsync(ApplicationTestHost app, string protocol, RecordedAppServer server)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients", protocol == "sabnzbd"
            ? new { name = "SABnzbd", protocol, host = server.Host, port = server.Port, password = "0123456789abcdef0123456789abcdef", moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true }
            : (object)new { name = "NZBGet", protocol, host = server.Host, port = server.Port, username = "nzbget", password = "tegbzn6789", moviesCategory = "Movies", tvCategory = "TV", priority = 10, isEnabled = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static object Provider() => new { host = Host, port = 563, useSsl = true, username = "recorder", password = "provider-pass-for-recording", connections = 8 };

    [Fact]
    public async Task Resuming_SABnzbd_as_a_whole_asks_it_to_resume_says_so_in_Activity_and_reports_a_refused_key_in_plain_words()
    {
        await using var sab = await SabnzbdAsync("client-paused-and-resumed");
        await using var wrong = await SabnzbdAsync("client-resume-wrong-key");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, "sabnzbd", sab);
        var refusedId = await AddAsync(app, "sabnzbd", wrong);

        var resumed = await app.Client.PostAsync($"/api/download-clients/{id}/resume", content: null);

        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var body = await resumed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("succeeded").GetBoolean());
        var said = body.GetProperty("message").GetString()!;
        Assert.StartsWith("Resumed SABnzbd", said);
        Assert.EndsWith(": it is downloading again.", said);
        Assert.Contains(sab.Requests, request => request.PathAndQuery.StartsWith("/api?mode=resume", StringComparison.Ordinal));
        Assert.Contains(said, await app.Client.GetStringAsync("/api/activity?take=50"));

        var refused = await app.Client.PostAsync($"/api/download-clients/{refusedId}/resume", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var refusal = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(refusal.GetProperty("succeeded").GetBoolean());
        Assert.Contains("SABnzbd", refusal.GetProperty("message").GetString());
        Assert.DoesNotContain("Resumed SABnzbd", refusal.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Resuming_NZBGet_as_a_whole_releases_all_three_of_its_pauses_in_order_and_says_so_in_Activity()
    {
        await using var nzbget = await NzbgetAsync("client-paused-and-resumed");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, "nzbget", nzbget);

        var resumed = await app.Client.PostAsync($"/api/download-clients/{id}/resume", content: null);

        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var said = (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()!;
        Assert.StartsWith("Resumed NZBGet", said);
        Assert.EndsWith(": it is downloading again.", said);
        var methods = nzbget.Requests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("method").GetString()).Where(method => method!.StartsWith("resume", StringComparison.Ordinal)).ToArray();
        Assert.Equal("resumedownload,resumepost,resumescan", string.Join(",", methods));
        Assert.Contains(said, await app.Client.GetStringAsync("/api/activity?take=50"));
    }

    [Fact]
    public async Task Giving_SABnzbd_a_news_server_sends_it_the_servers_details_and_reads_back_that_it_kept_it()
    {
        await using var sab = await SabnzbdAsync("usenet-provider-set");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, "sabnzbd", sab);

        var response = await app.Client.PostAsJsonAsync("/api/download-clients/usenet-provider", Provider());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("saved").GetBoolean());
        var client = Assert.Single(body.GetProperty("clients").EnumerateArray());
        Assert.Equal(id, client.GetProperty("clientId").GetString());
        Assert.StartsWith("SABnzbd", client.GetProperty("message").GetString());
        Assert.EndsWith($" now uses {Host}.", client.GetProperty("message").GetString());
        var set = Assert.Single(sab.Requests, request => request.PathAndQuery.Contains("mode=set_config", StringComparison.Ordinal));
        Assert.Contains($"host={Host}", set.PathAndQuery);
        Assert.Contains("port=563", set.PathAndQuery);
        Assert.Contains("ssl=1", set.PathAndQuery);
        Assert.Contains("connections=8", set.PathAndQuery);
        Assert.Contains(sab.Requests, request => request.PathAndQuery.Contains("mode=get_config", StringComparison.Ordinal));
        Assert.DoesNotContain("provider-pass-for-recording", body.ToString());
    }

    [Fact]
    public async Task Giving_NZBGet_a_news_server_writes_its_settings_reloads_it_and_waits_until_it_shows_the_server()
    {
        await using var nzbget = await NzbgetAsync("usenet-provider-set");
        await using var app = await ApplicationTestHost.StartAsync();
        await AddAsync(app, "nzbget", nzbget);

        var response = await app.Client.PostAsJsonAsync("/api/download-clients/usenet-provider", Provider());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("saved").GetBoolean());
        var said = Assert.Single(body.GetProperty("clients").EnumerateArray()).GetProperty("message").GetString();
        Assert.StartsWith("NZBGet", said);
        Assert.EndsWith($" now uses {Host}.", said);
        var methods = nzbget.Requests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("method").GetString()).ToList();
        Assert.True(methods.IndexOf("loadconfig") < methods.IndexOf("saveconfig") && methods.IndexOf("saveconfig") < methods.IndexOf("reload"));
        var saved = JsonDocument.Parse(nzbget.Requests.First(request => request.Body.Contains("\"saveconfig\"", StringComparison.Ordinal)).Body).RootElement.GetProperty("params")[0];
        Assert.Contains(saved.EnumerateArray(), option => option.GetProperty("Name").GetString() == "Server1.Host" && option.GetProperty("Value").GetString() == Host);
        Assert.DoesNotContain(saved.EnumerateArray(), option => option.GetProperty("Name").GetString() is "ConfigFile" or "AppBin" or "AppDir" or "Version");
        Assert.DoesNotContain("provider-pass-for-recording", body.ToString());
    }

    [Fact]
    public async Task A_provider_sent_to_a_sabnzbd_that_refuses_the_key_is_reported_as_not_kept_and_a_nonsense_provider_or_no_usenet_client_is_refused_in_plain_words()
    {
        await using var wrong = await SabnzbdAsync("usenet-provider-wrong-key");
        await using var app = await ApplicationTestHost.StartAsync();

        var noClient = await app.Client.PostAsJsonAsync("/api/download-clients/usenet-provider", Provider());
        Assert.Equal(HttpStatusCode.BadRequest, noClient.StatusCode);
        Assert.Contains("There is no SABnzbd or NZBGet connection to give the provider to.", await noClient.Content.ReadAsStringAsync());

        await AddAsync(app, "sabnzbd", wrong);
        var refused = await app.Client.PostAsJsonAsync("/api/download-clients/usenet-provider", Provider());
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("saved").GetBoolean());
        Assert.Contains("did not accept the provider", Assert.Single(body.GetProperty("clients").EnumerateArray()).GetProperty("message").GetString());

        var nonsense = await app.Client.PostAsJsonAsync("/api/download-clients/usenet-provider", new { host = "http://news.example.test/path", port = 563, useSsl = true, username = "a", password = "b", connections = 8 });
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
        Assert.Contains("Enter only the server address, without http:// or a path.", await nonsense.Content.ReadAsStringAsync());
        var tooMany = await app.Client.PostAsJsonAsync("/api/download-clients/usenet-provider", new { host = Host, port = 563, useSsl = true, username = "a", password = "b", connections = 500 });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }
}
