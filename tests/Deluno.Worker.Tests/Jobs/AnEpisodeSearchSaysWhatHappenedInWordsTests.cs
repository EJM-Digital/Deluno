using Deluno.Integrations.Search;
using Deluno.Worker.Jobs;

namespace Deluno.Worker.Tests.Jobs;

/// <summary>
/// #1159: Activity's line for an episode search that sent nothing said "held" and nothing more. It now says what
/// happened: "No usable release yet: ..." when everything offered was turned down, "Held for manual review
/// because ..." when a usable release needs a person's yes, and nothing extra when a release was sent (that has
/// its own "was automatically sent to ..." line).
/// </summary>
public sealed class AnEpisodeSearchSaysWhatHappenedInWordsTests
{
    [Fact]
    public void When_every_release_was_turned_down_the_line_says_no_usable_release_and_why()
    {
        var plan = AcquisitionDecisionPipeline.NeverNameARejectedCandidateBest(Plan(
            Candidate("Another.Show.S01E09.1080p.WEB-DL", "rejected", "This release is for something else: its name does not give the title \"The Wire\"."),
            Candidate("Another.Show.S01E10.1080p.WEB-DL", "rejected", "This release is for something else: its name does not give the title \"The Wire\".")));

        var line = EpisodeSearchJobHandler.ResultLine(1, 9, Outcome(plan), AcquisitionDecisionPipeline.BuildSearchResult(plan, 1));

        Assert.NotNull(line);
        Assert.StartsWith("S01E09: No usable release yet: 2 were for a different title", line, StringComparison.Ordinal);
        Assert.DoesNotContain("held", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void When_a_usable_release_needs_a_persons_yes_the_line_says_held_for_manual_review_and_why()
    {
        var plan = Plan(Candidate("The.Wire.S01E09.1080p.WEB-DL-TARS", "eligible", null));

        var outcome = Outcome(plan);
        var line = EpisodeSearchJobHandler.ResultLine(1, 9, outcome, AcquisitionDecisionPipeline.BuildSearchResult(plan, 1));

        Assert.Equal("held", outcome);
        Assert.NotNull(line);
        Assert.StartsWith("S01E09: ", line, StringComparison.Ordinal);
        Assert.Contains("Held for manual review because the best candidate is", line, StringComparison.Ordinal);
    }

    [Fact]
    public void When_a_release_is_sent_there_is_no_second_line_because_the_grab_line_says_so()
    {
        Assert.Null(EpisodeSearchJobHandler.ResultLine(1, 9, "matched", "Ready to send to 1 download client."));
        Assert.Null(EpisodeSearchJobHandler.ResultLine(1, 9, "held", " "));
    }

    // The outcome the pipeline gives a plan (AcquisitionDecisionPipeline.cs: checked / matched / held).
    private static string Outcome(MediaSearchPlan plan)
        => plan.BestCandidate is null ? "checked"
            : AcquisitionDecisionPipeline.IsSafeForAutomaticDispatch(plan.BestCandidate) ? "matched" : "held";

    private static MediaSearchPlan Plan(params MediaSearchCandidate[] candidates)
        => new(candidates.FirstOrDefault(), candidates, $"Best feed candidate is {candidates[0].ReleaseName}.");

    private static MediaSearchCandidate Candidate(string releaseName, string status, string? reason)
        => new(releaseName, "ix", "Indexer", "WEB 1080p", 100, MeetsCutoff: true, Summary: releaseName,
            DecisionStatus: status, DecisionReasons: reason is null ? null : [reason]);
}
