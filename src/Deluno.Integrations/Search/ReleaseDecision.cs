namespace Deluno.Integrations.Search;

public sealed record ReleaseDecision(
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
    double? EstimatedBitrateMbps);
