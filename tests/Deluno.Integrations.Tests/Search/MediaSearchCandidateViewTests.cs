using System.Text.Json;
using Deluno.Integrations.Search;
using Deluno.Quality.Contracts;
using Deluno.Quality.ReleasePreferences;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// Movies and TV must answer "what did Deluno find?" the same way. They had a
/// hand-written copy each and had already drifted: the movie route returned
/// MatchedCustomFormats and the series route did not, so on TV the evidence
/// that a custom format matched was simply absent from the answer.
/// </summary>
public class MediaSearchCandidateViewTests
{
    private static MediaSearchCandidate Candidate(PreferenceEvaluation? evaluation = null)
        => new(
            ReleaseName: "Breaking.Bad.S01E03.1080p.AMZN.WEBRip.DDP5.1.x264-DELUNO",
            IndexerId: "indexer-1",
            IndexerName: "E2E Torznab fixture",
            Quality: "WEB 1080p",
            Score: 42,
            MeetsCutoff: true,
            Summary: "Eligible under the typed release plan.",
            DecisionStatus: ReleaseDecisionStatuses.Eligible,
            CustomFormatScore: 100,
            SeederScore: 220,
            SizeScore: 80,
            MatchedCustomFormats: [new CustomFormatMatchResult("format-1", "AMZN", 100, ["releaseTitle", "source:webrip"], [], true)],
            PreferenceEvaluation: evaluation,
            IndexerProtocol: "torznab",
            ReleaseAgeHours: 5.5,
            Leechers: 3);

    [Fact]
    public void A_candidate_carries_what_it_matched_when_there_is_no_typed_evaluation()
    {
        var json = JsonSerializer.SerializeToElement(MediaSearchCandidateView.For(Candidate()));

        var matched = Assert.Single(json.GetProperty("MatchedCustomFormats").EnumerateArray());
        Assert.Equal("AMZN", matched.GetProperty("FormatName").GetString());
        Assert.Equal(42, json.GetProperty("Score").GetInt32());
        Assert.Equal("Breaking.Bad.S01E03.1080p.AMZN.WEBRip.DDP5.1.x264-DELUNO", json.GetProperty("ReleaseName").GetString());
    }

    /// <summary>
    /// #609: the screens send a hand-picked candidate's indexer back on the
    /// grab. Without it a tracker's page went to the client unresolved and
    /// per-indexer client routing had nothing to route on.
    /// </summary>
    [Fact]
    public void A_candidate_says_which_indexer_it_came_from()
    {
        var json = JsonSerializer.SerializeToElement(MediaSearchCandidateView.For(Candidate()));

        Assert.Equal("indexer-1", json.GetProperty("IndexerId").GetString());
    }

    /// <summary>
    /// #753: the enriched results columns (protocol, release age,
    /// seeders/leechers) the manual Search page and the per-title interactive
    /// search results both add.
    /// </summary>
    [Fact]
    public void A_candidate_carries_its_protocol_age_and_leechers()
    {
        var json = JsonSerializer.SerializeToElement(MediaSearchCandidateView.For(Candidate()));

        Assert.Equal("torznab", json.GetProperty("IndexerProtocol").GetString());
        Assert.Equal(5.5, json.GetProperty("ReleaseAgeHours").GetDouble());
        Assert.Equal(3, json.GetProperty("Leechers").GetInt32());
    }

    /// <summary>
    /// The legacy score fields describe the old score-based decision. Returning
    /// both invites a reader to add up numbers from two different systems.
    /// </summary>
    [Fact]
    public void A_typed_evaluation_replaces_the_legacy_scores_rather_than_sitting_beside_them()
    {
        var evaluation = new PreferenceEvaluation(
            "plan-1",
            "v1",
            "hash-1",
            PreferenceEvaluationStatus.BelowGoal,
            hardGatesPassed: true,
            targetsMet: false,
            families: [],
            reasons: ["Custom rule - WEB Tier 02 is below its target."]);

        var json = JsonSerializer.SerializeToElement(MediaSearchCandidateView.For(Candidate(evaluation)));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("Score").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("CustomFormatScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("SeederScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("SizeScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("MatchedCustomFormats").ValueKind);
        Assert.Equal("plan-1", json.GetProperty("PreferenceEvaluation").GetProperty("PlanId").GetString());
    }
}
