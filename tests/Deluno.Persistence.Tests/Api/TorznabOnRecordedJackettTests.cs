using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Integrations.Search;
using Deluno.Persistence.Tests.Support;
using Deluno.Tests.Recorded;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1301: Deluno's real indexer test, search and feed read, run against what a real Torznab indexer (Jackett 0.24.2850)
/// answered. A local server replays the recorded replies; the app is the whole application over HTTP. A wrong key is what
/// Jackett recorded for it: HTTP 200 with an error element, code 100.
/// </summary>
public sealed class TorznabOnRecordedJackettTests
{
    private const string JackettPath = "/api/v2.0/indexers/yts/results/torznab/api";
    private const string Key = "k-secret-1";

    private static async Task<(RecordedIndexerServer Server, ApplicationTestHost App, IndexerItem Indexer)> StartAsync(
        Func<CreateIndexerRequest, CreateIndexerRequest>? shape = null, RecordedReply? answerEverythingWith = null, bool feed = false)
    {
        var server = await RecordedIndexerServer.StartAsync(feed
            ? [RecordedReplies.Jackett("search-feed"), RecordedReplies.Jackett("caps")]
            : [RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps")]);
        server.AnswerEverythingWith = answerEverythingWith;
        var app = await ApplicationTestHost.StartAsync();
        var request = new CreateIndexerRequest("Jackett YTS", "torznab", "public", server.BaseUrl + JackettPath, Key, 10, "2000", "", "movies", true);
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(shape is null ? request : shape(request), CancellationToken.None);
        return (server, app, indexer);
    }

    private static RecordedReply WrongKey => RecordedReplies.Jackett("caps-wrong-api-key").Single.Reply;

    private static async Task<JsonElement> TestAsync(ApplicationTestHost app, string id)
    {
        var response = await app.Client.PostAsync($"/api/indexers/{id}/test", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string QueryOf(string request, string name)
        => System.Web.HttpUtility.ParseQueryString(request[(request.IndexOf('?') + 1)..])[name] ?? string.Empty;

    // ---- the Test button (tn3, tn4) ----

    [Fact]
    public async Task The_test_asks_t_search_with_limit_1_and_the_key_on_the_indexers_own_api_path_and_calls_a_valid_answer_healthy()
    {
        var (server, app, indexer) = await StartAsync();
        await using var _ = server; await using var __ = app;

        var body = await TestAsync(app, indexer.Id);

        Assert.Equal("healthy", body.GetProperty("healthStatus").GetString());
        var tested = server.Requests.First(request => QueryOf(request, "t") == "search");
        Assert.StartsWith(JackettPath + "?", tested);
        Assert.Equal("1", QueryOf(tested, "limit"));
        Assert.Equal(Key, QueryOf(tested, "apikey"));
        Assert.Equal(IndexerApiEndpoint.UserAgent, server.LastUserAgent);
    }

    [Fact]
    public async Task An_indexer_given_only_a_host_is_tested_on_slash_api_and_one_with_a_custom_api_path_on_that_path()
    {
        await using var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-feed"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IConnectionsRepository>();
        var plain = await repository.CreateIndexerAsync(new CreateIndexerRequest("Plain", "torznab", "public", server.BaseUrl, Key, 10, "2000", "", "movies", true), CancellationToken.None);
        var custom = await repository.CreateIndexerAsync(new CreateIndexerRequest("Custom", "torznab", "public", server.BaseUrl, Key, 10, "2000", "", "movies", true) { ApiPath = "/torznab/v2" }, CancellationToken.None);

        Assert.Equal("healthy", (await TestAsync(app, plain.Id)).GetProperty("healthStatus").GetString());
        Assert.Equal("healthy", (await TestAsync(app, custom.Id)).GetProperty("healthStatus").GetString());

        Assert.Contains(server.Requests, request => request.StartsWith("/api?") && QueryOf(request, "t") == "search");
        Assert.Contains(server.Requests, request => request.StartsWith("/torznab/v2?") && QueryOf(request, "t") == "search");
    }

    [Fact]
    public async Task A_wrong_key_answered_200_with_an_error_element_is_a_degraded_test_that_says_the_indexer_refused_it()
    {
        var (server, app, indexer) = await StartAsync(answerEverythingWith: WrongKey);
        await using var _ = server; await using var __ = app;

        var body = await TestAsync(app, indexer.Id);

        Assert.Equal("degraded", body.GetProperty("healthStatus").GetString());
        Assert.Equal("auth", body.GetProperty("failureCategory").GetString());
        Assert.Contains("refused the request", body.GetProperty("message").GetString());
        Assert.Contains("Invalid API key", body.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, body.GetRawText());
    }

    // ---- searching (tn19, tn22, tn26) ----

    [Fact]
    public async Task A_text_search_asks_t_search_with_the_words_the_category_a_limit_of_100_and_the_key_and_lists_what_the_indexer_returned()
    {
        var (server, app, indexer) = await StartAsync();
        await using var _ = server; await using var __ = app;

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        var asked = Assert.Single(server.Requests, request => QueryOf(request, "t") == "search" && QueryOf(request, "limit") == "100");
        Assert.Equal("Sintel", QueryOf(asked, "q"));
        Assert.Equal("2000", QueryOf(asked, "cat"));
        Assert.Equal(Key, QueryOf(asked, "apikey"));
        var recorded = XDocument.Parse(RecordedReplies.Jackett("search-text").Single.Reply.Body!).Descendants("item").First();
        var candidate = JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("candidates").EnumerateArray().First();
        Assert.Equal(recorded.Element("title")!.Value, candidate.GetProperty("releaseName").GetString());
        Assert.Equal("torznab", candidate.GetProperty("indexerProtocol").GetString());
    }

    [Fact]
    public async Task A_result_keeps_the_title_link_size_seeders_peers_and_age_exactly_as_the_indexer_listed_it()
    {
        var (server, app, indexer) = await StartAsync();
        await using var _ = server; await using var __ = app;

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        XNamespace torznab = "http://torznab.com/schemas/2015/feed";
        var item = XDocument.Parse(RecordedReplies.Jackett("search-text").Single.Reply.Body!).Descendants("item").First();
        string Attr(string name) => item.Elements(torznab + "attr").First(a => (string?)a.Attribute("name") == name).Attribute("value")!.Value;
        var candidate = JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("candidates").EnumerateArray()
            .First(c => c.GetProperty("releaseName").GetString() == item.Element("title")!.Value);
        Assert.Equal(long.Parse(item.Element("size")!.Value), candidate.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(int.Parse(Attr("seeders")), candidate.GetProperty("seeders").GetInt32());
        Assert.Equal(int.Parse(Attr("peers")), candidate.GetProperty("leechers").GetInt32());
        Assert.Equal(item.Element("enclosure")!.Attribute("url")!.Value, candidate.GetProperty("downloadUrl").GetString());
        Assert.True(candidate.GetProperty("releaseAgeHours").GetDouble() > 0, "the age is worked out from the listed publish date");
    }

    [Fact]
    public async Task A_disabled_indexer_is_tested_as_disabled_and_no_search_is_sent_to_it()
    {
        var (server, app, indexer) = await StartAsync(request => request with { IsEnabled = false });
        await using var _ = server; await using var __ = app;

        var body = await TestAsync(app, indexer.Id);

        Assert.Equal("disabled", body.GetProperty("healthStatus").GetString());
        Assert.Contains("Disabled until you turn it on", body.GetProperty("message").GetString());
        // Only its capabilities are asked for (to fill the form); no search is sent to an indexer that is switched off.
        Assert.DoesNotContain(server.Requests, request => QueryOf(request, "t") == "search");
    }

    [Fact]
    public async Task A_search_the_indexers_own_query_limit_refuses_is_never_sent_to_the_indexer_and_lists_nothing()
    {
        var (server, app, indexer) = await StartAsync(request => request with { QueryLimit = 1 });
        await using var _ = server; await using var __ = app;

        var first = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });
        var second = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        Assert.NotEmpty(JsonSerializer.Deserialize<JsonElement>(await first.Content.ReadAsStringAsync()).GetProperty("candidates").EnumerateArray());
        Assert.Empty(JsonSerializer.Deserialize<JsonElement>(await second.Content.ReadAsStringAsync()).GetProperty("candidates").EnumerateArray());
        Assert.Single(server.Requests, request => QueryOf(request, "t") == "search");
    }

    /// <summary>
    /// Found by the test above: the second search, which the indexer's own limit stopped before any call was made, is counted on
    /// the scoreboard as a query and as a failed one ("Needs attention: 50% of 2 queries failed"). The planner's own comment says a
    /// refused call must never count as a query attempt. Skipped until that is fixed; the figures are from the real run.
    /// </summary>
    [Fact]
    public async Task A_search_stopped_by_the_indexers_own_limit_is_not_counted_as_a_query_or_a_failure_on_the_scoreboard()
    {
        var (server, app, indexer) = await StartAsync(request => request with { QueryLimit = 1 });
        await using var _ = server; await using var __ = app;
        await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });
        await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        var board = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard");

        var row = board.GetProperty("indexers").EnumerateArray().Single();
        Assert.Equal(1, row.GetProperty("totalQueries").GetInt32());
        Assert.Equal(0, row.GetProperty("failedQueries").GetInt32());
    }

    [Fact]
    public async Task An_indexers_additional_parameters_ride_along_on_a_search_and_the_key_is_not_sent_twice()
    {
        var (server, app, indexer) = await StartAsync(request => request with { AdditionalParameters = "token=abc123&apikey=another&extra=1" });
        await using var _ = server; await using var __ = app;

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var asked = Assert.Single(server.Requests, request => QueryOf(request, "t") == "search" && QueryOf(request, "limit") == "100");
        Assert.Equal("abc123", QueryOf(asked, "token"));
        Assert.Equal("1", QueryOf(asked, "extra"));
        Assert.Equal(Key, QueryOf(asked, "apikey"));
        Assert.Equal(1, asked.Split('&').Count(part => part.StartsWith("apikey=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_search_with_a_wrong_key_answered_200_with_an_error_is_an_authentication_failure_and_a_row_in_the_indexer_history()
    {
        var (server, app, indexer) = await StartAsync(answerEverythingWith: WrongKey);
        await using var _ = server; await using var __ = app;

        var response = await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, raw);
        Assert.Empty(JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("candidates").EnumerateArray());
        var history = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history");
        var row = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Contains(indexer.Name, row.GetRawText());
        Assert.Equal("auth-failure", row.GetProperty("eventType").GetString());
        Assert.DoesNotContain(Key, history.GetRawText());
        Assert.DoesNotContain(Key, raw);
    }

    // ---- the RSS feed (tn39) ----

    [Fact]
    public async Task An_rss_feed_read_with_a_wrong_key_answered_200_with_an_error_is_not_read_as_empty_and_names_the_refusal()
    {
        var (server, app, indexer) = await StartAsync(answerEverythingWith: WrongKey);
        await using var _ = server; await using var __ = app;
        using var scope = app.Services.CreateScope();

        var feed = await scope.ServiceProvider.GetRequiredService<IIndexerFeedReader>().ReadRecentAsync(indexer, CancellationToken.None);

        Assert.False(feed.Read);
        Assert.Empty(feed.Releases);
        Assert.Contains("Invalid API key", feed.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Key, feed.Problem);
    }

    [Fact]
    public async Task An_rss_feed_read_asks_t_search_with_no_term_and_reads_the_recorded_releases()
    {
        var (server, app, indexer) = await StartAsync(feed: true);
        await using var _ = server; await using var __ = app;
        using var scope = app.Services.CreateScope();

        var feed = await scope.ServiceProvider.GetRequiredService<IIndexerFeedReader>().ReadRecentAsync(indexer, CancellationToken.None);

        Assert.True(feed.Read, feed.Problem);
        var recorded = XDocument.Parse(RecordedReplies.Jackett("search-feed").Single.Reply.Body!).Descendants("item").Select(item => item.Element("title")!.Value).ToArray();
        Assert.Equal(recorded, feed.Releases.Select(release => release.ReleaseName));
        var asked = Assert.Single(server.Requests);
        Assert.Equal("search", QueryOf(asked, "t"));
        Assert.Equal(string.Empty, QueryOf(asked, "q"));
        Assert.Equal(Key, QueryOf(asked, "apikey"));
    }

    // ---- the key (tn46, tn47) ----

    [Fact]
    public async Task A_saved_key_is_hidden_from_the_api_never_written_to_disk_in_plain_and_a_blank_edit_keeps_it()
    {
        await using var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-feed"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var created = await app.Client.PostAsJsonAsync("/api/indexers", new { name = "Jackett YTS", protocol = "torznab", baseUrl = server.BaseUrl + JackettPath, apiKey = Key, mediaScope = "movies", isEnabled = true });
        var createdText = await created.Content.ReadAsStringAsync();
        Assert.True(created.IsSuccessStatusCode, createdText);
        var id = JsonSerializer.Deserialize<JsonElement>(createdText).GetProperty("id").GetString()!;

        var listed = await app.Client.GetStringAsync("/api/indexers");

        Assert.DoesNotContain(Key, createdText);
        Assert.DoesNotContain(Key, listed);
        foreach (var file in Directory.EnumerateFiles(app.DataRoot, "*.db*", SearchOption.AllDirectories))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.Latin1);
            Assert.DoesNotContain(Key, await reader.ReadToEndAsync());
        }

        // A blank key on an edit is "keep the saved one": the next test still presents it.
        var edit = await app.Client.PutAsJsonAsync($"/api/indexers/{id}", new { name = "Jackett YTS renamed" });
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync());
        Assert.Equal("healthy", (await TestAsync(app, id)).GetProperty("healthStatus").GetString());
        Assert.Equal(Key, QueryOf(server.Requests.Last(request => QueryOf(request, "t") == "search"), "apikey"));

        // A typed key replaces it.
        var replaced = await app.Client.PutAsJsonAsync($"/api/indexers/{id}", new { apiKey = "k-new-2" });
        Assert.True(replaced.IsSuccessStatusCode, await replaced.Content.ReadAsStringAsync());
        await TestAsync(app, id);
        Assert.Equal("k-new-2", QueryOf(server.Requests.Last(request => QueryOf(request, "t") == "search"), "apikey"));
    }

    [Fact]
    public async Task A_saved_key_that_cannot_be_read_here_is_not_sent_and_the_test_says_to_enter_it_again()
    {
        await using var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-feed"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync();
        var repository = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await repository.CreateIndexerAsync(new CreateIndexerRequest("Jackett YTS", "torznab", "public", server.BaseUrl + JackettPath, Key, 10, "2000", "", "movies", true), CancellationToken.None);
        await MakeSavedKeyUnreadableAsync(app, indexer.Id);

        var body = await TestAsync(app, indexer.Id);

        Assert.Equal("degraded", body.GetProperty("healthStatus").GetString());
        Assert.Contains("Enter it again", body.GetProperty("message").GetString());
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task The_key_is_in_no_log_line_no_failure_text_and_no_history_row_whether_the_indexer_accepts_it_or_refuses_it()
    {
        var logs = new CapturedLogs();
        await using var server = await RecordedIndexerServer.StartAsync(RecordedReplies.Jackett("search-text"), RecordedReplies.Jackett("caps"));
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
        var indexer = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest("Jackett YTS", "torznab", "public", server.BaseUrl + JackettPath, Key, 10, "2000", "", "movies", true), CancellationToken.None);
        var texts = new List<string>();

        texts.Add((await TestAsync(app, indexer.Id)).GetRawText());
        texts.Add(await (await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } })).Content.ReadAsStringAsync());
        server.AnswerEverythingWith = WrongKey;
        texts.Add((await TestAsync(app, indexer.Id)).GetRawText());
        texts.Add(await (await app.Client.PostAsJsonAsync("/api/search", new { query = "Sintel", searchType = "movie", indexerIds = new[] { indexer.Id } })).Content.ReadAsStringAsync());
        texts.Add(await app.Client.GetStringAsync("/api/indexer-history"));
        texts.Add(await app.Client.GetStringAsync("/api/indexers"));

        Assert.All(texts, text => Assert.DoesNotContain(Key, text));
        Assert.NotEmpty(logs.Lines);
        Assert.All(logs.Lines, line => Assert.DoesNotContain(Key, line));
    }

    /// <summary>The saved value was protected under another key, as on a backup restored from another computer.</summary>
    private static async Task MakeSavedKeyUnreadableAsync(ApplicationTestHost app, string id)
    {
        var protector = new Deluno.Security.DataProtectionSecretProtector(
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(app.DataRoot, "keys-elsewhere"))));
        var saved = protector.Protect("indexer:api-key", Key);
        var factory = app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>();
        await using var connection = await factory.OpenConnectionAsync(Deluno.Infrastructure.Storage.DelunoDatabaseNames.Platform, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE indexer_sources SET api_key = $key WHERE id = $id";
        var keyParameter = command.CreateParameter();
        keyParameter.ParameterName = "$key";
        keyParameter.Value = saved;
        command.Parameters.Add(keyParameter);
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$id";
        idParameter.Value = id;
        command.Parameters.Add(idParameter);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class CapturedLogs : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToArray(); } }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose() { }

        private sealed class Capture(CapturedLogs owner) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines) owner._lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
            }
        }
    }
}
