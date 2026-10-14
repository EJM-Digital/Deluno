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
        await connection.InvokeAsync<System.Text.Json.JsonElement>("Resume", 0L, new[] { "dashboard", "queue", "activity", "live" });

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
