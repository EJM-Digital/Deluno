using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Deluno.Connections.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Search;
using Deluno.Integrations.Search.Trackers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Search.Trackers;

/// <summary>
/// #1301: the tracker engine's sign-in methods, cookie sign-in, redirect limit, request delay, download resolving, test connection and
/// recent-releases feed. A tracker site has no recorded reply to replay (a private one needs an account), so the pages here are a
/// site written for the test and the engine, definition parser and templates are the real ones.
/// </summary>
public sealed class TrackerTestAndRecentFeedTests
{
    // ---- test connection and the recent feed (tr30, tr34, tn41) ----------------------------------------------------------

    private static readonly TrackerDefinitionCatalog Catalog = new(overrideDirectory: null, NullLogger<TrackerDefinitionCatalog>.Instance);

    private static IndexerItem Tracker(string definitionId = "yts", string baseUrl = "https://yts.example/")
        => new(
            Id: "indexer-1", Name: "A tracker", Protocol: "tracker", Privacy: "public", BaseUrl: baseUrl, ApiKey: null, Priority: 1, Categories: "2000,5000",
            Tags: "", MediaScope: "both", IsEnabled: true, HealthStatus: "healthy", LastHealthMessage: null, LastHealthFailureCategory: null, LastHealthLatencyMs: null,
            LastHealthTestUtc: null, ConsecutiveFailures: 0, RateLimitedUntilUtc: null, DisabledReason: null, CreatedUtc: DateTimeOffset.UtcNow, UpdatedUtc: DateTimeOffset.UtcNow)
        { DefinitionId = definitionId };

    private static TrackerRelease Release(string title) => new() { Title = title, DownloadUrl = "https://yts.example/dl/" + title.Length };

    [Fact]
    public async Task A_tracker_test_says_how_many_releases_it_read_that_its_page_was_empty_that_its_rows_were_unreadable_and_that_it_did_not_answer_in_time()
    {
        var engine = new Mock<ITrackerEngine>();
        var next = new Queue<Func<TrackerSearchResult>>();
        engine.Setup(e => e.SearchAsync(It.IsAny<TrackerConnection>(), It.IsAny<TrackerQuery>(), It.IsAny<CancellationToken>()))
            .Returns<TrackerConnection, TrackerQuery, CancellationToken>((_, _, _) => Task.FromResult(next.Dequeue()()));
        var indexers = new TrackerIndexers(Catalog, engine.Object);
        next.Enqueue(() => new TrackerSearchResult([Release("one"), Release("three")], [], 0));
        next.Enqueue(() => new TrackerSearchResult([], [], 0));
        next.Enqueue(() => new TrackerSearchResult([], ["row 1: no title"], 5));

        var healthy = await indexers.TestAsync(Tracker(), CancellationToken.None);
        var empty = await indexers.TestAsync(Tracker(), CancellationToken.None);
        var unreadable = await indexers.TestAsync(Tracker(), CancellationToken.None);

        Assert.Equal(("healthy", "Reached yts.example and read 2 releases from its latest page.", null), healthy);
        Assert.Equal(("healthy", "Reached yts.example; its latest page had nothing in these categories.", null), empty);
        Assert.Equal("degraded", unreadable.HealthStatus);
        Assert.Equal("unexpected-response", unreadable.FailureCategory);
        Assert.Contains("could not read its results: row 1: no title", unreadable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tracker_test_names_a_refused_sign_in_as_auth_a_tracker_that_is_down_as_unreachable_and_a_slow_one_as_not_answering_in_time()
    {
        var engine = new Mock<ITrackerEngine>();
        var next = new Queue<Func<CancellationToken, Task<TrackerSearchResult>>>();
        engine.Setup(e => e.SearchAsync(It.IsAny<TrackerConnection>(), It.IsAny<TrackerQuery>(), It.IsAny<CancellationToken>()))
            .Returns<TrackerConnection, TrackerQuery, CancellationToken>((_, _, token) => next.Dequeue()(token));
        var indexers = new TrackerIndexers(Catalog, engine.Object, budget: TimeSpan.FromMilliseconds(50));
        next.Enqueue(_ => throw new TrackerException("A tracker did not accept the sign-in. Check the username and password."));
        next.Enqueue(_ => throw new TrackerException("A tracker is not answering."));
        next.Enqueue(async token =>
        {
            await Task.Delay(Timeout.Infinite, token); // reason: a tracker that never answers; the test's own 50 ms budget cancels the token
            return null!;
        });

        var refused = await indexers.TestAsync(Tracker(), CancellationToken.None);
        var down = await indexers.TestAsync(Tracker(), CancellationToken.None);
        var slow = await indexers.TestAsync(Tracker(), CancellationToken.None);

        Assert.Equal("auth", refused.FailureCategory);
        Assert.Equal("degraded", refused.HealthStatus);
        Assert.Equal("connectivity", down.FailureCategory);
        Assert.Equal("unreachable", down.HealthStatus);
        Assert.Equal("unreachable", slow.HealthStatus);
        Assert.Contains("did not answer within", slow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trackers_recent_feed_is_a_search_with_no_words_in_the_indexers_categories_and_an_unreadable_one_is_not_read_as_empty()
    {
        TrackerQuery? asked = null;
        var calls = 0;
        var engine = new Mock<ITrackerEngine>();
        engine.Setup(e => e.SearchAsync(It.IsAny<TrackerConnection>(), It.IsAny<TrackerQuery>(), It.IsAny<CancellationToken>()))
            .Returns<TrackerConnection, TrackerQuery, CancellationToken>((_, query, _) =>
            {
                asked ??= query;
                return ++calls == 1
                    ? Task.FromResult(new TrackerSearchResult([Release("Night.of.the.Living.Dead.1968.1080p-GRP"), Release("Dune.2021.2160p-GRP")], [], 0))
                    : throw new TrackerException("A tracker is not answering.");
            });
        var trackerIndexers = new TrackerIndexers(Catalog, engine.Object);
        var throttle = new Mock<IOutboundRequestThrottle>();
        throttle.Setup(t => t.TryAcquireAsync(It.IsAny<string>(), It.IsAny<OutboundRate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(TimeSpan.Zero);
        var reader = new IndexerFeedReader(Mock.Of<IHttpClientFactory>(), new PassThrough(), throttle.Object, NullLogger<IndexerFeedReader>.Instance, trackerIndexers);

        var feed = await reader.ReadRecentAsync(Tracker(), CancellationToken.None);
        var down = await reader.ReadRecentAsync(Tracker(), CancellationToken.None);

        Assert.True(feed.Read, feed.Problem);
        Assert.Equal(["Night.of.the.Living.Dead.1968.1080p-GRP", "Dune.2021.2160p-GRP"], feed.Releases.Select(release => release.ReleaseName));
        Assert.NotNull(asked);
        Assert.Equal(string.Empty, asked!.Keywords);
        Assert.Equal(TrackerCategories.Parse("2000,5000"), asked.Categories);
        Assert.False(down.Read);
        Assert.Contains("not answering", down.Problem, StringComparison.Ordinal);
    }

    private sealed class PassThrough : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(IntegrationResilienceRequest request, Func<CancellationToken, Task<T>> operation, Func<T, IntegrationResilienceOutcome> classify, CancellationToken cancellationToken)
            => new(await operation(cancellationToken), false, false, 1, null, null);

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
