using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1240: the Keep seeding screen reads and saves what a person chose, checks a category in the client, and clears a
/// move that did not work. Nothing is on after a fresh start.
/// </summary>
public sealed class TheKeepSeedingScreenApiTests
{
    [Fact]
    public async Task A_fresh_install_has_nothing_chosen_and_every_client_and_site_listed()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("Q", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("S", "sabnzbd", "localhost", 8081, null, null, null, "movies", "tv", null, 2, true), CancellationToken.None);
        var torrents = await connections.CreateIndexerAsync(new CreateIndexerRequest("Torrents", "torznab", "private", "http://t.test/", "k", 10, "2000", "", "movies", true), CancellationToken.None);
        await connections.CreateIndexerAsync(new CreateIndexerRequest("Usenet", "newznab", "public", "http://n.test/", "k", 10, "2000", "", "movies", true), CancellationToken.None);

        var view = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/keep-seeding");

        Assert.Empty(view.GetProperty("tagCategories").EnumerateArray());
        Assert.Empty(view.GetProperty("problems").EnumerateArray());
        var clients = view.GetProperty("clients").EnumerateArray().ToArray();
        Assert.Equal(2, clients.Length);
        Assert.All(clients, client => Assert.Equal(JsonValueKind.Null, client.GetProperty("category").ValueKind));
        var torrent = clients.Single(client => client.GetProperty("protocol").GetString() == "qbittorrent");
        Assert.True(torrent.GetProperty("canKeepSeeding").GetBoolean());
        var usenet = clients.Single(client => client.GetProperty("protocol").GetString() == "sabnzbd");
        Assert.False(usenet.GetProperty("canKeepSeeding").GetBoolean());
        Assert.Contains("only a torrent can keep seeding", usenet.GetProperty("reason").GetString());
        // A Usenet site has no torrent to keep seeding, so only the torrent site is offered.
        var site = Assert.Single(view.GetProperty("indexers").EnumerateArray());
        Assert.Equal(torrents.Id, site.GetProperty("indexerId").GetString());
        Assert.Equal("private", site.GetProperty("privacy").GetString());
        Assert.True(site.GetProperty("isPrivate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("privateOverride").ValueKind);
    }

    [Fact]
    public async Task What_a_person_chooses_is_kept_and_a_private_override_is_applied_either_way()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var client = await connections.CreateDownloadClientAsync(new CreateDownloadClientRequest("Q", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);
        var priv = await connections.CreateIndexerAsync(new CreateIndexerRequest("Private one", "torznab", "private", "http://t.test/", "k", 10, "2000", "", "movies", true), CancellationToken.None);
        var pub = await connections.CreateIndexerAsync(new CreateIndexerRequest("Public one", "torznab", "public", "http://p.test/", "k", 10, "2000", "", "movies", true), CancellationToken.None);

        var response = await app.Client.PutAsJsonAsync("/api/download-health/keep-seeding", new
        {
            clientCategories = new Dictionary<string, string> { [client.Id] = "keep-seeding" },
            indexers = new Dictionary<string, object>
            {
                [priv.Id] = new { privateOverride = false, tags = new[] { "ratio" }, category = "site-keep" },
                [pub.Id] = new { privateOverride = true }
            },
            tagCategories = new Dictionary<string, string> { ["ratio"] = "tag-keep" }
        });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var view = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/keep-seeding");
        Assert.Equal("keep-seeding", view.GetProperty("clients")[0].GetProperty("category").GetString());
        var sites = view.GetProperty("indexers").EnumerateArray().ToDictionary(site => site.GetProperty("indexerId").GetString()!);
        Assert.False(sites[priv.Id].GetProperty("isPrivate").GetBoolean());
        Assert.Equal("private", sites[priv.Id].GetProperty("privacy").GetString());
        Assert.Equal("site-keep", sites[priv.Id].GetProperty("category").GetString());
        Assert.Equal("ratio", sites[priv.Id].GetProperty("tags")[0].GetString());
        Assert.True(sites[pub.Id].GetProperty("isPrivate").GetBoolean());
        Assert.Equal("public", sites[pub.Id].GetProperty("privacy").GetString());
        var tag = Assert.Single(view.GetProperty("tagCategories").EnumerateArray());
        Assert.Equal(("ratio", "tag-keep"), (tag.GetProperty("tag").GetString(), tag.GetProperty("category").GetString()));
    }

    [Fact]
    public async Task The_category_Deluno_uses_for_its_own_downloads_is_refused_with_the_reason()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await app.Services.GetRequiredService<IConnectionsRepository>()
            .CreateDownloadClientAsync(new CreateDownloadClientRequest("Q", "qbittorrent", "localhost", 8080, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);

        var response = await app.Client.PutAsJsonAsync("/api/download-health/keep-seeding", new { clientCategories = new Dictionary<string, string> { [client.Id] = "Movies" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is the category Deluno uses for downloads on", await response.Content.ReadAsStringAsync());
        var view = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/keep-seeding");
        Assert.Equal(JsonValueKind.Null, view.GetProperty("clients")[0].GetProperty("category").ValueKind);
    }

    [Fact]
    public async Task A_usenet_client_cannot_be_given_a_keep_seeding_category()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await app.Services.GetRequiredService<IConnectionsRepository>()
            .CreateDownloadClientAsync(new CreateDownloadClientRequest("S", "sabnzbd", "localhost", 8081, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);

        var response = await app.Client.PutAsJsonAsync("/api/download-health/keep-seeding", new { clientCategories = new Dictionary<string, string> { [client.Id] = "keep" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("only a torrent can keep seeding", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_category_with_a_control_character_or_too_long_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var tooLong = await app.Client.PutAsJsonAsync("/api/download-health/keep-seeding", new { tagCategories = new Dictionary<string, string> { ["ratio"] = new string('x', 101) } });
        var control = await app.Client.PutAsJsonAsync("/api/download-health/keep-seeding", new { tagCategories = new Dictionary<string, string> { ["ratio"] = "bad\u0007name" } });

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, control.StatusCode);
    }

    [Fact]
    public async Task Checking_a_category_needs_a_name_and_a_known_client()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var noName = await app.Client.PostAsJsonAsync("/api/download-health/keep-seeding/check", new { clientId = "x", category = "  " });
        var unknown = await app.Client.PostAsJsonAsync("/api/download-health/keep-seeding/check", new { clientId = "nope", category = "keep" });

        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Checking_a_category_on_a_usenet_client_says_only_a_torrent_can_keep_seeding()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var client = await app.Services.GetRequiredService<IConnectionsRepository>()
            .CreateDownloadClientAsync(new CreateDownloadClientRequest("S", "sabnzbd", "localhost", 8081, null, null, null, "movies", "tv", null, 1, true), CancellationToken.None);

        var result = await (await app.Client.PostAsJsonAsync("/api/download-health/keep-seeding/check", new { clientId = client.Id, category = "keep" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("unsupported", result.GetProperty("status").GetString());
        Assert.False(result.GetProperty("supported").GetBoolean());
        Assert.Contains("only a torrent can keep seeding", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_problem_is_listed_with_its_reason_and_clearing_it_removes_it()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var store = app.Services.GetRequiredService<IKeepSeedingStore>();
        var now = DateTimeOffset.UtcNow;
        await store.SaveProblemAsync(new KeepSeedingProblem(
            "c1:abc", "c1", "My qBittorrent", "abc", "Example.Movie", KeepSeedingProblemKinds.Refused,
            "Deluno could not move Example.Movie to keep in My qBittorrent: it has no such category.", "keep", now, now, 4), CancellationToken.None);

        var view = await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/keep-seeding");
        var problem = Assert.Single(view.GetProperty("problems").EnumerateArray());
        Assert.Contains("has no such category", problem.GetProperty("message").GetString());
        Assert.Equal(4, problem.GetProperty("attempts").GetInt32());

        var cleared = await app.Client.PostAsync("/api/download-health/keep-seeding/problems/c1:abc/clear", null);
        Assert.True(cleared.IsSuccessStatusCode);
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/download-health/keep-seeding")).GetProperty("problems").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/download-health/keep-seeding/problems/c1:abc/clear", null)).StatusCode);
    }

    [Fact]
    public async Task A_rule_saved_through_the_api_without_the_setting_keeps_removing_and_one_that_moves_is_kept()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "no-progress", ["enabled"] = true, ["strikeLimit"] = 3, ["timerMinutes"] = 30, ["appliesTo"] = "both",
            ["minCompletionPercent"] = 0, ["maxCompletionPercent"] = 100, ["resetStrikesOnProgress"] = true,
            ["actionPreset"] = "replace", ["threshold"] = 0
        };

        var plain = await app.Client.PutAsJsonAsync("/api/download-health/rules/new", body);
        Assert.True(plain.IsSuccessStatusCode, await plain.Content.ReadAsStringAsync());
        var saved = (await plain.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(rule => rule.GetProperty("kind").GetString() == "no-progress" && rule.GetProperty("actionPreset").GetString() == "replace");
        Assert.Equal("remove", saved.GetProperty("privateAction").GetString());

        body["privateAction"] = "move-and-replace";
        body["blocklistWhenMoved"] = false;
        var moves = await app.Client.PutAsJsonAsync($"/api/download-health/rules/{saved.GetProperty("id").GetString()}", body);
        Assert.True(moves.IsSuccessStatusCode, await moves.Content.ReadAsStringAsync());
        var stored = (await moves.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(rule => rule.GetProperty("id").GetString() == saved.GetProperty("id").GetString());
        Assert.Equal("move-and-replace", stored.GetProperty("privateAction").GetString());
        Assert.False(stored.GetProperty("blocklistWhenMoved").GetBoolean());

        body["actionPreset"] = "retry-elsewhere";
        var refused = await app.Client.PutAsJsonAsync($"/api/download-health/rules/{saved.GetProperty("id").GetString()}", body);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("action must be Replace", await refused.Content.ReadAsStringAsync());
    }
}
