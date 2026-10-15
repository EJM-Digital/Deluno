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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// The rate limits of the web pipeline Deluno ships (DelunoRequestPipeline, the same one WindowsAppServerTests starts), on a real
/// Kestrel listener with a real data folder, with the limits set small: sign-in attempts are limited per address, API-key traffic
/// per key, and a browser session, the static files and the hub are never limited.
/// </summary>
public sealed class RateLimitsOnTheRealPipelineTests : IAsyncLifetime
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";
    private const int Permits = 3;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-rate-limit-server", Guid.NewGuid().ToString("N"));
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
        builder.Configuration["Security:Login:PermitLimit"] = Permits.ToString();
        builder.Configuration["Security:Login:WindowSeconds"] = "60";
        builder.Configuration["Security:Api:PermitLimit"] = Permits.ToString();
        builder.Configuration["Security:Api:WindowSeconds"] = "60";

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
            if (!keep)
            {
                builder.Services.Remove(descriptor);
            }
        }

        _app = builder.Build();
        _app.UseDelunoRequestPipeline();
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private async Task<HttpResponseMessage> LoginAsync(string password)
        => await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest(Username, password));

    [Fact]
    public async Task Sign_in_attempts_from_one_address_are_limited_the_wrong_ones_count_and_the_limit_answers_429_with_when_to_try_again()
    {
        var bootstrap = await _client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest(Username, "Test Owner", Password));
        Assert.True(bootstrap.IsSuccessStatusCode || bootstrap.StatusCode == HttpStatusCode.Conflict, "bootstrap answered " + bootstrap.StatusCode);

        // The bootstrap call is on the sign-in policy too, so it is the first of the three: one wrong password, then the right one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync("not-the-password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(Password)).StatusCode);

        var limited = await LoginAsync(Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retryAfter) && retryAfter.Single() == "60");
        Assert.Equal("Rate limit exceeded.", JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Api_key_traffic_is_limited_per_key_while_a_browser_session_the_static_files_and_the_hub_are_never_limited()
    {
        await _client.PostAsJsonAsync("/api/auth/bootstrap", new BootstrapUserRequest(Username, "Test Owner", Password));
        var login = await LoginAsync(Password);
        login.EnsureSuccessStatusCode();
        var session = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        async Task<string> NewKeyAsync(string name)
        {
            using var create = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys/") { Content = JsonContent.Create(new { name, scopes = "read" }) };
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
            var created = await _client.SendAsync(create);
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        }

        async Task<HttpResponseMessage> CallAsync(string? key = null, string? bearer = null, string path = "/api/system/overview")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (key is not null) request.Headers.Add("X-Api-Key", key);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return await _client.SendAsync(request);
        }

        var keyA = await NewKeyAsync("Key A");
        var keyB = await NewKeyAsync("Key B");

        for (var call = 0; call < Permits; call++)
        {
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(key: keyA)).StatusCode);
        }

        var over = await CallAsync(key: keyA);
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode);
        Assert.True(over.Headers.TryGetValues("Retry-After", out var retryAfter) && retryAfter.Single() == "60");
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(key: keyB)).StatusCode);

        for (var call = 0; call < Permits * 4; call++)
        {
            Assert.Equal(HttpStatusCode.OK, (await CallAsync(bearer: session)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/assets/app-abc.js")).StatusCode);
        }

        for (var call = 0; call < Permits * 2; call++)
        {
            var negotiate = await _client.PostAsync("/hubs/deluno/negotiate?negotiateVersion=1", content: null);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, negotiate.StatusCode);
        }
    }
}
