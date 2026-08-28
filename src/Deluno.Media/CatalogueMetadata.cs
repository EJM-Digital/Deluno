using System.Text.Json;
using Deluno.Contracts;
using Deluno.Integrations.Metadata;

namespace Deluno.Media;

/// <summary>
/// Turns what a provider answered into what the catalogue stores.
///
/// <para><b>This mapping is the one that keeps going wrong.</b> Four separate
/// attempts to persist a show's <c>status</c> each succeeded silently while
/// writing the wrong columns, because the mapping was written out again at every
/// call site — a fifteen-argument positional list in the endpoint, another in
/// the worker's refresh job, another in the repository. Adding a field meant
/// finding all of them, and missing one produced no error at all: a column that
/// exists, is declared in the filter registry, and is never written, so the
/// filter over it returns no rows and looks like a fair answer.</para>
///
/// <para>So there is one mapping, it takes the provider's own record, and every
/// write path goes through it. A field added to <see cref="MetadataSearchResult"/>
/// is either mapped here or mapped nowhere — which is a thing a test can check,
/// and <c>CatalogueMediaFactsTests</c> does.</para>
/// </summary>
public static class CatalogueMetadata
{
    /// <param name="madeBy">
    /// A film's studio or a show's network. The catalogues answer the same
    /// question with different provider fields, so the caller says which,
    /// rather than this guessing from <see cref="MetadataSearchResult.MediaType"/>.
    /// </param>
    public static MediaMetadataUpdate ToUpdate(string id, MetadataSearchResult metadata, string? madeBy)
        => new(
            id,
            metadata.Provider,
            metadata.ProviderId,
            metadata.OriginalTitle,
            metadata.Overview,
            metadata.PosterUrl,
            metadata.BackdropUrl,
            metadata.Rating,
            string.Join(", ", metadata.Genres),
            metadata.ExternalUrl,
            metadata.ImdbId,
            JsonSerializer.Serialize(metadata),
            metadata.RuntimeMinutes,
            metadata.Popularity,
            metadata.VoteCount,
            metadata.Status,
            madeBy,
            metadata.Certification,
            metadata.Collection,
            metadata.OriginalLanguage,
            ToRatings(metadata.Ratings),
            // Joined the way genres are, so the existing "contains" operator
            // reads it unchanged. An empty list stores null rather than an
            // empty string: null means "the provider did not say", which the
            // COALESCE in the write respects, and "" would mean "it said none".
            metadata.Keywords is { Count: > 0 } keywords ? string.Join(", ", keywords) : null);

    /// <summary>
    /// The scores Deluno keeps a column for, and only those.
    ///
    /// <para>A provider may answer with sources Deluno has no column for. They
    /// stay in the metadata blob, which is where the detail page reads them
    /// from, and are simply not filterable — as opposed to being silently
    /// dropped into whichever column happened to be next.</para>
    /// </summary>
    private static IReadOnlyList<MediaRatingFact> ToRatings(IReadOnlyList<MetadataRatingItem>? ratings)
        => ratings is null
            ? []
            : [.. ratings
                .Where(rating => RatingSources.Find(rating.Source) is not null)
                .Select(rating => new MediaRatingFact(
                    RatingSources.Find(rating.Source)!.Source,
                    rating.Score,
                    rating.VoteCount))];
}
