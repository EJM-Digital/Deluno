namespace Deluno.Movies.Contracts;

/// <summary>
/// A movie placed on a date. One movie can appear more than once — a cinema date
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
    bool Monitored,
    /// <summary>
    /// The stored wanted status — <c>missing</c>, <c>upgrade</c>, <c>covered</c>
    /// or <c>upcoming</c>. Null when Deluno tracks the movie in no library.
    ///
    /// The calendar draws the same mark as the shelf from this. Without it the
    /// page had only <see cref="HasFile"/> and had to invent its own words for
    /// what it saw — "Watching for it" in blue, for a title the shelf beside it
    /// was calling Missing in red (#302).
    /// </summary>
    string? WantedStatus);
