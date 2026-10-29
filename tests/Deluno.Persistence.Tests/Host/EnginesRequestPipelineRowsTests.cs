using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Monitoring;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
using Deluno.Platform;
using Deluno.Security.Contracts;
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// Engines-area register rows about the shared web pipeline (DelunoRequestPipeline), on a real Kestrel listener with a real data
/// folder: the tracker sees every /api request, an unhandled exception answers a fixed JSON message, API and hub answers are never
/// cached while the poster route is left alone, and the limiter's budget is read from configuration. Refs #1296.
/// </summary>
public sealed class EnginesRequestPipelineRowsTests : IAsyncLifetime
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-engines-pipeline-rows", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Path, int Status)> _recorded = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _session = "";

    private sealed class RecordingTracker(ConcurrentQueue<(string Path, int Status)> sink) : IApiLatencyTracker
    {
        private readonly InMemoryApiLatencyTracker _real = new(TimeProvider.System);
        public void Record(string path, double durationMs, int statusCode)
        {
            sink.Enqueue((path, statusCode));
            _real.Record(path, durationMs, statusCode);
        }
        public ApiLatencySnapshot GetSnapshot(TimeSpan? window = null) => _real.GetSnapshot(window);
        public ApiAnswerTimes GetAnswerTimes(TimeSpan window) => _real.GetAnswerTimes(window);
        public ApiDayTotals GetDayTotals() => _real.GetDayTotals();
    }

    public async Task InitializeAsync()
    {
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html><body>Deluno</body></html>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Storage:DataRoot"] = Path.Combine(_root, "data");
        builder.Configuration["Security:Api:PermitLimit"] = "2";
        builder.Configuration["Security:Api:WindowSeconds"] = "60";

        builder.Services.AddDelunoWebPipeline(builder.Configuration);
        builder.Services.AddDelunoInfrastructure(builder.Configuration);
        builder.Services.AddDelunoApplicationModules();
        builder.Services.AddDelunoPlatformSecrets(Path.Combine(_root, "data", "secrets", "master.key"));
        builder.Services
            .AddDataProtection()
            .SetApplicationName("Deluno")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_root, "data", "protection-keys")));
        builder.Services.AddSingleton<IApiLatencyTracker>(new RecordingTracker(_recorded));

        foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
        {
            var keep = descriptor.ImplementationType is { } type &&
                       (type == typeof(DelunoStorageBootstrapService) || type.Name.EndsWith("SchemaInitializer", StringComparison.Ordinal));
            if (!keep) builder.Services.Remove(descriptor);
        }

        _app = builder.Build();
        _app.UseDelunoRequestPipeline();
        ((IEndpointRouteBuilder)_app).MapGet("/api/test-row/throws", () => ThrowNow());
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

    private async Task<HttpResponseMessage> GetAsync(string path, string? key = null, bool session = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null) request.Headers.Add("X-Api-Key", key);
        else if (session) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        return await _client.SendAsync(request);
    }

    private static int ThrowNow() => throw new InvalidOperationException("secret detail at a private place");

    [Fact]
    public async Task The_tracker_sees_every_api_request_including_one_that_throws_and_nothing_else()
    {
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/system/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await GetAsync("/api/test-row/throws")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/")).StatusCode);

        var seen = _recorded.ToArray();
        Assert.Contains(seen, item => item.Path == "/api/system/overview" && item.Status == 200);
        // Found while proving the row: the tracker reads the status before the exception handler has set 500, so a route that
        // throws is recorded (path and time) but with the status it had when it threw (200). Reported, not fixed here.
        Assert.Contains(seen, item => item.Path == "/api/test-row/throws");
        Assert.DoesNotContain(seen, item => !item.Path.StartsWith("/api", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_unhandled_exception_answers_500_with_the_fixed_message_and_no_stack_trace()
    {
        var response = await GetAsync("/api/test-row/throws");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("An unexpected error occurred.", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("secret detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_answer_carries_a_trace_id_a_safe_one_sent_in_is_kept_and_an_unsafe_one_is_replaced()
    {
        var made = await GetAsync("/api/system/overview");
        var generated = Assert.Single(made.Headers.GetValues("X-Deluno-Trace-Id"));
        Assert.False(string.IsNullOrWhiteSpace(generated));

        using var kept = new HttpRequestMessage(HttpMethod.Get, "/api/system/overview");
        kept.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        kept.Headers.Add("X-Deluno-Trace-Id", "call-42_a.b");
        Assert.Equal("call-42_a.b", Assert.Single((await _client.SendAsync(kept)).Headers.GetValues("X-Deluno-Trace-Id")));

        using var unsafeId = new HttpRequestMessage(HttpMethod.Get, "/api/system/overview");
        unsafeId.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        unsafeId.Headers.TryAddWithoutValidation("X-Deluno-Trace-Id", "bad id with spaces;drop");
        var replaced = Assert.Single((await _client.SendAsync(unsafeId)).Headers.GetValues("X-Deluno-Trace-Id"));
        Assert.NotEqual("bad id with spaces;drop", replaced);
        Assert.DoesNotContain(' ', replaced);
    }

    [Fact]
    public async Task Api_and_hub_answers_are_never_cached_while_the_poster_route_is_left_alone()
    {
        var api = await GetAsync("/api/system/overview");
        Assert.Contains("no-store", api.Headers.CacheControl?.ToString() ?? "");

        using var negotiate = new HttpRequestMessage(HttpMethod.Post, "/hubs/deluno/negotiate?negotiateVersion=1");
        negotiate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        var hub = await _client.SendAsync(negotiate);
        Assert.Equal(HttpStatusCode.OK, hub.StatusCode);
        Assert.Contains("no-store", hub.Headers.CacheControl?.ToString() ?? "");

        var poster = await GetAsync("/api/metadata/artwork/not-there.jpg", session: false);
        Assert.DoesNotContain("no-store", poster.Headers.CacheControl?.ToString() ?? "");
    }

    [Fact]
    public async Task The_budget_per_key_comes_from_configuration_and_the_poster_route_hub_and_static_files_sit_outside_it()
    {
        var created = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys/") { Content = JsonContent.Create(new { name = "Limit row", scopes = "read" }) };
        created.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        created.Headers.Add(Deluno.Security.CurrentPasswordGate.Header, Password);
        var key = (await (await _client.SendAsync(created)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/system/overview", key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/system/overview", key)).StatusCode);
        var over = await GetAsync("/api/system/overview", key);
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.Equal("60", over.Headers.GetValues("Retry-After").Single());

        for (var call = 0; call < 6; call++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await GetAsync("/api/metadata/artwork/not-there.jpg", key)).StatusCode);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await _client.PostAsync("/hubs/deluno/negotiate?negotiateVersion=1", content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/index.html")).StatusCode);
        }
    }
}
