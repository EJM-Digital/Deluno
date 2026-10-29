using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Integrations.Search;
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
/// #1301: a Torznab/Newznab indexer's test, search and RSS read, and the indexer-proxy routes, through the whole application over
/// a real loopback port. What the indexer says is Jackett 0.24.2850's recorded reply; a bare HTTP status carries no app shape, so
/// those are built here; a redirect points at the recorded reply, and the "request limit reached" error is the recorded Jackett
/// error element with only its code and wording changed (no account is at hand to be limited).
/// </summary>
public sealed class AnIndexerAnswersOverRealHttpTests
{
    private const string Key = "k-secret-jackett";

    private sealed class Served(WebApplication app, string baseUrl, List<string> requests, List<IHeaderDictionary> headers) : IAsyncDisposable
    {
        public string BaseUrl { get; } = baseUrl;
        public IReadOnlyList<string> Requests { get { lock (requests) return requests.ToArray(); } }
        public IReadOnlyList<IHeaderDictionary> Headers { get { lock (requests) return headers.ToArray(); } }
        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<Served> ServeAsync(Func<HttpContext, Task> answer)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var requests = new List<string>();
        var headers = new List<IHeaderDictionary>();
        app.Run(async context =>
        {
            lock (requests)
            {
                requests.Add(context.Request.Path + context.Request.QueryString);
                headers.Add(context.Request.Headers);
            }

            await answer(context);
        });
        await app.StartAsync();
        return new Served(app, app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/'), requests, headers);
    }

    private static Task Write(HttpContext context, RecordedReply reply)
    {
        context.Response.StatusCode = reply.Status;
        context.Response.ContentType = reply.Headers.TryGetValue("content-type", out var type) ? type : "application/xml";
        return context.Response.WriteAsync(reply.Body ?? string.Empty, Encoding.UTF8);
    }

    private static Task Status(HttpContext context, int status, string? retryAfter = null)
    {
        context.Response.StatusCode = status;
        if (retryAfter is not null) context.Response.Headers.RetryAfter = retryAfter;
        return Task.CompletedTask;
    }

