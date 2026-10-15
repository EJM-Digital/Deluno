using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Deluno.Infrastructure;
using Deluno.Infrastructure.Storage;
using Deluno.Platform;
using Deluno.Security.Contracts;
using Deluno.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// #1176: the Windows app's own web server answers real HTTP requests.
///
/// <para>The rc.30 fix batch added an explicit <c>UseRouting()</c> to the
/// Windows app, which switched off the authentication and authorization steps
/// <c>WebApplication</c> adds by itself, and every endpoint with authorization
/// metadata answered 500 on the installed build. The browser tests drive
/// Deluno.Host and the tray tests never started the server, so nothing
/// noticed. The Windows app and Deluno.Host now share one pipeline
/// (<see cref="DelunoRequestPipeline"/>); this starts it the way the Windows
/// app configures it (a real Kestrel listener on a free port, a temporary data
/// root, the same services, <c>UseDelunoRequestPipeline()</c> with no
/// overrides) so the pipeline cannot break without a test failing. It runs on
/// Linux too: only the Windows-only extras (firewall, updater) are left out.</para>
/// </summary>
public sealed class WindowsAppServerTests : IAsyncLifetime
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-windows-app-server", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html><body>Deluno</body></html>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "app-abc.js"), "// bundle");

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
        var hosted = builder.Services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray();
        foreach (var descriptor in hosted)
        {
            var keep = descriptor.ImplementationType is { } type &&
                       (type == typeof(DelunoStorageBootstrapService) ||
                        type.Name.EndsWith("SchemaInitializer", StringComparison.Ordinal));
            if (!keep)
            {
                builder.Services.Remove(descriptor);
            }
        }

        _app = builder.Build();
        _app.UseDelunoRequestPipeline();
        await _app.StartAsync();

        var address = _app.Urls.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Health_answers_200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_authorized_endpoint_without_a_session_is_401_never_500()
    {
        var response = await _client.GetAsync("/api/system/overview");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_same_endpoint_with_a_session_is_200()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/overview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await SignInAsync());

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_poster_route_with_an_unknown_key_is_404_not_401()
    {
        var response = await _client.GetAsync("/api/metadata/artwork/" + new string('a', 64));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_api_path_is_404_and_a_page_route_is_the_app_shell()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/no-such-route");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await SignInAsync());
        var api = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);

        var page = await _client.GetAsync("/movies");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", page.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Hashed_bundle_files_are_cached_for_good()
    {
        var response = await _client.GetAsync("/assets/app-abc.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task The_version_alias_reaches_the_same_endpoints()
    {
        var response = await _client.GetAsync("/api/v1/auth/bootstrap-status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// #1178: a browser that signs in, joins the screen subjects and asks to resume
    /// is pushed what the server raises, over each transport SignalR can fall back
    /// to, through the same pipeline the Windows app runs.
    /// </summary>
    [Theory]
    [InlineData(HttpTransportType.WebSockets)]
    [InlineData(HttpTransportType.ServerSentEvents)]
    [InlineData(HttpTransportType.LongPolling)]
    public async Task A_signed_in_browser_that_joins_the_subjects_is_pushed_what_the_server_raises(HttpTransportType transport)
    {
        var token = await SignInAsync();
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/deluno"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.Transports = transport;
            })
            .Build();
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        connection.On<System.Text.Json.JsonElement>("RealtimeEvent", envelope => received.Enqueue(envelope.GetProperty("name").GetString()!));

        await connection.StartAsync();
        await connection.InvokeAsync("Subscribe", new[] { "dashboard", "queue", "activity", "live" });
        await connection.InvokeAsync<System.Text.Json.JsonElement>("Resume", 0L, new[] { "dashboard", "queue", "activity", "live" }, (string?)null);

        var watchers = _app.Services.GetRequiredService<Deluno.Contracts.IRealtimeWatchers>();
        Assert.Equal(1, watchers.Count("live"));

        // The publisher is what turns an event into a push; the host's other background work is left out of this fixture.
        var publisher = _app.Services.GetRequiredService<Deluno.Contracts.IRealtimeEventPublisher>();
        await ((IHostedService)publisher).StartAsync(CancellationToken.None);
        await publisher.PublishDownloadProgressAsync("d1", "A film", 50, 3.5, null, "downloading", CancellationToken.None);
        await publisher.PublishActivityEventAddedAsync("a1", "Searched", "search", "info", DateTimeOffset.UtcNow.ToString("O"), CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !(received.Contains("DownloadProgress") && received.Contains("ActivityEventAdded")))
        {
            await Task.Delay(100); // reason: poll interval inside a bounded wait (10 s above) for pushes arriving over a real socket
        }

        Assert.Contains("DownloadProgress", received);
        Assert.Contains("ActivityEventAdded", received);
    }

    /// <summary>
    /// Allowed Hosts (#939) sits ahead of routing in the one shared pipeline, so an address the person did not list is
    /// turned away from the page, the API, the live hub and the external API alike, and a listed one still gets in.
    /// </summary>
    [Fact]
    public async Task Allowed_hosts_turns_an_unlisted_address_away_from_the_page_the_api_the_live_hub_and_the_external_api()
    {
        var token = await SignInAsync();
        using (var save = new HttpRequestMessage(HttpMethod.Put, "/api/security/allowed-hosts")
        {
            Content = JsonContent.Create(new UpdateAllowedHostsRequest(true, ["deluno.lan"]))
        })
        {
            save.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            save.Headers.Host = "deluno.lan";
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(save)).StatusCode);
        }

        async Task<HttpResponseMessage> AskAsync(HttpMethod method, string path, string host)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Host = host;
            return await _client.SendAsync(request);
        }

        var surfaces = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/movies"),
            (HttpMethod.Get, "/api/system/overview"),
            (HttpMethod.Post, "/hubs/deluno/negotiate?negotiateVersion=1"),
            (HttpMethod.Get, "/api/integrations/external/health")
        };
        foreach (var (method, path) in surfaces)
        {
            using var refused = await AskAsync(method, path, "stranger.example");
            Assert.True(HttpStatusCode.BadRequest == refused.StatusCode, $"{path} answered {refused.StatusCode} to an unlisted address");
            Assert.Contains("Deluno does not answer on this address", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var listed = await AskAsync(method, path, "deluno.lan");
            Assert.NotEqual(HttpStatusCode.BadRequest, listed.StatusCode);
            Assert.NotEqual(HttpStatusCode.Unauthorized, listed.StatusCode);
        }
    }

    /// <summary>
    /// "Skip sign-in on the home network" (#993) is judged by the same gate that guards the API and the live hub: with it
    /// on, a request from this computer reaches both with no token; with it off, both answer 401.
    /// </summary>
    [Fact]
    public async Task Skipping_sign_in_on_the_home_network_opens_the_api_and_the_live_hub_to_a_local_request_and_off_turns_both_away()
    {
        var token = await SignInAsync();
        async Task SetSkipAsync(bool enabled)
        {
            using var save = new HttpRequestMessage(HttpMethod.Put, "/api/security/local-network-auth")
            {
                Content = JsonContent.Create(new UpdateLocalNetworkAuthRequest(enabled, [], [], []))
            };
            save.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(save)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/system/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/hubs/deluno/negotiate?negotiateVersion=1", content: null)).StatusCode);

        await SetSkipAsync(true);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/system/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("/hubs/deluno/negotiate?negotiateVersion=1", content: null)).StatusCode);

        await SetSkipAsync(false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/system/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/hubs/deluno/negotiate?negotiateVersion=1", content: null)).StatusCode);
    }

    /// <summary>
    /// A reverse proxy or Docker host in front of Deluno shows up as an address to tick well before Allowed Hosts is switched on,
    /// because the host it names is recorded whether or not the setting is on (#939).
    /// </summary>
    [Fact]
    public async Task A_host_named_by_a_reverse_proxy_is_offered_on_the_security_page_before_allowed_hosts_is_on()
    {
        var token = await SignInAsync();
        using (var seen = new HttpRequestMessage(HttpMethod.Get, "/health"))
        {
            seen.Headers.Add("X-Forwarded-Host", "media.example.com");
            Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(seen)).StatusCode);
        }

        using var overview = new HttpRequestMessage(HttpMethod.Get, "/api/security/allowed-hosts");
        overview.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var body = await (await _client.SendAsync(overview)).Content.ReadFromJsonAsync<AllowedHostsOverview>();

        Assert.False(body!.Enabled);
        var offered = Assert.Single(body.DetectedHosts, candidate => candidate.Host == "media.example.com");
        Assert.Equal("Seen in a request forwarded from a reverse proxy or Docker host", offered.Reason);
    }

    /// <summary>Creates the first account through the real API and signs in as it.</summary>
    private async Task<string> SignInAsync()
    {
        var bootstrap = await _client.PostAsJsonAsync(
            "/api/auth/bootstrap",
            new BootstrapUserRequest(Username, "Test Owner", Password));
        Assert.True(
            bootstrap.IsSuccessStatusCode || bootstrap.StatusCode == HttpStatusCode.Conflict,
            "bootstrap answered " + bootstrap.StatusCode);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, Password));
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
        return body!.AccessToken;
    }
}
