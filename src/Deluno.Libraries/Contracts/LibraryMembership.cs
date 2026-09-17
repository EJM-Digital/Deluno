namespace Deluno.Libraries.Contracts;

/// <summary>
/// Which libraries a title being added belongs to (#530).
///
/// <para>A title is in the libraries a person chose. Adding one used to make it
/// wanted in every library of its type, so each library's schedule searched for
/// it through its own indexers and clients, and a title meant for one library
/// was grabbed through another.</para>
/// </summary>
public static class LibraryMembership
{
    /// <param name="requestedLibraryId">The library the person chose; null when they did not say.</param>
    /// <param name="existingLibraryIds">The libraries the title is already in, for a title added again.</param>
    /// <returns>The libraries to hold it, or none when the requested library is not one of that type.</returns>
    public static IReadOnlyList<LibraryItem> Targets(
        IReadOnlyList<LibraryItem> libraries,
        string mediaType,
        string? requestedLibraryId,
        IReadOnlyCollection<string> existingLibraryIds)
    {
        var ofType = libraries.Where(library => string.Equals(library.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!string.IsNullOrWhiteSpace(requestedLibraryId))
        {
            return ofType.Where(library => string.Equals(library.Id, requestedLibraryId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        // Added again without a choice: it stays where it is.
        var existing = ofType.Where(library => existingLibraryIds.Contains(library.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (existing.Length > 0)
        {
            return existing;
        }

        // A new title with no choice goes to the first library, as the add
        // dialog lists them: with one library of the type, that is the one.
        return ofType.Take(1).ToArray();
    }
}
