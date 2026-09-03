using Deluno.Contracts;

namespace Deluno.Platform.Tests.Contracts;

/// <summary>
/// The five words Deluno uses for what happened to a subtitle, and the sentence
/// they add up to in Activity.
///
/// <para>What these hold is that the sentence stays a sentence. The version this
/// replaced said <i>"Fetched 3 of 5 subtitle(s)"</i>, which cannot tell a person
/// whether the three are in time, whether Deluno is still looking, or whether
/// the two that did not arrive are worth chasing — and those are the only three
/// questions somebody reading it has.</para>
/// </summary>
public sealed class SubtitleDecisionTests
{
    [Fact]
    public void A_run_where_nothing_was_owed_says_so_rather_than_reporting_five_zeroes()
    {
        Assert.Equal(
            "Nothing in Films was short of a subtitle.",
            SubtitleDecisions.Describe(new SubtitleDecisionTally(), "Films"));
    }

    [Fact]
    public void Only_the_states_that_happened_are_named()
    {
        var tally = new SubtitleDecisionTally()
            .With(SubtitleDecision.MeetsSubtitlePlan)
            .With(SubtitleDecision.MeetsSubtitlePlan)
            .With(SubtitleDecision.NeedsSync);

        var described = SubtitleDecisions.Describe(tally, "Films");

        Assert.Equal(
            "Fetched 3 of 3 subtitle(s) for Films: 2 made for the exact file and 1 queued to be timed against the audio.",
            described);
        Assert.DoesNotContain("0 ", described, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_found_nothing_does_not_claim_to_have_fetched()
    {
        var tally = new SubtitleDecisionTally()
            .With(SubtitleDecision.Rejected)
            .With(SubtitleDecision.Rejected)
            .With(SubtitleDecision.NeedsReview);

        Assert.Equal(
            "Looked for 3 subtitle(s) in Films: 1 that need a look and 2 nobody had.",
            SubtitleDecisions.Describe(tally, "Films"));
    }

    /// <summary>
    /// Held is the three states with a file on disk. A person who reads
    /// "fetched 4 of 5" and then finds four subtitles is being told the truth;
    /// counting the refusals would not be.
    /// </summary>
    [Fact]
    public void Held_counts_every_state_with_a_file_on_disk_and_no_others()
    {
        var tally = new SubtitleDecisionTally()
            .With(SubtitleDecision.MeetsSubtitlePlan)
            .With(SubtitleDecision.NeedsSync)
            .With(SubtitleDecision.UsableFallback)
            .With(SubtitleDecision.NeedsReview)
            .With(SubtitleDecision.Rejected);

        Assert.Equal(5, tally.Total);
        Assert.Equal(3, tally.Held);
        Assert.StartsWith("Fetched 3 of 5 subtitle(s) for Films:", SubtitleDecisions.Describe(tally, "Films"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Every decision has to be storable, and no two may share a name — the
    /// column is read back to work out what a library is still owed.
    /// </summary>
    [Fact]
    public void Every_decision_stores_under_its_own_name()
    {
        var names = Enum.GetValues<SubtitleDecision>().Select(decision => decision.ToStoredName()).ToArray();

        Assert.Equal(5, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }

    /// <summary>
    /// One clause takes no "and", and the list never trails a comma. Read one
    /// after another in Activity, either would read as a truncation.
    /// </summary>
    [Fact]
    public void One_state_reads_as_one_clause()
    {
        var described = SubtitleDecisions.Describe(
            new SubtitleDecisionTally().With(SubtitleDecision.UsableFallback), "Films");

        Assert.Equal(
            "Fetched 1 of 1 subtitle(s) for Films: 1 watchable but still worth upgrading.",
            described);
        Assert.DoesNotContain(" and ", described, StringComparison.Ordinal);
    }
}
