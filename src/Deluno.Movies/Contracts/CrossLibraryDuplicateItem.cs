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

/// <summary>
/// The same film held twice, as two separate catalogue rows.
///
/// <para>Distinct from <see cref="CrossLibraryDuplicateItem"/>, which is one
/// catalogue row appearing in two libraries — usually deliberate. This one is
/// always a mistake, and until #419 nothing could see it: the only thing named
/// "duplicates" grouped by movie id, so two rows for one film could never
/// qualify.</para>
/// </summary>
public sealed record DuplicateTitleGroup(
    string Title,
    int? ReleaseYear,
    string? ImdbId,
    /// <summary>How the rows were judged to be the same film: "imdb" or "title-and-year".</summary>
    string MatchedOn,
    IReadOnlyList<DuplicateTitleEntry> Entries);

public sealed record DuplicateTitleEntry(
    string MovieId,
    string Title,
    int? ReleaseYear,
    string? ImdbId,
    bool HasMetadata,
    string? FilePath,
    DateTimeOffset CreatedUtc);

/// <summary>Both kinds of duplicate, because they are different problems.</summary>
public sealed record MovieDuplicateReport(
    IReadOnlyList<DuplicateTitleGroup> SameFilmTwice,
    IReadOnlyList<CrossLibraryDuplicateItem> SameFilmInTwoLibraries);
