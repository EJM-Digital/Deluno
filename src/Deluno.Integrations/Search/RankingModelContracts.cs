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
    string Notes,
    bool ModelLoaded = false,
    string? ActiveModelVersion = null,
    DateTimeOffset? LastTrainedUtc = null,
    int TrainingSampleCount = 0,
    double? LastAuc = null,
    double? LastAccuracy = null,
    IReadOnlyList<string>? AvailableVersions = null);

public sealed record RankingModelTrainingResult(
    bool Success,
    string Message,
    string? ModelVersion,
    int SampleCount,
    double? Auc,
    double? Accuracy,
    DateTimeOffset CompletedUtc);

public sealed record RankingModelRollbackRequest(string Version);

public interface IReleaseRankingModelService
{
    ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked);
    RankingModelStatus GetStatus();
}

public interface IReleaseRankingModelAdminService
{
    Task<RankingModelTrainingResult> TrainAsync(string reason, CancellationToken cancellationToken);
    bool TryRollback(string version, out string message);
}
