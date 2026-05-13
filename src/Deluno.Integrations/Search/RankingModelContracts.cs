namespace Deluno.Integrations.Search;

public sealed record ReleaseRankingFeatures(
    int? Seeders,
    long? SizeBytes,
    int QualityDelta,
    int CustomFormatScore,
    int SourcePriorityScore,
    double? EstimatedBitrateMbps,
    double? ReleaseAgeHours);

public sealed record ReleaseRankingBoostResult(
    bool Enabled,
    bool Applied,
    int BoostPoints,
    string Explanation);

public sealed record RankingModelStatus(
    bool Enabled,
    bool AutoDispatchImpactEnabled,
    int MaxAbsoluteBoost,
    string Mode,
    string Notes);

public interface IReleaseRankingModelService
{
    ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked);
    RankingModelStatus GetStatus();
}
