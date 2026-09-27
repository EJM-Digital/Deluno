namespace Deluno.Quality;

public sealed record QualityTierDefinition(
    string Name,
    int Rank,
    double MovieMinGb,
    /// <summary>
    /// The film size (#771) release scoring aims for within this tier's
    /// Min/Max band — Radarr's "Preferred" column. A release exactly at this
    /// size scores best; one further off, in either direction, scores lower
    /// without being rejected, as long as it still falls inside Min/Max.
    /// </summary>
    double MoviePreferredGb,
    double MovieMaxGb,
    double EpisodeMinMb,
    /// <summary>The episode equivalent of <see cref="MoviePreferredGb"/>, in MB.</summary>
    double EpisodePreferredMb,
    double EpisodeMaxMb,
    int ScoreCeiling)
{
    /// <summary>
    /// A shipped default for a tier's Preferred size: 60% of the way from Min
    /// to Max, biased toward the top of the band rather than the exact
    /// midpoint — most encoders use most of the headroom a tier allows.
    /// Both this table's shipped defaults and <c>QualityTypicalSizes</c>'
    /// compute from this one formula, so the two never hand-type numbers
    /// that could quietly drift apart.
    /// </summary>
    public static double DefaultPreferred(double min, double max)
        => max > 0 ? Math.Round(min + (max - min) * 0.6, 2) : min;
}

public sealed record QualityUpgradeStopPolicy(
    bool StopWhenCutoffMet,
    bool RequireCustomFormatGainForSameQuality);

public sealed record QualityModelSnapshot(
    string Version,
    IReadOnlyList<QualityTierDefinition> Tiers,
    QualityUpgradeStopPolicy UpgradeStop,
    DateTimeOffset UpdatedUtc);

public sealed record UpdateQualityModelRequest(
    IReadOnlyList<QualityTierDefinition>? Tiers,
    QualityUpgradeStopPolicy? UpgradeStop);
