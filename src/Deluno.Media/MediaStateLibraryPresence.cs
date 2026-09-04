using Deluno.Contracts;
using Deluno.Integrations.Metadata;

namespace Deluno.Media;

/// <summary>
/// Marks the search results the catalogue already holds.
///
/// <para>The identity it asks about is the one the Add request sends -
/// provider, provider id, IMDb id, title and year - so the question this
/// answers is literally "what would Add do with this?", and the answer comes
/// from the same matcher Add uses. Anything less exact and the Add screen would
/// be guessing on the catalogue's behalf.</para>
/// </summary>
public sealed class MediaStateLibraryPresence(IMediaStateRepository repository) : IMetadataLibraryPresence
{
    public async Task<IReadOnlyList<MetadataSearchResult>> MarkHeldTitlesAsync(
        IReadOnlyList<MetadataSearchResult> results,
        CancellationToken cancellationToken)
    {
        if (results.Count == 0)
        {
            return results;
        }

        var marked = results.ToArray();

        // Grouped by kind because films and shows are different tables in
        // different databases. A search asks for one or the other, so this is
        // normally a single group - but a mixed list must not have its shows
        // looked up in the movie catalogue and come back new.
        foreach (var group in results
            .Select((result, index) => (result, index))
            .GroupBy(item => KindOf(item.result.MediaType)))
        {
            var indexes = group.Select(item => item.index).ToArray();
            var identities = group.Select(item => IdentityOf(item.result)).ToArray();
            var found = await repository.FindExistingEntryIdsAsync(
                group.Key,
                identities,
                cancellationToken);

            for (var position = 0; position < indexes.Length; position++)
            {
                if (found[position] is { } entryId)
                {
                    marked[indexes[position]] = marked[indexes[position]] with { LibraryEntryId = entryId };
                }
            }
        }

        return marked;
    }

    private static MediaKind KindOf(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() is "tv" or "shows" or "series"
            ? MediaKind.Series
            : MediaKind.Movie;

    /// <summary>
    /// The fields an Add would carry that decide identity. Everything else a
    /// result holds - artwork, cast, overview - is detail the match ignores.
    /// </summary>
    private static MediaEntryCreate IdentityOf(MetadataSearchResult result)
        => new(
            result.Title,
            result.Year,
            result.ImdbId,
            Monitored: false,
            result.Provider,
            result.ProviderId,
            OriginalTitle: null,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Rating: null,
            Genres: null,
            ExternalUrl: null,
            MetadataJson: null);
}
