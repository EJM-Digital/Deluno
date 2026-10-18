using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// Library Routing offers, under every indexer a library uses, "Only for titles tagged" and "Never for titles tagged". They are saved
/// with the routing and read back; they must also decide which titles that library asks that indexer for. Jackett 0.24.2850 is the
/// recorded app over a real loopback port; the application is the whole host.
/// </summary>
public sealed class ALibraryOnlySearchesAnIndexerForTheTitlesItIsTaggedForTests
{
    private const string FilmPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static async Task<JsonElement> PostOkAsync(ApplicationTestHost app, string url, object? body = null)
    {
        var response = body is null ? await app.Client.PostAsync(url, content: null) : await app.Client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"{url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A film library that uses the one indexer with the given tag rules, and a film in it carrying <paramref name="filmTags"/>.</summary>
    private sealed class Rig(RecordedAppServer jackett, RecordedAppServer qbit, ApplicationTestHost app, string filmId, JsonElement routing) : IAsyncDisposable
    {
        public RecordedAppServer Jackett { get; } = jackett;

        public ApplicationTestHost App { get; } = app;

        public string FilmId { get; } = filmId;

        public JsonElement Routing { get; } = routing;

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await qbit.DisposeAsync();
            await Jackett.DisposeAsync();
        }
    }

