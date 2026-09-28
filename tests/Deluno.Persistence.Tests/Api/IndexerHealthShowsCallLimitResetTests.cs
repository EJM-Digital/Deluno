using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #977: an indexer at its query or grab limit shows in Health, in plain
/// words, how many it has used and when the cap resets.
/// <see cref="IIndexerCallLimiter.Peek"/> already computed this for the
/// Health section's use, but nothing outside its own test called it and no
/// API exposed <c>ResetsAtUtc</c>. GET /api/indexers now computes
/// QueryLimitStatus/GrabLimitStatus per indexer from the live limiter so the
/// screen can read them directly.
/// </summary>
public sealed class IndexerHealthShowsCallLimitResetTests
{
    [Fact]
    public async Task An_indexer_at_its_query_limit_reports_usage_and_reset_time_over_the_api()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IConnectionsRepository>();
        var callLimiter = app.Services.GetRequiredService<IIndexerCallLimiter>();

        var created = await repository.CreateIndexerAsync(
            new CreateIndexerRequest("Capped Tracker", "torznab", "private", "https://capped.example", "key", 10, "2000", "", "both", true),
            CancellationToken.None);

        var updated = await repository.UpdateIndexerAsync(
            created.Id,
            new UpdateIndexerRequest(
                Name: null, Protocol: null, Privacy: null, BaseUrl: null, ApiKey: null, Priority: null,
                Categories: null, Tags: null, MediaScope: null, IsEnabled: null,
                QueryLimit: 1, LimitsUnit: "day"),
            CancellationToken.None);
        Assert.NotNull(updated);

        var now = DateTimeOffset.UtcNow;
        // Reach the query cap directly through the same limiter the search
        // planner consults, so the read-side status is exercised against
        // real state rather than a freshly seeded one.
        Assert.True(callLimiter.TryConsumeQuery(updated!, now).Allowed);
        var second = callLimiter.TryConsumeQuery(updated!, now);
        Assert.False(second.Allowed);

        var response = await app.Client.GetAsync("/api/indexers");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        var indexerJson = items.EnumerateArray().First(item => item.GetProperty("id").GetString() == created.Id);

        var queryLimitStatus = indexerJson.GetProperty("queryLimitStatus");
        Assert.False(queryLimitStatus.GetProperty("allowed").GetBoolean());
        Assert.Equal(1, queryLimitStatus.GetProperty("limit").GetInt32());
        Assert.Equal(1, queryLimitStatus.GetProperty("count").GetInt32());
        Assert.Equal("day", queryLimitStatus.GetProperty("unit").GetString());
        Assert.NotNull(queryLimitStatus.GetProperty("resetsAtUtc").GetString());
    }

    [Fact]
    public async Task An_indexer_under_its_limit_reports_no_reset_time()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IConnectionsRepository>();

        var created = await repository.CreateIndexerAsync(
            new CreateIndexerRequest("Uncapped Tracker", "torznab", "private", "https://uncapped.example", "key", 10, "2000", "", "both", true),
            CancellationToken.None);

        var response = await app.Client.GetAsync("/api/indexers");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        var indexerJson = items.EnumerateArray().First(item => item.GetProperty("id").GetString() == created.Id);

        Assert.Equal(JsonValueKind.Null, indexerJson.GetProperty("queryLimitStatus").GetProperty("limit").ValueKind);
        Assert.Equal(JsonValueKind.Null, indexerJson.GetProperty("grabLimitStatus").GetProperty("limit").ValueKind);
    }
}
