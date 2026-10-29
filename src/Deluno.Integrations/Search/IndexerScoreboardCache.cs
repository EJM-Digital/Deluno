using Deluno.Infrastructure.Caching;
using Deluno.Infrastructure.Storage;
using Deluno.Jobs.Contracts;

namespace Deluno.Integrations.Search;

/// <summary>
/// The scoreboard's counts, kept for sixty seconds per window (#1081, wired in #1406). They group
/// tens of thousands of query rows and every dispatch by indexer; a page of
/// totals over thirty days does not need recounting each time it is opened. The
/// list of configured indexers is not kept, so adding or renaming one shows at once.
/// Any write to the jobs database (a query logged, a dispatch recorded) moves its write generation and
/// drops the kept answer, so nothing older than a change it could have seen is ever served.
/// </summary>
public sealed class IndexerScoreboardCache(TimeProvider clock, IDelunoDatabaseConnectionFactory databases)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public GenerationMemo<IndexerScoreboardSnapshot> Memo { get; } = new(clock);

    /// <summary>The counts for one named window, from the cache when they were read less than sixty seconds ago and nothing was written since.</summary>
    public Task<IndexerScoreboardSnapshot> GetAsync(
        string window,
        Func<Task<IndexerScoreboardSnapshot>> compute,
        CancellationToken cancellationToken)
        => Memo.GetOrAddAsync(window, databases.GetWriteGeneration(DelunoDatabaseNames.Jobs), Lifetime, compute, cancellationToken);
}
