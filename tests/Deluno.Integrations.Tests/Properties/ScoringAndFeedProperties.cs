using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using Deluno.Connections.Contracts;
using Deluno.Infrastructure.Resilience;
using Deluno.Integrations.Search;
using Deluno.Integrations.Search.Trackers;
using Deluno.Quality.Contracts;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.Properties;

/// <summary>
/// #1271: quality and custom-format scoring, and the Newznab/Torznab feed parsing, as properties.
/// The name of each test states the invariant.
/// </summary>
public sealed class ScoringAndFeedProperties
{
    private static readonly string[] Tokens = ["x264", "x265", "1080p", "2160p", "BluRay", "WEB-DL", "REMUX", "HDR", "GRP", "NTb"];

    private sealed record Scenario(string[] Present, CustomFormatItem[] Formats);

    private static CustomFormatItem Format(int index, string token, int score, bool upgradeAllowed)
        => new(
            Id: $"f{index}",
            Name: $"Format {index}",
            MediaType: "movies",
            Score: score,
            TrashId: null,
            Conditions: $"[{{\"type\":\"releaseTitle\",\"value\":\"{token}\",\"required\":true}}]",
            UpgradeAllowed: upgradeAllowed,
            CreatedUtc: DateTimeOffset.UnixEpoch,
            UpdatedUtc: DateTimeOffset.UnixEpoch);

    private static Arbitrary<Scenario> Scenarios()
        => (from presentCount in Gen.Choose(0, Tokens.Length)
            from present in Gen.Elements(Tokens).ArrayOf(presentCount)
            from formatCount in Gen.Choose(0, 8)
            from formats in (from token in Gen.Elements(Tokens)
                             from score in Gen.Choose(-10000, 10000)
                             from upgrade in Gen.Elements(true, false)
                             select (token, score, upgrade)).ArrayOf(formatCount)
            select new Scenario(present.Distinct().ToArray(), formats.Select((f, i) => Format(i, f.token, f.score, f.upgrade)).ToArray())).ToArbitrary();

    private static string NameOf(Scenario scenario) => "Night.Of.The.Living.Dead.1968." + string.Join('.', scenario.Present);

