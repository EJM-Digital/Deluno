using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Resilience;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The indexer and indexer-proxy routes when the request is right (coverage register, area api), against the real
/// replies of Jackett 0.24.2850 and FlareSolverr 3.5.2 (tests/fixtures/recorded) served over a real loopback port: the
/// real adapter and the real HTTP client talk to something that answers as those apps answered.
/// </summary>
public sealed class IndexerRoutesAgainstRecordedAppsTests
{
    private const string TorznabPath = "/api/v2.0/indexers/yts/results/torznab/api";

    private static Task<RecordedAppServer> JackettAsync(params string[] states)
        => RecordedAppServer.StartAsync("jackett", states.Select(RecordedReplies.Jackett));

    private static object Form(RecordedAppServer server, string apiKey = "0123456789abcdef0123456789abcdef", string? name = null)
        => new
        {
            name,
            protocol = "torznab",
            privacy = "public",
            baseUrl = server.BaseUrl + TorznabPath,
            apiKey,
            priority = 25,
            categories = "2000",
            tags = "",
            mediaScope = "movies",
            isEnabled = true
        };

    private static async Task<JsonElement> CreateAsync(ApplicationTestHost app, object form)
    {
        var response = await app.Client.PostAsJsonAsync("/api/indexers", form);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> IndexerAsync(ApplicationTestHost app, string id)
        => (await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Testing_a_filled_in_form_asks_the_real_indexer_for_its_capabilities_and_reports_it_healthy_without_saving()
    {
        await using var jackett = await JackettAsync("caps");
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/indexers/test", Form(jackett));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());
        Assert.Contains(jackett.Requests, request => request.PathAndQuery.Contains("t=caps", StringComparison.Ordinal));
        Assert.All(jackett.Requests, request => Assert.Contains("apikey=0123456789abcdef0123456789abcdef", request.PathAndQuery, StringComparison.Ordinal));
        Assert.Empty(jackett.Unanswered);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/indexers")).Replace(" ", ""));
    }

    [Fact]
    public async Task Testing_a_form_with_the_wrong_api_key_says_so_plainly_and_is_not_healthy()
    {
        await using var jackett = await JackettAsync("caps-wrong-api-key");
        await using var app = await ApplicationTestHost.StartAsync();

        var response = await app.Client.PostAsJsonAsync("/api/indexers/test", Form(jackett, apiKey: "wrong-key"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual("healthy", body.GetProperty("healthStatus").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.Empty(jackett.Unanswered);
    }

    [Fact]
    public async Task A_saved_indexer_is_tested_edited_and_removed_and_each_step_shows_in_the_list()
    {
        await using var jackett = await JackettAsync("caps");
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await CreateAsync(app, Form(jackett));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("untested", created.GetProperty("healthStatus").GetString());

        var tested = await app.Client.PostAsync($"/api/indexers/{id}/test", content: null);

        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.Equal("healthy", (await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        Assert.Equal("healthy", (await IndexerAsync(app, id)).GetProperty("healthStatus").GetString());
        Assert.Empty(jackett.Unanswered);

        var edit = await app.Client.PutAsJsonAsync($"/api/indexers/{id}", new { priority = 5, categories = "2000,2040", isEnabled = false });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var edited = await IndexerAsync(app, id);
        Assert.Equal(5, edited.GetProperty("priority").GetInt32());
        Assert.Equal("2000, 2040", edited.GetProperty("categories").GetString());
        Assert.False(edited.GetProperty("isEnabled").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/indexers/no-such-indexer", new { priority = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/indexers/no-such-indexer/test", content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync($"/api/indexers/{id}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/indexers")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/indexers/{id}")).StatusCode);
    }

    [Fact]
    public async Task Resetting_a_tripped_circuit_closes_the_circuit_searches_consult_and_the_indexer_tests_healthy_again()
    {
        await using var jackett = await JackettAsync("caps");
        await using var app = await ApplicationTestHost.StartAsync();
        var id = (await CreateAsync(app, Form(jackett))).GetProperty("id").GetString()!;
        var resilience = app.Services.GetRequiredService<IIntegrationResiliencePolicy>();
        var searchKey = $"indexer:{id}:{jackett.BaseUrl}";
        var opened = await resilience.ExecuteAsync(
            new IntegrationResilienceRequest(searchKey, "search", MaxAttempts: 1, FailureThreshold: 1, ServiceType: "indexer", ServiceId: id, ServiceName: "Jackett"),
            _ => Task.FromResult("down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.True(opened.CircuitOpened);
        Assert.True(resilience.IsCircuitOpen(searchKey, out _));

        var reset = await app.Client.PostAsync($"/api/indexers/{id}/reset-circuit", content: null);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False(resilience.IsCircuitOpen(searchKey, out _), "the circuit a search consults is closed, not only the stored row");
        Assert.Equal("untested", (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        var tested = await app.Client.PostAsync($"/api/indexers/{id}/test", content: null);
        Assert.Equal("healthy", (await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsync("/api/indexers/no-such-indexer/reset-circuit", content: null)).StatusCode);
    }

    [Fact]
    public async Task An_indexer_proxy_is_added_edited_and_removed_and_the_indexer_that_used_it_goes_direct()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var added = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "flaresolverr", address = "http://192.168.1.9:8191" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var proxy = await added.Content.ReadFromJsonAsync<JsonElement>();
        var proxyId = proxy.GetProperty("id").GetString()!;
        Assert.Equal("flaresolverr", proxy.GetProperty("proxyType").GetString());
        Assert.Equal("FlareSolverr on 192.168.1.9", proxy.GetProperty("name").GetString());

        var edited = await app.Client.PutAsJsonAsync($"/api/indexer-proxies/{proxyId}", new { address = "http://192.168.1.20:8191", isEnabled = true });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("FlareSolverr on 192.168.1.20", (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());

        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Behind a challenge", "torznab", "public", "http://indexer.invalid/api", "key", 25, "2000", "", "movies", IsEnabled: true, ProxyId: proxyId),
            CancellationToken.None);
        Assert.Equal(proxyId, (await IndexerAsync(app, indexer.Id)).GetProperty("proxyId").GetString());

        Assert.Equal(HttpStatusCode.OK, (await app.Client.DeleteAsync($"/api/indexer-proxies/{proxyId}")).StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/indexer-proxies")).Replace(" ", ""));
        Assert.Equal(JsonValueKind.Null, (await IndexerAsync(app, indexer.Id)).GetProperty("proxyId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/indexer-proxies/{proxyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/indexer-proxies/no-such-proxy", new { address = "http://localhost:8191" })).StatusCode);
    }
}
