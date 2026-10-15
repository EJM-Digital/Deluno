using System.Net;
using System.Text;
using System.Text.Json;
using Deluno.Connections.Contracts;
using Deluno.Connections.Data;
using Deluno.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Search;
using Deluno.Libraries.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Deluno.Quality;
using Deluno.Quality.Contracts;
using Deluno.Quality.Guides;
using Deluno.Quality.ReleasePreferences;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Search;

public sealed class FeedMediaSearchPlannerReasonTests
{
    [Fact]
    public async Task No_linked_indexers_returns_no_indexers_reason()
    {
        var connections = new Mock<IConnectionsRepository>();
        connections
            .Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexerItem>());

        var plan = await CreatePlanner(connections.Object).BuildPlanAsync(
            "Example",
            2026,
            "movies",
            null,
            "WEB 1080p",
            [],
            cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.NoIndexers, plan.Reason);
        Assert.Empty(plan.Candidates);
    }

    private static (Mock<IRealtimeEventPublisher> Publisher, List<IndexerQueryRecordedEvent> Queries) RecordingQueries()
    {
        var queries = new List<IndexerQueryRecordedEvent>();
        var publisher = new Mock<IRealtimeEventPublisher>();
        publisher
            .Setup(p => p.PublishLiveEventAsync(LiveEventNames.IndexerQueryRecorded, It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<string, object, CancellationToken>((_, payload, _) =>
            {
                lock (queries)
                {
                    queries.Add((IndexerQueryRecordedEvent)payload);
                }
            })
            .Returns(Task.CompletedTask);
        return (publisher, queries);
    }

    /// <summary>
    /// #1097: each indexer question is pushed to Live as it is asked and again as it is answered,
    /// so a person sees which indexer is being asked and how long it took.
    /// </summary>
    [Fact]
    public async Task Each_indexer_query_is_pushed_as_it_is_asked_and_as_it_is_answered()
    {
        var indexer = CreateIndexer("fixture-indexer", "Fixture indexer");
        var (publisher, queries) = RecordingQueries();

        await CreatePlanner(CreateConnections(indexer).Object, realtime: publisher.Object).BuildPlanAsync(
            "Example", 2026, "movies", null, "WEB 1080p", [CreateSource(indexer)], cancellationToken: CancellationToken.None);

        Assert.Equal(["asked", "answered"], queries.Select(query => query.Phase).ToArray());
        Assert.All(queries, query =>
        {
            Assert.Equal("fixture-indexer", query.IndexerId);
            Assert.Equal("Fixture indexer", query.IndexerName);
            Assert.Equal("Example 2026", query.Query);
        });
        Assert.Null(queries[0].ResultCount);
        Assert.Equal(0, queries[1].ResultCount);
        Assert.False(queries[1].Failed);
        Assert.NotNull(queries[1].ElapsedMs);
    }

    [Fact]
    public async Task An_indexer_that_fails_is_pushed_as_answered_with_its_reason()
    {
        var indexer = CreateIndexer("unreachable-indexer", "Primary indexer");
        var (publisher, queries) = RecordingQueries();

        await CreatePlanner(CreateConnections(indexer).Object, resiliencePolicy: new TimingOutResiliencePolicy(), realtime: publisher.Object).BuildPlanAsync(
            "Example", 2026, "movies", null, "WEB 1080p", [CreateSource(indexer)], cancellationToken: CancellationToken.None);

        var answered = Assert.Single(queries, query => query.Phase == "answered");
        Assert.True(answered.Failed);
        Assert.False(string.IsNullOrWhiteSpace(answered.Failure));
    }

    /// <summary>
    /// A failure that cannot name the indexer it came from is not attributable,
    /// which is half of what #338 asks for. The resilience policy only knew the
    /// operation, so a person was told "indexer.search indexer.search failed"
    /// and left to guess which of their indexers to look at.
    /// </summary>
    [Fact]
    public async Task An_indexer_failure_names_the_indexer_and_not_the_operation_key()
    {
        var indexer = CreateIndexer("unreachable-indexer", "Primary indexer");
        var connections = CreateConnections(indexer);

        var plan = await CreatePlanner(
            connections.Object,
            resiliencePolicy: new TimingOutResiliencePolicy()).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        var failure = Assert.Single(plan.Failures ?? []);

        Assert.Equal("indexer", failure.ServiceType);
        Assert.Equal("unreachable-indexer", failure.ServiceId);
        Assert.Equal("Primary indexer", failure.ServiceName);
        Assert.Equal("search", failure.Operation);

        // The sentence a person actually reads.
        Assert.Equal("Primary indexer search failed: The service did not answer in time.", failure.Summary);
        Assert.DoesNotContain("indexer.search", failure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", failure.Summary, StringComparison.Ordinal);

        // The plumbing detail is kept, just not presented as the explanation.
        Assert.Contains("HttpClient", failure.UpstreamDetail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(IntegrationFailureKind.Timeout, failure.Kind);
    }

    /// <summary>
    /// The paused-integration wording travels through the same Summary
    /// template, so it must not name the service a second time.
    /// </summary>
    [Fact]
    public void A_paused_integration_names_the_service_once()
    {
        var failure = IntegrationFailureFactory.CircuitOpen(
            "indexer",
            "primary",
            "Primary indexer",
            "search",
            DateTimeOffset.UtcNow.AddMinutes(5),
            "Deluno paused it after repeated failures.");

        Assert.Equal(
            "Primary indexer search failed: Deluno paused it after repeated failures.",
            failure.Summary);

        var occurrences = failure.Summary.Split("Primary indexer").Length - 1;
        Assert.Equal(1, occurrences);
    }

    /// <summary>
    /// An indexer that does not answer is an ordinary, expected condition, and
    /// this planner already carries a typed failure for it. It used to throw a
    /// NullReferenceException out of the telemetry builder instead, so the API
    /// returned HTTP 500 and the UI could only say "The search request failed."
    /// Found on the deployed lab in Chrome, not by any test.
    /// </summary>
    [Fact]
    public async Task An_unreachable_indexer_is_reported_as_a_failure_rather_than_crashing_the_search()
    {
        var indexer = CreateIndexer("unreachable-indexer", "Unreachable indexer");
        var connections = CreateConnections(indexer);

        var plan = await CreatePlanner(
            connections.Object,
            resiliencePolicy: new ExhaustedResiliencePolicy()).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.AllIndexersFailed, plan.Reason);
        Assert.Empty(plan.Candidates);
        Assert.Null(plan.BestCandidate);

        // The failure is attributable, which is the whole point of carrying it.
        var failure = Assert.Single(plan.Failures ?? []);
        Assert.Equal("Unreachable indexer", failure.ServiceName);
    }

    /// <summary>
    /// #606: The Pirate Bay's circuit was open and every search silently
    /// skipped it -- healthy/untested on the Indexers screen, no telemetry,
    /// nothing in this search's failures. Both halves of that are asserted
    /// here: the skip is attributable in this search's own result, and it is
    /// also written to the indexer's own health so the screen says so without
    /// anyone having to read a search log.
    /// </summary>
    [Fact]
    public async Task A_search_skipped_by_an_open_circuit_is_reported_in_failures_and_on_the_indexer_itself()
    {
        var indexer = CreateIndexer("tpb-id", "The Pirate Bay");
        var connections = CreateConnections(indexer);
        var retryAfterUtc = DateTimeOffset.Parse("2026-09-25T18:30:00Z");

        var plan = await CreatePlanner(
            connections.Object,
            resiliencePolicy: new CircuitOpenResiliencePolicy(retryAfterUtc)).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.CircuitOpen, plan.Reason);
        var failure = Assert.Single(plan.Failures ?? []);
        Assert.Equal("The Pirate Bay", failure.ServiceName);
        Assert.Equal(IntegrationFailureKind.CircuitOpen, failure.Kind);

        // The same fact must reach the indexer's own health -- a hidden
        // circuit that only a search log shows is exactly what let the rig
        // keep reporting the indexer as healthy while it was never asked.
        connections.Verify(
            repository => repository.UpdateIndexerHealthAsync(
                "tpb-id",
                "circuitOpen",
                It.Is<string>(message =>
                    message.Contains("The Pirate Bay", StringComparison.Ordinal) &&
                    message.Contains("18:30", StringComparison.Ordinal) &&
                    message.Contains("Reset", StringComparison.Ordinal)),
                "circuitOpen",
                null,
                It.IsAny<CancellationToken>(),
                It.IsAny<IntegrationFailure>()),
            Times.Once);
    }

    [Fact]
    public async Task Malformed_feed_returns_all_indexers_failed_and_logs_indexer_warning()
    {
        var logger = new RecordingLogger<FeedMediaSearchPlanner>();
        var indexer = CreateIndexer("malformed-indexer", "Malformed indexer");
        var connections = CreateConnections(indexer);

        var plan = await CreatePlanner(
            connections.Object,
            new FixedFeedHandler("not xml"),
            logger).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.AllIndexersFailed, plan.Reason);
        Assert.Contains(logger.Messages, message =>
            message.Level == LogLevel.Warning &&
            message.Text.Contains("Malformed indexer", StringComparison.Ordinal) &&
            message.Text.Contains("could not read", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// #598: when every indexer was still inside its request interval, nobody
    /// was asked. That used to read as an empty search ("no results… check
    /// indexer health") and cost the title a whole retry window.
    /// </summary>
    [Fact]
    public async Task A_search_no_indexer_had_time_for_says_the_indexers_were_busy()
    {
        var indexer = CreateIndexer("busy-indexer", "Busy indexer");
        var plan = await CreatePlanner(CreateConnections(indexer).Object, indexersBusy: true).BuildPlanAsync(
            "Cleaner",
            2007,
            "movies",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.IndexersBusy, plan.Reason);
        Assert.Empty(plan.Candidates);
        Assert.Contains("No indexer was asked", plan.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// #1184: a pacing slot the caller can only get after waiting at least <c>needs</c>. The feed read that raises a
    /// search has just used the slot of the indexer it read.
    /// </summary>
    private static IOutboundRequestThrottle ThrottleNeedingAWaitOf(TimeSpan needs, List<TimeSpan>? offered = null)
    {
        var throttle = new Mock<IOutboundRequestThrottle>();
        throttle
            .Setup(service => service.TryAcquireAsync(It.IsAny<string>(), It.IsAny<OutboundRate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((string _, OutboundRate _, TimeSpan maxWait, CancellationToken _) =>
            {
                offered?.Add(maxWait);
                return new ValueTask<TimeSpan?>(maxWait >= needs ? needs : null);
            });
        return throttle.Object;
    }

    private const string ExactTargetFeed =
        "<rss><channel><item><title>Big.Buck.Bunny.2008.1080p.WEB-DL.x264-GOLDEN</title>" +
        "<link>https://fixture.invalid/release/1</link>" +
        "<enclosure url=\"https://fixture.invalid/release/1.nzb\" length=\"1700000000\" type=\"application/x-nzb\" /></item></channel></rss>";

    /// <summary>
    /// #1184: the indexer whose new release started a search is waited for through its whole request interval, so
    /// the search still sees its exact-target release. Without that, the ordinary twenty-second wait lapses on an
    /// indexer with a longer interval and the search is decided without the very release that raised it.
    /// </summary>
    [Fact]
    public async Task A_search_started_by_an_indexers_new_release_waits_for_that_indexers_slot_and_sees_its_release()
    {
        var indexer = CreateIndexer("usenet-indexer", "Usenet indexer") with { RequestIntervalSeconds = 60 };
        var offered = new List<TimeSpan>();
        var planner = CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(ExactTargetFeed),
            throttleOverride: ThrottleNeedingAWaitOf(TimeSpan.FromSeconds(60), offered));

        var ordinary = await planner.BuildPlanAsync(
            "Big Buck Bunny", 2008, "movies", null, "WEB 1080p", [CreateSource(indexer)], cancellationToken: CancellationToken.None);
        var patient = await planner.BuildPlanAsync(
            "Big Buck Bunny", 2008, "movies", null, "WEB 1080p", [CreateSource(indexer)],
            cancellationToken: CancellationToken.None, patientIndexerIds: ["usenet-indexer"]);

        // An ordinary search is paced out after twenty seconds, exactly as before.
        Assert.Equal(MediaSearchReasons.IndexersBusy, ordinary.Reason);
        Assert.Empty(ordinary.Candidates);
        Assert.Equal(TimeSpan.FromSeconds(20), offered[0]);

        // The search that indexer started waited its interval plus a margin, and has the release.
        Assert.True(offered[1] >= TimeSpan.FromSeconds(60));
        var candidate = Assert.Single(patient.Candidates);
        Assert.Contains("GOLDEN", candidate.ReleaseName, StringComparison.Ordinal);
        Assert.Empty(patient.IndexersNotHeardFrom ?? []);
        Assert.True(AcquisitionDecisionPipeline.IsSafeForAutomaticDispatch(candidate), $"{candidate.DecisionStatus} cutoff={candidate.MeetsCutoff} delta={candidate.QualityDelta}");
    }

    /// <summary>
    /// #1184: an indexer a search could not hear from is named, with when to ask again, even when another indexer
    /// answered. The handler uses it to re-queue a feed-started search shortly instead of waiting a retry window.
    /// </summary>
    [Fact]
    public async Task An_indexer_skipped_for_pacing_is_named_with_when_to_ask_it_again()
    {
        var slow = CreateIndexer("slow-indexer", "Slow indexer") with { BaseUrl = "https://slow.invalid/api", RequestIntervalSeconds = 90 };
        var quick = CreateIndexer("quick-indexer", "Quick indexer") with { BaseUrl = "https://quick.invalid/api" };
        var connections = new Mock<IConnectionsRepository>();
        connections
            .Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([slow, quick]);
        var throttle = new Mock<IOutboundRequestThrottle>();
        throttle
            .Setup(service => service.TryAcquireAsync(It.IsAny<string>(), It.IsAny<OutboundRate>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((string host, OutboundRate _, TimeSpan _, CancellationToken _) =>
                new ValueTask<TimeSpan?>(host.StartsWith("slow", StringComparison.Ordinal) ? null : TimeSpan.Zero));

        var plan = await CreatePlanner(connections.Object, new FixedFeedHandler(ExactTargetFeed), throttleOverride: throttle.Object).BuildPlanAsync(
            "Big Buck Bunny", 2008, "movies", null, "WEB 1080p", [CreateSource(slow), CreateSource(quick)], cancellationToken: CancellationToken.None);

        Assert.NotEmpty(plan.Candidates);
        var missed = Assert.Single(plan.IndexersNotHeardFrom ?? []);
        Assert.Equal("slow-indexer", missed.IndexerId);
        Assert.Equal(TimeSpan.FromSeconds(90) + TimeSpan.FromMinutes(1), missed.AskAgainAfter);
    }

    [Fact]
    public async Task Empty_feed_returns_no_results_reason()
    {
        var indexer = CreateIndexer("empty-indexer", "Empty indexer");
        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(
                """
                <?xml version="1.0" encoding="UTF-8"?>
                <rss version="2.0"><channel /></rss>
                """),
            new RecordingLogger<FeedMediaSearchPlanner>()).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(MediaSearchReasons.NoResults, plan.Reason);
        Assert.Empty(plan.Candidates);
    }

    /// <summary>
    /// #752: a generic Torznab item's magneturl attribute is captured into the
    /// release. With the indexer's own Prefer magnet links switch on, the
    /// magnet becomes what Deluno actually grabs, not the enclosure .torrent
    /// link the item also carried.
    /// </summary>
    [Fact]
    public async Task A_torznab_items_magnet_attribute_is_preferred_when_the_indexer_asks_for_it()
    {
        var indexer = CreateIndexer("magnet-preferring-indexer", "Magnet preferring indexer") with { PreferMagnetUrl = true };
        var feed = "<rss><channel><item>" +
            "<title>Example.Release.2026.WEB.1080p</title>" +
            "<enclosure url=\"https://fixture.invalid/release/1.torrent\" length=\"1000\" type=\"application/x-bittorrent\" />" +
            "<torznab:attr xmlns:torznab=\"http://torznab.com/schemas/2015/feed\" name=\"magneturl\" value=\"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567\" />" +
            "</item></channel></rss>";

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(feed)).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        var candidate = Assert.Single(plan.Candidates);
        Assert.StartsWith("magnet:", candidate.DownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Without the switch, the enclosure/.torrent link is kept even when a magnet is also offered.</summary>
    [Fact]
    public async Task A_torznab_items_torrent_link_is_kept_when_the_indexer_does_not_prefer_magnet()
    {
        var indexer = CreateIndexer("magnet-indifferent-indexer", "Magnet indifferent indexer");
        var feed = "<rss><channel><item>" +
            "<title>Example.Release.2026.WEB.1080p</title>" +
            "<enclosure url=\"https://fixture.invalid/release/1.torrent\" length=\"1000\" type=\"application/x-bittorrent\" />" +
            "<torznab:attr xmlns:torznab=\"http://torznab.com/schemas/2015/feed\" name=\"magneturl\" value=\"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567\" />" +
            "</item></channel></rss>";

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(feed)).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal("https://fixture.invalid/release/1.torrent", candidate.DownloadUrl);
    }

    /// <summary>A usenet (newznab) indexer's own field is torrent-only; a magneturl attribute (never sent by usenet indexers in practice) must never override its link.</summary>
    [Fact]
    public async Task A_newznab_indexer_never_prefers_a_magnet_even_if_one_is_present()
    {
        var indexer = CreateIndexer("usenet-indexer", "Usenet indexer") with { Protocol = "newznab", PreferMagnetUrl = true };
        var feed = "<rss><channel><item>" +
            "<title>Example.Release.2026.WEB.1080p</title>" +
            "<enclosure url=\"https://fixture.invalid/release/1.nzb\" length=\"1000\" type=\"application/x-nzb\" />" +
            "<torznab:attr xmlns:torznab=\"http://torznab.com/schemas/2015/feed\" name=\"magneturl\" value=\"magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567\" />" +
            "</item></channel></rss>";

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(feed)).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal("https://fixture.invalid/release/1.nzb", candidate.DownloadUrl);
    }

    [Fact]
    public async Task Feed_parser_keeps_every_returned_item_and_requests_a_protocol_limit()
    {
        var indexer = CreateIndexer("full-feed-indexer", "Full feed indexer");
        var handler = new FixedFeedHandler(CreateFeed(45));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(45, plan.Candidates.Count);
        Assert.False(plan.CandidatesTruncatedByIndexer);
        Assert.Contains("limit=100", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Full_indexer_page_is_reported_as_potentially_truncated()
    {
        var indexer = CreateIndexer("full-page-indexer", "Full page indexer");

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(CreateFeed(100))).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(100, plan.Candidates.Count);
        Assert.True(plan.CandidatesTruncatedByIndexer);
    }

    [Fact]
    public async Task Equivalent_release_names_are_deduplicated_after_deterministic_ranking()
    {
        var indexer = CreateIndexer("duplicate-indexer", "Duplicate indexer");
        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(
                """
                <rss><channel>
                  <item><title>Example.Release.WEB.1080p</title><link>https://fixture.invalid/one</link></item>
                  <item><title>example_release_web_1080p</title><link>https://fixture.invalid/two</link></item>
                </channel></rss>
                """)).BuildPlanAsync(
                    "Example",
                    2026,
                    "movies",
                    null,
                    "WEB 1080p",
                    [CreateSource(indexer)],
                    cancellationToken: CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Same(plan.Candidates[0], plan.BestCandidate);
        Assert.Equal("Example.Release.WEB.1080p", plan.BestCandidate!.ReleaseName);
    }

    [Fact]
    public async Task Absolute_tv_numbering_uses_an_alternate_search_query()
    {
        var indexer = CreateIndexer("absolute-tv-indexer", "Absolute TV indexer", "tv");
        var handler = new FixedFeedHandler(CreateFeed(1));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 1,
                episodeNumber: 2,
                numberingScheme: "absolute",
                absoluteNumber: 101,
                cancellationToken: CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Contains("t=search", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=Example%20101", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("season=", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ep=", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Scene_tv_numbering_uses_scene_season_and_episode_fields()
    {
        var indexer = CreateIndexer("scene-tv-indexer", "Scene TV indexer", "tv");
        var handler = new FixedFeedHandler(CreateFeed(1));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 1,
                episodeNumber: 2,
                numberingScheme: "scene",
                sceneSeasonNumber: 3,
                sceneEpisodeNumber: 4,
                cancellationToken: CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Contains("t=tvsearch", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=Example", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("season=3", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ep=4", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Season_search_keeps_the_title_clean_and_uses_the_dedicated_season_field()
    {
        var indexer = CreateIndexer("season-tv-indexer", "Season TV indexer", "tv");
        var handler = new FixedFeedHandler(CreateFeed(1));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 2,
                cancellationToken: CancellationToken.None);

        Assert.Single(plan.Candidates);
        Assert.Contains("t=tvsearch", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q=Example", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("season=2", handler.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Season%202", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#742: an anime title queries the indexer's own anime category numbers when configured.</summary>
    [Fact]
    public async Task An_anime_tv_search_uses_the_indexers_anime_categories_when_configured()
    {
        var indexer = CreateIndexer("anime-indexer", "Anime indexer", "tv") with { AnimeCategories = "5070" };
        var handler = new FixedFeedHandler(CreateFeed(1));

        await CreatePlanner(CreateConnections(indexer).Object, handler).BuildPlanAsync(
            "Example",
            2026,
            "tv",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            genres: "Action, Anime",
            cancellationToken: CancellationToken.None);

        Assert.Contains("cat=5070", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An indexer with no anime categories configured keeps using its normal ones for an anime search, which for a TV search is the TV family only (#1337).</summary>
    [Fact]
    public async Task An_anime_tv_search_falls_back_to_normal_categories_when_none_are_configured()
    {
        var indexer = CreateIndexer("no-anime-indexer", "No anime indexer", "tv");
        var handler = new FixedFeedHandler(CreateFeed(1));

        await CreatePlanner(CreateConnections(indexer).Object, handler).BuildPlanAsync(
            "Example",
            2026,
            "tv",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            genres: "Anime",
            cancellationToken: CancellationToken.None);

        Assert.Contains("cat=5000", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A non-anime TV search never uses anime categories, even when the indexer has some configured.</summary>
    [Fact]
    public async Task A_non_anime_tv_search_ignores_the_indexers_anime_categories()
    {
        var indexer = CreateIndexer("drama-indexer", "Drama indexer", "tv") with { AnimeCategories = "5070" };
        var handler = new FixedFeedHandler(CreateFeed(1));

        await CreatePlanner(CreateConnections(indexer).Object, handler).BuildPlanAsync(
            "Example",
            2026,
            "tv",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            genres: "Drama",
            cancellationToken: CancellationToken.None);

        Assert.Contains("cat=5000", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>#753: the manual Search page's own category picker narrows a newznab/torznab query to just the categories chosen, instead of the indexer's own defaults.</summary>
    [Fact]
    public async Task A_chosen_category_list_overrides_the_indexers_own_default_categories()
    {
        var indexer = CreateIndexer("category-indexer", "Category indexer");
        var handler = new FixedFeedHandler(CreateFeed(1));

        await CreatePlanner(CreateConnections(indexer).Object, handler).BuildPlanAsync(
            "Example",
            2026,
            "movies",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None,
            categoryIds: [2040, 2050]);

        Assert.Contains("cat=2040%2c2050", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A chosen category from the other media type's family is dropped rather than sent to the wrong engine.</summary>
    [Fact]
    public async Task A_chosen_category_outside_this_searchs_family_is_ignored()
    {
        var indexer = CreateIndexer("mixed-category-indexer", "Mixed category indexer");
        var handler = new FixedFeedHandler(CreateFeed(1));

        await CreatePlanner(CreateConnections(indexer).Object, handler).BuildPlanAsync(
            "Example",
            2026,
            "movies",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None,
            categoryIds: [5040]);

        // Nothing survived the family filter, so the indexer's own default stands.
        Assert.Contains("cat=2000", handler.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_candidate_carries_its_protocol_age_leechers_and_grabs_for_the_enriched_results_columns()
    {
        // #753/#969: the manual Search page and the per-title interactive
        // search results both add protocol/age/seeders/leechers/grabs
        // columns -- this is where those values are captured, straight off
        // the same torznab attributes the release-acceptance decision
        // already reads.
        var indexer = CreateIndexer("enriched-indexer", "Enriched indexer");
        var handler = new FixedFeedHandler(
            $"""
            <rss><channel>
              <item>
                <title>Example.Release.WEB.1080p</title>
                <link>https://fixture.invalid/x</link>
                <pubDate>{DateTimeOffset.UtcNow.AddHours(-5):R}</pubDate>
                <torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="seeders" value="12" />
                <torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="peers" value="3" />
                <torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="grabs" value="47" />
              </item>
            </channel></rss>
            """);

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Equal("torznab", candidate.IndexerProtocol);
        Assert.Equal(12, candidate.Seeders);
        Assert.Equal(3, candidate.Leechers);
        Assert.Equal(47, candidate.Grabs);
        Assert.NotNull(candidate.ReleaseAgeHours);
        Assert.InRange(candidate.ReleaseAgeHours!.Value, 4.9, 5.1);
    }

    [Fact]
    public async Task A_candidate_leaves_grabs_blank_when_the_indexer_never_reports_it()
    {
        // #969: "blank when the indexer does not report it" -- an indexer
        // that never sends a grabs attr must not surface a fabricated 0.
        var indexer = CreateIndexer("no-grabs-indexer", "No grabs indexer");
        var handler = new FixedFeedHandler(
            $"""
            <rss><channel>
              <item>
                <title>Example.Release.WEB.1080p</title>
                <link>https://fixture.invalid/x</link>
                <pubDate>{DateTimeOffset.UtcNow.AddHours(-5):R}</pubDate>
                <torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="seeders" value="12" />
              </item>
            </channel></rss>
            """);

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        var candidate = Assert.Single(plan.Candidates);
        Assert.Null(candidate.Grabs);
    }

    [Fact]
    public async Task Typed_search_exposes_and_uses_the_explicit_seeder_tie_break_family()
    {
        var indexer = CreateIndexer("seeders-indexer", "Seeders indexer");
        var handler = new FixedFeedHandler(
            """
            <rss><channel>
              <item><title>Example.Release.Z.WEB.1080p</title><link>https://fixture.invalid/z</link><torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="seeders" value="0" /></item>
              <item><title>Example.Release.A.WEB.1080p</title><link>https://fixture.invalid/a</link><torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="seeders" value="12" /></item>
            </channel></rss>
            """);

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                cancellationToken: CancellationToken.None);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Equal("Example.Release.A.WEB.1080p", plan.BestCandidate!.ReleaseName);
        var seederFamily = Assert.Single(
            plan.BestCandidate.PreferenceEvaluation!.Families,
            family => family.FamilyId == "transient.seeders");
        Assert.Equal(PreferenceFactState.Present, seederFamily.State);
        Assert.Equal("available", seederFamily.SelectedLevelId);
        Assert.DoesNotContain("score", seederFamily.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// #646: a fresh season search - nothing installed, so
    /// SeasonPackReplacementDecision never runs - had no factor anywhere for
    /// "this release is a whole season in one file". A pack and a same-quality
    /// single episode scored identically on everything but size, so the season
    /// search took whichever the indexers happened to order first.
    /// </summary>
    [Fact]
    public async Task Season_search_prefers_a_season_pack_over_a_same_quality_single_episode()
    {
        var indexer = CreateIndexer("season-pack-indexer", "Season pack indexer", "tv");
        var handler = new FixedFeedHandler(
            """
            <rss><channel>
              <item><title>Example.S02E01.1080p.WEB-DL</title><link>https://fixture.invalid/episode</link></item>
              <item><title>Example.S02.1080p.WEB-DL</title><link>https://fixture.invalid/pack</link></item>
            </channel></rss>
            """);

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 2,
                cancellationToken: CancellationToken.None);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Equal("Example.S02.1080p.WEB-DL", plan.BestCandidate!.ReleaseName);
        var packFamily = Assert.Single(
            plan.BestCandidate.PreferenceEvaluation!.Families,
            family => family.FamilyId == "transient.season-pack");
        Assert.Equal(PreferenceFactState.Present, packFamily.State);
        Assert.Equal("whole", packFamily.SelectedLevelId);
    }

    /// <summary>
    /// The season-pack tie-break must never outrank a persistent family: a
    /// pack at a lower tier than a clearly better single episode still loses,
    /// the same as any other transient signal (seeders included).
    /// </summary>
    [Fact]
    public async Task Season_search_does_not_let_a_pack_beat_a_clearly_better_quality_release()
    {
        var indexer = CreateIndexer("season-pack-quality-indexer", "Season pack quality indexer", "tv");
        var handler = new FixedFeedHandler(
            """
            <rss><channel>
              <item><title>Example.S02.1080p.WEB-DL</title><link>https://fixture.invalid/pack</link></item>
              <item><title>Example.S02E01.2160p.WEB-DL</title><link>https://fixture.invalid/episode</link></item>
            </channel></rss>
            """);

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 2,
                cancellationToken: CancellationToken.None);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Equal("Example.S02E01.2160p.WEB-DL", plan.BestCandidate!.ReleaseName);
    }

    /// <summary>
    /// A search for one episode already refuses a season pack as a title
    /// mismatch (ReleaseTitleMatch.EpisodeMismatch): "the search was for one
    /// episode". The season-pack tie-break must not change that outcome.
    /// </summary>
    [Fact]
    public async Task Episode_search_is_unaffected_by_the_season_pack_tie_break()
    {
        var indexer = CreateIndexer("episode-search-indexer", "Episode search indexer", "tv");
        var handler = new FixedFeedHandler(
            """
            <rss><channel>
              <item><title>Example.S02.1080p.WEB-DL</title><link>https://fixture.invalid/pack</link></item>
              <item><title>Example.S02E03.1080p.WEB-DL</title><link>https://fixture.invalid/episode</link></item>
            </channel></rss>
            """);

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            handler).BuildPlanAsync(
                "Example",
                2026,
                "tv",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                seasonNumber: 2,
                episodeNumber: 3,
                cancellationToken: CancellationToken.None);

        Assert.Equal(2, plan.Candidates.Count);
        Assert.Equal("Example.S02E03.1080p.WEB-DL", plan.BestCandidate!.ReleaseName);
        var packCandidate = plan.Candidates.Single(candidate => candidate.ReleaseName == "Example.S02.1080p.WEB-DL");
        Assert.Equal(ReleaseDecisionStatuses.Rejected, packCandidate.DecisionStatus);
    }

    [Fact]
    public async Task Automatic_and_interactive_typed_searches_return_the_same_installed_file_comparison()
    {
        var indexer = CreateIndexer("comparison-indexer", "Comparison indexer");
        var plan = ReleasePreferencePlanFactory.CreateQualityPlan("movies", "WEB 2160p");
        var currentFacts = ReleasePreferenceFactFactory.FromReleaseName(
            plan,
            "Example.Release.2026.1080p.WEB-DL-GRP",
            "WEB 1080p");
        var snapshot = new PreferenceEvaluationSnapshot(
            MediaId: "movie-1",
            LibraryId: "library",
            FileIdentity: "preference-file/v1:movie-1",
            FilePath: "/library/Example.Release.2026.1080p.WEB-DL-GRP.mkv",
            FileSizeBytes: 2_000_000_000,
            PlanId: plan.Id,
            PlanVersion: plan.Version,
            PlanHash: plan.PlanHash,
            Facts: currentFacts,
            Evaluation: ReleasePreferenceEvaluator.Evaluate(plan, currentFacts),
            MatchedRuleIds: [],
            EvaluatedUtc: DateTimeOffset.UnixEpoch,
            Source: "test");
        var feed = new FixedFeedHandler(
            "<rss><channel><item><title>Example.Release.2026.2160p.WEB-DL-GRP</title><link>https://fixture.invalid/upgrade</link></item></channel></rss>");
        var planner = CreatePlanner(CreateConnections(indexer).Object, feed);

        var automatic = await planner.BuildPlanAsync(
            "Example Release",
            2026,
            "movies",
            "WEB 1080p",
            "WEB 2160p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None,
            searchKind: AcquisitionSearchKinds.Automatic,
            currentPreferenceEvaluation: snapshot,
            preferencePlan: plan,
            currentFilePresent: true);
        var interactive = await planner.BuildPlanAsync(
            "Example Release",
            2026,
            "movies",
            "WEB 1080p",
            "WEB 2160p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None,
            searchKind: AcquisitionSearchKinds.Interactive,
            currentPreferenceEvaluation: snapshot,
            preferencePlan: plan,
            currentFilePresent: true);

        var automaticCandidate = Assert.IsType<MediaSearchCandidate>(automatic.BestCandidate);
        var interactiveCandidate = Assert.IsType<MediaSearchCandidate>(interactive.BestCandidate);
        Assert.Equal(PreferenceCandidateStatus.Upgrade, automaticCandidate.PreferenceComparison?.Status);
        Assert.Equal(automaticCandidate.DecisionStatus, interactiveCandidate.DecisionStatus);
        Assert.Equal(
            JsonSerializer.Serialize(automaticCandidate.PreferenceComparison),
            JsonSerializer.Serialize(interactiveCandidate.PreferenceComparison));
        Assert.Equal(automaticCandidate.Summary, interactiveCandidate.Summary);
    }

    [Fact]
    public async Task Runtime_search_compiles_against_the_persisted_active_guide_package()
    {
        var indexer = CreateIndexer("guide-indexer", "Guide indexer");
        var guideFormat = GuidePackageCatalog.Current.CustomFormats
            .First(format => format.MappingStatus == GuideMappingStatus.Reviewed && format.MappedTraitIds.Count > 0 && GuideFormatConditions.AppliesTo(format, "movies"));
        var activePackage = GuidePackageCatalog.Current with
        {
            Version = GuidePackageCatalog.Current.Version + 1,
            Source = GuidePackageCatalog.Current.Source with { UpstreamRevision = "fixture-active-revision" },
            IntegritySha256 = null
        };
        var guideStore = new Mock<IGuidePackageStore>();
        guideStore
            .Setup(store => store.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredGuidePackage(activePackage, true, DateTimeOffset.UtcNow));

        var plan = await CreatePlanner(
            CreateConnections(indexer).Object,
            new FixedFeedHandler(CreateFeed(1)),
            guidePackageStore: guideStore.Object).BuildPlanAsync(
                "Example",
                2026,
                "movies",
                null,
                "WEB 1080p",
                [CreateSource(indexer)],
                customFormats:
                [
                    new CustomFormatItem(
                        "format-1",
                        guideFormat.Name,
                        "movies",
                        guideFormat.OriginalScore,
                        guideFormat.TrashId,
                        GuideFormatConditions.For(guideFormat),
                        true,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow)
                ],
                cancellationToken: CancellationToken.None);

        var evaluation = Assert.Single(plan.Candidates).PreferenceEvaluation;
        Assert.NotNull(evaluation);
        Assert.Equal($"{activePackage.Version}:{activePackage.Source.UpstreamRevision}", evaluation.PlanVersion);
    }

    /// <summary>
    /// #630: <c>ASizeFloorScalesWithRuntimeTests</c> proves the scaling math in
    /// <c>ScoreSize</c> itself; this proves the value actually arrives there
    /// through the real path production uses -
    /// <c>AcquisitionDecisionPipeline.PlanAsync</c> passes
    /// <c>AcquisitionDecisionRequest.RuntimeMinutes</c> into this same
    /// <c>BuildPlanAsync</c> parameter, which crosses two more private method
    /// boundaries (<c>TrySearchIndexerAsync</c>, <c>ParseCandidates</c>)
    /// before reaching <c>ReleaseDecisionInput</c>. A dropped parameter
    /// anywhere in that chain is a silent regression - the build still
    /// succeeds, and the release is judged against the wrong runtime again.
    /// </summary>
    [Fact]
    public async Task Runtime_minutes_passed_into_build_plan_reaches_the_parsed_candidates_size_decision()
    {
        var indexer = CreateIndexer("short-film-indexer", "Short Film Indexer");
        var feed = new FixedFeedHandler(
            """
            <rss><channel>
              <item><title>Tears.of.Steel.2012.1080p.WEB-DL.DD5.1.H.264-GRP</title><link>https://fixture.invalid/tears-of-steel</link><torznab:attr xmlns:torznab="http://torznab.com/schemas/2015/feed" name="size" value="200000000" /></item>
            </channel></rss>
            """);
        var sizeRules = new[] { new ProfileSizeRule("WEB 1080p", 1.5, 25.0, 420, 3800) };

        var withoutRuntime = await CreatePlanner(CreateConnections(indexer).Object, feed).BuildPlanAsync(
            "Tears of Steel",
            2012,
            "movies",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            sizeRules: sizeRules,
            cancellationToken: CancellationToken.None);

        var withRuntime = await CreatePlanner(CreateConnections(indexer).Object, feed).BuildPlanAsync(
            "Tears of Steel",
            2012,
            "movies",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            sizeRules: sizeRules,
            runtimeMinutes: 12,
            cancellationToken: CancellationToken.None);

        Assert.Equal(ReleaseDecisionStatuses.Rejected, Assert.Single(withoutRuntime.Candidates).DecisionStatus);
        Assert.Equal("preferred", Assert.Single(withRuntime.Candidates).DecisionStatus);
    }

    /// <summary>
    /// #606: EZTV's JSON API only filters by IMDb id server-side; without one
    /// it hands back its newest torrents across every show. This proves the
    /// title's IMDb id actually reaches the tracker query through the real
    /// path production uses - <c>BuildPlanAsync</c>'s <c>imdbId</c> parameter,
    /// through <c>TrySearchIndexerAsync</c> into <c>TrackerQueryFor</c> and
    /// onto <c>TrackerQuery.ImdbId</c> - rather than being dropped along the
    /// way and leaving the tracker to search by keywords alone.
    /// </summary>
    [Fact]
    public async Task A_titles_own_imdb_id_reaches_the_tracker_query()
    {
        var tracker = CreateIndexer("tracker-1", "EZTV Tracker", "tv") with
        {
            Protocol = "tracker",
            BaseUrl = "https://eztvx.to/",
            Categories = "5000",
            DefinitionId = "eztv"
        };
        var recordingEngine = new RecordingTrackerEngine();
        var trackers = new Deluno.Integrations.Search.Trackers.TrackerIndexers(
            new Deluno.Integrations.Search.Trackers.TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<Deluno.Integrations.Search.Trackers.TrackerDefinitionCatalog>.Instance),
            recordingEngine);

        await CreatePlanner(CreateConnections(tracker).Object, trackerIndexers: trackers).BuildPlanAsync(
            "Young Sheldon",
            2017,
            "tv",
            null,
            "WEB 1080p",
            [CreateSource(tracker)],
            seasonNumber: 7,
            episodeNumber: 9,
            imdbId: "tt6226232",
            cancellationToken: CancellationToken.None);

        Assert.NotNull(recordingEngine.LastQuery);
        Assert.Equal("tt6226232", recordingEngine.LastQuery!.ImdbId);
        Assert.Equal(7, recordingEngine.LastQuery.Season);
        Assert.Equal(9, recordingEngine.LastQuery.Episode);
    }

    /// <summary>
    /// #971: a tracker behind a Cloudflare check is its own kind of failure,
    /// not an unclassified one - the indexer's health must say plainly that
    /// a Cloudflare helper is needed, and it must never be counted the same
    /// way as an outage or a rejected search.
    /// </summary>
    [Fact]
    public async Task A_cloudflare_blocked_tracker_search_is_classified_as_blocked_not_unknown()
    {
        var tracker = CreateIndexer("tracker-1", "1337x") with
        {
            Protocol = "tracker",
            BaseUrl = "https://1337x.to/",
            Categories = "5000",
            DefinitionId = "1337x"
        };
        var blockedEngine = new CloudflareBlockedTrackerEngine();
        var trackers = new Deluno.Integrations.Search.Trackers.TrackerIndexers(
            new Deluno.Integrations.Search.Trackers.TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<Deluno.Integrations.Search.Trackers.TrackerDefinitionCatalog>.Instance),
            blockedEngine);

        var plan = await CreatePlanner(CreateConnections(tracker).Object, trackerIndexers: trackers).BuildPlanAsync(
            "Example",
            2026,
            "movies",
            null,
            "WEB 1080p",
            [CreateSource(tracker)],
            cancellationToken: CancellationToken.None);

        var failure = Assert.Single(plan.Failures ?? []);
        Assert.Equal(IntegrationFailureKind.Blocked, failure.Kind);
        Assert.Contains("Cloudflare", failure.Message, StringComparison.Ordinal);
        Assert.False(failure.IsTransient);
    }

    private sealed class CloudflareBlockedTrackerEngine : Deluno.Integrations.Search.Trackers.ITrackerEngine
    {
        public Task<Deluno.Integrations.Search.Trackers.TrackerSearchResult> SearchAsync(
            Deluno.Integrations.Search.Trackers.TrackerConnection connection, Deluno.Integrations.Search.Trackers.TrackerQuery query, CancellationToken cancellationToken)
            => throw new Deluno.Integrations.Search.Trackers.TrackerException(
                "1337x.to is behind a Cloudflare check that turns automated searches away. A Cloudflare helper (FlareSolverr or Byparr), set in Preferences, General, gets Deluno past it; if one is set, it could not get through either.",
                new Deluno.Integrations.Search.Trackers.TrackerBlockedException("1337x.to is behind a Cloudflare check."));

        public Task<Deluno.Integrations.Search.Trackers.TrackerGrab> ResolveDownloadAsync(
            Deluno.Integrations.Search.Trackers.TrackerConnection connection, string link, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingTrackerEngine : Deluno.Integrations.Search.Trackers.ITrackerEngine
    {
        public Deluno.Integrations.Search.Trackers.TrackerQuery? LastQuery { get; private set; }

        public Task<Deluno.Integrations.Search.Trackers.TrackerSearchResult> SearchAsync(
            Deluno.Integrations.Search.Trackers.TrackerConnection connection, Deluno.Integrations.Search.Trackers.TrackerQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(new Deluno.Integrations.Search.Trackers.TrackerSearchResult([], [], 0));
        }

        public Task<Deluno.Integrations.Search.Trackers.TrackerGrab> ResolveDownloadAsync(
            Deluno.Integrations.Search.Trackers.TrackerConnection connection, string link, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>#754: a search that hits a bad key or a dead login (HTTP 401/403) writes the same combined Indexer History row an RSS sync's own auth failure does, through the shared recorder.</summary>
    [Fact]
    public async Task A_search_that_hits_an_indexer_auth_failure_records_it_to_the_shared_indexer_history()
    {
        var indexer = CreateIndexer("locked-indexer", "Locked indexer");
        var handler = new FixedStatusHandler(HttpStatusCode.Unauthorized);
        var recorder = new RecordingAuthFailureRecorder();

        await CreatePlanner(CreateConnections(indexer).Object, handler, authFailureRecorder: recorder).BuildPlanAsync(
            "Example",
            2026,
            "movies",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None);

        var recorded = Assert.Single(recorder.Recorded);
        Assert.Equal("locked-indexer", recorded.IndexerId);
        Assert.Equal("search", recorded.Action);
    }

    [Fact]
    public async Task A_search_that_hits_a_non_auth_failure_does_not_record_an_auth_failure()
    {
        var indexer = CreateIndexer("broken-indexer", "Broken indexer");
        var handler = new FixedStatusHandler(HttpStatusCode.BadRequest);
        var recorder = new RecordingAuthFailureRecorder();

        await CreatePlanner(CreateConnections(indexer).Object, handler, authFailureRecorder: recorder).BuildPlanAsync(
            "Example",
            2026,
            "movies",
            null,
            "WEB 1080p",
            [CreateSource(indexer)],
            cancellationToken: CancellationToken.None);

        Assert.Empty(recorder.Recorded);
    }

    private sealed class FixedStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class RecordingAuthFailureRecorder : IIndexerAuthFailureRecorder
    {
        public List<(string? IndexerId, string IndexerName, string Action, string Message)> Recorded { get; } = [];

        public Task RecordAsync(string? indexerId, string indexerName, string action, string message, string? detailsJson, CancellationToken cancellationToken)
        {
            Recorded.Add((indexerId, indexerName, action, message));
            return Task.CompletedTask;
        }
    }

    private static FeedMediaSearchPlanner CreatePlanner(
        IConnectionsRepository connections,
        HttpMessageHandler? handler = null,
        ILogger<FeedMediaSearchPlanner>? logger = null,
        IGuidePackageStore? guidePackageStore = null,
        IIntegrationResiliencePolicy? resiliencePolicy = null,
        bool indexersBusy = false,
        Deluno.Integrations.Search.Trackers.TrackerIndexers? trackerIndexers = null,
        IIndexerAuthFailureRecorder? authFailureRecorder = null,
        IRealtimeEventPublisher? realtime = null,
        IOutboundRequestThrottle? throttleOverride = null)
    {
        var platform = new Mock<IPlatformSettingsRepository>();
        platform
            .Setup(repository => repository.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultSettings());

        var quality = new Mock<IQualityModelService>();
        quality
            .Setup(service => service.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityModelSnapshot(
                "test",
                [new QualityTierDefinition("WEB 1080p", 70, 1.0, 12.0, 20.0, 350, 1750, 3000, 50)],
                new QualityUpgradeStopPolicy(true, true),
                DateTimeOffset.UtcNow));

        var throttle = new Mock<IOutboundRequestThrottle>();
        throttle
            .Setup(service => service.TryAcquireAsync(
                It.IsAny<string>(),
                It.IsAny<OutboundRate>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<TimeSpan?>(indexersBusy ? null : TimeSpan.Zero));

        return new FeedMediaSearchPlanner(
            platform.Object,
            connections,
            new FixedClientFactory(handler ?? new FixedFeedHandler("<rss><channel /></rss>")),
            resiliencePolicy ?? new PassthroughResiliencePolicy(),
            quality.Object,
            new DisabledRankingModelService(),
            throttleOverride ?? throttle.Object,
            logger ?? NullLogger<FeedMediaSearchPlanner>.Instance,
            guidePackageStore: guidePackageStore,
            trackerIndexers: trackerIndexers,
            indexerAuthFailureRecorder: authFailureRecorder,
            realtimeEventPublisher: realtime);
    }

    private static Mock<IConnectionsRepository> CreateConnections(IndexerItem indexer)
    {
        var connections = new Mock<IConnectionsRepository>();
        connections
            .Setup(repository => repository.ListIndexersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([indexer]);
        return connections;
    }

    /// <summary>
    /// #634: a dead tracker took every search down with it for minutes. It gets
    /// a budget, and when that runs out the search goes on without it.
    /// </summary>
    [Fact]
    public async Task A_tracker_that_does_not_answer_in_time_is_left_behind_rather_than_waited_for()
    {
        var tracker = CreateIndexer("tracker-1", "Dead Tracker") with { Protocol = "tracker", BaseUrl = "https://yts.mx/" };
        tracker = tracker with { DefinitionId = "yts" };
        var trackers = new Deluno.Integrations.Search.Trackers.TrackerIndexers(
            new Deluno.Integrations.Search.Trackers.TrackerDefinitionCatalog(overrideDirectory: null, NullLogger<Deluno.Integrations.Search.Trackers.TrackerDefinitionCatalog>.Instance),
            new SilentTrackerEngine(),
            budget: TimeSpan.FromMilliseconds(300));
        var started = System.Diagnostics.Stopwatch.StartNew();

        var plan = await CreatePlanner(CreateConnections(tracker).Object, trackerIndexers: trackers).BuildPlanAsync(
            "Example Release", 2026, "movies", null, "WEB 1080p", [CreateSource(tracker)], cancellationToken: CancellationToken.None);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"the search waited {started.Elapsed}");
        var failure = Assert.Single(plan.Failures ?? []);
        Assert.Contains("did not answer within", failure.Message, StringComparison.Ordinal);
        Assert.Contains("went on without it", failure.Message, StringComparison.Ordinal);
    }

    private sealed class SilentTrackerEngine : Deluno.Integrations.Search.Trackers.ITrackerEngine
    {
        public async Task<Deluno.Integrations.Search.Trackers.TrackerSearchResult> SearchAsync(
            Deluno.Integrations.Search.Trackers.TrackerConnection connection, Deluno.Integrations.Search.Trackers.TrackerQuery query, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken); // reason: a search that never answers until cancelled
            throw new InvalidOperationException("unreachable");
        }

        public Task<Deluno.Integrations.Search.Trackers.TrackerGrab> ResolveDownloadAsync(
            Deluno.Integrations.Search.Trackers.TrackerConnection connection, string link, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static IndexerItem CreateIndexer(string id, string name, string mediaScope = "movies")
        => new(
            Id: id,
            Name: name,
            Protocol: "torznab",
            Privacy: "public",
            BaseUrl: "https://fixture.invalid/torznab/api",
            ApiKey: null,
            Priority: 1,
            Categories: "2000",
            Tags: "",
            MediaScope: mediaScope,
            IsEnabled: true,
            HealthStatus: "healthy",
            LastHealthMessage: null,
            LastHealthFailureCategory: null,
            LastHealthLatencyMs: null,
            LastHealthTestUtc: null,
            ConsecutiveFailures: 0,
            RateLimitedUntilUtc: null,
            DisabledReason: null,
            CreatedUtc: DateTimeOffset.UtcNow,
            UpdatedUtc: DateTimeOffset.UtcNow);

    private static LibrarySourceLinkItem CreateSource(IndexerItem indexer)
        => new(
            "source-link",
            "library",
            indexer.Id,
            indexer.Name,
            1,
            "",
            "",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static PlatformSettingsSnapshot DefaultSettings()
        => new(
            MovieRootPath: null,
            SeriesRootPath: null,
            DownloadsPath: null,
            IncompleteDownloadsPath: null,
            AutoStartJobs: true,
            EnableNotifications: false,
            RenameOnImport: true,
            UseHardlinks: false,
            CleanupEmptyFolders: true,
            MovieFolderFormat: "{Movie Title} ({Release Year})",
            SeriesFolderFormat: "{Series Title}",
            MovieFileFormat: "{Movie Title} ({Release Year}) [{Quality}] [{Custom Formats}]",
            EpisodeFileFormat: "S{Season:00}E{Episode:00} - {Episode Title}",
            HostBindAddress: "127.0.0.1",
            HostPort: 5099,
            UrlBase: "",
            RequireAuthentication: false,
            UiTheme: "system",
            DefaultMovieView: "poster",
            DefaultShowView: "poster",
            MetadataCertificationCountry: "AU",
            MetadataLanguage: "en",
            MetadataProviderMode: "tmdb",
            MetadataBrokerUrl: "",
            MetadataBrokerConfigured: false,
            MetadataTmdbApiKeyConfigured: false,
            MetadataOmdbApiKeyConfigured: false,
            ReleaseNeverGrabPatterns: "",
            SearchScoringMode: "weighted",
            ImportRecoveryRetentionDays: 30,
            UpdatedUtc: DateTimeOffset.UtcNow);

    private static string CreateFeed(int itemCount)
        => $"<rss><channel>{string.Join(string.Empty, Enumerable.Range(1, itemCount).Select(index =>
            $"<item><title>Example.Release.{index:00}.WEB.1080p</title><link>https://fixture.invalid/release/{index}</link></item>"))}</channel></rss>";

    private sealed class FixedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedFeedHandler(string payload) : HttpMessageHandler
    {
        public string Query { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Query = request.RequestUri?.Query ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/xml")
            });
        }
    }

    /// <summary>
    /// The real policy's behaviour for a request that timed out: no value, and
    /// a failure built from the caller's own service identity.
    /// </summary>
    private sealed class TimingOutResiliencePolicy : IIntegrationResiliencePolicy
    {
        public Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => Task.FromResult(new IntegrationResilienceResult<T>(
                default!,
                CircuitOpen: false,
                CircuitOpened: true,
                Attempts: 3,
                FailureMessage: "timed out",
                RetryAfterUtc: null,
                Failure: IntegrationFailureFactory.FromException(
                    request.ServiceType ?? "integration",
                    request.ServiceId ?? request.Key,
                    request.ServiceName ?? request.Operation,
                    request.Operation,
                    new TaskCanceledException(
                        "The request was canceled due to the configured HttpClient.Timeout of 12 seconds elapsing."),
                    retryScheduled: true,
                    attempts: 3)));

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = DateTimeOffset.MinValue;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Behaves the way the real policy does once its retries are exhausted:
    /// no value, a failure message, and a typed failure. Every other test here
    /// uses a passthrough policy that always succeeds, which is exactly why an
    /// unreachable indexer could crash the whole search unnoticed.
    /// </summary>
    private sealed class ExhaustedResiliencePolicy : IIntegrationResiliencePolicy
    {
        public Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => Task.FromResult(new IntegrationResilienceResult<T>(
                default!,
                CircuitOpen: false,
                CircuitOpened: true,
                Attempts: 3,
                FailureMessage: "The request was canceled due to the configured HttpClient.Timeout of 12 seconds elapsing.",
                RetryAfterUtc: null,
                Failure: IntegrationFailureFactory.FromLegacy(
                    "indexer",
                    "unreachable-indexer",
                    "Unreachable indexer",
                    "search",
                    "unreachable",
                    "The indexer did not answer.")));

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = DateTimeOffset.MinValue;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Behaves the way the real policy does when the circuit it consults is
    /// already open: no attempts made at all, and a CircuitOpen failure built
    /// from the caller's own service identity, exactly as
    /// <c>IntegrationResiliencePolicy.ExecuteAsync</c> returns it (#606).
    /// </summary>
    private sealed class CircuitOpenResiliencePolicy(DateTimeOffset retryAfterUtc) : IIntegrationResiliencePolicy
    {
        public Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
            => Task.FromResult(new IntegrationResilienceResult<T>(
                default!,
                CircuitOpen: true,
                CircuitOpened: false,
                Attempts: 0,
                FailureMessage: "Deluno paused it after repeated failures.",
                RetryAfterUtc: retryAfterUtc,
                Failure: IntegrationFailureFactory.CircuitOpen(
                    request.ServiceType ?? "integration",
                    request.ServiceId ?? request.Key,
                    request.ServiceName ?? request.Operation,
                    request.Operation,
                    retryAfterUtc,
                    "Deluno paused it after repeated failures.")));

        public bool IsCircuitOpen(string key, out DateTimeOffset outRetryAfterUtc)
        {
            outRetryAfterUtc = retryAfterUtc;
            return true;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class PassthroughResiliencePolicy : IIntegrationResiliencePolicy
    {
        public async Task<IntegrationResilienceResult<T>> ExecuteAsync<T>(
            IntegrationResilienceRequest request,
            Func<CancellationToken, Task<T>> operation,
            Func<T, IntegrationResilienceOutcome> classifyResult,
            CancellationToken cancellationToken)
        {
            var value = await operation(cancellationToken);
            _ = classifyResult(value);
            return new IntegrationResilienceResult<T>(value, false, false, 1, null, null);
        }

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = DateTimeOffset.MinValue;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class DisabledRankingModelService : IReleaseRankingModelService
    {
        public ReleaseRankingBoostResult Score(ReleaseRankingFeatures features, bool hardBlocked)
            => new(false, false, 0, "Ranking model disabled.");

        public RankingModelStatus GetStatus()
            => new(false, false, 0, "disabled", "Test ranking model is disabled.");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogMessage> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullLogger.Instance.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(new LogMessage(logLevel, formatter(state, exception)));
    }

    private sealed record LogMessage(LogLevel Level, string Text);
}
