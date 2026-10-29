using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// A Search-page query across several indexers when one of them is broken, with the replies the indexers really gave (Jackett 0.24.2850:
/// the Sintel feed, its 500 for an indexer it does not know, its JSON where a feed should be; GitHub's real 404): the page keeps the
/// other indexers' results, the failure is shown with its status and is never an empty "found nothing", and an indexer that is switched
/// off, or does not cover the kind of title, is not asked at all.
/// </summary>
public sealed class ASearchWithAnIndexerThatFailsOnRecordedIndexersTests
{
    private const string TorznabPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static Task<RecordedIndexerServer> WorkingAsync()
        => RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));

    private static async Task<RecordedIndexerServer> BrokenAsync(RecordedReply reply)
    {
        var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        server.AnswerEverythingWith = reply;
        return server;
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, string name, string baseUrl, string mediaScope = "movies", bool enabled = true, bool? interactiveSearch = null)
    {
        var response = await app.Client.PostAsJsonAsync("/api/indexers", new
        {
            name, protocol = "torznab", privacy = "public", baseUrl = baseUrl + TorznabPath, apiKey = Key, priority = 25,
            categories = "2000", tags = "", mediaScope, isEnabled = enabled, preferMagnetUrl = true, interactiveSearchEnabled = interactiveSearch ?? true
        });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> SearchAsync(ApplicationTestHost app, params string[] indexerIds)
    {
        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Theory]
    [InlineData("jackett-500", 500)]
    [InlineData("github-404", 404)]
    [InlineData("jackett-json", 200)]
    public async Task One_broken_indexer_does_not_cost_the_page_the_others_results_and_its_failure_is_shown_with_what_it_answered_and_kept_in_the_history_as_a_failure(string how, int status)
    {
        var reply = how switch
        {
            "jackett-500" => RecordedReplies.Jackett("unknown-indexer").Single.Reply,
            "github-404" => RecordedReplies.Github("api-not-found").Single.Reply,
            _ => RecordedReplies.Jackett("server-config").Single.Reply
        };
        await using var good = await WorkingAsync();
        await using var broken = await BrokenAsync(reply);
        await using var app = await ApplicationTestHost.StartAsync();
        var goodId = await AddAsync(app, "Working indexer", good.BaseUrl);
        var brokenId = await AddAsync(app, "Broken indexer", broken.BaseUrl);

        var search = await SearchAsync(app, goodId, brokenId);

        var candidate = Assert.Single(search.GetProperty("candidates").EnumerateArray());
        Assert.Equal("Sintel (2010) 720p BRRip x264 -YTS", candidate.GetProperty("releaseName").GetString());
        var failure = Assert.Single(search.GetProperty("failures").EnumerateArray());
        Assert.Equal(brokenId, failure.GetProperty("serviceId").GetString());
        Assert.Equal("Broken indexer", failure.GetProperty("serviceName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("message").GetString()));
        if (status != 200)
        {
            Assert.Equal(status, failure.GetProperty("httpStatus").GetInt32());
        }

        // Indexer History says the broken indexer failed; it does not say it found nothing.
        var history = (await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history")).GetProperty("items").EnumerateArray().ToArray();
        var brokenRows = history.Where(item => item.GetProperty("indexerId").GetString() == brokenId).ToArray();
        Assert.NotEmpty(brokenRows);
        Assert.All(brokenRows, item => Assert.Equal("failed", item.GetProperty("outcome").GetString()));
        Assert.Equal("matched", history.Single(item => item.GetProperty("indexerId").GetString() == goodId).GetProperty("outcome").GetString());
        Assert.Contains(failure.GetProperty("message").GetString()!, brokenRows.Single().GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_indexer_that_is_switched_off_or_does_not_cover_the_kind_of_title_or_has_interactive_search_off_is_not_asked()
    {
        await using var asked = await WorkingAsync();
        await using var off = await WorkingAsync();
        await using var wrongKind = await WorkingAsync();
        await using var noInteractive = await WorkingAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var askedId = await AddAsync(app, "Asked", asked.BaseUrl);
        var offId = await AddAsync(app, "Switched off", off.BaseUrl, enabled: false);
        var wrongKindId = await AddAsync(app, "Shows only", wrongKind.BaseUrl, mediaScope: "tv");
        var noInteractiveId = await AddAsync(app, "No interactive", noInteractive.BaseUrl, interactiveSearch: false);

        var search = await SearchAsync(app, askedId, offId, wrongKindId, noInteractiveId);

        Assert.Single(search.GetProperty("candidates").EnumerateArray());
        Assert.Contains(asked.Requests, request => request.Contains("t=search", StringComparison.Ordinal));
        Assert.DoesNotContain(off.Requests, request => request.Contains("t=search", StringComparison.Ordinal));
        Assert.DoesNotContain(wrongKind.Requests, request => request.Contains("t=search", StringComparison.Ordinal));
        Assert.DoesNotContain(noInteractive.Requests, request => request.Contains("t=search", StringComparison.Ordinal));
    }
}
