using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #587: RSS sync reads an indexer's newest releases with the same request a
/// search makes, minus the search term.
/// </summary>
public sealed class ReadingAnIndexerFeedTests
{
    [Fact]
    public async Task A_feed_is_the_search_address_with_no_search_term()
    {
        Uri? asked = null;
        var reader = Reader(request =>
        {
            asked = request.RequestUri;
            return Rss("""
                <item><title>The.General.1926.1080p.BluRay.x264-DELUNO</title><pubDate>Wed, 23 Sep 2026 11:18:00 +0000</pubDate></item>
                <item><title>Breaking.Bad.S01E07.1080p.WEB-DL-DELUNO</title></item>
                """);
        });

        var feed = await reader.ReadRecentAsync(Indexer(), CancellationToken.None);

        Assert.True(feed.Read);
        Assert.Equal(new[] { "The.General.1926.1080p.BluRay.x264-DELUNO", "Breaking.Bad.S01E07.1080p.WEB-DL-DELUNO" }, feed.Releases.Select(r => r.ReleaseName));
        Assert.Equal(DateTimeOffset.Parse("2026-09-23T11:18:00Z"), feed.Releases[0].PublishedUtc);
        Assert.Null(feed.Releases[1].PublishedUtc);
        var query = asked!.Query;
        Assert.Contains("t=search", query);
        Assert.DoesNotContain("q=", query);
        Assert.Contains("cat=2000%2C5000", query);
        Assert.Contains("apikey=key-1", query);
    }

    [Fact]
    public async Task A_feed_that_answers_with_an_error_is_not_read_as_empty()
    {
        var feed = await Reader(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)).ReadRecentAsync(Indexer(), CancellationToken.None);

        Assert.False(feed.Read);
        Assert.Contains("403", feed.Problem);
    }

    private static IndexerFeedReader Reader(Func<HttpRequestMessage, HttpResponseMessage> send)
    {
        var throttle = new Mock<IOutboundRequestThrottle>();
        throttle.Setup(t => t.TryAcquireAsync(It.IsAny<string>(), It.IsAny<OutboundRate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TimeSpan.Zero);
        return new IndexerFeedReader(
            new StubFactory(new HttpClient(new StubHandler(send))),
            new PassThrough(),
            throttle.Object,
            NullLogger<IndexerFeedReader>.Instance);
    }

    private static IndexerItem Indexer()
        => ((IndexerItem)RuntimeHelpers.GetUninitializedObject(typeof(IndexerItem))) with
        {
            Id = "idx-1", Name = "Soak Torznab", BaseUrl = "http://127.0.0.1:9117", ApiKey = "key-1", Categories = "2000, 5000", IsEnabled = true
        };

    private static HttpResponseMessage Rss(string items)
        => new(HttpStatusCode.OK) { Content = new StringContent($"<rss><channel>{items}</channel></rss>", Encoding.UTF8, "application/rss+xml") };

    private sealed class PassThrough : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classify,
            CancellationToken cancellationToken)
        {
            try
            {
                return new IntegrationResilienceResult<T>(await operation(cancellationToken), false, false, 1, null, null);
            }
            catch (HttpRequestException exception)
            {
                return new IntegrationResilienceResult<T>(default, false, false, 1, exception.Message, null);
            }
        }

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }
    }

    private sealed class StubFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
