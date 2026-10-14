using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1301: the media servers API (create, list, update, delete, test) with a real local server that answers as Jellyfin 12.2.0
/// recorded: the whole application over HTTP, the app's own client talking to it, nothing in between replaced.
/// </summary>
public sealed class MediaServerApiOnRecordedJellyfinTests
{
    private const string Token = "jf-secret-token-1";

    private sealed class JellyfinServer(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl => baseUrl;

        public List<string> Requests { get; } = [];

        public List<string> Authorizations { get; } = [];

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    /// <summary>The recorded reply to the kind of call (the catalogue listings are picked by what is asked for).</summary>
    private static RecordedReply? ReplyFor(string method, string path, string query)
    {
        if (method == "GET" && path == "/System/Info") return RecordedReplies.Jellyfin("identity").Exchanges[1].Reply;
        if (method == "GET" && path == "/Library/VirtualFolders") return RecordedReplies.Jellyfin("libraries").Exchanges[0].Reply;
        if (method == "GET" && path == "/Users") return RecordedReplies.Jellyfin("libraries").Exchanges[1].Reply;
        return null;
    }

    private static async Task<JellyfinServer> StartServerAsync(bool refuseEverything = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        JellyfinServer? server = null;
        app.Run(async context =>
        {
            server!.Requests.Add(context.Request.Method + " " + context.Request.Path + context.Request.QueryString);
            server.Authorizations.Add(context.Request.Headers.Authorization.ToString());
            var reply = refuseEverything
                ? RecordedReplies.Jellyfin("wrong-token").Exchanges[0].Reply
                : ReplyFor(context.Request.Method, context.Request.Path, context.Request.QueryString.Value ?? string.Empty);
            if (reply is null)
            {
                context.Response.StatusCode = 404;
                return;
            }

            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.Headers.TryGetValue("content-type", out var type) ? type : "application/json";
            await context.Response.WriteAsync(reply.Body ?? string.Empty, Encoding.UTF8);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new JellyfinServer(app, address.TrimEnd('/'));
        return server;
    }

    private static async Task<JsonElement> SaveAsync(ApplicationTestHost app, HttpMethod method, string path, object body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        var response = await app.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    [Fact]
    public async Task A_server_is_created_listed_tested_updated_and_deleted_and_its_token_is_never_returned_nor_written_in_plain()
    {
        await using var jellyfin = await StartServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();

        var created = await SaveAsync(app, HttpMethod.Post, "/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = jellyfin.BaseUrl, token = Token, isEnabled = true });
        var id = created.GetProperty("id").GetString()!;
        var tested = await SaveAsync(app, HttpMethod.Post, $"/api/media-servers/{id}/test", new { });

        Assert.True(tested.GetProperty("ok").GetBoolean(), tested.GetRawText());
        Assert.Equal("12.2.0", tested.GetProperty("version").GetString());
        Assert.Equal(2, tested.GetProperty("libraryCount").GetInt32());
        var listed = await app.Client.GetStringAsync("/api/media-servers");
        var server = Assert.Single(JsonSerializer.Deserialize<JsonElement>(listed).EnumerateArray());
        // A server is named after the app and where it runs (connections never ask for a name).
        Assert.StartsWith("Jellyfin on ", server.GetProperty("name").GetString());
        Assert.Equal("healthy", server.GetProperty("healthStatus").GetString());
        Assert.False(string.IsNullOrEmpty(server.GetProperty("serverId").GetString()));
        Assert.DoesNotContain(Token, listed);
        Assert.DoesNotContain(Token, created.GetRawText());
        Assert.All(jellyfin.Authorizations, header => Assert.Equal($"MediaBrowser Token=\"{Token}\"", header));
        foreach (var file in Directory.EnumerateFiles(app.DataRoot, "*.db*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1);
            Assert.DoesNotContain(Token, await reader.ReadToEndAsync());
        }

        var deleted = await app.Client.DeleteAsync($"/api/media-servers/{id}");
        Assert.Equal(System.Net.HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(JsonSerializer.Deserialize<JsonElement>(await app.Client.GetStringAsync("/api/media-servers")).EnumerateArray());
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await app.Client.DeleteAsync($"/api/media-servers/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_edit_with_a_blank_token_keeps_the_saved_one_and_a_new_address_forgets_who_the_old_server_was()
    {
        await using var first = await StartServerAsync();
        await using var second = await StartServerAsync();
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await SaveAsync(app, HttpMethod.Post, "/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = first.BaseUrl, token = Token, isEnabled = true });
        var id = created.GetProperty("id").GetString()!;
        await SaveAsync(app, HttpMethod.Post, $"/api/media-servers/{id}/test", new { });
        Assert.False(string.IsNullOrEmpty(Assert.Single(JsonSerializer.Deserialize<JsonElement>(await app.Client.GetStringAsync("/api/media-servers")).EnumerateArray()).GetProperty("serverId").GetString()));

        // Renamed with the token left blank: the saved token is still the one presented.
        await SaveAsync(app, HttpMethod.Put, $"/api/media-servers/{id}", new { kind = "jellyfin", name = "Lounge", baseUrl = first.BaseUrl, token = "", isEnabled = true });
        await SaveAsync(app, HttpMethod.Post, $"/api/media-servers/{id}/test", new { });
        Assert.Equal($"MediaBrowser Token=\"{Token}\"", first.Authorizations.Last());
        Assert.Equal(1, JsonSerializer.Deserialize<JsonElement>(await app.Client.GetStringAsync("/api/media-servers")).GetArrayLength());

        // Moved to another address: whoever the old one was is forgotten until the new one is asked.
        await SaveAsync(app, HttpMethod.Put, $"/api/media-servers/{id}", new { kind = "jellyfin", name = "Lounge", baseUrl = second.BaseUrl, token = "", isEnabled = true });
        var moved = Assert.Single(JsonSerializer.Deserialize<JsonElement>(await app.Client.GetStringAsync("/api/media-servers")).EnumerateArray());
        Assert.True(string.IsNullOrEmpty(moved.GetProperty("serverId").GetString()));
        Assert.Empty(second.Requests);
        await SaveAsync(app, HttpMethod.Post, $"/api/media-servers/{id}/test", new { });
        Assert.NotEmpty(second.Requests);
        Assert.False(string.IsNullOrEmpty(Assert.Single(JsonSerializer.Deserialize<JsonElement>(await app.Client.GetStringAsync("/api/media-servers")).EnumerateArray()).GetProperty("serverId").GetString()));
    }

    [Fact]
    public async Task A_server_that_refuses_the_token_is_listed_as_not_healthy_and_the_test_says_it_refused()
    {
        await using var jellyfin = await StartServerAsync(refuseEverything: true);
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await SaveAsync(app, HttpMethod.Post, "/api/media-servers", new { kind = "jellyfin", name = "Living room", baseUrl = jellyfin.BaseUrl, token = "wrong", isEnabled = true });
        var id = created.GetProperty("id").GetString()!;

        var tested = await SaveAsync(app, HttpMethod.Post, $"/api/media-servers/{id}/test", new { });

        Assert.False(tested.GetProperty("ok").GetBoolean());
        Assert.Contains("refused the token", tested.GetProperty("message").GetString());
        var server = Assert.Single(JsonSerializer.Deserialize<JsonElement>(await app.Client.GetStringAsync("/api/media-servers")).EnumerateArray());
        Assert.NotEqual("healthy", server.GetProperty("healthStatus").GetString());
    }
}
