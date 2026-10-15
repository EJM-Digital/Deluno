using Deluno.Integrations.Metadata;

namespace Deluno.Persistence.Tests.Support;

/// <summary>
/// A metadata provider a test can steer and then question.
///
/// <para>The real one reaches TMDb or the managed broker over the network,
/// which a test must not, and whose answers are not Deluno's to assert. This
/// stands in for it wherever the thing under test is what Deluno does with a
/// lookup rather than what a provider knows.</para>
///
/// <para><b>It honours a provider id the way every real implementation does.</b>
/// A supplied id is an identity assertion, not a search hint: TMDb's direct
/// path takes <c>GetDetailsByIdAsync</c> and the managed gateway takes its own
/// detail lookup, and both answer with exactly one record carrying the cast,
/// crew, runtime and certification a search card never has. A double that
/// ignored the id would let the very defect this exists to catch pass.</para>
/// </summary>
internal sealed class RecordingMetadataProvider(
    IReadOnlyList<MetadataSearchResult> cards,
    IReadOnlyDictionary<string, MetadataSearchResult>? detailsByProviderId = null,
    bool answerOnlyTheTitleAsked = false) : IMetadataProvider
{
    private readonly IReadOnlyDictionary<string, MetadataSearchResult> _details =
        detailsByProviderId ?? new Dictionary<string, MetadataSearchResult>(StringComparer.Ordinal);

    /// <summary>Every lookup this provider was asked for, in order.</summary>
    public List<MetadataLookupRequest> Requests { get; } = [];

    public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        var providerId = request.ProviderId?.Trim();
        if (!string.IsNullOrWhiteSpace(providerId))
        {
            return Task.FromResult<IReadOnlyList<MetadataSearchResult>>(
                _details.TryGetValue(providerId, out var detail) ? [detail] : []);
        }

        var mediaType = request.MediaType?.Trim().ToLowerInvariant() is "tv" or "shows" or "series" ? "tv" : "movies";
        return Task.FromResult<IReadOnlyList<MetadataSearchResult>>(
            cards.Where(card => card.MediaType == mediaType
                && (!answerOnlyTheTitleAsked || (request.Query ?? string.Empty).Contains(card.Title, StringComparison.OrdinalIgnoreCase))).ToArray());
    }

    // Everything else throws. If a route under test starts needing more of the
    // provider than this, the test should say so rather than quietly exercising
    // a stub nobody has looked at.

    public Task<MetadataProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<MetadataProviderStatus> CheckReachabilityAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<MetadataProviderRecordLookup> ResolveProviderRecordAsync(
        MetadataLookupRequest request,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<MetadataSeason>> GetSeriesCatalogueAsync(
        string providerId,
        MetadataCatalogueSource source,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<MetadataEpisodeGroup>> GetSeriesEpisodeGroupsAsync(
        string providerId,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<MetadataReleaseDates> GetMovieReleaseDatesAsync(
        string providerId,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<MetadataCollection?> GetMovieCollectionAsync(
        string providerId,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    /// <summary>Every card this test gave it, for the requested kind — Discover's "popular" grid (#785).</summary>
    public Task<IReadOnlyList<MetadataSearchResult>> GetPopularAsync(
        string mediaType,
        int page,
        CancellationToken cancellationToken)
    {
        var normalized = mediaType.Trim().ToLowerInvariant() is "tv" or "shows" or "series" ? "tv" : "movies";
        return Task.FromResult<IReadOnlyList<MetadataSearchResult>>(
            cards.Where(card => card.MediaType == normalized).ToArray());
    }
}
