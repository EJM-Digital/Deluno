using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The download-client routes when the request is right (coverage register, area api), against the real replies of the
/// real apps: the client Deluno talks to is a loopback server that answers with what qBittorrent 5.2.1 and SABnzbd 5.1.3
/// really said (tests/fixtures/recorded), so the real adapter, the real HTTP client and the real routes run as installed.
/// </summary>
public sealed class DownloadClientRoutesAgainstRecordedAppsTests
{
    private static Task<RecordedAppServer> QbittorrentAsync(params string[] states)
        => RecordedAppServer.StartAsync("qbittorrent", states.Select(RecordedReplies.Qbittorrent));

    private static Task<RecordedAppServer> SabnzbdAsync(params string[] states)
        => RecordedAppServer.StartAsync("sabnzbd", states.Select(RecordedReplies.Sabnzbd));

    private static object QbittorrentForm(RecordedAppServer server, string name = "qBittorrent")
        => new
        {
            name,
            protocol = "qbittorrent",
            host = server.Host,
            port = server.Port,
            username = "deluno",
            password = "a-test-password",
            moviesCategory = "movies",
            tvCategory = "tv",
            priority = 10,
            isEnabled = true
        };

    private static object SabnzbdForm(RecordedAppServer server, string name = "SABnzbd")
        => new
        {
            name,
            protocol = "sabnzbd",
            host = server.Host,
            port = server.Port,
            password = "0123456789abcdef0123456789abcdef",
            moviesCategory = "movies",
            tvCategory = "tv",
            priority = 10,
            isEnabled = true
        };

    private static async Task<JsonElement> CreateClientAsync(ApplicationTestHost app, object form)
    {
        var response = await app.Client.PostAsJsonAsync("/api/download-clients", form);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ClientAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/download-clients")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Testing_a_filled_in_form_signs_in_to_the_real_client_and_reports_it_healthy_without_saving_anything()
    {
        await using var qbit = await QbittorrentAsync("sign-in", "version", "settings");
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/download-clients/test", QbittorrentForm(qbit));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());
        Assert.Contains(qbit.Requests, request => request.Method == "POST" && request.PathAndQuery.EndsWith("/api/v2/auth/login", StringComparison.Ordinal));
        Assert.Contains("username=deluno", qbit.Requests.First(request => request.PathAndQuery.EndsWith("auth/login", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
        Assert.Empty(qbit.Unanswered);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/download-clients")).Replace(" ", ""));
    }

    [Fact]
    public async Task Testing_a_form_the_client_refuses_the_login_for_says_so_in_plain_words_and_saves_nothing()
    {
        await using var qbit = await QbittorrentAsync("login-refused");
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/download-clients/test", QbittorrentForm(qbit));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual("healthy", body.GetProperty("healthStatus").GetString());
        Assert.Contains("refused Deluno's username and password", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Empty(qbit.Unanswered);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/download-clients")).Replace(" ", ""));
    }

    [Fact]
    public async Task Testing_a_saved_client_reports_and_remembers_its_health_and_editing_it_changes_what_is_stored_and_tested()
    {
        await using var qbit = await QbittorrentAsync("sign-in", "version", "settings");
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await CreateClientAsync(app, QbittorrentForm(qbit, "Torrent box"));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("untested", created.GetProperty("healthStatus").GetString());

        var tested = await app.Client.PostAsync($"/api/download-clients/{id}/test", content: null);

        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        var result = await tested.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", result.GetProperty("healthStatus").GetString());
        var stored = await ClientAsync(app, id);
        Assert.Equal("healthy", stored.GetProperty("healthStatus").GetString());
        Assert.NotEqual(JsonValueKind.Null, stored.GetProperty("lastHealthTestUtc").ValueKind);
        Assert.Empty(qbit.Unanswered);

        var edit = await app.Client.PutAsJsonAsync($"/api/download-clients/{id}", new { name = "Torrent box (renamed)", moviesCategory = "films", priority = 5 });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = await ClientAsync(app, id);
        Assert.Equal("films", edited.GetProperty("moviesCategory").GetString());
        Assert.Equal(5, edited.GetProperty("priority").GetInt32());
        Assert.Equal(qbit.Port, edited.GetProperty("port").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/download-clients/no-such-client", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/download-clients/no-such-client/test", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/download-clients/{id}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/download-clients")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/download-clients/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_path_mapping_is_added_tested_against_the_real_folder_and_removed_without_touching_the_client()
    {
        await using var sab = await SabnzbdAsync("version");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await CreateClientAsync(app, SabnzbdForm(sab))).GetProperty("id").GetString()!;
        var local = Directory.CreateDirectory(Path.Combine(app.DataRoot, "downloads", "complete")).FullName;
        var requestsBefore = sab.Requests.Count;

        var added = await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/path-mappings", new { remotePath = "/data/complete", localPath = local });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var mapping = await added.Content.ReadFromJsonAsync<JsonElement>();
        var mappingId = mapping.GetProperty("id").GetString()!;
        Assert.Equal("/data/complete", mapping.GetProperty("remotePath").GetString());
        Assert.Equal(mappingId, Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>($"/api/download-clients/{id}/path-mappings")).EnumerateArray()).GetProperty("id").GetString());

        var reachable = await app.Client.PostAsync($"/api/download-clients/{id}/path-mappings/{mappingId}/test", content: null);
        var reached = await reachable.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(reached.GetProperty("reachable").GetBoolean());
        Assert.Contains($"Deluno can reach {local}", reached.GetProperty("message").GetString(), StringComparison.Ordinal);
        Directory.Delete(local);
        var gone = await (await app.Client.PostAsync($"/api/download-clients/{id}/path-mappings/{mappingId}/test", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(gone.GetProperty("reachable").GetBoolean());
        Assert.Contains("cannot reach", gone.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/download-clients/{id}/path-mappings/{mappingId}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync($"/api/download-clients/{id}/path-mappings")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/download-clients/{id}/path-mappings/{mappingId}")).StatusCode);
        Assert.Equal(requestsBefore, sab.Requests.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync($"/api/download-clients/{id}/path-mappings", new { remotePath = "", localPath = local })).StatusCode);
    }
}
