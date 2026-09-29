using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Api.Network;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1061: the sign-in the installer generated for a client is shown to a signed-in
/// person, never to an API key (a GET only ever needs the read scope, so the policy
/// alone would have let a read-only key take it).
/// </summary>
public sealed class BundledComponentCredentialsEndpointTests
{
    private sealed class OneEntryStore : IBundledComponentCredentialStore
    {
        private readonly Dictionary<string, BundledComponentCredentials> _entries = new() { ["qbittorrent"] = new("deluno", "Generated-Pass") };
        public bool Has(string kind) => _entries.ContainsKey(kind);
        public BundledComponentCredentials? TryGet(string kind) => _entries.GetValueOrDefault(kind);
        public void Save(string kind, BundledComponentCredentials credentials) => _entries[kind] = credentials;
        public void Remove(string kind) => _entries.Remove(kind);
    }

    private static Task<ApplicationTestHost> Start()
        => ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            services.RemoveAll<IBundledComponentCredentialStore>();
            services.AddSingleton<IBundledComponentCredentialStore>(new OneEntryStore());
        });

    [Fact]
    public async Task A_signed_in_person_is_given_the_sign_in()
    {
        await using var app = await Start();

        var response = await app.Client.GetAsync("/api/system/bundled-components/qbittorrent/credentials");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Generated-Pass", body.GetProperty("password").GetString());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("system")]
    public async Task An_API_key_is_refused_whatever_its_scope(string scopes)
    {
        await using var app = await Start();
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Key " + scopes, scopes });
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        using var other = app.CreateAnonymousClient();
        other.DefaultRequestHeaders.Add("X-Api-Key", key);

        var refused = await other.GetAsync("/api/system/bundled-components/qbittorrent/credentials");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain("Generated-Pass", await refused.Content.ReadAsStringAsync());
    }
}
