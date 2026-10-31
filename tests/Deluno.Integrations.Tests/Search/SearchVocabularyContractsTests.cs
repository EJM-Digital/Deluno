using System.Text.Json;
using System.Text.Json.Serialization;
using Deluno.Connections.Contracts;
using Deluno.Integrations.Metadata;
using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// The small vocabularies of search: how far down a list each decision status sits and what it says in
/// words, the season-pack forwarder, the routing records, a proxy's address and an id that arrives as a
/// number or a string.
/// </summary>
public sealed class SearchVocabularyContractsTests
{
    [Theory]
    [InlineData("rejected", 3, 4, "rejected by a hard rule")]
    [InlineData("held", 2, 3, "waiting for your review")]
    [InlineData("delayed", 2, 3, "waiting for its timing rule to clear")]
    [InlineData("risky", 2, 3, "usable only with caution")]
    [InlineData("equivalent", 1, 2, "equivalent to the file you already have")]
    [InlineData("current-better", 1, 2, "not as good as the file you already have")]
    [InlineData("preferred", 0, 0, "the preferred candidate")]
    [InlineData("acceptable", 0, 1, "usable but not the best available")]
    [InlineData("eligible", 0, 1, "usable, but it does not meet every preference yet")]
    [InlineData("something-new", 0, 3, "something-new")]
    [InlineData(null, 0, 3, "unclassified")]
    public void Every_status_has_its_place_in_both_orders_and_its_plain_words(string? status, int typedRank, int legacyRank, string words)
    {
        Assert.Equal(typedRank, ReleaseDecisionStatuses.TypedStageRank(status));
        Assert.Equal(legacyRank, ReleaseDecisionStatuses.LegacyStageRank(status));
        Assert.Equal(words, ReleaseDecisionStatuses.Describe(status));
    }

    [Fact]
    public void The_season_pack_detector_hands_the_name_to_the_shared_classifier()
    {
        var season = SeasonPackDetector.Classify("Show.Name.S02.1080p.WEB-DL-GRP");
        Assert.True(season.IsSeason);
        Assert.False(season.IsEpisode);
        Assert.Equal(2, season.Season);

        var episode = SeasonPackDetector.Classify("Show.Name.S02E05.1080p.WEB-DL-GRP");
        Assert.True(episode.IsEpisode);
        Assert.Equal((2, 5), (episode.Season, episode.Episode));

        Assert.True(SeasonPackDetector.CoversEpisode("Show.Name.S02.1080p.WEB-DL-GRP", 2, 5));
        Assert.False(SeasonPackDetector.CoversEpisode("Show.Name.S03.1080p.WEB-DL-GRP", 2, 5));
    }

    [Fact]
    public async Task The_routing_records_keep_what_they_are_given()
    {
        var now = DateTimeOffset.Parse("2026-10-31T00:00:00Z");
        var preferences = new IntelligentRoutingPreferences("1080p", 12.5, ["GRP"]);
        var record = new DownloadClientRecord(10, 8, 0.8, 30, now);
        var snapshot = new IntelligentRoutingSnapshot(now, preferences, new Dictionary<string, double> { ["i"] = 0.5 }, new Dictionary<string, double> { ["c"] = 0.9 });
        Assert.Null(snapshot.DownloadClientRecords);
        Assert.Equal(("1080p", 12.5), (preferences.PreferredQuality, preferences.AverageCustomFormatScore));
        Assert.Equal(["GRP"], preferences.PreferredReleaseGroups);
        Assert.Equal((10, 8, 0.8, 30, now), (record.Counted, record.Imported, record.Rate, record.WindowDays, record.LastSendUtc));
        Assert.Equal(0.5, snapshot.IndexerSuccessRates["i"]);
        Assert.Equal(0.9, snapshot.DownloadClientSuccessRates["c"]);
        Assert.Equal(now, snapshot.ComputedUtc);

        var anomaly = new IntelligentRoutingAnomaly("code", "warn", "sum", "det", now);
        Assert.Equal(("code", "warn", "sum", "det", now), (anomaly.Code, anomaly.Severity, anomaly.Summary, anomaly.Details, anomaly.DetectedUtc));

        var request = new IntelligentReleaseRecommendationRequest("rel", 5, 100, 1, 2, 3, "idx", "dc", 4.5, 6.5);
        Assert.Equal(("rel", 5, 100L, 1, 2, 3, "idx", "dc", 4.5, 6.5),
            (request.ReleaseName, request.Seeders, request.SizeBytes, request.QualityDelta, request.CustomFormatScore, request.SourcePriorityScore, request.IndexerName, request.DownloadClientId, request.EstimatedBitrateMbps, request.ReleaseAgeHours));

        var recommendation = new IntelligentReleaseRecommendation(80, "good", "sum", 0.5, 0.6, default!, "mode");
        Assert.Equal((80, "good", "sum", 0.5, 0.6, "mode"),
            (recommendation.RecommendationScore, recommendation.RecommendationLabel, recommendation.Summary, recommendation.IndexerSuccessRate, recommendation.DownloadClientSuccessRate, recommendation.ScoringMode));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("http", "proxy:8080", "http://proxy:8080")]
    [InlineData("socks4", " proxy:1080 ", "socks4://proxy:1080")]
    [InlineData("SOCKS5", "proxy:1080", "socks5://proxy:1080")]
    [InlineData("flaresolverr", "proxy:8191", "http://proxy:8191")]
    [InlineData("socks5", "https://already:443", "https://already:443")]
    [InlineData("socks5", "   ", "")]
    public void A_proxy_address_becomes_a_full_url(string type, string address, string url)
        => Assert.Equal(url, ProxyAddressFormat.ToUrl(type, address));

    private sealed record Ids([property: JsonConverter(typeof(NumberOrStringConverter))] string? TvdbId);

    [Theory]
    [InlineData("{\"TvdbId\":\"abc\"}", "abc")]
    [InlineData("{\"TvdbId\":12345}", "12345")]
    [InlineData("{\"TvdbId\":12.5}", "12.5")]
    [InlineData("{\"TvdbId\":null}", null)]
    public void An_id_may_arrive_as_a_string_or_a_number(string json, string? expected)
        => Assert.Equal(expected, JsonSerializer.Deserialize<Ids>(json)!.TvdbId);

    [Fact]
    public void An_id_that_is_neither_is_refused_and_an_id_is_written_as_a_string()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Ids>("{\"TvdbId\":true}"));
        Assert.Equal("{\"TvdbId\":\"77\"}", JsonSerializer.Serialize(new Ids("77")));
    }
}
