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
        public List<(string? Username, string? Password)> Accounts { get; } = [];

        public Task<InstalledComponentConnectResult?> ConnectAsync(string kind, string? username, string? password, string delunoBaseUrl, CancellationToken cancellationToken)
        {
            Kinds.Add(kind);
            Accounts.Add((username, password));
            return Task.FromResult<InstalledComponentConnectResult?>(kind switch
            {
                "qbittorrent" => new InstalledComponentConnectResult("qbittorrent", "qBittorrent", true, "qBittorrent is connected."),
                "weir" => new InstalledComponentConnectResult("weir", "Weir", false, "Weir did not accept that username and password."),
                _ => null
            });
        }

        public Task<IReadOnlyList<InstalledComponentConnectResult>> ConnectAllAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<InstalledComponentConnectResult>>(
            [
                new InstalledComponentConnectResult("qbittorrent", "qBittorrent", true, "qBittorrent is connected."),
                new InstalledComponentConnectResult("weir", "Weir", false, "Weir needs your own account to connect.", NeedsAccount: true)
            ]);

        public Task<IReadOnlyDictionary<string, InstalledComponentState>> StatesAsync(IReadOnlyList<BundledComponentStatus> detected, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, InstalledComponentState>>(new Dictionary<string, InstalledComponentState>
            {
                ["qbittorrent"] = new(true, true, false, false),
                ["weir"] = new(true, false, true, false)
            });
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
    public async Task The_component_list_says_what_is_installed_and_connected_and_never_carries_a_password()
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
    public async Task The_component_list_carries_installed_connected_and_the_Weir_account_flags()
    {
        await using var app = await Start();

        var body = await app.Client.GetFromJsonAsync<JsonElement>("/api/system/bundled-components");

        var qbit = body.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "qbittorrent");
        Assert.True(qbit.GetProperty("installed").GetBoolean());
        Assert.True(qbit.GetProperty("connected").GetBoolean());
        Assert.False(qbit.GetProperty("needsAccount").GetBoolean());
        var weir = body.EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "weir");
        Assert.True(weir.GetProperty("needsAccount").GetBoolean());
        Assert.False(weir.GetProperty("accountExists").GetBoolean());
        Assert.All(body.EnumerateArray(), item => Assert.False(item.TryGetProperty("hasCredentials", out _)));
    }

    [Fact]
    public async Task Connecting_Weir_passes_the_persons_account_on_and_the_answer_never_echoes_the_password()
    {
        var connector = new FakeConnector();
        await using var app = await Start(connector);

        var response = await app.Client.PostAsJsonAsync("/api/system/bundled-components/weir/connect", new { username = "owner", password = "My-Weir-Pass-1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("My-Weir-Pass-1", text);
        var body = JsonSerializer.Deserialize<JsonElement>(text);
        Assert.False(body.GetProperty("healthy").GetBoolean());
        Assert.Equal("weir", body.GetProperty("kind").GetString());
        Assert.Equal("Weir did not accept that username and password.", body.GetProperty("message").GetString());
        Assert.Equal(("owner", "My-Weir-Pass-1"), Assert.Single(connector.Accounts));
    }

    [Fact]
    public async Task Connect_all_answers_one_result_per_app_and_Weir_says_it_needs_an_account()
    {
        await using var app = await Start();

        var response = await app.Client.PostAsync("/api/system/bundled-components/connect-all", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(["qbittorrent", "weir"], results.Select(item => item.GetProperty("kind").GetString()!).ToArray());
        Assert.True(results[1].GetProperty("needsAccount").GetBoolean());
    }

    [Fact]
    public async Task Connect_all_needs_a_signed_in_person()
    {
        await using var app = await Start();
        using var anonymous = app.CreateAnonymousClient();

        var response = await anonymous.PostAsync("/api/system/bundled-components/connect-all", null);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_stored_login_route_is_gone_and_the_tool_list_says_nothing_about_a_login()
    {
        await using var app = await Start();

        var login = await app.Client.GetAsync("/api/companion-tools/weir/login");
        var list = await app.Client.GetAsync("/api/companion-tools");

        Assert.NotEqual("application/json", login.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("hasLogin", await list.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Connecting_a_kind_that_cannot_be_connected_this_way_is_not_found()
    {
        await using var app = await Start();

        var response = await app.Client.PostAsync("/api/system/bundled-components/nonsense/connect", null);

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
    public async Task On_start_every_login_an_earlier_Deluno_stored_for_a_tool_is_deleted()
    {
        await using var app = await Start();
        var factory = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using (var connection = await factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, CancellationToken.None))
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO companion_tool_logins (tool_id, url, username, secret, secret_label, updated_utc) VALUES ('weir', 'http://127.0.0.1:9347', 'deluno-admin', 'x', 'Password', '2026-09-01T00:00:00Z');";
            await insert.ExecuteNonQueryAsync();
        }

        await app.Services.GetRequiredService<Deluno.Companions.ICompanionLoginCleanup>().ForgetAllAsync(CancellationToken.None);

        await using var check = await factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, CancellationToken.None);
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM companion_tool_logins;";
        Assert.Equal(0L, Convert.ToInt64(await count.ExecuteScalarAsync()));
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
