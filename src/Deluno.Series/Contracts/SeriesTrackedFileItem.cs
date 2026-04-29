namespace Deluno.Series.Contracts;

public sealed record SeriesTrackedFileItem(
    string SeriesId,
    string? EpisodeId,
    string LibraryId,
    string Title,
    int? StartYear,
    int? SeasonNumber,
    int? EpisodeNumber,
    string FilePath,
    long? FileSizeBytes,
    DateTimeOffset? ImportedUtc,
    DateTimeOffset? LastVerifiedUtc);
