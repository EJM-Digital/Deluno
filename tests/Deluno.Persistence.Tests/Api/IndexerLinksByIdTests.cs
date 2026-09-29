using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Libraries.Contracts;
using Deluno.Libraries.Data;
using Deluno.Movies.Data;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1077: an indexer's grabs, history and query statistics are filed under its
/// id, so renaming the indexer changes nothing about them.
/// </summary>
public sealed class IndexerLinksByIdTests
{
    private const string Release = "Wanted.Film.2011.1080p.WEB-DL-DELUNO";

    [Fact]
    public async Task Renaming_an_indexer_keeps_its_grabs_its_query_stats_and_its_history()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var indexer = await CreateIndexerAsync(connections, "Fixture");
        var library = await CreateLibraryAsync(app);

        var added = await app.Client.PostAsJsonAsync("/api/movies", new { title = "Wanted Film", releaseYear = 2011, monitored = true });
        var movieId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // A grab and a search recorded while it is called "Fixture".
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-wanted", Release, "Fixture", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        await app.Services.GetRequiredService<IMovieCatalogRepository>().RecordSearchAttemptAsync(
            movieId, library.Id, "manual-grab", "grabbed", DateTimeOffset.UtcNow, null, null, Release, "Fixture", null, CancellationToken.None);
        var stats = app.Services.GetRequiredService<IIndexerQueryStatsRepository>();
        await stats.RecordBatchAsync([Query(indexer.Id, "Fixture")], CancellationToken.None);

        var link = await app.Services.GetRequiredService<IJobQueueRepository>()
            .FindDispatchLinkForDownloadAsync("client-1", "no-hash", Release, CancellationToken.None);
        Assert.Equal(indexer.Id, link!.IndexerId);

        // Rename it, and go on using it.
        await connections.UpdateIndexerAsync(
            indexer.Id,
            new UpdateIndexerRequest("A better name", null, null, null, null, null, null, null, null, null),
            CancellationToken.None);
        await stats.RecordBatchAsync([Query(indexer.Id, "A better name")], CancellationToken.None);
        await app.Services.GetRequiredService<IJobQueueRepository>().RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-two", "Another.Film.2012.1080p.WEB-DL-DELUNO", "A better name", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);

        // The grab made before the rename is still tied to the same indexer.
        var older = await app.Services.GetRequiredService<IJobQueueRepository>()
            .FindDispatchLinkForDownloadAsync("client-1", "no-hash", Release, CancellationToken.None);
        Assert.Equal(indexer.Id, older!.IndexerId);

        // The scoreboard has one row for it, counting everything, under its new name.
        var scoreboard = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=30");
        var row = Assert.Single(scoreboard.GetProperty("indexers").EnumerateArray(), item => item.GetProperty("id").GetString() == indexer.Id);
        Assert.Equal("A better name", row.GetProperty("name").GetString());
        Assert.Equal(2, row.GetProperty("totalQueries").GetInt64());
        Assert.Equal(2, row.GetProperty("totalGrabs").GetInt64());
        Assert.Equal(2, row.GetProperty("successfulGrabs").GetInt64());
        Assert.Single(scoreboard.GetProperty("indexers").EnumerateArray());

        // Its search history is found under the new name and carries the same id.
        var history = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexer-history?indexerName=" + Uri.EscapeDataString("A better name"));
        var historyRow = Assert.Single(history.GetProperty("items").EnumerateArray());
        Assert.Equal(indexer.Id, historyRow.GetProperty("indexerId").GetString());
        Assert.Equal("A better name", historyRow.GetProperty("indexerName").GetString());
    }

    [Fact]
    public async Task Older_rows_written_under_a_name_are_linked_to_the_indexer_that_had_it_and_a_stranger_is_left_alone()
    {
        await using var app = await ApplicationTestHost.StartAsync();
        var connections = app.Services.GetRequiredService<IConnectionsRepository>();
        var library = await CreateLibraryAsync(app);
        var queue = app.Services.GetRequiredService<IJobQueueRepository>();

        // Both are written before any indexer of that name exists, as a row from before ids were kept is.
        await queue.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-one", Release, "Later indexer", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        await queue.RecordDownloadDispatchAsync(
            library.Id, "movies", "movie", "title-two", "Other.Film.2013.1080p.WEB-DL-DELUNO", "Stranger", "client-1", "qBittorrent", "sent", null,
            cancellationToken: CancellationToken.None);
        var indexer = await CreateIndexerAsync(connections, "later INDEXER");

        var linked = await IndexerIdentity.BackfillAsync(app.Services.GetRequiredService<IDelunoDatabaseConnectionFactory>(), CancellationToken.None);

        Assert.Equal(1, linked);
        var dispatches = app.Services.GetRequiredService<IDownloadDispatchesRepository>();
        var one = await queue.FindDispatchLinkForDownloadAsync("client-1", "no-hash", Release, CancellationToken.None);
        var two = await queue.FindDispatchLinkForDownloadAsync("client-1", "no-hash", "Other.Film.2013.1080p.WEB-DL-DELUNO", CancellationToken.None);
        Assert.Equal(indexer.Id, one!.IndexerId);
        Assert.Null(two!.IndexerId);
        Assert.Equal("Stranger", two.IndexerName);
        _ = dispatches;
    }

    private static IndexerQueryLogEntry Query(string indexerId, string indexerName)
        => new(indexerId, indexerName, "Wanted Film", "2000", "movies", "search", "matched", 120, 4, DateTimeOffset.UtcNow);

    private static Task<IndexerItem> CreateIndexerAsync(IConnectionsRepository connections, string name)
        => connections.CreateIndexerAsync(
            new CreateIndexerRequest(name, "torznab", "public", "http://indexer.invalid/api", null, 25, "2000", "", "both", IsEnabled: true),
            CancellationToken.None);

    private static async Task<LibraryItem> CreateLibraryAsync(ApplicationTestHost app)
        => await app.Services.GetRequiredService<ILibrariesRepository>().CreateLibraryAsync(
            new CreateLibraryRequest(
                "Films", "movies", "library", Path.Combine(app.DataRoot, "movies"), Path.Combine(app.DataRoot, "downloads"), null,
                "direct-import", null, null, null, null, true, true, true, 12, 36, 25),
            CancellationToken.None);
}
