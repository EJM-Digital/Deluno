using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Search page's type decides which engine is asked: Movies asks the movie categories (2000), TV asks the TV categories (5000) and
/// nothing of the other, Basic asks both. Jackett 0.24.2850 is the recorded app over a real loopback port and notes what it was asked;
/// the application is the whole host, with one indexer that carries both families.
/// </summary>
public sealed class TheSearchPageAsksOnlyTheEngineOfTheTypeChosenTests
{
    private const string TorznabPath = "/api/v2.0/indexers/thepiratebay/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static Task<RecordedAppServer> JackettAsync()
        => RecordedAppServer.StartAsync(
            "jackett",
            new[] { "caps", "search-tv", "search-movie-imdb", "search-text" }.Select(RecordedReplies.Jackett),
            RecordedAppServer.Chain(
                (request, _) => request.RequestUri is { } uri && uri.Query.Contains("t=search&q=", StringComparison.Ordinal) ? RecordedReplies.Jackett("search-tv").Exchanges[1].Reply.ToResponse() : null,
                RecordedAppServer.ByQuery(
                    "t",
                    ("caps", RecordedReplies.Jackett("caps").Exchanges[0]),
                    ("tvsearch", RecordedReplies.Jackett("search-tv").Exchanges[0]),
                    ("movie", RecordedReplies.Jackett("search-movie-imdb").Exchanges[0]))));

    private static async Task<(ApplicationTestHost App, string IndexerId)> HostAsync(RecordedAppServer jackett)
    {
        var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + TorznabPath, apiKey = Key, priority = 25,
            categories = "2000,5000", tags = "", mediaScope = "both", isEnabled = true, preferMagnetUrl = true
        });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (app, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
    }

    private static string[] Searches(RecordedAppServer jackett)
        => jackett.Requests.Select(request => request.PathAndQuery).Where(path => path.Contains("t=search", StringComparison.Ordinal) || path.Contains("t=tvsearch", StringComparison.Ordinal) || path.Contains("t=movie", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task A_TV_search_asks_the_indexer_for_the_TV_categories_only_and_every_result_is_tagged_tv()
    {
        await using var jackett = await JackettAsync();
        var (app, indexerId) = await HostAsync(jackett);
        await using var _ = app;

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Breaking Bad", searchType = "tv", indexerIds = new[] { indexerId } });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var candidates = JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("candidates").EnumerateArray().ToArray();
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.Equal("tv", candidate.GetProperty("searchType").GetString()));
        var asked = Searches(jackett);
        Assert.NotEmpty(asked);
        Assert.All(asked, path => Assert.DoesNotContain("cat=2000", path));
        Assert.All(asked, path => Assert.DoesNotContain("t=movie", path));
        Assert.Contains(asked, path => path.Contains("cat=5000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_movie_search_asks_for_the_movie_categories_only()
    {
        await using var jackett = await JackettAsync();
        var (app, indexerId) = await HostAsync(jackett);
        await using var _ = app;

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexerId } });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var asked = Searches(jackett);
        Assert.NotEmpty(asked);
        Assert.All(asked, path => Assert.DoesNotContain("cat=5000", path));
        Assert.All(asked, path => Assert.DoesNotContain("t=tvsearch", path));
    }
}
