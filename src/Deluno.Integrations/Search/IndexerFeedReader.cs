using System.Xml.Linq;
using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging;

namespace Deluno.Integrations.Search;

/// <summary>One release an indexer's feed lists.</summary>
public sealed record IndexerFeedRelease(string ReleaseName, DateTimeOffset? PublishedUtc);

/// <param name="Read">Whether the indexer answered. An unread feed is not an empty one.</param>
public sealed record IndexerFeedResult(bool Read, IReadOnlyList<IndexerFeedRelease> Releases, string? Problem = null);

/// <summary>Reads an indexer's recent-releases feed (#587).</summary>
public interface IIndexerFeedReader
{
    Task<IndexerFeedResult> ReadRecentAsync(IndexerItem indexer, CancellationToken cancellationToken);
}

/// <summary>
/// The newest releases an indexer lists, read the way a search reads it.
///
/// <para>Same address rules (<see cref="FeedMediaSearchPlanner.BuildFeedUrl"/>),
/// same pacing per host, same circuit and same HTTP client as a search, so a
/// feed read and a search cannot between them hit an indexer harder than either
/// would alone, and an indexer that is down for searches is down for this.</para>
/// </summary>
public sealed class IndexerFeedReader(
    IHttpClientFactory httpClientFactory,
    IIntegrationResiliencePolicy resiliencePolicy,
    IOutboundRequestThrottle outboundRequestThrottle,
    ILogger<IndexerFeedReader> logger,
    TrackerIndexers? trackerIndexers = null,
    Deluno.Connections.Data.IConnectionsRepository? connectionsRepository = null,
    /// <summary>
    /// One pooled handler per assigned indexer proxy (#976), so a feed read
    /// actually goes out through the proxy assigned to that indexer instead
    /// of always Deluno's own direct connection.
    /// </summary>
    Deluno.Connections.Data.IIndexerProxyHttpClientFactory? proxyHttpClientFactory = null) : IIndexerFeedReader
{
    private static readonly TimeSpan MaxThrottleWait = TimeSpan.FromSeconds(20);

    public async Task<IndexerFeedResult> ReadRecentAsync(IndexerItem indexer, CancellationToken cancellationToken)
    {
        // A tracker's newest releases are a search with no words (#606).
        var isTracker = IndexerProtocols.IsTracker(indexer.Protocol);
        if (!Uri.TryCreate(isTracker ? indexer.BaseUrl : FeedMediaSearchPlanner.BuildFeedUrl(indexer), UriKind.Absolute, out var uri))
        {
            return new IndexerFeedResult(false, [], $"{indexer.Name} has no usable address.");
        }

        var waited = await outboundRequestThrottle.TryAcquireAsync(
            FeedMediaSearchPlanner.ThrottleKey(uri),
            indexer.RequestIntervalSeconds is { } interval
                ? OutboundRate.FromInterval(TimeSpan.FromSeconds(Math.Max(2, interval)))
                : OutboundRate.PerIndexerDefault,
            MaxThrottleWait,
            cancellationToken);
        if (waited is null)
        {
            return new IndexerFeedResult(false, [], $"{indexer.Name} is still inside its request interval.");
        }

        var result = await resiliencePolicy.ExecuteAsync(
            new IntegrationResilienceRequest(
                $"indexer:{indexer.Id}:{FeedMediaSearchPlanner.SanitizeAddress(indexer.BaseUrl)}",
                "rss",
                FailureThreshold: 2,
                ServiceType: "indexer",
                ServiceId: indexer.Id,
                ServiceName: indexer.Name),
            async token =>
            {
                if (isTracker)
                {
                    if (trackerIndexers is null)
                    {
                        return new IndexerFeedResult(false, [], "This Deluno cannot search trackers.");
                    }

                    try
                    {
                        var feed = await trackerIndexers.SearchAsync(indexer, new TrackerQuery(string.Empty, Categories: TrackerCategories.Parse(indexer.Categories)), token);
                        return new IndexerFeedResult(true, ReadReleases(feed));
                    }
                    catch (TrackerException exception)
                    {
                        return new IndexerFeedResult(false, [], exception.Message);
                    }
                }

                var http = await CreateIndexerHttpClientAsync(indexer, token);
                using var response = await http.GetAsync(uri, token);
                if (!response.IsSuccessStatusCode)
                {
                    if (IntegrationResiliencePolicy.IsTransientHttpStatusCode(response.StatusCode))
                    {
                        throw new HttpRequestException(
                            $"Indexer {indexer.Name} returned transient HTTP {(int)response.StatusCode}.",
                            null,
                            response.StatusCode);
                    }

                    return new IndexerFeedResult(false, [], $"{indexer.Name} answered HTTP {(int)response.StatusCode}.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var document = await XDocument.LoadAsync(stream, LoadOptions.None, token);
                if (IndexerApiError.TryRead(document) is { } apiError)
                {
                    // HTTP 200 with an <error> element is a refusal, not an empty feed (#1311).
                    return new IndexerFeedResult(false, [], apiError.PlainMessage(indexer.Name));
                }

                return new IndexerFeedResult(true, ReadReleases(document));
            },
            _ => IntegrationResilienceOutcome.Success,
            cancellationToken);

        if (result.Succeeded && result.Value is { } read)
        {
            return read;
        }

        logger.LogInformation("Could not read {IndexerName}'s feed: {Problem}", indexer.Name, result.FailureMessage ?? "circuit open");
        return new IndexerFeedResult(
            false,
            [],
            result.CircuitOpen
                ? $"{indexer.Name} is resting after repeated failures."
                : result.FailureMessage ?? $"{indexer.Name} did not answer.");
    }

    /// <summary>Same routing a search uses (#976): this indexer's assigned proxy when it has one and it is an HTTP/SOCKS one, the shared direct client otherwise.</summary>
    private async Task<HttpClient> CreateIndexerHttpClientAsync(IndexerItem indexer, CancellationToken cancellationToken)
    {
        if (proxyHttpClientFactory is null || connectionsRepository is null || string.IsNullOrWhiteSpace(indexer.ProxyId))
        {
            return httpClientFactory.CreateClient("indexers");
        }

        var proxy = await Deluno.Connections.Data.IndexerProxyResolver.ResolveAsync(indexer, connectionsRepository, cancellationToken);
        return proxy is null
            ? httpClientFactory.CreateClient("indexers")
            : proxyHttpClientFactory.CreateClient(proxy);
    }

    private static IndexerFeedRelease[] ReadReleases(XDocument document)
        => document.Descendants("item")
            .Select(item => new IndexerFeedRelease(
                FeedMediaSearchPlanner.ReadReleaseTitle(item),
                FeedMediaSearchPlanner.ReadPublishedUtc(item)))
            .Where(release => !string.IsNullOrWhiteSpace(release.ReleaseName))
            .ToArray();
}
