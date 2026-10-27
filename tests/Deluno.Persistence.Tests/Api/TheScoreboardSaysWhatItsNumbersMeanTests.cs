using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// The indexer scoreboard (/api/indexers/scoreboard) turns each indexer's counted queries and grabs into a row and a one-line
/// reading, and the whole board into one insight sentence. No indexer is asked anything here: the counts are Deluno's own
/// records, written the way a search writes them, so the sums and the sentences are what is under test.
/// </summary>
public sealed class TheScoreboardSaysWhatItsNumbersMeanTests
{
    [Fact]
    public async Task A_row_reports_its_queries_its_failure_share_and_says_in_words_what_that_means_and_the_board_picks_the_sentence()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);
        var stats = app.Services.GetRequiredService<IIndexerQueryStatsRepository>();
        var queue = app.Services.GetRequiredService<IJobQueueRepository>();

        var healthy = await CreateIndexerAsync(app, "Healthy one");
        var failing = await CreateIndexerAsync(app, "Failing one");
        var quiet = await CreateIndexerAsync(app, "Quiet one");
        var unused = await CreateIndexerAsync(app, "Never asked");

        // Healthy: two answered searches (120 ms each) and one grab that went through.
        await stats.RecordBatchAsync([Query(healthy.Id, "Healthy one", "matched", 120), Query(healthy.Id, "Healthy one", "matched", 120)], CancellationToken.None);
        await queue.RecordDownloadDispatchAsync(library.Id, "movies", "movie", "title-1", "Wanted.Film.2011.1080p.WEB-DL-DELUNO", "Healthy one", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);
        // Failing: two of four failed, a quarter or more is "needs attention".
        await stats.RecordBatchAsync([
            Query(failing.Id, "Failing one", "failed", 10), Query(failing.Id, "Failing one", "failed", 10),
            Query(failing.Id, "Failing one", "matched", 10), Query(failing.Id, "Failing one", "matched", 10)], CancellationToken.None);
        // Quiet: answered, never produced a grab.
        await stats.RecordBatchAsync([Query(quiet.Id, "Quiet one", "matched", 30), Query(quiet.Id, "Quiet one", "matched", 30), Query(quiet.Id, "Quiet one", "matched", 30)], CancellationToken.None);

        var board = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=30");

        Assert.Equal(30, board.GetProperty("windowDays").GetInt32());
        Assert.Equal(4, board.GetProperty("totalIndexers").GetInt32());
        Assert.Equal(9, board.GetProperty("totalQueries").GetInt64());
        Assert.Equal(1, board.GetProperty("successfulGrabs").GetInt64());
        Assert.Equal(1d / 9d, board.GetProperty("conversionRate").GetDouble(), 6);

        string Reading(string id) => Row(board, id).GetProperty("recommendation").GetString()!;
        Assert.Equal("Answered 2 queries and produced 1 successful grabs at 120 ms average latency.", Reading(healthy.Id));
        Assert.Equal("Needs attention: 50% of 4 queries failed in the last 30 days.", Reading(failing.Id));
        Assert.Equal("Answered 3 queries but produced no successful grabs in the last 30 days.", Reading(quiet.Id));
        Assert.Equal("No query history for Never asked in the last 30 days.", Reading(unused.Id));

        Assert.Equal(2, Row(board, failing.Id).GetProperty("failedQueries").GetInt64());
        Assert.Equal(1, Row(board, healthy.Id).GetProperty("successfulGrabs").GetInt64());

        // Rows run best first: the one with a grab, then the busiest.
        var order = board.GetProperty("indexers").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["Healthy one", "Failing one", "Quiet one", "Never asked"], order);

        // The sentence names the busiest indexer that never produced a grab (4 queries) and the one that did.
        Assert.Equal(
            "Failing one answered 4 queries without a successful grab in this window; Healthy one produced the most successful grabs (1).",
            board.GetProperty("insight").GetString());
    }

    [Fact]
    public async Task With_no_history_the_board_says_nothing_has_been_recorded_and_a_board_where_every_asked_indexer_grabbed_names_the_best()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var library = await CreateLibraryAsync(app);

        var empty = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=7");
        Assert.Equal(7, empty.GetProperty("windowDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("conversionRate").ValueKind);
        Assert.Equal(
            "No indexer queries have been recorded in the last 7 days. Deluno will build the scoreboard as searches and health checks run.",
            empty.GetProperty("insight").GetString());

        var only = await CreateIndexerAsync(app, "Only one");
        await app.Services.GetRequiredService<IIndexerQueryStatsRepository>().RecordBatchAsync([Query(only.Id, "Only one", "matched", 50), Query(only.Id, "Only one", "matched", 50)], CancellationToken.None);
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(library.Id, "movies", "movie", "title-1", "Wanted.Film.2011.1080p.WEB-DL-DELUNO", "Only one", "client-1", "qBittorrent", "sent", null, cancellationToken: CancellationToken.None);

        var board = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=7");
        Assert.Equal("Only one produced the most successful grabs (1) from 2 queries in this window.", board.GetProperty("insight").GetString());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(400, 365)]
    [InlineData(90, 90)]
    public async Task The_window_is_kept_between_one_day_and_a_year(int asked, int used)
    {
        await using var app = await ApplicationTestHost.StartAsync();

        var board = await app.Client.GetFromJsonAsync<JsonElement>($"/api/indexers/scoreboard?days={asked}");

        Assert.Equal(used, board.GetProperty("windowDays").GetInt32());
    }

    [Fact]
    public async Task A_removed_indexer_keeps_its_row_on_the_board_as_removed_and_is_not_counted_among_the_configured_ones()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        await app.Services.GetRequiredService<IIndexerQueryStatsRepository>().RecordBatchAsync([Query("gone-id", "Gone indexer", "matched", 40)], CancellationToken.None);

        var board = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=30");

        var row = Row(board, "gone-id");
        Assert.Equal("removed", row.GetProperty("healthStatus").GetString());
        Assert.False(row.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(0, board.GetProperty("totalIndexers").GetInt32());
        Assert.Equal(1, board.GetProperty("totalQueries").GetInt64());
    }

    private static JsonElement Row(JsonElement board, string id)
        => board.GetProperty("indexers").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id);

    private static IndexerQueryLogEntry Query(string indexerId, string indexerName, string outcome, int elapsedMilliseconds)
        => new(indexerId, indexerName, "Wanted Film", "2000", "movies", "search", outcome, elapsedMilliseconds, 4, DateTimeOffset.UtcNow);

    private static async Task<IndexerItem> CreateIndexerAsync(ApplicationTestHost app, string name)
    {
        var created = await app.Services.GetRequiredService<IConnectionsRepository>().CreateIndexerAsync(
            new CreateIndexerRequest(name, "torznab", "public", "http://indexer.invalid/api", null, 25, "2000", "", "both", IsEnabled: true),
            CancellationToken.None);
        await LegacyConnectionNames.SetAsync(app.Services.GetRequiredService<Deluno.Infrastructure.Storage.IDelunoDatabaseConnectionFactory>(), "indexer_sources", created.Id, name);
        return created with { Name = name };
    }

    private static async Task<LibraryItem> CreateLibraryAsync(ApplicationTestHost app)
        => await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Films", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 36, 25),
            CancellationToken.None);
}
