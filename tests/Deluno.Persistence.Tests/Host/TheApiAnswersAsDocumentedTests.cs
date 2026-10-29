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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Deluno.Persistence.Tests.Host;

/// <summary>
/// What the web pipeline Deluno ships answers, as the integration docs state it, on a real listener: every error answer carries
/// the API version header (a server error answer lacks it, reported), a generated key is limited the same whether it arrives as X-Api-Key or as a bearer token, and the
/// API document and its page open without signing in.
/// </summary>
public sealed class TheApiAnswersAsDocumentedTests : IAsyncLifetime
{
    private const string Username = "owner";
    private const string Password = "Deluno-Test-2026!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-api-as-documented", Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _session = "";

    public async Task InitializeAsync()
    {
        var webRoot = Path.Combine(_root, "wwwroot");
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<html><body>Deluno</body></html>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration["Storage:DataRoot"] = Path.Combine(_root, "data");
        builder.Configuration["Security:Api:PermitLimit"] = "6";
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

    private async Task<string> NewKeyAsync(string name)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/api-keys/") { Content = JsonContent.Create(new { name, scopes = "read" }) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session);
        return (await (await _client.SendAsync(create)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
    }

    [Fact]
    public async Task Error_answers_401_404_and_429_carry_the_api_version_header()
    {
        var unauthorised = await _client.GetAsync("/api/system/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorised.StatusCode);
        Assert.Equal("v1", unauthorised.Headers.GetValues("X-Deluno-Api-Version").Single());

        var missing = await GetAsync("/api/not-a-route-at-all");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("v1", missing.Headers.GetValues("X-Deluno-Api-Version").Single());

        var key = await NewKeyAsync("Header row");
        for (var call = 0; call < 6; call++) Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/system/overview", key)).StatusCode);
        var limited = await GetAsync("/api/system/overview", key);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("v1", limited.Headers.GetValues("X-Deluno-Api-Version").Single());
    }

    [Fact]
    public async Task A_generated_key_sent_as_a_bearer_token_shares_the_budget_of_the_same_key_sent_as_X_Api_Key()
    {
        var key = await NewKeyAsync("Bearer row");
        async Task<HttpResponseMessage> AsBearerAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/system/overview");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return await _client.SendAsync(request);
        }

        // Three calls each way spend one budget of six.
        for (var call = 0; call < 3; call++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/system/overview", key)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await AsBearerAsync()).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AsBearerAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await GetAsync("/api/system/overview", key)).StatusCode);

        // A different key has a budget of its own.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/api/system/overview", await NewKeyAsync("Another"))).StatusCode);
    }

    [Fact]
    public async Task The_api_document_and_its_page_open_without_signing_in()
    {
        var document = await _client.GetAsync("/api/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Contains("Deluno API", await document.Content.ReadAsStringAsync());

        var page = await _client.GetAsync("/api/docs/index.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }
}
