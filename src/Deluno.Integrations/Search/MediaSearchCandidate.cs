namespace Deluno.Integrations.Search;

public sealed record MediaSearchCandidate(
    string ReleaseName,
    string IndexerId,
    string IndexerName,
    string Quality,
    int Score,
    bool MeetsCutoff,
    string Summary,
    string? DownloadUrl = null,
    long? SizeBytes = null,
    int? Seeders = null,
    string DecisionStatus = "eligible",
    IReadOnlyList<string>? DecisionReasons = null,
    IReadOnlyList<string>? RiskFlags = null,
    int QualityDelta = 0,
    int CustomFormatScore = 0,
    int SeederScore = 0,
    int SizeScore = 0,
    string? ReleaseGroup = null,
    double? EstimatedBitrateMbps = null);