    private static bool Matches(Scenario scenario, CustomFormatItem format)
    {
        var token = format.Conditions.Split('"')[7];
        return NameOf(scenario).Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_custom_format_total_is_the_sum_of_the_scores_of_exactly_the_formats_whose_condition_the_name_holds()
        => Prop.ForAll(Scenarios(), scenario =>
        {
            var total = CustomFormatMatcher.Evaluate(NameOf(scenario), scenario.Formats, out var matched);
            var expected = scenario.Formats.Where(f => Matches(scenario, f)).ToArray();
            return total == expected.Sum(f => f.Score)
                   && matched.Select(m => m.FormatId).SequenceEqual(expected.Select(f => f.Id));
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void The_upgrade_score_counts_only_the_matched_formats_that_are_allowed_to_drive_an_upgrade_and_still_lists_them_all()
        => Prop.ForAll(Scenarios(), scenario =>
        {
            var upgrade = CustomFormatMatcher.EvaluateUpgradeScore(NameOf(scenario), scenario.Formats, out var matched);
            var expected = scenario.Formats.Where(f => Matches(scenario, f)).ToArray();
            return upgrade == expected.Where(f => f.UpgradeAllowed).Sum(f => f.Score)
                   && matched.Count == expected.Length;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void The_matches_alone_are_the_matches_the_scored_call_returns()
        => Prop.ForAll(Scenarios(), scenario =>
        {
            _ = CustomFormatMatcher.Evaluate(NameOf(scenario), scenario.Formats, out var scored);
            var alone = CustomFormatMatcher.EvaluateMatches(NameOf(scenario), scenario.Formats);
            return scored.Select(m => m.FormatId).SequenceEqual(alone.Select(m => m.FormatId));
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void The_order_the_formats_are_listed_in_never_changes_the_total()
        => Prop.ForAll(Scenarios(), scenario =>
        {
            var forward = CustomFormatMatcher.Evaluate(NameOf(scenario), scenario.Formats, out _);
            var reversed = CustomFormatMatcher.Evaluate(NameOf(scenario), scenario.Formats.Reverse().ToArray(), out _);
            return forward == reversed;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void The_case_of_the_name_never_changes_the_total()
        => Prop.ForAll(Scenarios(), scenario =>
            CustomFormatMatcher.Evaluate(NameOf(scenario).ToUpperInvariant(), scenario.Formats, out _)
            == CustomFormatMatcher.Evaluate(NameOf(scenario).ToLowerInvariant(), scenario.Formats, out _)).QuickCheckThrowOnFailure();

    [Fact]
    public void A_format_that_does_not_match_adds_nothing_and_a_matching_one_added_twice_counts_twice()
        => Prop.ForAll(Scenarios(), scenario =>
        {
            var name = NameOf(scenario);
            var before = CustomFormatMatcher.Evaluate(name, scenario.Formats, out _);
            var never = Format(99, "zzz-not-in-any-name", 5000, true);
            var unchanged = CustomFormatMatcher.Evaluate(name, [.. scenario.Formats, never], out _);
            var doubled = CustomFormatMatcher.Evaluate(name, [.. scenario.Formats, .. scenario.Formats], out _);
            return before == unchanged && doubled == 2 * before;
        }).QuickCheckThrowOnFailure();

    [Fact]
    public void No_formats_and_no_matches_score_nothing()
        => Prop.ForAll(Scenarios(), scenario =>
            CustomFormatMatcher.Evaluate(NameOf(scenario), null, out var none) == 0 && none.Count == 0
            && CustomFormatMatcher.Evaluate(NameOf(scenario), [], out var empty) == 0 && empty.Count == 0).QuickCheckThrowOnFailure();

    [Property]
    public bool Scoring_never_throws_for_any_name_and_any_condition_text(string? name, string? conditions, int score, long? size)
    {
        var format = new CustomFormatItem("x", "x", "movies", score, null, conditions ?? string.Empty, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        _ = CustomFormatMatcher.Evaluate(name ?? string.Empty, [format], out _, null, size);
        _ = CustomFormatMatcher.DryRun(name ?? string.Empty, [format]);
        return true;
    }

    // ----- feed parsing -----

    private static readonly XNamespace Torznab = "http://torznab.com/schemas/2015/feed";

    private static Arbitrary<DateTimeOffset> Dates()
        => Gen.Choose(0, 120 * 365 * 24)
            .Select(hours => new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(hours)).ToArbitrary();

    [Fact]
    public void A_publish_date_written_in_the_feed_is_read_back_as_the_same_instant_in_UTC()
        => Prop.ForAll(Dates(), date =>
        {
            var item = new XElement("item", new XElement("pubDate", date.ToString("r", CultureInfo.InvariantCulture)));
            return FeedMediaSearchPlanner.ReadPublishedUtc(item) == date && FeedMediaSearchPlanner.ReadPublishedUtc(item)!.Value.Offset == TimeSpan.Zero;
        }).QuickCheckThrowOnFailure();

    [Property]
    public bool A_publish_date_that_is_any_text_never_throws_and_is_either_absent_or_a_UTC_instant(string? text)
    {
        var item = new XElement("item", new XElement("pubDate", text ?? string.Empty));
        var read = FeedMediaSearchPlanner.ReadPublishedUtc(item);
        return read is null || read.Value.Offset == TimeSpan.Zero;
    }

    [Property]
    public bool A_title_that_is_any_text_never_throws_and_is_never_null(string? text)
        => FeedMediaSearchPlanner.ReadReleaseTitle(new XElement("item", new XElement("title", text ?? string.Empty))) is not null;

    [Fact]
    public void A_release_written_as_a_feed_reads_back_with_its_title_size_and_date_and_the_size_is_never_negative()
        => Prop.ForAll(
            (from title in Gen.Elements("Night.Of.The.Living.Dead.1968.1080p.BluRay.x264-GRP", "Show.S01E02.720p.WEB-DL-NTb", "Film & Co (2001)")
             from size in Gen.Choose(0, int.MaxValue)
             from date in Dates().Generator
             select (title, size: (long)size * 4, date)).ToArbitrary(),
            item =>
            {
                var release = new TrackerRelease { Title = item.title, DownloadUrl = "https://t.example/d?id=1", Size = item.size, PublishDate = item.date };
                var xml = XDocument.Parse(TorznabFeed.Write([release]).ToString());
                var element = xml.Descendants("item").Single();
                var sizeAttribute = element.Elements(Torznab + "attr").Single(a => a.Attribute("name")!.Value == "size").Attribute("value")!.Value;
                return FeedMediaSearchPlanner.ReadReleaseTitle(element) == item.title
                       && FeedMediaSearchPlanner.ReadPublishedUtc(element) == item.date
                       && long.TryParse(sizeAttribute, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) && size == item.size && size >= 0;
            }).QuickCheckThrowOnFailure();

    [Property]
    public bool A_feed_body_of_any_text_never_makes_the_reader_throw_and_a_failed_read_has_no_releases(string? body)
    {
        var feed = Reader(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/rss+xml") })
            .ReadRecentAsync(Indexer(), CancellationToken.None).GetAwaiter().GetResult();
        return feed.Read || feed.Releases.Count == 0;
    }

    [Property]
    public bool A_well_formed_feed_with_any_text_in_its_items_never_throws_and_every_release_has_a_name(string? title, string? date, string? other)
    {
        static string Esc(string? text) => System.Security.SecurityElement.Escape(new string((text ?? string.Empty).Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).ToArray())) ?? string.Empty;
        var body = $"<rss><channel><item><title>{Esc(title)}</title><pubDate>{Esc(date)}</pubDate><size>{Esc(other)}</size></item><item><title>x</title></item></channel></rss>";
        var feed = Reader(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/rss+xml") })
            .ReadRecentAsync(Indexer(), CancellationToken.None).GetAwaiter().GetResult();
        return feed.Read && feed.Releases.All(r => !string.IsNullOrWhiteSpace(r.ReleaseName) && (r.PublishedUtc is null || r.PublishedUtc.Value.Offset == TimeSpan.Zero));
    }

    [Fact(Skip = "DEFECT: a tracker release whose title holds a control character (a stray backspace or line break in scraped HTML) makes TorznabFeed.Write throw an ArgumentException \"'\\u0008', hexadecimal value 0x08, is an invalid character\". One such row loses the whole page of results from that tracker. Owner to decide.")]
    public void A_release_with_a_control_character_in_its_title_still_gives_a_feed_that_can_be_written()
        => Prop.ForAll(Gen.Choose(1, 31).Where(code => code is not (9 or 10 or 13)).ToArbitrary(), code =>
        {
            var release = new TrackerRelease { Title = "Film" + (char)code + "2001", DownloadUrl = "https://t.example/d?id=1" };
            return TorznabFeed.Write([release]).ToString().Length > 0;
        }).QuickCheckThrowOnFailure();

    [Fact(Skip = "DEFECT (shrunk case): see A_release_with_a_control_character_in_its_title_still_gives_a_feed_that_can_be_written.")]
    public void A_title_with_a_backspace_does_not_stop_the_feed_being_written_shrunk_case()
        => Assert.NotEmpty(TorznabFeed.Write([new TrackerRelease { Title = "Film\b2001", DownloadUrl = "https://t.example/d?id=1" }]).ToString());

    // ----- stubs, as in ReadingAnIndexerFeedTests -----

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
            Id = "idx-1", Name = "Property Torznab", BaseUrl = "http://127.0.0.1:9117", ApiKey = "key-1", Categories = "2000", IsEnabled = true
        };

    /// <summary>Runs the operation and turns any exception into a failed result, as the real policy does.</summary>
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
            catch (Exception exception)
            {
                return new IntegrationResilienceResult<T>(default, false, false, 1, exception.Message, null);
            }
        }

        public bool IsCircuitOpen(string key, out DateTimeOffset retryAfterUtc)
        {
            retryAfterUtc = default;
            return false;
        }

        public Task CloseCircuitsAsync(string serviceType, string serviceId, CancellationToken cancellationToken)
            => Task.CompletedTask;
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
