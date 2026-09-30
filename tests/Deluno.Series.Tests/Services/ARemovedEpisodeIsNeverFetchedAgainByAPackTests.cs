using Deluno.Integrations.Search;
using Deluno.Jobs.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Services;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #1072 (James, 30 Sep): a season still airing is removed granularly. Andor S02 has E01-E06 removed (each
/// unmonitored on its own), E07 airs, E08-E10 are still to come. The new episode is fetched by itself, never by a
/// season pack that would bring E01-E06 back.
/// </summary>
public sealed class ARemovedEpisodeIsNeverFetchedAgainByAPackTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");

    private static SeriesEpisodeInventoryItem[] AndorSeasonTwo()
        => Enumerable.Range(1, 10)
            .Select(number => Episode($"e-{number:00}", number,
                aired: number <= 7 ? Now.AddDays(-70 + number * 7) : Now.AddDays(number - 7),
                monitored: number > 6))
            .ToArray();

    [Fact]
    public void A_season_with_removed_episodes_is_never_planned_as_a_pack_only_the_new_episode_is_searched_alone()
    {
        var inventory = AndorSeasonTwo();
        var due = new[] { new EpisodeSearchPlanItem("e-07", "andor", 2, 7, "Episode 7") };

        var plan = SeasonSearchPlanner.Decide(due, Now, (_, _) => inventory);

        Assert.Empty(plan.SeasonSearches);
        Assert.Equal(["e-07"], plan.EpisodeSearches.Select(item => item.EpisodeId));
    }

    [Fact]
    public void Even_a_fully_aired_season_with_one_removed_episode_is_not_planned_as_a_pack()
    {
        var inventory = Enumerable.Range(1, 4)
            .Select(number => Episode($"e-{number}", number, Now.AddDays(-30 + number), monitored: number != 1))
            .ToArray();
        var due = inventory.Where(episode => episode.Monitored)
            .Select(episode => new EpisodeSearchPlanItem(episode.EpisodeId, "andor", 2, episode.EpisodeNumber, "x"))
            .ToArray();

        var plan = SeasonSearchPlanner.Decide(due, Now, (_, _) => inventory);

        Assert.Empty(plan.SeasonSearches);
        Assert.Equal(3, plan.EpisodeSearches.Count);
    }

    [Fact]
    public void A_search_never_takes_a_season_pack_for_a_season_with_a_removed_episode()
    {
        var pack = Candidate("Andor.S02.1080p.WEB-DL.DDP5.1.H.264-GROUP");
        var single = Candidate("Andor.S02E07.1080p.WEB-DL.DDP5.1.H.264-GROUP");
        var plan = new MediaSearchPlan(pack, [pack, single], "Found");
        var request = Request(new[] { 2 });

        var kept = AcquisitionDecisionPipeline.LeavePacksThatWouldRestoreRemovedEpisodes(plan, request);

        Assert.Equal(single, kept.BestCandidate);
        Assert.Equal([single], kept.Candidates);

        // A pack for a season with nothing removed is still allowed.
        var other = AcquisitionDecisionPipeline.LeavePacksThatWouldRestoreRemovedEpisodes(plan, Request(new[] { 1 }));
        Assert.Equal(pack, other.BestCandidate);

        // And a person choosing from the preview list still sees everything.
        var preview = AcquisitionDecisionPipeline.LeavePacksThatWouldRestoreRemovedEpisodes(plan, Request(new[] { 2 }) with { PreviewOnly = true });
        Assert.Equal(2, preview.Candidates.Count);
    }

    [Fact]
    public void An_inventory_names_the_seasons_that_have_a_removed_episode()
    {
        var detail = new SeriesInventoryDetail("andor", "Andor", 2022, 2, 10, 0, AndorSeasonTwo());

        Assert.Equal([2], detail.SeasonsWithRemovedEpisodes());
    }

    [Fact]
    public void A_person_asking_for_a_season_search_gets_packs_even_when_they_unmonitored_an_episode_but_automatic_searches_do_not()
    {
        var detail = new SeriesInventoryDetail("andor", "Andor", 2022, 2, 10, 0, AndorSeasonTwo());

        Assert.Null(SeasonSearchExecutor.SeasonsToProtectFromPacks(detail, "manual-season"));
        Assert.Equal([2], SeasonSearchExecutor.SeasonsToProtectFromPacks(detail, "automatic-season"));
    }

    private static AcquisitionDecisionRequest Request(IReadOnlyCollection<int>? seasonsWithRemoved)
        => new("Andor", 2022, "tv", null, null, [], [], SeasonsWithRemovedEpisodes: seasonsWithRemoved);

    private static MediaSearchCandidate Candidate(string name)
        => new(name, "idx", "Indexer", "WEB 1080p", 100, true, "ok");

    private static SeriesEpisodeInventoryItem Episode(string id, int number, DateTimeOffset? aired, bool monitored = true)
        => new(id, 2, number, null, null, aired, monitored, false, "", "", false, null, null, false, null, null, null, Now, null, null, null, null, null);
}
