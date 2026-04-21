namespace Deluno.Platform.Quality;

public sealed record LibraryQualityDecision(
    string WantedStatus,
    string WantedReason,
    bool QualityCutoffMet,
    string? CurrentQuality,
    string? TargetQuality);
