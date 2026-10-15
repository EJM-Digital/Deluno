using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
using Deluno.Platform;
using Deluno.Security.Contracts;
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// API keys against the pipeline every host ships (the one the Windows app and the container run: a real Kestrel listener on a
/// real data folder, with the sign-in gate and its scope table in front of every route). The ApplicationTestHost leaves that
/// gate out, so what the documents promise about each key scope is asked of the pipeline that is really shipped: the backup
/// routes and the API documentation, and the commands the Home Assistant recipe and other tools call.
/// </summary>
public sealed class TheShippedPipelineWithRealKeysTests : IAsyncLifetime
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-shipped-pipeline-keys", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _session = null!;

    public async Task InitializeAsync()
    {
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html><body>Deluno</body></html>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Storage:DataRoot"] = Path.Combine(_root, "data");

        builder.Services.AddDelunoWebPipeline(builder.Configuration);
        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoApplicationModules();
        builder.Services.AddDelunoPlatformSecrets(Path.Combine(_root, "data", "secrets", "master.key"));
        builder.Services
            .AddDataProtection()
            .SetApplicationName("Deluno")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_root, "data", "protection-keys")));

        // Only the schema set-up runs; the pollers and sweeps are not what is under test.
        foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
        {
            var keep = descriptor.ImplementationType is { } type &&
                       (type == typeof(DelunoStorageBootstrapService) || type.Name.EndsWith("SchemaInitializer", StringComparison.Ordinal));
            if (!keep) builder.Services.Remove(descriptor);
        }

        _app = builder.Build();
        _app.UseDelunoRequestPipeline();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
        _session = await SignInAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<string> SignInAsync()
    {
        var bootstrap = await _client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest(Username, "Test Owner", Password));
        Assert.True(bootstrap.IsSuccessStatusCode || bootstrap.StatusCode == HttpStatusCode.Conflict, "bootstrap answered " + bootstrap.StatusCode);
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, Password));
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    private async Task<HttpResponseMessage> AsOwnerAsync(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    /// <summary>A key with exactly these scopes, as the owner would make it on the API keys screen.</summary>
    private async Task<string> KeyAsync(string scopes)
    {
        using var created = await AsOwnerAsync(HttpMethod.Post, "/api/api-keys/", new { name = "Scope " + scopes, scopes });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
    }

    private async Task<HttpResponseMessage> WithKeyAsync(string key, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Api-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _client.SendAsync(request);
    }

    private string[] BackupZips() => Directory.Exists(_root) ? Directory.GetFiles(Path.Combine(_root, "data"), "*.zip", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task A_write_key_is_refused_a_backup_naming_the_system_scope_and_a_system_key_makes_a_real_archive_on_disk()
    {
        var write = await KeyAsync("write");
        var system = await KeyAsync("system");

        using var refused = await WithKeyAsync(write, HttpMethod.Post, "/api/backups", new { reason = "test" });
        var zipsAfterRefusal = BackupZips().Length;
        using var allowed = await WithKeyAsync(system, HttpMethod.Post, "/api/backups", new { reason = "test" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("system", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, zipsAfterRefusal);
        Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync());
        var zip = Assert.Single(BackupZips());
        Assert.True(new FileInfo(zip).Length > 0);
    }

    [Fact]
    public async Task A_read_key_cannot_list_the_backups_or_download_one_but_a_system_key_can()
    {
        var read = await KeyAsync("read");
        var system = await KeyAsync("read,system");
        using var made = await WithKeyAsync(system, HttpMethod.Post, "/api/backups", new { reason = "test" });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var backupId = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("backup").GetProperty("id").GetString()!;

        using var listRead = await WithKeyAsync(read, HttpMethod.Get, "/api/backups");
        using var downloadRead = await WithKeyAsync(read, HttpMethod.Get, $"/api/backups/{backupId}/download");
        using var listSystem = await WithKeyAsync(system, HttpMethod.Get, "/api/backups");
        using var downloadSystem = await WithKeyAsync(system, HttpMethod.Get, $"/api/backups/{backupId}/download");

        Assert.Equal(HttpStatusCode.Forbidden, listRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, downloadRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listSystem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, downloadSystem.StatusCode);
        Assert.True((await downloadSystem.Content.ReadAsByteArrayAsync()).Length > 0);
    }

    // DEFECT: the Windows app and service build their pipeline with no options, so no OpenAPI document is registered and the
    // documented /api/openapi/v1.json answers 404 (observed on this pipeline).
    [Fact(Skip = "Defect: the shipped pipeline (Windows app and service) serves no API documentation; GET /api/openapi/v1.json answers 404 although the docs name it.")]
    public async Task The_API_documentation_the_docs_promise_answers_a_signed_in_owner()
    {
        using var spec = await AsOwnerAsync(HttpMethod.Get, "/api/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
        Assert.Contains("openapi", await spec.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // DEFECT: DelunoRequestPipeline.ResolveApiScopes asks for `write` on every API write outside a short list, so a key with only
    // `queue` is refused 403 at the sign-in gate although the documents give this route the queue scope.
    [Fact]
    public async Task A_queue_only_key_can_remove_a_download_as_the_documents_say()
    {
        var queue = await KeyAsync("queue");

        // The download does not exist, so the handler answers "not found" or refuses it as not Deluno's own, but it is reached.
        using var response = await WithKeyAsync(queue, HttpMethod.Delete, "/api/queue/no-client/no-item");

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // DEFECT: POST /api/command is gated to `write` first, so a key with only the scope the table names (queue or system) never reaches
    // the command's own scope check and is refused 403.
    [Fact]
    public async Task A_queue_key_can_ask_for_a_download_check_through_the_command_route()
    {
        var queue = await KeyAsync("queue");

        using var refresh = await WithKeyAsync(queue, HttpMethod.Post, "/api/command", new { name = "RefreshMonitoredDownloads" });

        Assert.Equal(HttpStatusCode.Created, refresh.StatusCode);
    }

    [Fact]
    public async Task A_system_key_can_ask_for_a_backup_through_the_command_route()
    {
        var system = await KeyAsync("system");

        using var backup = await WithKeyAsync(system, HttpMethod.Post, "/api/command", new { name = "Backup" });

        Assert.Equal(HttpStatusCode.Created, backup.StatusCode);
        Assert.Single(BackupZips());
    }

    [Fact]
    public async Task A_key_with_read_write_and_system_asks_for_a_backup_through_the_command_route_and_a_real_archive_appears()
    {
        var both = await KeyAsync("read,write,system");

        using var backup = await WithKeyAsync(both, HttpMethod.Post, "/api/command", new { name = "Backup" });

        Assert.Equal(HttpStatusCode.Created, backup.StatusCode);
        Assert.Single(BackupZips());
    }

    [Fact]
    public async Task A_write_only_key_asking_for_a_backup_through_the_command_route_is_refused_and_no_archive_is_made()
    {
        var write = await KeyAsync("write");

        using var backup = await WithKeyAsync(write, HttpMethod.Post, "/api/command", new { name = "Backup" });

        Assert.Equal(HttpStatusCode.Forbidden, backup.StatusCode);
        Assert.Empty(BackupZips());
    }
}
