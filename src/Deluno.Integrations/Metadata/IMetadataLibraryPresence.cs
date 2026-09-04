namespace Deluno.Integrations.Metadata;

/// <summary>
/// Says which search results the catalogue already holds.
///
/// <para><b>Why an interface here.</b> The answer lives in the media store, and
/// <c>Deluno.Media</c> references this project - so this project cannot
/// reference it back. The search endpoint asks for this instead, and the media
/// module supplies the one implementation.</para>
///
/// <para>The implementation must not carry its own idea of what counts as the
/// same title. Deluno already has exactly one - the matcher <c>AddAsync</c>
/// uses to decide whether it is inserting or handing back a row - and any
/// second copy would eventually tell the Add screen something the Add button
/// then contradicts.</para>
/// </summary>
public interface IMetadataLibraryPresence
{
    /// <summary>
    /// Returns the same results, each carrying
    /// <see cref="MetadataSearchResult.LibraryEntryId"/> when the catalogue
    /// already holds it.
    /// </summary>
    Task<IReadOnlyList<MetadataSearchResult>> MarkHeldTitlesAsync(
        IReadOnlyList<MetadataSearchResult> results,
        CancellationToken cancellationToken);
}
