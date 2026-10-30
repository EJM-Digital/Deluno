using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Resilience;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #606: The Pirate Bay's search circuit opened, survived a redeploy because
/// it is persisted in integration_circuit_states, and the "Reset circuit"
/// button only ever touched the indexer_sources row -- health_status and
/// last_health_message -- never the resilience policy that a search actually
/// consults. Reset said "Circuit reset manually" and nothing changed.
///
/// These exercise the real endpoints over HTTP, through the same
/// <see cref="IIntegrationResiliencePolicy"/> the search planner uses, so a
/// DI wiring mistake (the endpoint resolving a different instance, say)
/// would fail these the way it would fail in production.
/// </summary>
public sealed class ResetCircuitClosesTheRealCircuitTests
{
    [Fact]
    public async Task Reset_circuit_closes_the_circuit_a_search_would_have_consulted()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IConnectionsRepository>();
        var resiliencePolicy = app.Services.GetRequiredService<IIntegrationResiliencePolicy>();

        var indexer = await repository.CreateIndexerAsync(
            new CreateIndexerRequest("The Pirate Bay", "torznab", "public", "https://thepiratebay.example", "", 10, "2000", "", "both", true),
            CancellationToken.None);

        // Stands in for what the search path does: build its own key for this
        // indexer and drive it open through repeated failures. The exact
        // suffix is deliberately not the same one the manual Test endpoint
        // would build, because that mismatch is part of the bug.
        var searchKey = $"indexer:{indexer.Id}:https://thepiratebay.example";
        await OpenCircuitAsync(resiliencePolicy, "indexer", indexer.Id, indexer.Name, searchKey);
        Assert.True(resiliencePolicy.IsCircuitOpen(searchKey, out _));

        var response = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/reset-circuit", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        Assert.False(resiliencePolicy.IsCircuitOpen(searchKey, out _));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("untested", body.GetProperty("healthStatus").GetString());
    }

    [Fact]
    public async Task A_successful_test_closes_the_circuit_even_when_it_was_opened_under_a_different_key()
    {
        await using var server = await HealthyServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IConnectionsRepository>();
        var resiliencePolicy = app.Services.GetRequiredService<IIntegrationResiliencePolicy>();

        var indexer = await repository.CreateIndexerAsync(
            new CreateIndexerRequest("Geek-alike", "newznab", "private", server.BaseUrl, "fixture-key", 10, "2000", "", "both", true),
            CancellationToken.None);

        // A circuit opened under the *search* key format, not the Test
        // endpoint's own key. A Test that only reset its own key would leave
        // this one open, and the next search would still skip the indexer
        // even though the person who tested it was told it was healthy.
        var searchKey = $"indexer:{indexer.Id}:{server.BaseUrl}";
        await OpenCircuitAsync(resiliencePolicy, "indexer", indexer.Id, indexer.Name, searchKey);
        Assert.True(resiliencePolicy.IsCircuitOpen(searchKey, out _));

        var response = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());

        Assert.False(resiliencePolicy.IsCircuitOpen(searchKey, out _));
    }

    private static async Task OpenCircuitAsync(
        IIntegrationResiliencePolicy policy,
        string serviceType,
        string serviceId,
        string serviceName,
        string key)
    {
        var request = new IntegrationResilienceRequest(
            key,
            "search",
            MaxAttempts: 1,
            FailureThreshold: 1,
            ServiceType: serviceType,
            ServiceId: serviceId,
            ServiceName: serviceName);

        var result = await policy.ExecuteAsync(
            request,
            _ => Task.FromResult("down"),
            _ => IntegrationResilienceOutcome.RetryableFailure,
            CancellationToken.None);
        Assert.True(result.CircuitOpened);
    }

    private sealed class FakeServer(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<FakeServer> HealthyServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            context.Response.ContentType = "application/rss+xml";
            // NZBHydra2 9.1.1's recorded Newznab answer to a search that finds nothing.
            await context.Response.WriteAsync(Deluno.Tests.Recorded.RecordedReplies.Nzbhydra2("search-text").Exchanges[0].Reply.Body!);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeServer(app, address);
    }
}
