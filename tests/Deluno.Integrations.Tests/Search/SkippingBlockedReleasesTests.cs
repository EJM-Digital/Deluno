using Deluno.Contracts;
using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// A release Deluno has refused is skipped, and the skipping is said out loud.
///
/// <para>DESIGN-007 decision 1. James chose "refuse it, and tell you" over
/// "refuse it, say nothing" for a reason that only shows up in the bad case:
/// once every candidate has been refused, a silent skip reports "no results
/// found", which blames the indexers for a decision Deluno made and leaves a
/// person with nothing to act on. That is the Radarr behaviour this whole line
/// of work exists to remove, reappearing inside its own fix.</para>
/// </summary>
public sealed class SkippingBlockedReleasesTests
{
    [Fact]
    public void A_blocked_release_is_dropped_and_the_next_one_wins()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Nebula"), Candidate("Arrival.2016.1080p", "Nebula"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(plan, Keys("Arrival.2016.2160p", "Nebula"));

        Assert.Equal("Arrival.2016.1080p", result.BestCandidate!.ReleaseName);
        Assert.Single(result.Candidates);
        Assert.Contains("Skipped 1 release you have blocked.", result.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The case that decided the wording. Silence here reads as "your indexers
    /// have nothing", which is untrue and unactionable.
    /// </summary>
    [Fact]
    public void Blocking_everything_says_so_rather_than_reporting_an_empty_search()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Nebula"), Candidate("Arrival.2016.1080p", "Orbit"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(
            plan,
            Keys("Arrival.2016.2160p", "Nebula").Concat(Keys("Arrival.2016.1080p", "Orbit")).ToHashSet(StringComparer.Ordinal));

        Assert.Null(result.BestCandidate);
        Assert.Empty(result.Candidates);
        Assert.Contains("Skipped 2 releases you have blocked.", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Nothing else was offered", result.Summary, StringComparison.Ordinal);
        Assert.Equal(MediaSearchReasons.NoUsableRelease, result.Reason);
    }

    [Fact]
    public void A_search_with_nothing_blocked_is_left_exactly_as_it_was()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Nebula"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(plan, Keys("Something.Else.1080p", "Nebula"));

        Assert.Same(plan.Candidates, result.Candidates);
        Assert.Equal(plan.Summary, result.Summary);
    }

    /// <summary>
    /// The same release from a different indexer is a different offer, and
    /// Deluno has said nothing about it.
    /// </summary>
    [Fact]
    public void Blocking_one_indexers_copy_leaves_anothers_alone()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Nebula"), Candidate("Arrival.2016.2160p", "Orbit"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(plan, Keys("Arrival.2016.2160p", "Nebula"));

        Assert.Equal("Orbit", result.BestCandidate!.IndexerName);
    }

    /// <summary>
    /// Matching survives the difference between what an indexer prints and
    /// what somebody typed.
    /// </summary>
    [Fact]
    public void Matching_ignores_case_and_surrounding_space()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Nebula"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(plan, Keys("  ARRIVAL.2016.2160P  ", "nebula"));

        Assert.Null(result.BestCandidate);
    }

    // ------------------------------------------------------------------ helpers

    private static HashSet<string> Keys(string releaseName, string indexerName)
        => new(StringComparer.Ordinal) { BlockedReleaseKeys.For(releaseName, indexerName) };

    private static MediaSearchPlan Plan(params MediaSearchCandidate[] candidates)
        => new(
            BestCandidate: candidates.FirstOrDefault(),
            Candidates: candidates,
            Summary: candidates.Length == 0
                ? "Nothing found."
                : $"Best feed candidate is {candidates[0].ReleaseName} from {candidates[0].IndexerName}.");

    private static MediaSearchCandidate Candidate(string releaseName, string indexerName)
        => new(
            releaseName,
            indexerName.ToLowerInvariant(),
            indexerName,
            "WEB 1080p",
            100,
            MeetsCutoff: true,
            Summary: releaseName);
}
