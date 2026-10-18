using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Search page asks every indexer it was given, and what they found comes back as one list ranked once, not one indexer's
/// ranking after another's. Jackett 0.24.2850 is the recorded app over a real loopback port, answering for two of its trackers: the
/// public tracker with TV releases and the film tracker with a film; both are linked to Deluno as separate indexers.
/// </summary>
public sealed class TheSearchPageAsksEveryLinkedIndexerAndRanksTheirReleasesOnceTests
{
    private const string FirstPath = "/api/v2.0/indexers/thepiratebay/results/torznab/api";
    private const string SecondPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Every_linked_indexer_is_asked_and_the_releases_of_both_come_back_in_one_ranked_list_with_none_lost()
    {
        await using var jackett = await RecordedAppServer.StartAsync(
            "jackett",
            new[] { "caps", "search-tv", "search-text" }.Select(RecordedReplies.Jackett),
            RecordedAppServer.Chain(
                (request, _) => request.RequestUri is { } uri && uri.Query.Contains("t=search&q=", StringComparison.Ordinal)
                    ? (uri.AbsolutePath.Contains("/yts/", StringComparison.Ordinal) ? RecordedReplies.Jackett("search-text").Exchanges[0] : RecordedReplies.Jackett("search-tv").Exchanges[1]).Reply.ToResponse()
                    : null,
                RecordedAppServer.ByQuery("t", ("caps", RecordedReplies.Jackett("caps").Exchanges[0]), ("tvsearch", RecordedReplies.Jackett("search-tv").Exchanges[0]))));
        await using var app = await ApplicationTestHost.StartAsync();
        var ids = new List<string>();
        foreach (var path in new[] { FirstPath, SecondPath })
        {
            var created = await app.Client.PostAsJsonAsync("/api/indexers", new
            {
                protocol = "torznab", privacy = "public", baseUrl = jackett.BaseUrl + path, apiKey = Key, priority = 25,
                categories = "2000,5000", tags = "", mediaScope = "both", isEnabled = true, preferMagnetUrl = true
            });
            Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
            ids.Add((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);
        }

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Breaking Bad", searchType = "tv", indexerIds = ids });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var body = JsonSerializer.Deserialize<JsonElement>(raw);
        Assert.Equal(2, body.GetProperty("indexersQueried").GetInt32());
        Assert.Contains(jackett.Requests, request => request.PathAndQuery.StartsWith(FirstPath, StringComparison.Ordinal) && request.PathAndQuery.Contains("t=tvsearch", StringComparison.Ordinal) || request.PathAndQuery.StartsWith(FirstPath, StringComparison.Ordinal) && request.PathAndQuery.Contains("t=search&q=", StringComparison.Ordinal));
        Assert.Contains(jackett.Requests, request => request.PathAndQuery.StartsWith(SecondPath, StringComparison.Ordinal) && request.PathAndQuery.Contains("t=search&q=", StringComparison.Ordinal));
        var candidates = body.GetProperty("candidates").EnumerateArray().ToArray();
        var byIndexer = candidates.GroupBy(candidate => candidate.GetProperty("indexerName").GetString()).ToDictionary(group => group.Key!, group => group.Count());
        Assert.Contains("thepiratebay", byIndexer.Keys);
        Assert.Contains("yts", byIndexer.Keys);
        // Ranked once: a status never comes back after a different one, which is what a list ranked indexer by indexer would do.
        var statuses = candidates.Select(candidate => candidate.GetProperty("decisionStatus").GetString()).ToArray();
        var seen = new List<string?>();
        foreach (var status in statuses)
        {
            if (seen.Count == 0 || seen[^1] != status) seen.Add(status);
        }

        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }
}
