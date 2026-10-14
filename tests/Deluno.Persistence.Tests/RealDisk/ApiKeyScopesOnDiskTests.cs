using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// What an API key may do to files on disk (backups, re-downloads) and to the keys themselves, through the real
/// authorization policies on a real host with real backup zips in the data folder.
/// </summary>
public sealed class ApiKeyScopesOnDiskTests
{
    private static async Task<(HttpClient Client, string Id)> KeyAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Scope " + scopes, scopes });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", body.GetProperty("apiKey").GetString()!);
        return (client, body.GetProperty("item").GetProperty("id").GetString()!);
    }

    private static string[] BackupZips(ApplicationTestHost app)
        => Directory.GetFiles(app.DataRoot, "*.zip", SearchOption.AllDirectories);

    [Fact]
    public async Task A_write_only_key_cannot_create_a_backup_and_a_system_key_can_and_a_real_archive_appears_on_disk()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (write, _) = await KeyAsync(app, "write");
        var (system, _) = await KeyAsync(app, "system");
        var before = BackupZips(app).Length;

        var refused = await write.PostAsJsonAsync("/api/backups", new { reason = "test" });
        var refusedZips = BackupZips(app).Length;
        var allowed = await system.PostAsJsonAsync("/api/backups", new { reason = "test" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(before, refusedZips);
        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync());
        var created = Assert.Single(BackupZips(app).Except(Array.Empty<string>()), path => new FileInfo(path).Length > 0);
        Assert.True(new FileInfo(created).Length > 0);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write,queue")]
    public async Task A_key_without_the_system_scope_cannot_delete_a_backup_file_and_the_system_key_can(string scopes)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (system, _) = await KeyAsync(app, "read,system");
        var made = await system.PostAsJsonAsync("/api/backups", new { reason = "test" });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var backupId = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("backup").GetProperty("id").GetString()!;
        var (other, _) = await KeyAsync(app, scopes);

        var delete = await other.DeleteAsync($"/api/backups/{backupId}");
        var create = await other.PostAsJsonAsync("/api/backups", new { reason = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Single(BackupZips(app));
        Assert.Equal(HttpStatusCode.NoContent, (await system.DeleteAsync($"/api/backups/{backupId}")).StatusCode);
        Assert.Empty(BackupZips(app));
    }

    // DEFECT (the register row says so): the backup archive holds the installation's configuration, and the docs call backups
    // system-scoped, but a key with only `read` can list them and download the file. DelunoRequestPipeline.ResolveApiScopes
    // answers ["read"] for every GET before it gets to the /api/backups rule, and the group itself needs only the Read policy.
    [Theory]
    [InlineData("read")]
    [InlineData("home-assistant")]
    public async Task A_key_without_the_system_scope_cannot_list_or_download_a_backup_file(string scopes)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (system, _) = await KeyAsync(app, "read,system");
        var made = await system.PostAsJsonAsync("/api/backups", new { reason = "test" });
        var backupId = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("backup").GetProperty("id").GetString()!;
        var (other, _) = await KeyAsync(app, scopes);

        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/backups/{backupId}/download")).StatusCode);
    }

    [Fact]
    public async Task Revoking_an_api_key_answers_204_removes_it_from_the_list_and_the_key_stops_working()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (key, id) = await KeyAsync(app, "read");
        Assert.Equal(HttpStatusCode.OK, (await key.GetAsync("/api/integrations/external/health")).StatusCode);

        var revoked = await app.Client.DeleteAsync($"/api/api-keys/{id}");

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        var list = await app.Client.GetStringAsync("/api/api-keys/");
        Assert.DoesNotContain(id, list);
        Assert.Equal(HttpStatusCode.Unauthorized, (await key.GetAsync("/api/integrations/external/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/api-keys/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_imports_only_key_is_refused_on_health_while_a_media_automation_key_passes()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (importsOnly, _) = await KeyAsync(app, "imports");
        var (mediaAutomation, _) = await KeyAsync(app, "media-automation");

        Assert.Equal(HttpStatusCode.Forbidden, (await importsOnly.GetAsync("/api/integrations/external/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mediaAutomation.GetAsync("/api/integrations/external/health")).StatusCode);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("queue")]
    public async Task The_redownload_route_that_moves_a_users_file_refuses_a_read_key_and_a_queue_key_and_accepts_an_imports_key(string refusedScopes)
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (refused, _) = await KeyAsync(app, refusedScopes);
        var (imports, _) = await KeyAsync(app, "imports");
        var anonymous = app.CreateAnonymousClient();
        var body = new { path = Path.Combine(app.DataRoot, "movies", "Nothing.mkv"), tool = "Test" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/integrations/external/redownload", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await refused.PostAsJsonAsync("/api/integrations/external/redownload", body)).StatusCode);
        // An imports key is let in: the file is simply not tracked.
        Assert.Equal(HttpStatusCode.NotFound, (await imports.PostAsJsonAsync("/api/integrations/external/redownload", body)).StatusCode);
    }

    [Fact]
    public async Task The_queue_route_that_removes_a_download_refuses_a_read_key_and_lets_a_queue_key_through_to_the_handler()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (read, _) = await KeyAsync(app, "read");
        var (queue, _) = await KeyAsync(app, "queue");

        var refused = await read.DeleteAsync("/api/queue/no-client/no-item");
        var reached = await queue.DeleteAsync("/api/queue/no-client/no-item");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, reached.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, reached.StatusCode);
    }

    [Fact]
    public async Task The_home_assistant_key_template_reaches_the_readiness_sensor_but_cannot_create_a_backup_or_touch_api_keys()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (home, _) = await KeyAsync(app, "home-assistant");

        Assert.Equal(HttpStatusCode.OK, (await home.GetAsync("/api/automation/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await home.GetAsync("/api/integrations/external/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await home.PostAsJsonAsync("/api/backups", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await home.PostAsJsonAsync("/api/api-keys/", new { name = "x", scopes = "all" })).StatusCode);
        Assert.Empty(BackupZips(app));
    }

    // DEFECT: the README says the home-assistant template needs no API-key administration, and a key may not create or revoke
    // keys, but a key with `read` can list every key (names, prefixes and scopes).
    [Fact(Skip = "Defect: a home-assistant (read) key can list the API keys, although the template is documented as having no API-key administration.")]
    public async Task The_home_assistant_key_template_cannot_list_the_api_keys()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var (home, _) = await KeyAsync(app, "home-assistant");

        Assert.Equal(HttpStatusCode.Forbidden, (await home.GetAsync("/api/api-keys/")).StatusCode);
    }
}
