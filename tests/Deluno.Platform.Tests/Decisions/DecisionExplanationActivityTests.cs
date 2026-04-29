using Deluno.Jobs.Contracts;
using Deluno.Jobs.Decisions;

namespace Deluno.Platform.Tests.Decisions;

public sealed class DecisionExplanationActivityTests
{
    [Fact]
    public void FromActivity_parses_standard_decision_payload()
    {
        var activity = new ActivityEventItem(
            Id: "activity-1",
            Category: DecisionExplanationActivity.Category,
            Message: "movie.search: selected release",
            DetailsJson: """
            {
              "scope": "movie.search",
              "status": "matched",
              "reason": "The selected release met quality cutoff and custom format policy.",
              "inputs": {
                "title": "Dune Part Two",
                "sourceCount": "2"
              },
              "outcome": "Release was sent to qBittorrent.",
              "alternatives": [
                {
                  "name": "Dune.Part.Two.1080p",
                  "status": "rejected",
                  "reason": "Lower quality than target.",
                  "score": 120
                }
              ]
            }
            """,
            RelatedJobId: null,
            RelatedEntityType: "movie",
            RelatedEntityId: "movie-1",
            CreatedUtc: DateTimeOffset.Parse("2026-04-29T00:00:00Z"));

        var decision = DecisionExplanationActivity.FromActivity(activity);

        Assert.NotNull(decision);
        Assert.Equal("movie.search", decision.Scope);
        Assert.Equal("matched", decision.Status);
        Assert.Contains("quality cutoff", decision.Reason);
        Assert.Equal("Dune Part Two", decision.Inputs["title"]);
        Assert.Single(decision.Alternatives);
        Assert.Equal("rejected", decision.Alternatives[0].Status);
    }

    [Fact]
    public void FromActivity_ignores_non_decision_events()
    {
        var activity = new ActivityEventItem(
            "activity-2",
            "movie.search.manual",
            "Movie searched.",
            null,
            null,
            "movie",
            "movie-1",
            DateTimeOffset.Parse("2026-04-29T00:00:00Z"));

        Assert.Null(DecisionExplanationActivity.FromActivity(activity));
    }
}