    private static async Task<string> AddAsync(ApplicationTestHost app, string baseUrl, string? proxyId = null, string name = "Jackett YTS", int? requestIntervalSeconds = null)
    {
        var made = await app.Client.PostAsJsonAsync("/api/indexers", new { name, protocol = "torznab", privacy = "public", baseUrl, apiKey = Key, priority = 25, categories = "2000", tags = "", mediaScope = "movies", isEnabled = true, proxyId, requestIntervalSeconds });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> TestAsync(ApplicationTestHost app, string id)
    {
        var tested = await app.Client.PostAsync($"/api/indexers/{id}/test", null);
        Assert.True(tested.IsSuccessStatusCode, await tested.Content.ReadAsStringAsync());
        return await tested.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> SearchAsync(ApplicationTestHost app, string id)
    {
        var searched = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { id } });
        var raw = await searched.Content.ReadAsStringAsync();
        Assert.True(searched.IsSuccessStatusCode, raw);
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static async Task<IndexerFeedResult> ReadFeedAsync(ApplicationTestHost app, string id)
    {
        var item = (await app.Services.GetRequiredService<IConnectionsRepository>().ListIndexersAsync(CancellationToken.None)).Single(indexer => indexer.Id == id);
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IIndexerFeedReader>().ReadRecentAsync(item, CancellationToken.None);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_401_or_403_from_the_indexer_is_degraded_as_a_refused_key_and_the_key_is_not_shown(int status)
    {
        await using var indexer = await ServeAsync(context => Status(context, status));
        await using var app = await ApplicationTestHost.StartAsync();

        var tested = await TestAsync(app, await AddAsync(app, indexer.BaseUrl));

        Assert.Equal("degraded", tested.GetProperty("healthStatus").GetString());
        Assert.Equal("auth", tested.GetProperty("failureCategory").GetString());
        Assert.Contains($"authentication failed with {status}", tested.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, tested.GetRawText());
    }

    /// <summary>The address a redirect leads to carries the same query, so the key travels in the address on both hops.</summary>
    private static Func<HttpContext, Task> RedirectsOnceThenAnswers(RecordedReply reply)
        => context => context.Request.Path.StartsWithSegments("/moved")
            ? Write(context, reply)
            : Redirect(context);

    private static Task Redirect(HttpContext context)
    {
        context.Response.StatusCode = 302;
        context.Response.Headers.Location = "/moved/api" + context.Request.QueryString;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_redirected_test_is_followed_and_is_healthy_and_the_key_is_in_the_address_of_both_hops()
    {
        var recorded = RecordedReplies.Jackett("search-text").Exchanges[0].Reply;
        await using var indexer = await ServeAsync(RedirectsOnceThenAnswers(recorded));
        await using var app = await ApplicationTestHost.StartAsync();

        var tested = await TestAsync(app, await AddAsync(app, indexer.BaseUrl));

        Assert.Equal("healthy", tested.GetProperty("healthStatus").GetString());
        Assert.Equal(2, indexer.Requests.Count);
        Assert.StartsWith("/api", indexer.Requests[0], StringComparison.Ordinal);
        Assert.StartsWith("/moved/api", indexer.Requests[1], StringComparison.Ordinal);
        Assert.All(indexer.Requests, request => Assert.Contains($"apikey={Key}", request, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_redirected_search_and_a_redirected_feed_read_are_followed_and_give_the_recorded_results()
    {
        var search = RecordedReplies.Jackett("search-text").Exchanges[0].Reply;
        var feed = RecordedReplies.Jackett("search-feed").Exchanges[0].Reply;
        await using var indexer = await ServeAsync(RedirectsOnceThenAnswers(search));
        await using var feedIndexer = await ServeAsync(RedirectsOnceThenAnswers(feed));
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, indexer.BaseUrl);
        var feedId = await AddAsync(app, feedIndexer.BaseUrl, name: "Jackett feed");

        var searched = await SearchAsync(app, id);
        var read = await ReadFeedAsync(app, feedId);

        Assert.NotEmpty(searched.GetProperty("candidates").EnumerateArray());
        Assert.Empty(searched.GetProperty("failures").EnumerateArray());
        Assert.True(read.Read, read.Problem);
        Assert.NotEmpty(read.Releases);
        Assert.Contains(indexer.Requests, request => request.StartsWith("/moved/api", StringComparison.Ordinal) && request.Contains($"apikey={Key}", StringComparison.Ordinal));
        Assert.Contains(feedIndexer.Requests, request => request.StartsWith("/moved/api", StringComparison.Ordinal) && request.Contains($"apikey={Key}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_search_answered_500_is_tried_three_times_a_second_failed_search_makes_the_indexer_rest_and_a_resting_indexer_is_not_asked()
    {
        await using var indexer = await ServeAsync(context => Status(context, 500));
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, indexer.BaseUrl);

        var first = await SearchAsync(app, id);
        var afterFirst = indexer.Requests.Count;
        var second = await SearchAsync(app, id);
        var afterSecond = indexer.Requests.Count;
        var third = await SearchAsync(app, id);

        var failure = Assert.Single(first.GetProperty("failures").EnumerateArray());
        Assert.True(failure.GetProperty("isTransient").GetBoolean());
        Assert.Equal(500, failure.GetProperty("httpStatus").GetInt32());
        Assert.Equal(3, failure.GetProperty("attempts").GetInt32());
        Assert.Equal(3, afterFirst);
        Assert.Equal(6, afterSecond);
        Assert.NotNull(Assert.Single(second.GetProperty("failures").EnumerateArray()).GetProperty("retryAfterUtc").GetString());
        var resting = Assert.Single(third.GetProperty("failures").EnumerateArray());
        Assert.Equal("CircuitOpen", resting.GetProperty("kind").GetString());
        Assert.Equal(afterSecond, indexer.Requests.Count);
        Assert.Empty(third.GetProperty("candidates").EnumerateArray());
    }

    /// <summary>#1405: the three forms of what a 429 can say about when to come back: seconds, a date, and nothing.</summary>
    [Theory]
    [InlineData("30", 29, 90)]
    [InlineData("date:120", 100, 200)]
    [InlineData("", 59, 400)]
    public async Task A_search_answered_429_is_not_asked_again_at_once_and_the_indexer_rests_for_what_it_asked_in_plain_words(string retryAfter, int atLeastSeconds, int atMostSeconds)
    {
        var header = retryAfter.StartsWith("date:", StringComparison.Ordinal)
            ? DateTimeOffset.UtcNow.AddSeconds(int.Parse(retryAfter[5..], System.Globalization.CultureInfo.InvariantCulture)).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            : retryAfter.Length == 0 ? null : retryAfter;
        await using var indexer = await ServeAsync(context => Status(context, 429, header));
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, indexer.BaseUrl);
        var before = DateTimeOffset.UtcNow;

        var first = await SearchAsync(app, id);
        var second = await SearchAsync(app, id);

        Assert.Single(indexer.Requests);
        var failure = Assert.Single(first.GetProperty("failures").EnumerateArray());
        Assert.Equal("RateLimit", failure.GetProperty("kind").GetString());
        Assert.Equal(1, failure.GetProperty("attempts").GetInt32());
        var until = DateTimeOffset.Parse(failure.GetProperty("retryAfterUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange((until - before).TotalSeconds, atLeastSeconds, atMostSeconds);
        var skipped = Assert.Single(second.GetProperty("failures").EnumerateArray());
        Assert.Equal("RateLimit", skipped.GetProperty("kind").GetString());
        Assert.Contains("asked Deluno to wait", skipped.GetProperty("message").GetString(), StringComparison.Ordinal);
        var health = (await app.Services.GetRequiredService<IConnectionsRepository>().ListIndexersAsync(CancellationToken.None)).Single(item => item.Id == id);
        Assert.Contains("asked Deluno to wait", health.LastHealthMessage, StringComparison.Ordinal);
    }

    /// <summary>#1405: an RSS read answered 429 is not asked again at once, whichever way the Retry-After came, and the next read says why without asking.</summary>
    [Theory]
    [InlineData("30")]
    [InlineData("date")]
    [InlineData("")]
    public async Task A_feed_read_answered_429_is_not_asked_again_and_the_next_read_says_the_indexer_asked_to_wait(string retryAfter)
    {
        var header = retryAfter == "date" ? DateTimeOffset.UtcNow.AddSeconds(120).ToString("R", System.Globalization.CultureInfo.InvariantCulture) : retryAfter.Length == 0 ? null : retryAfter;
        await using var indexer = await ServeAsync(context => Status(context, 429, header));
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, indexer.BaseUrl, name: "Busy");

        var first = await ReadFeedAsync(app, id);
        var second = await ReadFeedAsync(app, id);

        Assert.Single(indexer.Requests);
        Assert.False(first.Read);
        Assert.Contains("429", first.Problem, StringComparison.Ordinal);
        Assert.False(second.Read);
        Assert.Contains("Busy asked Deluno to wait", second.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_search_that_never_gets_an_answer_times_out_and_is_tried_again_as_a_transient_failure()
    {
        await using var indexer = await ServeAsync(async context =>
        {
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { } // reason: the server that never answers; the client's timeout aborts the request, which ends this
        });
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, indexer.BaseUrl);

        var searched = await SearchAsync(app, id);

        var failure = Assert.Single(searched.GetProperty("failures").EnumerateArray());
        Assert.True(failure.GetProperty("isTransient").GetBoolean());
        Assert.Equal(3, failure.GetProperty("attempts").GetInt32());
        Assert.Equal(3, indexer.Requests.Count);
        Assert.Empty(searched.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task A_request_limit_error_at_200_is_a_refusal_that_is_not_a_bad_key_and_is_not_read_as_no_results()
    {
        var recordedError = RecordedReplies.Jackett("unknown-function").Exchanges[0].Reply;
        var limited = recordedError with { Body = System.Text.RegularExpressions.Regex.Replace(recordedError.Body!, "code=\"201\" description=\"[^\"]*\"", "code=\"500\" description=\"Request limit reached\"") };
        Assert.Contains("Request limit reached", limited.Body, StringComparison.Ordinal);
        await using var capped = await ServeAsync(context => Write(context, limited));
        await using var app = await ApplicationTestHost.StartAsync();
        var cappedId = await AddAsync(app, capped.BaseUrl, name: "Capped");

        var cappedSearch = await SearchAsync(app, cappedId);
        var cappedTest = await TestAsync(app, cappedId);

        var refusal = Assert.Single(cappedSearch.GetProperty("failures").EnumerateArray());
        Assert.Contains("Request limit reached", refusal.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("Protocol", refusal.GetProperty("kind").GetString());
        Assert.Equal("protocol", cappedTest.GetProperty("failureCategory").GetString());
        Assert.Empty(cappedSearch.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task A_feed_that_cannot_be_read_says_why_and_is_never_read_as_empty_and_the_indexer_rests_after_two_failed_reads()
    {
        var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var port = ((IPEndPoint)refused.LocalEndpoint).Port;
        refused.Stop();
        var recorded = RecordedReplies.Jackett("server-config").Exchanges[0].Reply;
        await using var tooMany = await ServeAsync(context => Status(context, 429, "30"));
        await using var malformed = await ServeAsync(context => Write(context, recorded with { Status = 200, Body = recorded.Body![..20] }));
        await using var app = await ApplicationTestHost.StartAsync();
        var downId = await AddAsync(app, $"http://127.0.0.1:{port}", name: "Down");

        var down = await ReadFeedAsync(app, downId);
        var limited = await ReadFeedAsync(app, await AddAsync(app, tooMany.BaseUrl, name: "Busy"));
        var garbled = await ReadFeedAsync(app, await AddAsync(app, malformed.BaseUrl, name: "Garbled"));
        await ReadFeedAsync(app, downId);
        var resting = await ReadFeedAsync(app, downId);

        Assert.False(down.Read);
        Assert.Contains("refused", down.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(down.Releases);
        Assert.False(limited.Read);
        Assert.Contains("429", limited.Problem, StringComparison.Ordinal);
        Assert.Single(tooMany.Requests); // #1405: a 429 is not asked again at once
        Assert.False(garbled.Read);
        Assert.False(string.IsNullOrWhiteSpace(garbled.Problem));
        Assert.False(resting.Read);
        Assert.Equal("Down is resting after repeated failures.", resting.Problem);
    }

    [Fact]
    public async Task A_feed_that_never_answers_times_out_and_is_not_read_as_empty()
    {
        await using var indexer = await ServeAsync(async context =>
        {
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); } catch (OperationCanceledException) { } // reason: the server that never answers; the client's timeout aborts the request, which ends this
        });
        await using var app = await ApplicationTestHost.StartAsync();

        var read = await ReadFeedAsync(app, await AddAsync(app, indexer.BaseUrl));

        Assert.False(read.Read);
        Assert.Empty(read.Releases);
        Assert.False(string.IsNullOrWhiteSpace(read.Problem));
        Assert.Equal(3, indexer.Requests.Count);
    }

    [Fact]
    public async Task A_feed_read_inside_the_indexers_request_interval_misses_its_slot_and_says_so_without_asking()
    {
        var feed = RecordedReplies.Jackett("search-feed").Exchanges[0].Reply;
        await using var indexer = await ServeAsync(context => Write(context, feed));
        await using var app = await ApplicationTestHost.StartAsync();
        var id = await AddAsync(app, indexer.BaseUrl, name: "Paced", requestIntervalSeconds: 60);

        var first = await ReadFeedAsync(app, id);
        var second = await ReadFeedAsync(app, id);

        Assert.True(first.Read, first.Problem);
        Assert.False(second.Read);
        Assert.Equal("Paced is still inside its request interval.", second.Problem);
        Assert.Single(indexer.Requests);
    }
}
