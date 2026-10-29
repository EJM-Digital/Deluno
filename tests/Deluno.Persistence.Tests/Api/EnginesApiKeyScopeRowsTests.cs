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

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Engines-area register rows about which API-key scopes the outside-caller routes admit, asked of the pipeline every host ships
/// (the sign-in gate and its scope table in front of every route; ApplicationTestHost leaves that gate out), on a real Kestrel
/// listener and a real data folder. Refs #1296.
/// </summary>
public sealed class EnginesApiKeyScopeRowsTests : IAsyncLifetime
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-engines-key-scopes", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _session = "";

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

        await _client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest(Username, "Test Owner", Password));
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, Password));
        login.EnsureSuccessStatusCode();
        _session = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class KeyClient(HttpClient inner, string key)
    {
        public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);
        public Task<HttpResponseMessage> PostAsJsonAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body);

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-Api-Key", key);
            if (body is not null) request.Content = JsonContent.Create(body);
            return await inner.SendAsync(request);
        }
    }

    private async Task<KeyClient> KeyClientAsync(string scopes)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys/") { Content = JsonContent.Create(new { name = "Row " + scopes, scopes }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var created = await _client.SendAsync(create);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return new KeyClient(_client, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!);
    }

    private static async Task<string?> MessageAsync(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();

    [Fact]
    public async Task A_read_and_imports_key_makes_the_calls_a_processor_makes_and_a_read_only_key_is_refused_the_events_with_the_policys_words()
    {
        var processor = await KeyClientAsync("read,imports");
        var readOnly = await KeyClientAsync("read");

        var health = await processor.GetAsync("/api/integrations/external/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // The scope gate lets the key through; the body is then checked (an empty report is a 400, never a 403).
        var admitted = await processor.PostAsJsonAsync("/api/integrations/processors/events", new { });
        Assert.NotEqual(HttpStatusCode.Forbidden, admitted.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, admitted.StatusCode);

        var refused = await readOnly.PostAsJsonAsync("/api/integrations/processors/events", new { });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        // The gate in front of /api/integrations asks for the one scope the API screen lists for the route (#1410).
        var refusal = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("This credential does not have the 'imports' scope.", refusal.GetProperty("error").GetString());
        Assert.Equal(["imports"], refusal.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(["read"], refusal.GetProperty("granted").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/integrations/external/health")).StatusCode);
    }

    [Fact]
    public async Task A_queue_only_key_passes_the_integrations_gate_for_trigger_refresh_and_is_refused_file_changed_which_needs_imports_exactly()
    {
        var queueOnly = await KeyClientAsync("queue");

        var refresh = await queueOnly.PostAsJsonAsync("/api/integrations/external/trigger-refresh", new { mediaType = "movies", reason = "row" });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        var changed = await queueOnly.PostAsJsonAsync("/api/integrations/external/file-changed", new { path = "/library/film.mkv", reason = "row", tool = "row" });
        Assert.Equal(HttpStatusCode.Forbidden, changed.StatusCode);
        Assert.Equal("This credential does not have the 'imports' scope.", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_outside_callers_import_preview_needs_exactly_the_imports_scope_so_an_imports_key_is_admitted_and_a_queue_or_read_only_key_is_refused()
    {
        var readOnly = await KeyClientAsync("read");
        var imports = await KeyClientAsync("imports");
        var queueOnly = await KeyClientAsync("queue");
        var body = new { sourcePath = Path.Combine(_root, "not-there.mkv"), mediaType = "movies", title = "Arrival", year = 2016 };

        var refused = await readOnly.PostAsJsonAsync("/api/integrations/external/import-preview", body);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("This credential does not have the 'imports' scope.", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await queueOnly.PostAsJsonAsync("/api/integrations/external/import-preview", body)).StatusCode);
        var admitted = await imports.PostAsJsonAsync("/api/integrations/external/import-preview", body);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.False((await admitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sourceExists").GetBoolean());
    }

    [Fact]
    public async Task The_download_client_webhook_needs_the_queue_scope_and_answers_an_unknown_download_404_with_a_plain_message()
    {
        var queueOnly = await KeyClientAsync("queue");
        var readOnly = await KeyClientAsync("read");

        var unknown = await queueOnly.PostAsJsonAsync("/api/download-clients/client-1/webhook", new { @event = "completed", dispatchId = "no-such-dispatch" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var body = await unknown.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("accepted").GetBoolean());
        var message = body.GetProperty("message").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("Exception", message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", message, StringComparison.Ordinal);

        var refused = await readOnly.PostAsJsonAsync("/api/download-clients/client-1/webhook", new { @event = "completed", dispatchId = "no-such-dispatch" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("This credential does not have the 'queue' scope.", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }
}