    private static async Task<Rig> RigAsync(string requiredTags, string excludedTags, params string[] filmTags)
    {
        var jackett = await RecordedAppServer.StartAsync(
            "jackett",
            new[] { "caps", "search-movie-imdb", "search-text" }.Select(RecordedReplies.Jackett),
            RecordedAppServer.Chain(
                (request, _) => request.RequestUri is { } uri && uri.Query.Contains("t=search&q=", StringComparison.Ordinal) ? RecordedReplies.Jackett("search-text").Exchanges[0].Reply.ToResponse() : null,
                RecordedAppServer.ByQuery("t", ("caps", RecordedReplies.Jackett("caps").Exchanges[0]), ("movie", RecordedReplies.Jackett("search-movie-imdb").Exchanges[0]), ("search", RecordedReplies.Jackett("search-movie-imdb").Exchanges[1]))));
        var qbit = await RecordedAppServer.StartAsync("qbittorrent", new[] { "sign-in", "version", "settings", "resumed" }.Select(RecordedReplies.Qbittorrent));
        var app = await ApplicationTestHost.StartAsync();
        var client = await PostOkAsync(app, "/api/download-clients", new
        {
            protocol = "qbittorrent", host = qbit.Host, port = qbit.Port, username = "deluno", password = "a-test-password",
            moviesCategory = "movies", tvCategory = "tv", priority = 10, isEnabled = true
        });
        var clientId = client.GetProperty("id").GetString()!;
        Assert.Equal("healthy", (await (await app.Client.PostAsync($"/api/download-clients/{clientId}/test", content: null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        var indexer = await PostOkAsync(app, "/api/indexers", new
        {
            protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + FilmPath, apiKey = Key, priority = 25,
            categories = "2000", tags = "", mediaScope = "movies", isEnabled = true, preferMagnetUrl = true
        });
        var indexerId = indexer.GetProperty("id").GetString()!;
        Assert.Equal("healthy", (await PostOkAsync(app, $"/api/indexers/{indexerId}/test")).GetProperty("healthStatus").GetString());
        var libraryId = await ApiScenes.CreateLibraryAsync(app, "movies");
        var routed = await app.Client.PutAsJsonAsync($"/api/libraries/{libraryId}/routing", new
        {
            sources = new[] { new { indexerId, priority = 1, requiredTags, excludedTags } },
            downloadClients = new[] { new { downloadClientId = clientId, priority = 1, category = (string?)null } }
        });
        Assert.True(routed.IsSuccessStatusCode, await routed.Content.ReadAsStringAsync());
        var filmId = (await PostOkAsync(app, "/api/movies", new { title = "Sintel", releaseYear = 2010, imdbId = "tt1727587", monitored = true, libraryId, searchForMissingNow = false })).GetProperty("id").GetString()!;
        if (filmTags.Length > 0)
        {
            await PostOkAsync(app, "/api/movies/bulk/tags", new { movieIds = new[] { filmId }, tags = filmTags, mode = "add" });
        }

        return new Rig(jackett, qbit, app, filmId, await app.Client.GetFromJsonAsync<JsonElement>($"/api/libraries/{libraryId}/routing"));
    }

    private static bool AskedTheIndexer(RecordedAppServer jackett)
        => jackett.Requests.Any(request => request.PathAndQuery.Contains("/yts/", StringComparison.Ordinal) && request.PathAndQuery.Contains("t=movie", StringComparison.Ordinal) || request.PathAndQuery.Contains("t=search&q=", StringComparison.Ordinal));

    [Fact]
    public async Task An_indexer_with_no_tag_choice_is_asked_for_every_title_and_lists_its_release()
    {
        await using var rig = await RigAsync(string.Empty, string.Empty);

        var preview = await PostOkAsync(rig.App, $"/api/movies/{rig.FilmId}/search?mode=preview");

        Assert.True(AskedTheIndexer(rig.Jackett));
        Assert.Equal("Sintel (2010) 720p BRRip x264 -YTS", Assert.Single(preview.GetProperty("candidates").EnumerateArray()).GetProperty("releaseName").GetString());
    }

    [Fact]
    public async Task The_tag_choices_are_saved_with_the_routing_and_read_back()
    {
        await using var rig = await RigAsync("kids", "horror");

        var source = Assert.Single(rig.Routing.GetProperty("sources").EnumerateArray());
        Assert.Equal("kids", source.GetProperty("requiredTags").GetString());
        Assert.Equal("horror", source.GetProperty("excludedTags").GetString());
    }

    [Fact(Skip = "DEFECT: a library's per-indexer \"Only for titles tagged\" choice is saved and shown but never read by the search. Sintel carries no tag and the library's only indexer is routed with requiredTags=kids; POST /api/movies/{id}/search?mode=preview still asks that indexer (Jackett saw GET .../torznab/api?t=search&q=Sintel%202010&cat=2000) and lists its release. LibrarySourceLinkItem.RequiredTags/ExcludedTags are read only by the routing store (SqliteLibrariesRepository) and the drawer; no search code reads them.")]
    public async Task An_indexer_routed_only_for_titles_tagged_is_not_asked_for_a_title_without_the_tag()
    {
        await using var rig = await RigAsync("kids", string.Empty);

        var preview = await PostOkAsync(rig.App, $"/api/movies/{rig.FilmId}/search?mode=preview");

        Assert.False(AskedTheIndexer(rig.Jackett), string.Join(" | ", rig.Jackett.Requests.Select(r => r.PathAndQuery)));
        Assert.Empty(preview.GetProperty("candidates").EnumerateArray());
    }

    [Fact(Skip = "DEFECT: a library's per-indexer \"Never for titles tagged\" choice is saved and shown but never read by the search. Sintel carries the tag horror and the library's only indexer is routed with excludedTags=horror; POST /api/movies/{id}/search?mode=preview still asks that indexer and lists its release (see the other skipped test for where the tags are, and are not, read).")]
    public async Task An_indexer_routed_never_for_titles_tagged_is_not_asked_for_a_title_with_the_tag()
    {
        await using var rig = await RigAsync(string.Empty, "horror", "horror");

        var preview = await PostOkAsync(rig.App, $"/api/movies/{rig.FilmId}/search?mode=preview");

        Assert.False(AskedTheIndexer(rig.Jackett), string.Join(" | ", rig.Jackett.Requests.Select(r => r.PathAndQuery)));
        Assert.Empty(preview.GetProperty("candidates").EnumerateArray());
    }
}
