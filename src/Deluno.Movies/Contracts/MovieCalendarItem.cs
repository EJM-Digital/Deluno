namespace Deluno.Movies.Contracts;

/// <summary>
/// A film placed on a date. One film can appear more than once — a cinema date
/// and a digital date are different events and the user cares about both — so
/// <see cref="Kind"/> says which this row is.
/// </summary>
public sealed record MovieCalendarItem(
    string MovieId,
    string Title,
    int? ReleaseYear,
    string? PosterUrl,
    /// <summary>inCinemas | digital | physical</summary>
    string Kind,
    DateOnly Date,
    bool HasFile,
    bool Monitored);
