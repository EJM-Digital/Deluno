using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The connection test asks an indexer the same address a search does (#624).
///
/// <para><b>Why this file exists.</b> NZBGeek, entered as its usual
/// <c>https://api.nzbgeek.info</c>, tested as degraded on the rig: "the
/// response did not look like Newznab". Search added <c>/api</c> to the base
/// URL and the test did not, so the test got NZBGeek's HTML home page.</para>
/// </summary>
public sealed class AnIndexerIsTestedWhereItIsSearchedTests
{
    [Theory]
    [InlineData("https://api.nzbgeek.info", "https://api.nzbgeek.info/api")]
    [InlineData("https://api.nzbgeek.info/", "https://api.nzbgeek.info/api")]
    [InlineData("https://api.nzbgeek.info/api", "https://api.nzbgeek.info/api")]
    [InlineData("https://api.nzbgeek.info/api/", "https://api.nzbgeek.info/api/")]
    [InlineData("http://127.0.0.1:9117/api/v2.0/indexers/all/results/torznab/api", "http://127.0.0.1:9117/api/v2.0/indexers/all/results/torznab/api")]
    [InlineData("https://indexer.example/custom?t=search", "https://indexer.example/custom?t=search")]
    public void The_api_endpoint_is_the_base_url_with_api_unless_it_already_names_one(string baseUrl, string endpoint)
        => Assert.Equal(endpoint, IndexerApiEndpoint.For(baseUrl));

    [Fact]
    public async Task An_indexer_entered_without_api_tests_healthy_when_its_api_answers()
    {
        await using var server = await NewznabServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Geek-alike", "newznab", "private", server.BaseUrl, "fixture-key", 10, "2000", "", "both", true),
            CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());
        Assert.StartsWith("/api", server.LastPath);
        Assert.Equal(IndexerApiEndpoint.UserAgent, server.LastUserAgent);
    }

    /// <summary>#625: NZBGeek refuses a request with no User-Agent, so searches send Deluno's.</summary>
    [Fact]
    public async Task Searches_tell_the_indexer_they_are_Deluno()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("indexers");

        Assert.Equal(IndexerApiEndpoint.UserAgent, http.DefaultRequestHeaders.UserAgent.ToString());
    }

    private sealed class FakeServer(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public string? LastPath { get; set; }

        public string? LastUserAgent { get; set; }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    /// <summary>A server that, like NZBGeek, answers Newznab on /api and HTML everywhere else.</summary>
    private static async Task<FakeServer> NewznabServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeServer? server = null;
        app.Run(async context =>
        {
            server!.LastPath = context.Request.Path.Value;
            server.LastUserAgent = context.Request.Headers.UserAgent.ToString();
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.ContentType = "application/rss+xml";
                await context.Response.WriteAsync("<?xml version=\"1.0\"?><rss version=\"2.0\" xmlns:newznab=\"http://www.newznab.com/DTD/2010/feeds/attributes/\"><channel></channel></rss>");
                return;
            }

            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<!DOCTYPE html><html><body>Home page</body></html>");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeServer(app, address);
        return server;
    }
}
