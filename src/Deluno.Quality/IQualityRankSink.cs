namespace Deluno.Quality;

/// <summary>
/// Somewhere that needs to know what a quality tier is worth, in its own
/// database, because SQL cannot ask C#.
///
/// The catalogue databases sort a shelf by quality, and an ORDER BY has to be a
/// number on an indexed column — `Remux 2160p` above `WEB 2160p` is a fact
/// about the ladder, not about the alphabet. So each catalogue keeps a small
/// `quality_ranks` table, seeded by its migration with the shipped ladder and
/// re-synced through this whenever somebody edits the quality model.
///
/// Without the re-sync, renaming or re-ranking a tier would leave every title
/// sorting by a number the ladder no longer agrees with — a stale copy of a
/// fact, which is the defect this codebase keeps paying for. The sink also
/// recomputes what it has cached, so the new order is true immediately rather
/// than the next time each file happens to change.
/// </summary>
public interface IQualityRankSink
{
    Task SyncQualityRanksAsync(IReadOnlyDictionary<string, int> ranks, CancellationToken cancellationToken);
}
