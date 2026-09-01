namespace Deluno.Quality.ReleasePreferences;

/// <summary>
/// Input for the typed release preview. The release name is only a convenient
/// source of open-world facts; callers may add probe or owner facts without
/// replacing those observations. A plan id/version is required so the preview
/// can be reproduced against the immutable decision contract used by search.
/// </summary>
public sealed record ReleasePreferencePreviewRequest(
    string? PlanId,
    string? PlanVersion,
    string? ReleaseName,
    string? CurrentReleaseName = null,
    string? CandidateQuality = null,
    string? CurrentQuality = null,
    int? Seeders = null,
    IReadOnlyList<PreferenceFact>? CandidateFacts = null,
    IReadOnlyList<PreferenceFact>? CurrentFacts = null);

/// <summary>
/// The complete, score-free result shown by the typed preview drawer. The
/// optional comparison is present only when an installed/current release was
/// supplied; otherwise the candidate is evaluated against the plan alone.
/// </summary>
public sealed record ReleasePreferencePreview(
    string ReleaseName,
    string PlanId,
    string PlanVersion,
    string PlanHash,
    IReadOnlyList<PreferenceFact> CandidateFacts,
    PreferenceEvaluation CandidateEvaluation,
    string? CurrentReleaseName,
    IReadOnlyList<PreferenceFact>? CurrentFacts,
    PreferenceEvaluation? CurrentEvaluation,
    PreferenceComparison? Comparison);
