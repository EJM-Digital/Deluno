using Deluno.Jobs.Contracts;
using Deluno.Jobs.Data;
using Deluno.Jobs.Decisions;
using Moq;

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

    [Fact]
    public void An_older_decision_row_reads_as_its_reason_without_the_scope_code()
    {
        // #1086: rows written before the fix start with the scope code.
        var activity = new ActivityEventItem(
            "activity-3",
            DecisionExplanationActivity.Category,
            "series.season-search: Best feed candidate is Lioness.2023.S02E01 from althub.co.za targeting WEB 1080p.",
            null,
            null,
            "series",
            "series-1",
            DateTimeOffset.Parse("2026-09-30T00:00:00Z"));

        Assert.Equal("Best feed candidate is Lioness.2023.S02E01 from althub.co.za targeting WEB 1080p.", activity.Message);
    }

    [Fact]
    public void Another_kind_of_event_keeps_a_leading_name_with_a_colon()
    {
        var activity = new ActivityEventItem(
            "activity-4",
            "retention.removed",
            "Big.Buck.Bunny.2008: removed under the rule.",
            null,
            null,
            "movie",
            "movie-1",
            DateTimeOffset.Parse("2026-09-30T00:00:00Z"));

        Assert.Equal("Big.Buck.Bunny.2008: removed under the rule.", activity.Message);
    }

    [Fact]
    public async Task A_new_decision_row_is_written_as_its_reason_alone()
    {
        string? written = null;
        var feed = new Mock<IActivityFeedRepository>();
        feed.Setup(repository => repository.RecordActivityAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string message, string? _, string? _, string? _, string? _, CancellationToken _) => written = message)
            .ReturnsAsync((ActivityEventItem)null!);

        await feed.Object.RecordDecisionAsync(
            new DecisionExplanationPayload("series.season-search", "matched", "Best feed candidate is X.", new Dictionary<string, string?>(), "Grabbed.", []),
            null, "series", "series-1", CancellationToken.None);

        Assert.Equal("Best feed candidate is X.", written);
    }
}
