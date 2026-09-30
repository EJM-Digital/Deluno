using Deluno.Infrastructure.Caching;
using Deluno.Jobs.Contracts;

namespace Deluno.Integrations.Search;

/// <summary>
/// The scoreboard's counts, kept for sixty seconds per window (#1081). They group
/// tens of thousands of query rows and every dispatch by indexer; a page of
/// totals over thirty days does not need recounting each time it is opened. The
/// list of configured indexers is not kept, so adding or renaming one shows at once.
/// </summary>
public sealed class IndexerScoreboardCache(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    public GenerationMemo<IndexerScoreboardSnapshot> Memo { get; } = new(clock);
}
