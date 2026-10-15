using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1301: Deluno's real indexer test and search against what NZBHydra2 9.1.1 answered as a Newznab indexer: its capabilities
/// (the test) and a search that found nothing. A local server replays the recorded replies; the app is the whole application over HTTP.
/// </summary>
public sealed class NewznabOnRecordedHydraTests
{
    private const string Key = "k-secret-hydra";

    private static async Task<(RecordedIndexerServer Server, ApplicationTestHost App, IndexerItem Indexer)> StartAsync(params string[] states)
    {
        var server = await RecordedIndexerServer.StartAsync(states.Select(RecordedReplies.Nzbhydra2).ToArray());
        var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("NZBHydra2", "newznab", "private", server.BaseUrl, Key, 10, "2000,5000", "", "both", true), CancellationToken.None);
        return (server, app, indexer);
    }

    [Fact]
    public async Task A_newznab_indexer_is_tested_by_asking_for_its_capabilities_with_the_key_and_a_valid_answer_is_healthy()
    {
        var (server, app, indexer) = await StartAsync("caps", "search-text");
        await using var _ = server; await using var __ = app;

        var response = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());
        var asked = server.Requests.First(request => request.Contains("apikey=" + Key, StringComparison.Ordinal));
        Assert.StartsWith("/api?", asked, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, body.GetRawText());
    }

    [Fact]
    public async Task A_search_the_newznab_indexer_answers_with_no_results_lists_nothing_and_is_not_a_failure()
    {
        var (server, app, indexer) = await StartAsync("caps", "search-text");
        await using var _ = server; await using var __ = app;

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        Assert.Empty(JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("candidates").EnumerateArray());
        var history = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history");
        Assert.DoesNotContain("auth-failure", history.GetRawText());
        Assert.DoesNotContain(Key, raw);
        Assert.Contains(server.Requests, request => request.Contains("t=search", StringComparison.Ordinal) && request.Contains("q=Sintel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_test_of_a_new_indexer_returns_the_categories_and_search_modes_the_indexer_gave_and_a_refused_caps_never_blocks_adding_it()
    {
        var (server, app, _) = await StartAsync("caps", "search-text");
        await using var _s = server; await using var _a = app;
        var form = new { name = "NZBHydra2 two", protocol = "newznab", privacy = "private", baseUrl = server.BaseUrl, apiKey = Key, priority = 10, categories = "2000", tags = "", mediaScope = "both", isEnabled = true };

        var tested = await app.Client.PostAsJsonAsync("/api/indexers/test", form);

        var body = await tested.Content.ReadFromJsonAsync<JsonElement>();
        var caps = body.GetProperty("caps");
        var recorded = System.Xml.Linq.XDocument.Parse(RecordedReplies.Nzbhydra2("caps").Exchanges[0].Reply.Body!)
            .Descendants().Where(element => element.Name.LocalName is "category" or "subcat").Select(element => (string)element.Attribute("id")!).ToHashSet();
        Assert.Equal(recorded, caps.GetProperty("categories").GetString()!.Split(',').ToHashSet());
        Assert.Equal("both", caps.GetProperty("mediaScope").GetString());

        server.AnswerEverythingWith = RecordedReplies.Nzbhydra2("caps-wrong-api-key").Exchanges[0].Reply;
        var added = await app.Client.PostAsJsonAsync("/api/indexers", form);
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_newznab_indexer_that_refuses_the_key_with_an_error_element_is_degraded_and_says_it_refused_the_request()
    {
        var (server, app, indexer) = await StartAsync("caps");
        await using var _ = server; await using var __ = app;
        server.AnswerEverythingWith = RecordedReplies.Nzbhydra2("caps-wrong-api-key").Exchanges[0].Reply;

        var response = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("degraded", body.GetProperty("healthStatus").GetString());
        Assert.Equal("auth", body.GetProperty("failureCategory").GetString());
        Assert.DoesNotContain(Key, body.GetRawText());
    }
}
