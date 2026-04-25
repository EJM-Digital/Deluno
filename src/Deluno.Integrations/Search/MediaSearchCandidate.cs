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
    int? Seeders = null);
