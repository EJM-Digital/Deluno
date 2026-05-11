namespace Deluno.Movies.Contracts;

public sealed record CrossLibraryDuplicateItem(
    string MovieId,
    string Title,
    int? ReleaseYear,
    string? ImdbId,
    IReadOnlyList<DuplicateLibraryEntry> Libraries);

public sealed record DuplicateLibraryEntry(
    string LibraryId,
    string LibraryName,
    string WantedStatus,
    bool HasFile,
    string? CurrentQuality);
