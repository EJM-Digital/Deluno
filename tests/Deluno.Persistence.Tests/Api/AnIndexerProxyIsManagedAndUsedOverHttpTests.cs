using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1301: the indexer-proxy routes over HTTP (add, list, edit with the password kept or cleared, remove), and that an indexer assigned to a
/// proxy is really asked through it. The "proxy" is a real loopback server that answers what Jackett 0.24.2850 really sent for the
/// call (the indexer's name is one nobody can resolve, so only a request that went through the proxy is answered).
/// </summary>
public sealed class AnIndexerProxyIsManagedAndUsedOverHttpTests
{
    private const string Unreachable = "http://indexer.invalid:9117";

    private sealed class ForwardProxy(WebApplication app, string address, List<(string Method, string Target, string Host)> seen) : IAsyncDisposable
    {
        public string Address { get; } = address;
        public IReadOnlyList<(string Method, string Target, string Host)> Seen { get { lock (seen) return seen.ToArray(); } }
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<ForwardProxy> StartProxyAsync(params string[] states)
    {
        var replies = states.Select(state => RecordedReplies.Jackett(state).Exchanges[0].Reply).ToArray();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var seen = new List<(string, string, string)>();
        app.Run(async context =>
        {
            lock (seen) seen.Add((context.Request.Method, context.Request.Path + context.Request.QueryString, context.Request.Host.ToString()));
            var reply = replies[0];
            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.Headers.TryGetValue("content-type", out var type) ? type : "application/xml";
            await context.Response.WriteAsync(reply.Body ?? string.Empty, Encoding.UTF8);
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ForwardProxy(app, new Uri(url).Authority, seen);
    }

    private static async Task<string> AddIndexerAsync(ApplicationTestHost app, string? proxyId)
        => (await (await app.Client.PostAsJsonAsync("/api/indexers", new { name = "Behind a proxy", protocol = "torznab", privacy = "public", baseUrl = Unreachable, apiKey = "k", priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true, proxyId }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

    [Fact]
    public async Task A_proxy_is_added_listed_edited_with_its_password_kept_or_cleared_and_removed_over_http_and_each_change_is_pushed()
    {
        var publisher = new RecordingRealtimeEventPublisher();
        var recorded = RecordedReplies.Jackett("search-feed");
        Assert.Equal(200, recorded.Exchanges[0].Reply.Status);
        await using var forward = await StartProxyAsync("search-feed");
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
            services.Replace(ServiceDescriptor.Singleton<IRealtimeEventPublisher>(publisher)));

        var added = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "http", address = forward.Address, username = "deluno", password = "s3cret-proxy" });
        var addedBody = await added.Content.ReadAsStringAsync();
        var created = JsonDocument.Parse(addedBody).RootElement;
        var id = created.GetProperty("id").GetString()!;
        var indexerId = await AddIndexerAsync(app, id);

        var listed = (await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-proxies")).EnumerateArray().Single();
        var keptResponse = await app.Client.PutAsJsonAsync($"/api/indexer-proxies/{id}", new { address = forward.Address, password = (string?)null });
        var kept = await keptResponse.Content.ReadFromJsonAsync<JsonElement>();
        var clearedResponse = await app.Client.PutAsJsonAsync($"/api/indexer-proxies/{id}", new { clearPassword = true });
        var cleared = await clearedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var changed = await app.Client.PutAsJsonAsync($"/api/indexer-proxies/{id}", new { password = "second-secret", isEnabled = true });
        var removed = await app.Client.DeleteAsync($"/api/indexer-proxies/{id}");

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.True(created.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain("s3cret-proxy", addedBody, StringComparison.Ordinal);
        Assert.False(created.TryGetProperty("password", out _));
        Assert.Equal("deluno", listed.GetProperty("username").GetString());
        Assert.Equal(new[] { "Behind a proxy" }, listed.GetProperty("assignedIndexerNames").EnumerateArray().Select(name => name.GetString()));
        Assert.True(kept.GetProperty("hasPassword").GetBoolean());
        Assert.False(cleared.GetProperty("hasPassword").GetBoolean());
        Assert.Equal("deluno", cleared.GetProperty("username").GetString());
        Assert.True((await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPassword").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/indexer-proxies")).Replace(" ", ""));
        var indexer = (await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers")).EnumerateArray().Single(item => item.GetProperty("id").GetString() == indexerId);
        Assert.True(!indexer.TryGetProperty("proxyId", out var assigned) || assigned.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(assigned.GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/indexer-proxies/{id}")).StatusCode);
        var pushes = publisher.Published.Where(entry => entry.Type == "IndexerProxy" && entry.Id == id).ToArray();
        Assert.True(pushes.Length >= 5, "the add, the three edits and the removal are each pushed");
        Assert.Contains(("Indexer", "*"), publisher.Published);
    }

    [Fact]
    public async Task The_proxy_routes_refuse_what_they_cannot_use_and_a_caller_with_no_sign_in_and_change_nothing()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var anonymous = app.CreateAnonymousClient();

        var blank = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "http", address = " " });
        var unknownKind = await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "carrier-pigeon", address = "192.168.1.9:3128" });
        var listedAnonymously = await anonymous.GetAsync("/api/indexer-proxies");
        var addedAnonymously = await anonymous.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "http", address = "192.168.1.9:3128" });

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, listedAnonymously.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, addedAnonymously.StatusCode);
        Assert.Equal("[]", (await app.Client.GetStringAsync("/api/indexer-proxies")).Replace(" ", ""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/indexer-proxies/missing", new { isEnabled = false })).StatusCode);
    }

    [Fact]
    public async Task An_indexer_assigned_to_a_proxy_is_tested_and_read_through_it_and_asked_directly_once_the_proxy_is_gone()
    {
        var feed = RecordedReplies.Jackett("search-feed").Exchanges[0].Reply;
        Assert.Equal(200, feed.Status);
        await using var forward = await StartProxyAsync("search-feed");
        await using var app = await ApplicationTestHost.StartAsync();
        var proxyId = (await (await app.Client.PostAsJsonAsync("/api/indexer-proxies", new { proxyType = "http", address = forward.Address })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var indexerId = await AddIndexerAsync(app, proxyId);

        var tested = await (await app.Client.PostAsync($"/api/indexers/{indexerId}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        var item = (await app.Services.GetRequiredService<IConnectionsRepository>().ListIndexersAsync(CancellationToken.None)).Single(i => i.Id == indexerId);
        IndexerFeedResult read;
        using (var scope = app.Services.CreateScope())
        {
            read = await scope.ServiceProvider.GetRequiredService<IIndexerFeedReader>().ReadRecentAsync(item, CancellationToken.None);
        }

        Assert.Equal("healthy", tested.GetProperty("healthStatus").GetString());
        Assert.True(read.Read, read.Problem);
        Assert.NotEmpty(read.Releases);
        Assert.True(forward.Seen.Count >= 2);
        Assert.All(forward.Seen, request => Assert.Equal("indexer.invalid:9117", request.Host));

        await app.Client.DeleteAsync($"/api/indexer-proxies/{proxyId}");
        var direct = await (await app.Client.PostAsync($"/api/indexers/{indexerId}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unreachable", direct.GetProperty("healthStatus").GetString());
    }
}
