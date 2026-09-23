using Deluno.Integrations.Search;

namespace Deluno.Integrations.Tests.Search;

/// <summary>
/// #593: a search for a whole show does not take one episode.
///
/// <para><b>Why this file exists.</b> On the soak rig, Breaking Bad's
/// <b>Search now</b> ran one show-level query. It picked
/// Breaking.Bad.S02E03, weighed it against the show's WEB 1080p (held by two
/// other episodes) and held it as "equivalent to the installed file". A single
/// episode is its own search's to judge, against its own file.</para>
/// </summary>
public sealed class AShowSearchLeavesEpisodesToTheirOwnSearchTests
{
    [Fact]
    public void A_show_search_passes_over_single_episodes_to_the_season_pack()
    {
        var plan = Plan(Candidate("Breaking.Bad.S02E03.1080p.WEB-DL"), Candidate("Breaking.Bad.S02.1080p.WEB-DL"));

        var result = AcquisitionDecisionPipeline.LeaveEpisodesToTheirOwnSearch(plan, Show());

        Assert.Equal("Breaking.Bad.S02.1080p.WEB-DL", result.BestCandidate!.ReleaseName);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void A_show_search_offered_only_episodes_takes_none_and_says_why()
    {
        var plan = Plan(Candidate("Breaking.Bad.S02E03.1080p.WEB-DL"), Candidate("Breaking.Bad.S01E04E05.1080p.WEB-DL"));

        var result = AcquisitionDecisionPipeline.LeaveEpisodesToTheirOwnSearch(plan, Show());

        Assert.Null(result.BestCandidate);
        Assert.Empty(result.Candidates);
        Assert.Contains("searched for on its own", result.Summary, StringComparison.Ordinal);
        Assert.Equal(MediaSearchReasons.NoUsableRelease, result.Reason);
    }

    /// <summary>
    /// The episode search, the preview list a person chooses from and a film
    /// are left exactly as they were.
    /// </summary>
    [Fact]
    public void Episode_searches_previews_and_films_are_untouched()
    {
        var plan = Plan(Candidate("Breaking.Bad.S02E03.1080p.WEB-DL"));

        Assert.Same(plan, AcquisitionDecisionPipeline.LeaveEpisodesToTheirOwnSearch(plan, Show() with { SeasonNumber = 2, EpisodeNumber = 3 }));
        Assert.Same(plan, AcquisitionDecisionPipeline.LeaveEpisodesToTheirOwnSearch(plan, Show() with { PreviewOnly = true }));
        Assert.Same(plan, AcquisitionDecisionPipeline.LeaveEpisodesToTheirOwnSearch(plan, Show() with { MediaType = "movies" }));
    }

    private static AcquisitionDecisionRequest Show()
        => new("Breaking Bad", 2008, "tv", "WEB 1080p", "WEB 1080p", [], []);

    private static MediaSearchPlan Plan(params MediaSearchCandidate[] candidates)
        => new(candidates.FirstOrDefault(), candidates, $"Best feed candidate is {candidates[0].ReleaseName}.");

    private static MediaSearchCandidate Candidate(string releaseName)
        => new(releaseName, "soak", "Soak Torznab", "WEB 1080p", 100, MeetsCutoff: true, Summary: releaseName);
}
