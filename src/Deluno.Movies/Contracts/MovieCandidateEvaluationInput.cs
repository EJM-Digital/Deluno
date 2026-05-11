using Deluno.Platform.Contracts;

namespace Deluno.Movies.Contracts;

public sealed record MovieCandidateEvaluationInput(
    string MovieId,
    string? CurrentQuality,
    string CandidateQuality,
    string? TargetQuality,
    bool UpgradeUntilCutoff,
    bool UpgradeUnknownItems,
    bool PreventLowerQualityReplacements,
    QualityProfileItem? Profile);
