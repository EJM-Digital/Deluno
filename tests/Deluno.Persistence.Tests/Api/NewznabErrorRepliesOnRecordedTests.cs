using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1311: every Torznab/Newznab error reply Jackett 0.24.2850 and NZBHydra2 9.1.1 recorded (HTTP 200 with an error
/// element) is read the same way by the test button, search and RSS: a wrong or missing key (code 100) as a credential
/// failure, every other code with its own description. Only recorded replies are used.
/// </summary>
public sealed class NewznabErrorRepliesOnRecordedTests
{
    private const string Key = "k-secret-1";

    public static TheoryData<string, string, bool> Replies => new()
    {
        { "jackett", "caps-wrong-api-key", true },
        { "jackett", "caps-no-api-key", true },
        { "jackett", "unknown-function", false },
        { "nzbhydra2", "caps-wrong-api-key", true },
        { "nzbhydra2", "caps-no-api-key", true },
        { "nzbhydra2", "unknown-function", false },
    };

    private static RecordedReply Reply(string app, string state)
        => (app == "jackett" ? RecordedReplies.Jackett(state) : RecordedReplies.Nzbhydra2(state)).Single.Reply;

    [Theory]
    [MemberData(nameof(Replies))]
    public void The_shared_reader_reads_each_recorded_error_reply_by_its_code(string app, string state, bool credential)
    {
        var reply = Reply(app, state);

        var error = IndexerApiError.TryRead(reply.Body);

        Assert.NotNull(error);
        Assert.Equal(credential, error!.IsCredentialFailure);
        Assert.Equal(credential ? 100 : (app == "jackett" ? 201 : 900), error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Description));
        var plain = error.PlainMessage("My indexer");
        Assert.StartsWith("My indexer ", plain);
        Assert.Equal(credential, plain.Contains("refused the API key", StringComparison.Ordinal));
        Assert.True(plain.Length < 400, plain);
    }

    [Fact]
    public void A_recorded_good_reply_is_not_an_error()
    {
        Assert.Null(IndexerApiError.TryRead(RecordedReplies.Nzbhydra2("search-text").Single.Reply.Body));
        Assert.Null(IndexerApiError.TryRead(RecordedReplies.Jackett("caps").Single.Reply.Body));
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, true)]
    [InlineData(102, true)]
    [InlineData(103, false)]
    [InlineData(200, false)]
    [InlineData(900, false)]
    public void Codes_100_to_102_are_a_credential_failure_and_the_rest_are_not(int code, bool credential)
        => Assert.Equal(credential, new IndexerApiError(code, "x").IsCredentialFailure);

    private static async Task<(RecordedIndexerServer Server, ApplicationTestHost App, IndexerItem Indexer)> StartAsync(string app, string state)
    {
        var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Nzbhydra2("caps"));
        server.AnswerEverythingWith = Reply(app, state);
        var host = await ApplicationTestHost.StartAsync();
        var indexer = await host.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Hydra", "newznab", "public", server.BaseUrl + "/", Key, 10, "2000", "", "movies", true), CancellationToken.None);
        return (server, host, indexer);
    }

    [Theory]
    [InlineData("nzbhydra2", "caps-wrong-api-key", "auth")]
    [InlineData("nzbhydra2", "unknown-function", "protocol")]
    public async Task NZBHydra2s_error_replies_make_the_test_degraded_with_the_right_category(string app, string state, string category)
    {
        var (server, host, indexer) = await StartAsync(app, state);
        await using var _ = server; await using var __ = host;

        var response = await host.Client.PostAsync($"/api/indexers/{indexer.Id}/test", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("degraded", body.GetProperty("healthStatus").GetString());
        Assert.Equal(category, body.GetProperty("failureCategory").GetString());
        Assert.Contains("refused the request", body.GetProperty("message").GetString());
        Assert.DoesNotContain(Key, body.GetRawText());
    }

    [Fact]
    public async Task NZBHydra2s_wrong_key_reply_to_a_search_is_an_authentication_failure_in_the_indexer_history_in_plain_words()
    {
        var (server, host, indexer) = await StartAsync("nzbhydra2", "caps-wrong-api-key");
        await using var _ = server; await using var __ = host;

        var response = await host.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var history = await host.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history");
        var row = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal("auth-failure", row.GetProperty("eventType").GetString());
        Assert.Contains("refused the API key", row.GetRawText());
        Assert.Contains("Wrong api key", row.GetRawText());
        Assert.DoesNotContain(Key, history.GetRawText() + raw);
    }

    [Fact]
    public async Task NZBHydra2s_other_error_reply_to_a_search_is_a_failed_search_with_its_description_not_found_nothing()
    {
        var (server, host, indexer) = await StartAsync("nzbhydra2", "unknown-function");
        await using var _ = server; await using var __ = host;

        var response = await host.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var history = await host.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history");
        var row = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal("failed", row.GetProperty("outcome").GetString());
        Assert.Contains("answered with an error instead of results", row.GetRawText());
        Assert.DoesNotContain("found nothing", row.GetRawText());
        Assert.DoesNotContain(Key, history.GetRawText() + raw);
    }

    [Theory]
    [InlineData("caps-wrong-api-key", "refused the API key")]
    [InlineData("unknown-function", "answered with an error instead of results")]
    public async Task NZBHydra2s_error_replies_are_never_an_empty_rss_read(string state, string words)
    {
        var (server, host, indexer) = await StartAsync("nzbhydra2", state);
        await using var _ = server; await using var __ = host;
        using var scope = host.Services.CreateScope();

        var feed = await scope.ServiceProvider.GetRequiredService<IIndexerFeedReader>().ReadRecentAsync(indexer, CancellationToken.None);

        Assert.False(feed.Read);
        Assert.Empty(feed.Releases);
        Assert.Contains(words, feed.Problem);
        Assert.DoesNotContain(Key, feed.Problem);
    }
}
