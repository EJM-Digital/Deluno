using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Notifications;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #859: a manual Test only ever announced a provider going bad
/// ("health.degraded"); nothing was ever said when it recovered. Covers an
/// indexer, a download client, and a processor connection — including a
/// non-indexer provider type the acceptance line calls for — each going
/// from unhealthy to healthy across two Test calls against the same fake
/// server. Media servers share the same helper (see
/// MediaServerEndpointRouteBuilderExtensions.DispatchHealthChangeAsync) and
/// are exercised by the media-server integration tests.
/// </summary>
public sealed class HealthRestoredNotificationTests
{
    [Fact]
    public async Task An_indexer_recovering_from_unhealthy_to_healthy_dispatches_health_restored_once()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var server = await ToggleableNewznabServerAsync();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));

        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Flaky", "newznab", "private", server.BaseUrl, "fixture-key", 10, "2000", "", "both", true),
            CancellationToken.None);

        server.Healthy = false;
        var degraded = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);
        Assert.True(degraded.IsSuccessStatusCode, await degraded.Content.ReadAsStringAsync());
        Assert.NotEqual("healthy", (await degraded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        Assert.Contains("health.degraded", outbound.DispatchedCategories);
        Assert.DoesNotContain("health.restored", outbound.DispatchedCategories);

        server.Healthy = true;
        var recovered = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);
        Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync());
        Assert.Equal("healthy", (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        Assert.Equal(1, outbound.DispatchedCategories.Count(category => category == "health.restored"));
    }

    [Fact]
    public async Task A_download_client_recovering_from_unhealthy_to_healthy_dispatches_health_restored_once()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var server = await ToggleableQbittorrentServerAsync();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));

        var client = await app.Services.GetRequiredService<IConnectionsRepository>().CreateDownloadClientAsync(
            new CreateDownloadClientRequest(
                Name: "Flaky client",
                Protocol: "qbittorrent",
                Host: null,
                Port: null,
                Username: null,
                Password: null,
                EndpointUrl: server.BaseUrl,
                MoviesCategory: "movies",
                TvCategory: "tv",
                CategoryTemplate: null,
                Priority: 1,
                IsEnabled: true),
            CancellationToken.None);

        server.Healthy = false;
        var degraded = await app.Client.PostAsync($"/api/download-clients/{client.Id}/test", null);
        Assert.True(degraded.IsSuccessStatusCode, await degraded.Content.ReadAsStringAsync());
        Assert.NotEqual("healthy", (await degraded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        Assert.Contains("health.degraded", outbound.DispatchedCategories);
        Assert.DoesNotContain("health.restored", outbound.DispatchedCategories);

        server.Healthy = true;
        var recovered = await app.Client.PostAsync($"/api/download-clients/{client.Id}/test", null);
        Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync());
        Assert.Equal("healthy", (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        Assert.Equal(1, outbound.DispatchedCategories.Count(category => category == "health.restored"));
    }

    [Fact]
    public async Task A_processor_connection_recovering_from_unhealthy_to_healthy_dispatches_health_restored_once()
    {
        var outbound = new RecordingOutboundNotificationService();
        await using var server = await ToggleableHeadProbeServerAsync();
        await using var app = await ApplicationTestHost.StartAsync(
            replaceServices: services => services.AddSingleton<IOutboundNotificationService>(outbound));

        var connection = await app.Services.GetRequiredService<IProcessorRepository>().CreateProcessorConnectionAsync(
            new CreateProcessorConnectionRequest("Flaky processor", "generic", server.BaseUrl, null, null, true),
            CancellationToken.None);

        server.Healthy = false;
        var degraded = await app.Client.PostAsync($"/api/integrations/processors/connections/{connection.Id}/test", null);
        Assert.True(degraded.IsSuccessStatusCode, await degraded.Content.ReadAsStringAsync());
        Assert.NotEqual("healthy", (await degraded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Contains("health.degraded", outbound.DispatchedCategories);
        Assert.DoesNotContain("health.restored", outbound.DispatchedCategories);

        server.Healthy = true;
        var recovered = await app.Client.PostAsync($"/api/integrations/processors/connections/{connection.Id}/test", null);
        Assert.True(recovered.IsSuccessStatusCode, await recovered.Content.ReadAsStringAsync());
        Assert.Equal("healthy", (await recovered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(1, outbound.DispatchedCategories.Count(category => category == "health.restored"));
    }

    private sealed class RecordingOutboundNotificationService : IOutboundNotificationService
    {
        public List<string> DispatchedCategories { get; } = [];

        public Task DispatchAsync(string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken, IReadOnlyList<string>? titleTags = null)
        {
            DispatchedCategories.Add(eventCategory);
            return Task.CompletedTask;
        }

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> DispatchToWebhookAsync(string webhookId, string eventCategory, string title, string message, string? detailsJson, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Notifications.Contracts.NotificationWebhookDeliveryResult?> ReplayAsync(string deliveryId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class ToggleableServer(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public bool Healthy { get; set; }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    /// <summary>Answers Newznab on /api when healthy, HTTP 500 otherwise.</summary>
    private static async Task<ToggleableServer> ToggleableNewznabServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        ToggleableServer? server = null;
        app.Run(async context =>
        {
            if (!context.Request.Path.StartsWithSegments("/api") || server!.Healthy is false)
            {
                context.Response.StatusCode = server!.Healthy ? StatusCodes.Status404NotFound : StatusCodes.Status500InternalServerError;
                return;
            }

            context.Response.ContentType = "application/rss+xml";
            await context.Response.WriteAsync("<?xml version=\"1.0\"?><rss version=\"2.0\" xmlns:newznab=\"http://www.newznab.com/DTD/2010/feeds/attributes/\"><channel></channel></rss>");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new ToggleableServer(app, address);
        return server;
    }

    /// <summary>Answers qBittorrent's version probe with 200 when healthy, 500 otherwise.</summary>
    private static async Task<ToggleableServer> ToggleableQbittorrentServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        ToggleableServer? server = null;
        app.Run(async context =>
        {
            if (!server!.Healthy)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("4.6.0");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new ToggleableServer(app, address);
        return server;
    }

    /// <summary>Answers a processor connection's HEAD probe with 200 when healthy, 500 otherwise.</summary>
    private static async Task<ToggleableServer> ToggleableHeadProbeServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        ToggleableServer? server = null;
        app.Run(context =>
        {
            context.Response.StatusCode = server!.Healthy ? StatusCodes.Status200OK : StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new ToggleableServer(app, address);
        return server;
    }
}
