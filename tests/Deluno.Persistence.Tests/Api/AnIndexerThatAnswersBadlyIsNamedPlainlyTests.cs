using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net;
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
/// #1301: how the test of an indexer names what it got back when that is not a Torznab/Newznab answer, through the whole application over HTTP.
/// The replies are ones an app really sent: Jackett 0.24.2850's JSON for a path that is not its Torznab one (server-config), the 404 GitHub's
/// API gave for a thing that does not exist (api-not-found: the status is what is classified, not the app), and a port nobody listens on.
/// </summary>
public sealed class AnIndexerThatAnswersBadlyIsNamedPlainlyTests
{
    private const string Key = "k-secret-jackett";

    private static async Task<(WebApplication App, string BaseUrl)> ServeAsync(RecordedReply? reply, HttpStatusCode? statusLine = null, string? retryAfter = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async context =>
        {
            if (reply is null)
            {
                context.Response.StatusCode = (int)statusLine!.Value;
                if (retryAfter is not null) context.Response.Headers.RetryAfter = retryAfter;
                return;
            }

            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = reply.Headers.TryGetValue("content-type", out var type) ? type : "application/json";
            await context.Response.WriteAsync(reply.Body ?? string.Empty, Encoding.UTF8);
        });
        await app.StartAsync();
        return (app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/'));
    }

    private static async Task<(string Id, JsonElement Tested)> AddAndTestAsync(ApplicationTestHost app, string baseUrl)
    {
        var made = await app.Client.PostAsJsonAsync("/api/indexers", new { name = "Jackett YTS", protocol = "torznab", privacy = "public", baseUrl, apiKey = Key, priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var id = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var tested = await app.Client.PostAsync($"/api/indexers/{id}/test", null);
        Assert.True(tested.IsSuccessStatusCode, await tested.Content.ReadAsStringAsync());
        return (id, await tested.Content.ReadFromJsonAsync<JsonElement>());
    }

    [Fact]
    public async Task An_address_that_answers_with_json_and_not_a_Torznab_feed_is_degraded_as_an_unexpected_response()
    {
        var recorded = RecordedReplies.Jackett("server-config").Exchanges[0].Reply;
        Assert.Equal(200, recorded.Status);
        var (server, url) = await ServeAsync(recorded);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync();

        var (_, tested) = await AddAndTestAsync(app, url);

        Assert.Equal("degraded", tested.GetProperty("healthStatus").GetString());
        Assert.Equal("unexpected-response", tested.GetProperty("failureCategory").GetString());
        Assert.Contains("did not look like Torznab", tested.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, tested.GetRawText());
    }

    [Fact]
    public async Task A_404_is_degraded_as_an_http_problem_that_names_the_status_not_as_unreachable_and_not_as_a_refused_key()
    {
        var recorded = RecordedReplies.Github("api-not-found").Exchanges[0].Reply;
        Assert.Equal(404, recorded.Status);
        var (server, url) = await ServeAsync(recorded);
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync();

        var (_, tested) = await AddAndTestAsync(app, url);

        Assert.Equal("degraded", tested.GetProperty("healthStatus").GetString());
        Assert.Equal("http", tested.GetProperty("failureCategory").GetString());
        Assert.Contains("returned 404", tested.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_nothing_listens_on_is_unreachable_as_a_connection_problem()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var app = await ApplicationTestHost.StartAsync();

        var (_, tested) = await AddAndTestAsync(app, $"http://127.0.0.1:{port}");

        Assert.Equal("unreachable", tested.GetProperty("healthStatus").GetString());
        Assert.Equal("connectivity", tested.GetProperty("failureCategory").GetString());
    }

    [Fact]
    public async Task A_429_with_a_wait_asked_for_is_a_busy_indexer_that_is_not_called_a_bad_key_and_the_wait_is_kept()
    {
        var (server, url) = await ServeAsync(null, HttpStatusCode.TooManyRequests, retryAfter: "120");
        await using var _ = server;
        await using var app = await ApplicationTestHost.StartAsync();

        var (id, tested) = await AddAndTestAsync(app, url);
        var searched = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { id } });
        var raw = await searched.Content.ReadAsStringAsync();

        Assert.True(searched.IsSuccessStatusCode, raw);
        Assert.NotEqual("auth", tested.GetProperty("failureCategory").GetString());
        var failure = Assert.Single(JsonDocument.Parse(raw).RootElement.GetProperty("failures").EnumerateArray());
        Assert.Equal(429, failure.GetProperty("httpStatus").GetInt32());
        Assert.Equal("RateLimit", failure.GetProperty("kind").GetString());
        Assert.True(failure.GetProperty("isTransient").GetBoolean());
        Assert.Empty(JsonDocument.Parse(raw).RootElement.GetProperty("candidates").EnumerateArray());
    }
}
