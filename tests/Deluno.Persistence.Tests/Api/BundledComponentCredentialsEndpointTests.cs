using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1061: Deluno's API never hands out an app's stored sign-in, to a signed-in
/// session or to an API key. "Use X" connects the app server-side and answers only
/// whether it worked.
/// </summary>
public sealed class BundledComponentCredentialsEndpointTests
{
    private const string Password = "Generated-Pass";

    private sealed class OneEntryStore : IBundledComponentCredentialStore
    {
        private readonly Dictionary<string, BundledComponentCredentials> _entries = new() { ["qbittorrent"] = new("deluno", Password) };
        public bool Has(string kind) => _entries.ContainsKey(kind);
        public BundledComponentCredentials? TryGet(string kind) => _entries.GetValueOrDefault(kind);
        public void Save(string kind, BundledComponentCredentials credentials) => _entries[kind] = credentials;
        public void Remove(string kind) => _entries.Remove(kind);
    }

    private sealed class FakeConnector : IInstalledComponentConnector
    {
        public List<string> Kinds { get; } = [];

        public Task<InstalledComponentConnectResult?> ConnectNowAsync(string kind, CancellationToken cancellationToken)
        {
            Kinds.Add(kind);
            return Task.FromResult<InstalledComponentConnectResult?>(kind == "qbittorrent"
                ? new InstalledComponentConnectResult("qBittorrent", true, "qBittorrent is connected.")
                : null);
        }
    }

    private sealed class RecordingHook : IDownloadClientDeletedHook
    {
        public List<(string Protocol, string? Host)> Deleted { get; } = [];

        public Task DownloadClientDeletedAsync(string protocol, string? host, CancellationToken cancellationToken)
        {
            Deleted.Add((protocol, host));
            return Task.CompletedTask;
        }
    }

    private static Task<ApplicationTestHost> Start(FakeConnector? connector = null, RecordingHook? hook = null)
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IBundledComponentCredentialStore>();
            services.AddSingleton<IBundledComponentCredentialStore>(new OneEntryStore());
            services.RemoveAll<IInstalledComponentConnector>();
            services.AddSingleton<IInstalledComponentConnector>(connector ?? new FakeConnector());
            if (hook is not null)
            {
                services.AddSingleton<IDownloadClientDeletedHook>(hook);
            }
        });

    [Fact]
    public async Task No_route_returns_an_apps_sign_in_to_a_signed_in_session()
    {
        await using var app = await Start();

        var response = await app.Client.GetAsync("/api/system/bundled-components/qbittorrent/credentials");

        Assert.DoesNotContain(Password, await response.Content.ReadAsStringAsync());
        Assert.NotEqual("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("system")]
    public async Task No_route_returns_an_apps_sign_in_to_an_API_key_of_any_scope(string scopes)
    {
        await using var app = await Start();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Key " + scopes, scopes });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var other = app.CreateAnonymousClient();
        other.DefaultRequestHeaders.Add("X-Api-Key", key);

        var response = await other.GetAsync("/api/system/bundled-components/qbittorrent/credentials");

        Assert.DoesNotContain(Password, await response.Content.ReadAsStringAsync());
        Assert.NotEqual("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_component_list_says_a_sign_in_exists_but_never_carries_it()
    {
        await using var app = await Start();

        var response = await app.Client.GetAsync("/api/system/bundled-components");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Connecting_an_installed_app_answers_whether_it_worked_and_never_the_sign_in()
    {
        var connector = new FakeConnector();
        await using var app = await Start(connector);

        var response = await app.Client.PostAsync("/api/system/bundled-components/qbittorrent/connect", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, text);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.True(body.GetProperty("healthy").GetBoolean());
        Assert.Equal("qBittorrent", body.GetProperty("name").GetString());
        Assert.Equal(["qbittorrent"], connector.Kinds);
    }

    [Fact]
    public async Task Connecting_a_kind_that_cannot_be_connected_this_way_is_not_found()
    {
        await using var app = await Start();

        var response = await app.Client.PostAsync("/api/system/bundled-components/weir/connect", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Connecting_needs_a_signed_in_person()
    {
        await using var app = await Start();
        using var anonymous = app.CreateAnonymousClient();

        var response = await anonymous.PostAsync("/api/system/bundled-components/qbittorrent/connect", null);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Deleting_a_download_client_tells_the_hook_its_protocol_and_host()
    {
        var hook = new RecordingHook();
        await using var app = await Start(hook: hook);
        var created = await app.Client.PostAsJsonAsync("/api/download-clients/", new
        {
            name = "qBittorrent",
            protocol = "qbittorrent",
            host = "localhost",
            port = 8081,
            moviesCategory = "deluno-movies",
            tvCategory = "deluno-tv"
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var deleted = await app.Client.DeleteAsync($"/api/download-clients/{id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(("qbittorrent", "localhost"), Assert.Single(hook.Deleted));
    }
}
