using Deluno.Libraries.Contracts;

namespace Deluno.Media;

/// <summary>
/// Which of a title's libraries a title-level action uses (#530).
///
/// <para>A title is in the libraries a person put it in - usually one, and
/// sometimes two on purpose, such as an HD and a 4K copy. Search, grab and a
/// forced re-download from the title's page each go through one library's
/// indexers and clients. With one library that is the answer. With several the
/// action names the library, and without one it asks rather than picking: the
/// first row it happened to read used to be the answer, which sent a proof
/// film's search through a different library's indexer and found nothing.</para>
/// </summary>
public static class MediaLibraryChoice
{
    /// <returns>The row to act on, or why none was chosen.</returns>
    public static (MediaWantedItem? Row, string? Problem) Pick(
        IReadOnlyList<MediaWantedItem> rows,
        string? libraryId,
        IReadOnlyList<LibraryItem> libraries,
        string entityType)
    {
        var mine = rows.Where(row => !string.IsNullOrWhiteSpace(row.LibraryId)).ToArray();
        if (mine.Length == 0)
        {
            return (null, $"This {entityType} is not currently linked to a searchable library.");
        }

        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            var chosen = mine.FirstOrDefault(row => string.Equals(row.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase));
            return chosen is null
                ? (null, $"This {entityType} is not in that library.")
                : (chosen, null);
        }

        if (mine.Length == 1)
        {
            return (mine[0], null);
        }

        var names = mine
            .Select(row => libraries.FirstOrDefault(library => string.Equals(library.Id, row.LibraryId, StringComparison.OrdinalIgnoreCase))?.Name ?? row.LibraryId)
            .ToArray();
        return (null, $"This {entityType} is in {names.Length} libraries ({string.Join(", ", names)}). Choose which library to use.");
    }
}
