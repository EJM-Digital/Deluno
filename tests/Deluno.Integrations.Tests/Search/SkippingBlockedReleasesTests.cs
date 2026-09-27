using Deluno.Contracts;
using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// A release Deluno has refused is skipped, and the skipping is said out loud.
///
/// <para>DESIGN-007 decision 1. The owner chose "refuse it, and tell you" over
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
    /// #874: The owner decided the blocklist key is the release's identity across
    /// every indexer for every refusal reason — a stall on one indexer or a
    /// failure Deluno cannot place is exactly as disqualifying on another,
    /// not only a refusal about the file itself. This is the every-indexer
    /// key <see cref="Deluno.Jobs.Data.SqliteBlockedReleaseRepository.ListKeysAsync"/>
    /// now adds for every reason, so it is included here the same way it
    /// would be read back from a real block.
    /// </summary>
    [Fact]
    public void A_release_refused_for_stalling_is_skipped_from_every_indexer_too()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Nebula"), Candidate("Arrival.2016.2160p", "Orbit"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(
            plan,
            Keys("Arrival.2016.2160p", "Nebula").Append(BlockedReleaseKeys.ForEveryIndexer("Arrival.2016.2160p")).ToHashSet(StringComparer.Ordinal));

        Assert.Null(result.BestCandidate);
        Assert.Empty(result.Candidates);
    }

    /// <summary>
    /// An unrelated release keeps its own indexer's copy: the every-indexer
    /// key only ever matches its own name.
    /// </summary>
    [Fact]
    public void A_stall_on_one_release_does_not_touch_an_unrelated_release()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Orbit"), Candidate("Some.Other.Movie.2020.1080p", "Orbit"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(
            plan,
            new HashSet<string>(StringComparer.Ordinal) { BlockedReleaseKeys.ForEveryIndexer("Arrival.2016.2160p") });

        Assert.Equal("Some.Other.Movie.2020.1080p", result.BestCandidate!.ReleaseName);
    }

    /// <summary>
    /// #585, #874: a refusal holds the release whichever indexer offers it.
    /// On the rig both soak indexers list the same names, one as a torrent
    /// and one as Usenet.
    /// </summary>
    [Fact]
    public void A_release_refused_for_its_content_is_skipped_from_every_indexer()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Orbit"), Candidate("Arrival.2016.1080p", "Orbit"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(
            plan,
            new HashSet<string>(StringComparer.Ordinal) { BlockedReleaseKeys.ForEveryIndexer("Arrival.2016.2160p") });

        Assert.Equal("Arrival.2016.1080p", result.BestCandidate!.ReleaseName);
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

    /// <summary>
    /// #802, #874: the same content re-offered by a different indexer, under
    /// its own name, is still refused — a blocked release does not get a free
    /// pass by turning up somewhere else with a hash that matches.
    /// </summary>
    [Fact]
    public void A_hash_blocked_on_one_indexer_is_skipped_from_another()
    {
        var plan = Plan(
            Candidate("Arrival.2016.2160p-GROUP1", "Nebula", hash: "ABCDEF0123456789ABCDEF0123456789ABCDEF01"),
            Candidate("Arrival.2016.1080p", "Orbit"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(
            plan,
            new HashSet<string>(StringComparer.Ordinal)
            {
                BlockedReleaseKeys.ForHash("ABCDEF0123456789ABCDEF0123456789ABCDEF01")
            });

        Assert.Equal("Arrival.2016.1080p", result.BestCandidate!.ReleaseName);
        Assert.Single(result.Candidates);
    }

    /// <summary>
    /// #802: a retitled re-upload of exactly the same file is still that
    /// file. The name a different re-uploader gave it does not matter — the
    /// hash is the release's identity.
    /// </summary>
    [Fact]
    public void The_same_hash_under_a_different_name_is_still_refused()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p.RETITLED-GROUP2", "Orbit", hash: "abcdef0123456789abcdef0123456789abcdef01"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(
            plan,
            new HashSet<string>(StringComparer.Ordinal)
            {
                BlockedReleaseKeys.ForHash("ABCDEF0123456789ABCDEF0123456789ABCDEF01")
            });

        Assert.Null(result.BestCandidate);
        Assert.Empty(result.Candidates);
    }

    /// <summary>
    /// A block made before #874 shipped only ever had a name+indexer key.
    /// Reading it must keep refusing exactly as it did — nothing about an old
    /// row's matching regresses just because a hash key can now exist too.
    /// </summary>
    [Fact]
    public void An_old_name_and_indexer_only_key_still_matches()
    {
        var plan = Plan(Candidate("Arrival.2016.2160p", "Nebula"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(plan, Keys("Arrival.2016.2160p", "Nebula"));

        Assert.Null(result.BestCandidate);
    }

    /// <summary>
    /// A hash on the blocklist says nothing about a release that neither
    /// shares it nor the blocked name.
    /// </summary>
    [Fact]
    public void An_unrelated_release_with_its_own_hash_is_not_refused()
    {
        var plan = Plan(Candidate("Some.Other.Movie.2020.1080p", "Nebula", hash: "1111111111111111111111111111111111111111"));

        var result = AcquisitionDecisionPipeline.ApplyBlocklist(
            plan,
            new HashSet<string>(StringComparer.Ordinal)
            {
                BlockedReleaseKeys.ForHash("ABCDEF0123456789ABCDEF0123456789ABCDEF01")
            });

        Assert.Equal("Some.Other.Movie.2020.1080p", result.BestCandidate!.ReleaseName);
        Assert.Single(result.Candidates);
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

    private static MediaSearchCandidate Candidate(string releaseName, string indexerName, string? hash = null)
        => new(
            releaseName,
            indexerName.ToLowerInvariant(),
            indexerName,
            "WEB 1080p",
            100,
            MeetsCutoff: true,
            Summary: releaseName,
            TorrentHashOrItemId: hash);
}
