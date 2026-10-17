using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The Dashboard's Connections card (#1282, #1098) against a real Torznab indexer's recorded replies (Jackett 0.24.2850,
/// replayed by a local server): the last check Deluno stored for each connection, and Test all, which tests the ones
/// that are switched on and leaves the others alone.
/// </summary>
public sealed class ConnectionChecksOnRecordedJackettTests
{
    private const string JackettPath = "/api/v2.0/indexers/yts/results/torznab/api";

    private static Task<IndexerItem> AddAsync(ApplicationTestHost app, RecordedIndexerServer server, string name, bool enabled)
        => app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest(name, "torznab", "public", server.BaseUrl + JackettPath, "k-secret-1", 10, "2000", "", "movies", enabled), CancellationToken.None);

    [Fact]
    public async Task Testing_one_indexer_stores_its_check_and_the_stored_checks_list_carries_it_with_its_real_time()
    {
        await using var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await AddAsync(app, server, "Jackett YTS", enabled: true);
        Assert.Empty((await app.Client.GetFromJsonAsync<JsonElement>("/api/connections/checks")).EnumerateArray());
        var before = DateTimeOffset.UtcNow.AddSeconds(-2);

        var tested = await app.Client.PostAsync($"/api/connections/indexer/{indexer.Id}/test", null);
        Assert.True(tested.IsSuccessStatusCode, await tested.Content.ReadAsStringAsync());

        var checks = await app.Client.GetFromJsonAsync<JsonElement>("/api/connections/checks");
        var item = Assert.Single(checks.EnumerateArray());
        Assert.Equal("indexer", item.GetProperty("kind").GetString());
        Assert.Equal(indexer.Id, item.GetProperty("id").GetString());
        Assert.True(item.GetProperty("ok").GetBoolean());
        Assert.True(item.GetProperty("checkedUtc").GetDateTimeOffset() >= before);
    }

    [Fact]
    public async Task Test_all_tests_every_indexer_that_is_switched_on_and_returns_each_answer_and_leaves_a_switched_off_one_alone()
    {
        await using var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var first = await AddAsync(app, server, "First", enabled: true);
        var second = await AddAsync(app, server, "Second", enabled: true);
        var off = await AddAsync(app, server, "Switched off", enabled: false);

        var response = await app.Client.PostAsync("/api/connections/test-all", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var results = body.GetProperty("results").EnumerateArray().Where(result => result.GetProperty("kind").GetString() == "indexer").ToArray();
        Assert.Equal(
            new[] { first.Id, second.Id }.Order(),
            results.Select(result => result.GetProperty("id").GetString()!).Order());
        Assert.All(results, result => Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString()));
        Assert.DoesNotContain(results, result => result.GetProperty("id").GetString() == off.Id);
        Assert.True(body.GetProperty("tested").GetInt32() >= 2);

        // Each answer is now the stored last check, so a screen opened afterwards shows its true time.
        var stored = (await app.Client.GetFromJsonAsync<JsonElement>("/api/connections/checks")).EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToArray();
        Assert.Contains(first.Id, stored);
        Assert.Contains(second.Id, stored);
        Assert.DoesNotContain(off.Id, stored);
    }
}
