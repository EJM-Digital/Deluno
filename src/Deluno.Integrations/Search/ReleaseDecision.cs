using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Search;

public sealed record ReleaseDecision(
    string PolicyVersion,
    string Status,
    int Score,
    bool MeetsCutoff,
    string Summary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> RiskFlags,
    int QualityDelta,
    int CustomFormatScore,
    int SeederScore,
    int SizeScore,
    string? ReleaseGroup,
    double? EstimatedBitrateMbps,
    PreferenceEvaluation? PreferenceEvaluation = null,
    PreferenceComparison? PreferenceComparison = null,
    // The first gate that refused the release, in the engine's own words; null when nothing hard refused it (#1098).
    string? RejectionGate = null);
