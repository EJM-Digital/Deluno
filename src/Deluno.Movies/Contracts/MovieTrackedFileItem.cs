namespace Deluno.Movies.Contracts;

public sealed record MovieTrackedFileItem(
    string MovieId,
    string LibraryId,
    string Title,
    int? ReleaseYear,
    string FilePath,
    long? FileSizeBytes,
    DateTimeOffset? ImportedUtc,
    DateTimeOffset? LastVerifiedUtc);
