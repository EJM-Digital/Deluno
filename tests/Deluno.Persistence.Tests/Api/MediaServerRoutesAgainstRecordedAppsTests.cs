using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The media-server routes when the request is right (coverage register, area api), against the real replies of Jellyfin
/// 12.2.0 (tests/fixtures/recorded) served over a real loopback port: the real client, the real HTTP calls and the real
/// answers, so a test, a saved connection and a path mapping behave as against the installed server.
/// </summary>
public sealed class MediaServerRoutesAgainstRecordedAppsTests
{
    private const string FoundPath = @"C:\golden\recorded-apps\me";
    private const string MissingPath = @"C:\nowhere\none";

    private static Task<RecordedAppServer> JellyfinAsync(params string[] states)
        => RecordedAppServer.StartAsync(
            "jellyfin",
            states.Select(RecordedReplies.Jellyfin),
            RecordedAppServer.ByQuery(
                "path",
                (FoundPath, RecordedReplies.Jellyfin("directory-contents").Exchanges[0]),
                (MissingPath, RecordedReplies.Jellyfin("directory-contents").Exchanges[1])));

    private static object Form(RecordedAppServer server, string token = "0123456789abcdef0123456789abcdef")
        => new { kind = "jellyfin", baseUrl = server.BaseUrl, token, isEnabled = true };

    private static async Task<JsonElement> CreateAsync(ApplicationTestHost app, object form)
    {
        var response = await app.Client.PostAsJsonAsync("/api/media-servers", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Testing_a_filled_in_form_asks_the_real_server_who_it_is_and_reports_its_version_without_saving()
    {
        await using var jellyfin = await JellyfinAsync("identity", "libraries");
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/media-servers/test", Form(jellyfin));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean(), body.ToString());
        Assert.Equal("12.2.0", body.GetProperty("version").GetString());
        Assert.Contains(jellyfin.Requests, request => request.PathAndQuery.StartsWith("/System/Info", StringComparison.Ordinal));
        Assert.Empty(jellyfin.Unanswered);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/media-servers")).Replace(" ", ""));
    }

    [Fact]
    public async Task Testing_with_a_token_the_server_refuses_says_so_in_plain_words()
    {
        await using var jellyfin = await JellyfinAsync("wrong-token");
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/media-servers/test", Form(jellyfin, token: "wrong"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.Empty(jellyfin.Unanswered);
    }

    [Fact]
    public async Task A_saved_media_server_is_tested_edited_and_removed_and_its_health_and_version_are_remembered()
    {
        await using var jellyfin = await JellyfinAsync("identity", "libraries");
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await CreateAsync(app, Form(jellyfin));
        var id = created.GetProperty("id").GetString()!;
        Assert.StartsWith("Jellyfin on", created.GetProperty("name").GetString(), StringComparison.Ordinal);

        var tested = await app.Client.PostAsync($"/api/media-servers/{id}/test", content: null);

        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.True((await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/media-servers")).EnumerateArray().Single();
        Assert.Equal("healthy", stored.GetProperty("healthStatus").GetString());
        Assert.Empty(jellyfin.Unanswered);

        var edit = await app.Client.PutAsJsonAsync($"/api/media-servers/{id}", new { kind = "jellyfin", baseUrl = jellyfin.BaseUrl, token = "", isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = (await app.Client.GetFromJsonAsync<JsonElement>("/api/media-servers")).EnumerateArray().Single();
        Assert.False(edited.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(jellyfin.BaseUrl, edited.GetProperty("baseUrl").GetString()!.TrimEnd('/'));

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/media-servers/no-such-server", Form(jellyfin))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/media-servers/no-such-server/test", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/media-servers/{id}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/media-servers")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/media-servers/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_path_mapping_is_added_tested_by_asking_the_server_whether_it_can_see_the_folder_and_removed()
    {
        await using var jellyfin = await JellyfinAsync("identity", "libraries");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await CreateAsync(app, Form(jellyfin))).GetProperty("id").GetString()!;
        var local = Directory.CreateDirectory(Path.Combine(app.DataRoot, "films")).FullName;

        var other = Directory.CreateDirectory(Path.Combine(app.DataRoot, "shows")).FullName;
        var seen = await AddMappingAsync(app, id, local, FoundPath);
        var unseen = await AddMappingAsync(app, id, other, MissingPath);

        var found = await (await app.Client.PostAsync($"/api/media-servers/{id}/path-mappings/{seen}/test", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(found.GetProperty("found").GetBoolean(), found.ToString());
        var missing = await (await app.Client.PostAsync($"/api/media-servers/{id}/path-mappings/{unseen}/test", content: null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(missing.GetProperty("found").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(missing.GetProperty("message").GetString()));
        Assert.Contains(jellyfin.Requests, request => request.PathAndQuery.StartsWith("/Environment/DirectoryContents", StringComparison.Ordinal));
        Assert.Empty(jellyfin.Unanswered);

        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/media-servers/{id}/path-mappings/{seen}")).StatusCode);
        Assert.Single((await app.Client.GetFromJsonAsync<JsonElement>($"/api/media-servers/{id}/path-mappings")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/media-servers/{id}/path-mappings/{seen}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsJsonAsync($"/api/media-servers/{id}/path-mappings", new { delunoPath = "", serverPath = "x", isEnabled = true })).StatusCode);
        Assert.True(Directory.Exists(local), "a mapping is only a note about two paths; nothing on disk changes");
    }

    private static async Task<string> AddMappingAsync(ApplicationTestHost app, string serverId, string delunoPath, string serverPath)
    {
        var response = await app.Client.PostAsJsonAsync($"/api/media-servers/{serverId}/path-mappings", new { delunoPath, serverPath, isEnabled = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
}
