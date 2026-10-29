using System.Net.Http.Json;
using System.Text.Json;
using Deluno.Jobs.Contracts;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Deluno.Persistence.Tests.Api;

/// <summary>
/// #1406: the scoreboard's counts group every query row and dispatch by indexer. A second read inside sixty seconds is answered
/// from the cache (one computation), and any write to the database the counts come from drops it at once.
/// </summary>
public sealed class TheIndexerScoreboardIsKeptForSixtySecondsTests
{
    private sealed class CountingStats(IIndexerQueryStatsRepository inner) : IIndexerQueryStatsRepository
    {
        public int Reads;

        public Task RecordBatchAsync(IReadOnlyList<IndexerQueryLogEntry> entries, CancellationToken cancellationToken)
            => inner.RecordBatchAsync(entries, cancellationToken);

        public Task<IndexerScoreboardSnapshot> GetScoreboardAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            return inner.GetScoreboardAsync(fromUtc, toUtc, cancellationToken);
        }

        public Task<int> PruneAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken)
            => inner.PruneAsync(beforeUtc, cancellationToken);
    }

    [Fact]
    public async Task Two_reads_inside_sixty_seconds_compute_once_and_a_write_to_the_counts_drops_the_kept_answer()
    {
        CountingStats? counting = null;
        await using var app = await ApplicationTestHost.StartAsync(replaceServices: services =>
        {
            var real = services.Single(service => service.ServiceType == typeof(IIndexerQueryStatsRepository));
            services.Remove(real);
            services.AddSingleton<IIndexerQueryStatsRepository>(provider =>
                counting = new CountingStats((IIndexerQueryStatsRepository)real.ImplementationFactory!(provider)));
        });

        var first = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=30");
        var second = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=30");
        var otherWindow = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=7");

        Assert.Equal(0, first.GetProperty("totalQueries").GetInt64());
        Assert.Equal(0, second.GetProperty("totalQueries").GetInt64());
        Assert.Equal(0, otherWindow.GetProperty("totalQueries").GetInt64());
        Assert.Equal(2, counting!.Reads); // one for 30 days, one for 7: the second 30-day read was answered from the cache

        await counting.RecordBatchAsync([new IndexerQueryLogEntry(
            "idx-1", "One", "q", "2000", "movie", "search", "matched", 10, 1, DateTimeOffset.UtcNow)], CancellationToken.None);
        var afterWrite = await app.Client.GetFromJsonAsync<JsonElement>("/api/indexers/scoreboard?days=30");

        Assert.Equal(1, afterWrite.GetProperty("totalQueries").GetInt64());
        Assert.Equal(3, counting.Reads);
    }
}
