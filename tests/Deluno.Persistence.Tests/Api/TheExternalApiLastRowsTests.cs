using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The last small promises of the external integration API (docs/external-integration-api.md), each through the real host:
/// who gets a renewed session token, who may read the health summary, the page limits of the wanted lists, and the order
/// of the episode answers.
/// </summary>
public sealed class TheExternalApiLastRowsTests
{
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async Task<HttpClient> KeyClientAsync(ApplicationTestHost app, string scopes)
    {
        var created = await app.Client.PostAsJsonAsync("/api/api-keys/", new { name = "Last rows " + scopes, scopes });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var key = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        var client = app.CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    [Fact]
    public async Task An_API_key_caller_never_gets_the_session_renewal_headers_however_long_the_install_has_run()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<TimeProvider>(clock));
        var key = await KeyClientAsync(app, "read");

        // Far past the half-way point of a seven-day session, which is when a session would be handed a fresh token.
        clock.Advance(TimeSpan.FromDays(5));
        var response = await key.GetAsync("/api/tags");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("X-Deluno-Access-Token"));
        Assert.False(response.Headers.Contains("X-Deluno-Access-Token-Expires"));
    }

    [Fact]
    public async Task The_health_summary_is_read_with_a_read_key_and_refused_without_one()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var read = await KeyClientAsync(app, "read");

        var allowed = await read.GetAsync("/api/system/health/summary");
        var anonymous = await app.CreateAnonymousClient().GetAsync("/api/system/health/summary");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(JsonValueKind.Object, (await allowed.Content.ReadFromJsonAsync<JsonElement>()).ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task A_wanted_list_starts_at_page_one_of_fifty_and_a_page_size_is_kept_between_one_and_two_hundred()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var defaults = await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/missing");
        var tooBig = await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/missing?pageSize=900");
        var tooSmall = await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/cutoff?pageSize=0&page=-3");

        Assert.Equal(1, defaults.GetProperty("page").GetInt32());
        Assert.Equal(50, defaults.GetProperty("pageSize").GetInt32());
        Assert.Equal(200, tooBig.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, tooSmall.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, tooSmall.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Every_wanted_record_carries_the_fields_a_caller_reads_for_a_film_and_for_an_episode()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "movies", "Films");
        await CreateLibraryAsync(app, "tv", "Shows");
        var added = await app.Client.PostAsJsonAsync("/api/automation/catalogue/bulk", new
        {
            items = new object[]
            {
                new { clientItemId = "f", mediaType = "movie", title = "Field Film", year = 2023 },
                new { clientItemId = "s", mediaType = "tv", title = "Field Show", year = 2020 }
            }
        });
        var items = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        var showId = items.EnumerateArray().Single(item => item.GetProperty("clientItemId").GetString() == "s").GetProperty("entityId").GetString();
        await app.Client.PostAsJsonAsync($"/api/automation/series/{showId}/episodes/bulk", new
        {
            episodes = new[] { new { clientItemId = "e", seasonNumber = 1, episodeNumber = 1, title = "Pilot" } }
        });

        var page = await app.Client.GetFromJsonAsync<JsonElement>("/api/wanted/missing");

        var records = page.GetProperty("records").EnumerateArray().ToArray();
        var film = records.Single(record => record.GetProperty("mediaType").GetString() == "movies");
        var episode = records.Single(record => record.GetProperty("mediaType").GetString() == "tv");
        Assert.Equal(2023, film.GetProperty("year").GetInt32());
        foreach (var name in new[] { "wantedStatus", "wantedReason", "currentQuality", "targetQuality", "lastSearchUtc", "nextEligibleSearchUtc", "airDateUtc", "episodeTitle" })
        {
            Assert.True(film.TryGetProperty(name, out _), $"A film record is missing {name}.");
            Assert.True(episode.TryGetProperty(name, out _), $"An episode record is missing {name}.");
        }

        Assert.Equal("Pilot", episode.GetProperty("episodeTitle").GetString());
        Assert.Equal("missing", film.GetProperty("wantedStatus").GetString());
    }

    [Fact]
    public async Task Episode_results_come_back_in_plain_text_order_of_their_client_ids()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await CreateLibraryAsync(app, "tv", "Shows");
        var added = await app.Client.PostAsJsonAsync("/api/automation/catalogue/bulk", new
        {
            items = new[] { new { clientItemId = "s", mediaType = "tv", title = "Order Show", year = 2020 } }
        });
        var showId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0].GetProperty("entityId").GetString();

        var response = await app.Client.PostAsJsonAsync($"/api/automation/series/{showId}/episodes/bulk", new
        {
            episodes = new[]
            {
                new { clientItemId = "10", seasonNumber = 1, episodeNumber = 10, title = "Ten" },
                new { clientItemId = "2", seasonNumber = 1, episodeNumber = 2, title = "Two" },
                new { clientItemId = "1", seasonNumber = 1, episodeNumber = 1, title = "One" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("episodes").EnumerateArray()
            .Select(item => item.GetProperty("clientItemId").GetString()!).ToArray();
        Assert.Equal(["1", "10", "2"], ids);
    }

    private static async Task CreateLibraryAsync(ApplicationTestHost app, string mediaType, string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(app.DataRoot, "libraries", name.ToLowerInvariant())).FullName;
        using var response = await app.Client.PostAsJsonAsync("/api/libraries/", new
        {
            name,
            mediaType,
            purpose = "collection",
            rootPath = root,
            importWorkflow = "copy",
            autoSearchEnabled = false,
            missingSearchEnabled = false,
            upgradeSearchEnabled = false
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }
}
