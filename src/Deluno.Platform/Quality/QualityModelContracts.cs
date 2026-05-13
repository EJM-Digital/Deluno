namespace Deluno.Platform.Quality;

public sealed record QualityTierDefinition(
    string Name,
    int Rank,
    double MovieMinGb,
    double MovieMaxGb,
    double EpisodeMinMb,
    double EpisodeMaxMb,
    int ScoreCeiling);

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
