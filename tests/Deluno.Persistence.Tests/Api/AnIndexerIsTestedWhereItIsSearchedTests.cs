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
    [InlineData("api.nzbplanet.net", "https://api.nzbplanet.net/api")]
    [InlineData(" api.nzbplanet.net/ ", "https://api.nzbplanet.net/api")]
    public void The_api_endpoint_is_the_base_url_with_api_unless_it_already_names_one(string baseUrl, string endpoint)
        => Assert.Equal(endpoint, IndexerApiEndpoint.For(baseUrl));

    /// <summary>#740: a custom API path replaces the default /api; a blank one keeps it.</summary>
    [Theory]
    [InlineData("https://indexer.example", "api/v2", "https://indexer.example/api/v2")]
    [InlineData("https://indexer.example", "/api/v2", "https://indexer.example/api/v2")]
    [InlineData("https://indexer.example/api/v2", "/api/v2", "https://indexer.example/api/v2")]
    [InlineData("https://indexer.example", "", "https://indexer.example/api")]
    [InlineData("https://indexer.example", null, "https://indexer.example/api")]
    public void A_custom_api_path_is_used_instead_of_the_default(string baseUrl, string? apiPath, string endpoint)
        => Assert.Equal(endpoint, IndexerApiEndpoint.For(baseUrl, apiPath));

    /// <summary>#740: the connection test and a real search both reach a non-default API path.</summary>
    [Fact]
    public async Task An_indexer_with_a_custom_api_path_is_tested_on_that_path()
    {
        await using var server = await NewznabServerAsync(apiSegment: "/api/v2");
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Custom path", "newznab", "private", server.BaseUrl, "fixture-key", 10, "2000", "", "both", true)
            {
                ApiPath = "/api/v2"
            },
            CancellationToken.None);

        var response = await app.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());
        Assert.StartsWith("/api/v2", server.LastPath);
    }

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

    /// <summary>
    /// #627: NZBPlanet was saved as "api.nzbplanet.net" and then tested only as
    /// "The address is not valid yet". A typed host means https, as in a
    /// browser, and an address that is still not one is refused at save.
    /// </summary>
    [Fact]
    public async Task An_address_typed_without_https_is_saved_as_https_and_junk_is_refused()
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var saved = await app.Client.PostAsJsonAsync("/api/indexers", new { name = "Planet", protocol = "newznab", baseUrl = "api.nzbplanet.net", apiKey = "k", mediaScope = "both", isEnabled = true });
        var junk = await app.Client.PostAsJsonAsync("/api/indexers", new { name = "Junk", protocol = "newznab", baseUrl = "not a web address", apiKey = "k", mediaScope = "both", isEnabled = true });

        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal("https://api.nzbplanet.net", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("baseUrl").GetString());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, junk.StatusCode);
        Assert.Contains("not a web address", await junk.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// #626: testing an edit before saving it. The drawer sends no key when it
    /// is left blank, which means "keep the saved one", so the test uses it.
    /// </summary>
    [Fact]
    public async Task Testing_an_unsaved_edit_uses_the_saved_key_when_none_is_typed()
    {
        await using var server = await NewznabServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Saved", "newznab", "private", "https://moved.invalid", "saved-key", 10, "2000", "", "both", true),
            CancellationToken.None);

        var response = await app.Client.PostAsJsonAsync(
            $"/api/indexers/test?id={indexer.Id}",
            new { name = "Saved", protocol = "newznab", baseUrl = server.BaseUrl, mediaScope = "both", isEnabled = true });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("healthy", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("healthStatus").GetString());
        Assert.Contains("apikey=saved-key", server.LastQuery);
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

        public string? LastQuery { get; set; }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    /// <summary>A server that, like NZBGeek, answers Newznab on /api (or a custom path) and HTML everywhere else.</summary>
    private static async Task<FakeServer> NewznabServerAsync(string apiSegment = "/api")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeServer? server = null;
        app.Run(async context =>
        {
            server!.LastPath = context.Request.Path.Value;
            server.LastUserAgent = context.Request.Headers.UserAgent.ToString();
            server.LastQuery = context.Request.QueryString.Value;
            if (context.Request.Path.StartsWithSegments(apiSegment))
            {
                context.Response.ContentType = "application/rss+xml";
                // NZBHydra2 9.1.1's recorded Newznab answer to a search that finds nothing.
                await context.Response.WriteAsync(Deluno.Tests.Recorded.RecordedReplies.Nzbhydra2("search-text").Exchanges[0].Reply.Body!);
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
