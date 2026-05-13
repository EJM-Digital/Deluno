namespace Deluno.Integrations.Search;

public sealed record IntelligentRoutingPreferences(
    string? PreferredQuality,
    double AverageCustomFormatScore,
    IReadOnlyList<string> PreferredReleaseGroups);

public sealed record IntelligentRoutingSnapshot(
    DateTimeOffset ComputedUtc,
    IntelligentRoutingPreferences Preferences,
    IReadOnlyDictionary<string, double> IndexerSuccessRates,
    IReadOnlyDictionary<string, double> DownloadClientSuccessRates);

public sealed record IntelligentRoutingAnomaly(
    string Code,
    string Severity,
    string Summary,
    string Details,
    DateTimeOffset DetectedUtc);

public sealed record IntelligentReleaseRecommendationRequest(
    string ReleaseName,
    int? Seeders,
    long? SizeBytes,
    int QualityDelta,
    int CustomFormatScore,
    int SourcePriorityScore,
    string? IndexerName,
    string? DownloadClientId,
    double? EstimatedBitrateMbps,
    double? ReleaseAgeHours);

public sealed record IntelligentReleaseRecommendation(
    int RecommendationScore,
    string RecommendationLabel,
    string Summary,
    double? IndexerSuccessRate,
    double? DownloadClientSuccessRate,
    ReleaseRankingBoostResult RankingBoost);

public interface IIntelligentRoutingService
{
    Task<IntelligentRoutingSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<IntelligentRoutingAnomaly>> DetectAnomaliesAsync(CancellationToken cancellationToken);
    Task<double?> GetDownloadClientSuccessRateAsync(string? downloadClientId, CancellationToken cancellationToken);
}
