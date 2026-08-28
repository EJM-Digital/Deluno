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
    Task SyncQualityRanksAsync(IReadOnlyList<QualityTierDefinition> tiers, CancellationToken cancellationToken);
}

/// <summary>
/// A tier's size rule in bytes, for the kind of media a catalogue holds.
///
/// <para>The model states a film's rule in gigabytes and an episode's in
/// megabytes. A catalogue holds one kind, so it converts its own — and the
/// conversion lives here rather than in two SQL statements that would have to
/// agree about 1024 against 1000.</para>
///
/// <para>The bounds travel with the rank for the same reason the rank travels
/// at all: #309 asks whether a file you already keep still matches the rules
/// you set — <i>"a 2160p file sitting at 4 GB was accepted under a profile that
/// says 2160p should be 7–60 GB"</i> — and that comparison has to happen in the
/// catalogue's own database, beside the file size, or it cannot be a
/// filter.</para>
/// </summary>
public static class QualityTierBytes
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private const long Megabyte = 1024L * 1024;

    public static (long Floor, long Ceiling) ForMovie(QualityTierDefinition tier)
        => (Convert(tier.MovieMinGb, Gigabyte), Convert(tier.MovieMaxGb, Gigabyte));

    public static (long Floor, long Ceiling) ForEpisode(QualityTierDefinition tier)
        => (Convert(tier.EpisodeMinMb, Megabyte), Convert(tier.EpisodeMaxMb, Megabyte));

    /// <summary>Zero and anything negative mean "no bound", not "size zero".</summary>
    private static long Convert(double value, long unit)
        => value <= 0 ? 0 : (long)Math.Round(value * unit);
}
