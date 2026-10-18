using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Every key, password, token and cookie a person saves with a connection is kept protected and is never handed back: the lists of
/// indexers (a Torznab key and a built-in tracker's saved cookie), download clients, proxies, media servers, Seerr servers (and the
/// secret its webhook was made with) and subtitle providers say only that a secret is there. Real host, real databases; the secrets
/// are strings that cannot be mistaken for anything else, searched for in the raw text of each answer.
/// </summary>
public sealed class NoListOfConnectionsEverReturnsASecretTests
{
    private const string IndexerKey = "SECRET-indexer-key-7f3a91";
    private const string TrackerCookie = "SECRET-tracker-cookie-5b02c8";
    private const string ClientPassword = "SECRET-client-password-1d4e77";
    private const string ProxyPassword = "SECRET-proxy-password-98ab31";
    private const string MediaToken = "SECRET-media-token-c3f6a0";
    private const string SeerrKey = "SECRET-seerr-key-62e9d4";
    private const string SubtitlePassword = "SECRET-subtitle-password-0a8b5c";
    private const string SubtitleKey = "SECRET-subtitle-key-e41f27";

    private static async Task<JsonElement> PostAsync(ApplicationTestHost app, string url, object body)
    {
        var response = await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"POST {url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_lists_say_that_a_secret_is_saved_and_never_what_it_is()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await PostAsync(app, "/api/indexers", new
        {
            name = "Torznab with a key", protocol = "torznab", baseUrl = "https://indexer.example.test/api", apiKey = IndexerKey, privacy = "private", priority = 10,
            categories = "2000", tags = "", mediaScope = "movies", isEnabled = false
        });
        var catalog = await app.Client.GetFromJsonAsync<JsonElement>("/api/trackers");
        var tracker = catalog.EnumerateArray().First();
        await PostAsync(app, "/api/indexers", new
        {
            name = "Built-in tracker", protocol = "tracker", baseUrl = tracker.GetProperty("links")[0].GetString(), definitionId = tracker.GetProperty("id").GetString(),
            definitionSettings = new Dictionary<string, string>(), definitionSecrets = new Dictionary<string, string> { ["cookie"] = TrackerCookie },
            privacy = "private", priority = 10, categories = "2000", tags = "", mediaScope = "movies", isEnabled = false
        });
        await PostAsync(app, "/api/download-clients", new
        {
            protocol = "qbittorrent", host = "localhost", port = 8080, username = "deluno", password = ClientPassword,
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = false
        });
        await PostAsync(app, "/api/indexer-proxies", new { name = "A proxy", proxyType = "http", address = "http://proxy.example.test:8080", username = "proxy-user", password = ProxyPassword, isEnabled = true });
        await PostAsync(app, "/api/media-servers", new { kind = "jellyfin", baseUrl = "http://media.example.test:8096", token = MediaToken, isEnabled = false });
        var seerr = await PostAsync(app, "/api/seerr-servers", new { name = "Seerr", baseUrl = "http://seerr.example.test:5055", apiKey = SeerrKey, isEnabled = false });
        var webhook = await PostAsync(app, $"/api/seerr-servers/{seerr.GetProperty("id").GetString()}/webhook-secret", new { });
        var webhookSecret = webhook.EnumerateObject().First(property => property.Value.ValueKind == JsonValueKind.String && property.Name.Contains("ecret", StringComparison.Ordinal)).Value.GetString()!;
        var subtitles = await app.Client.PutAsJsonAsync("/api/subtitle-providers/opensubtitles", new { providerKey = "opensubtitles", username = "someone", secret = SubtitlePassword, apiKey = SubtitleKey, priority = 10, isEnabled = false });
        Assert.True(subtitles.IsSuccessStatusCode, await subtitles.Content.ReadAsStringAsync());
        var secrets = new[] { IndexerKey, TrackerCookie, ClientPassword, ProxyPassword, MediaToken, SeerrKey, webhookSecret, SubtitlePassword, SubtitleKey };

        foreach (var list in new[] { "/api/indexers", "/api/download-clients", "/api/indexer-proxies", "/api/media-servers", "/api/seerr-servers", "/api/subtitle-providers", "/api/trackers" })
        {
            var raw = await app.Client.GetStringAsync(list);
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, raw);
            }
        }

        // What a list does say: that a secret is there, so a form can tell "kept" from "none yet".
        string Squashed(string raw) => raw.Replace(" ", string.Empty);
        var indexers = (await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers")).EnumerateArray().ToArray();
        Assert.Equal("cookie", Assert.Single(indexers.Single(item => item.GetProperty("name").GetString() == "Built-in tracker").GetProperty("definitionSecretsSet").EnumerateArray()).GetString());
        Assert.All(indexers, item => Assert.False(item.GetProperty("secretUnreadable").GetBoolean()));
        Assert.Contains("\"hasPassword\":true", Squashed(await app.Client.GetStringAsync("/api/indexer-proxies")));
        Assert.Contains("\"hasApiKey\":true", Squashed(await app.Client.GetStringAsync("/api/seerr-servers")));
        var providers = Squashed(await app.Client.GetStringAsync("/api/subtitle-providers"));
        Assert.Contains("\"hasSecret\":true", providers);
        Assert.Contains("\"hasApiKey\":true", providers);
    }
}
