namespace Deluno.Movies.Contracts;

/// <summary>
/// What happened when a film's tracked file was found gone (#1065). Converts to
/// <c>true</c> when a row was cleared, so callers that only need "did it work"
/// read it as a bool.
/// </summary>
/// <param name="Cleared">A tracked row matched and was cleared.</param>
/// <param name="LibraryId">The library whose copy was removed.</param>
/// <param name="OnlyThatLibrary">
/// The film is kept in another library that Deluno still wants, so only
/// <paramref name="LibraryId"/> stopped being wanted and the title stays monitored.
/// When false the whole title was unmonitored.
/// </param>
/// <param name="OtherLibraryIds">The other libraries still managing the film.</param>
public readonly record struct MovieFileGoneOutcome(
    bool Cleared,
    string? LibraryId = null,
    bool OnlyThatLibrary = false,
    IReadOnlyList<string>? OtherLibraryIds = null)
{
    public static readonly MovieFileGoneOutcome NotFound = new(false);

    public static implicit operator bool(MovieFileGoneOutcome outcome) => outcome.Cleared;
}
