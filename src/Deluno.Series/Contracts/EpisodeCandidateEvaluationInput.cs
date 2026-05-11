using Deluno.Platform.Contracts;

namespace Deluno.Series.Contracts;

public sealed record EpisodeCandidateEvaluationInput(
    string SeriesId,
    string? EpisodeId,
    string? CurrentQuality,
    string CandidateQuality,
    string? TargetQuality,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    bool PreventLowerQualityReplacements,
    bool IsSeasonPack,
    QualityProfileItem? Profile);
