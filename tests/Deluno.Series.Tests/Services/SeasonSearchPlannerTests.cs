using Deluno.Jobs.Contracts;
using Deluno.Series.Contracts;
using Deluno.Series.Services;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #646: the automatic library cycle searches a season as a season, not one
/// episode at a time, exactly when every monitored episode of that season has
/// aired and none of them is on disk.
/// </summary>
public sealed class SeasonSearchPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");

    [Fact]
    public void A_fully_missing_fully_aired_season_is_planned_as_one_season_search()
    {
        var due = new[]
        {
            Due("e-101", "series-1", 1, 1, "Bought"),
            Due("e-102", "series-1", 1, 2, "The Second Door"),
            Due("e-103", "series-1", 1, 3, "Login")
        };
        var inventory = new[]
        {
            Episode("e-101", 1, 1, aired: Now.AddDays(-30)),
            Episode("e-102", 1, 2, aired: Now.AddDays(-23)),
            Episode("e-103", 1, 3, aired: Now.AddDays(-16))
        };

        var plan = SeasonSearchPlanner.Decide(due, Now, (_, _) => inventory);

        var season = Assert.Single(plan.SeasonSearches);
        Assert.Equal("series-1", season.SeriesId);
        Assert.Equal(1, season.SeasonNumber);
        Assert.Equal(["e-101", "e-102", "e-103"], season.Episodes.Select(item => item.EpisodeId));
        Assert.Empty(plan.EpisodeSearches);
    }

    [Fact]
    public void A_partial_season_with_an_installed_episode_stays_per_episode()
    {
        var due = new[]
        {
            Due("e-101", "series-1", 1, 1, "Bought"),
            Due("e-102", "series-1", 1, 2, "The Second Door")
        };
        var inventory = new[]
        {
            Episode("e-101", 1, 1, aired: Now.AddDays(-30)),
            Episode("e-102", 1, 2, aired: Now.AddDays(-23)),
            // Already on disk: the season is not entirely missing.
            Episode("e-103", 1, 3, aired: Now.AddDays(-16), hasFile: true)
        };

        var plan = SeasonSearchPlanner.Decide(due, Now, (_, _) => inventory);

        Assert.Empty(plan.SeasonSearches);
        Assert.Equal(["e-101", "e-102"], plan.EpisodeSearches.Select(item => item.EpisodeId));
    }

    [Fact]
    public void A_season_with_an_episode_still_to_air_searches_only_the_aired_ones_per_episode()
    {
        var due = new[]
        {
            Due("e-101", "series-1", 1, 1, "Bought"),
            Due("e-102", "series-1", 1, 2, "The Second Door")
        };
        var inventory = new[]
        {
            Episode("e-101", 1, 1, aired: Now.AddDays(-30)),
            Episode("e-102", 1, 2, aired: Now.AddDays(-23)),
            // Not aired yet: the season is not fully aired.
            Episode("e-103", 1, 3, aired: Now.AddDays(5))
        };

        var plan = SeasonSearchPlanner.Decide(due, Now, (_, _) => inventory);

        Assert.Empty(plan.SeasonSearches);
        Assert.Equal(["e-101", "e-102"], plan.EpisodeSearches.Select(item => item.EpisodeId));
    }

    [Fact]
    public void An_episode_with_no_known_air_date_holds_the_season_to_per_episode_search()
    {
        var due = new[]
        {
            Due("e-101", "series-1", 1, 1, "Bought"),
            Due("e-102", "series-1", 1, 2, "The Second Door")
        };
        var inventory = new[]
        {
            Episode("e-101", 1, 1, aired: Now.AddDays(-30)),
            Episode("e-102", 1, 2, aired: Now.AddDays(-23)),
            // Air date not yet known: conservative, so this holds the season
            // to per-episode search rather than assuming it has aired.
            Episode("e-103", 1, 3, aired: null)
        };

        var plan = SeasonSearchPlanner.Decide(due, Now, (_, _) => inventory);

        Assert.Empty(plan.SeasonSearches);
        Assert.Equal(["e-101", "e-102"], plan.EpisodeSearches.Select(item => item.EpisodeId));
    }

    [Fact]
    public void One_missing_episode_is_not_a_season_and_stays_per_episode()
    {
        var due = new[]
        {
            Due("e-101", "series-1", 1, 1, "Bought")
        };
        var inventory = new[]
        {
            Episode("e-101", 1, 1, aired: Now.AddDays(-30))
        };

        var plan = SeasonSearchPlanner.Decide(due, Now, (_, _) => inventory);

        Assert.Empty(plan.SeasonSearches);
        Assert.Equal(["e-101"], plan.EpisodeSearches.Select(item => item.EpisodeId));
    }

    [Fact]
    public void An_episode_not_yet_due_for_its_own_retry_window_keeps_the_season_on_the_per_episode_path()
    {
        // The season is entirely missing and fully aired, but "e-103" did not
        // come back from ListEligibleWantedEpisodesAsync this cycle — its own
        // retry window has not opened yet. A pack search must not run ahead of
        // that window, so the season stays on the per-episode path for the
        // episodes that are actually due.
        var due = new[]
        {
            Due("e-101", "series-1", 1, 1, "Bought"),
            Due("e-102", "series-1", 1, 2, "The Second Door")
        };
        var inventory = new[]
        {
            Episode("e-101", 1, 1, aired: Now.AddDays(-30)),
            Episode("e-102", 1, 2, aired: Now.AddDays(-23)),
            Episode("e-103", 1, 3, aired: Now.AddDays(-16))
        };

        var plan = SeasonSearchPlanner.Decide(due, Now, (_, _) => inventory);

        Assert.Empty(plan.SeasonSearches);
        Assert.Equal(["e-101", "e-102"], plan.EpisodeSearches.Select(item => item.EpisodeId));
    }

    [Fact]
    public void Different_series_and_different_seasons_of_the_same_series_are_planned_independently()
    {
        var due = new[]
        {
            Due("a-101", "series-a", 1, 1, "A1"),
            Due("a-102", "series-a", 1, 2, "A2"),
            Due("a-201", "series-a", 2, 1, "B1"),
            Due("c-101", "series-c", 1, 1, "C1"),
            Due("c-102", "series-c", 1, 2, "C2")
        };

        var plan = SeasonSearchPlanner.Decide(due, Now, (seriesId, seasonNumber) => (seriesId, seasonNumber) switch
        {
            ("series-a", 1) =>
            [
                Episode("a-101", 1, 1, aired: Now.AddDays(-10)),
                Episode("a-102", 1, 2, aired: Now.AddDays(-3))
            ],
            ("series-c", 1) =>
            [
                Episode("c-101", 1, 1, aired: Now.AddDays(-10)),
                Episode("c-102", 1, 2, aired: Now.AddDays(-3))
            ],
            _ => []
        });

        Assert.Equal(2, plan.SeasonSearches.Count);
        Assert.Contains(plan.SeasonSearches, season => season.SeriesId == "series-a" && season.SeasonNumber == 1);
        Assert.Contains(plan.SeasonSearches, season => season.SeriesId == "series-c" && season.SeasonNumber == 1);
        // Series A's season 2 only has one due episode: not a season.
        Assert.Equal(["a-201"], plan.EpisodeSearches.Select(item => item.EpisodeId));
    }

    private static EpisodeSearchPlanItem Due(string episodeId, string seriesId, int season, int episode, string title)
        => new(episodeId, seriesId, season, episode, title);

    private static SeriesEpisodeInventoryItem Episode(
        string id,
        int season,
        int episode,
        DateTimeOffset? aired,
        bool hasFile = false,
        bool monitored = true,
        string? title = "An episode")
        => new(
            id,
            season,
            episode,
            title,
            null,
            aired,
            monitored,
            hasFile,
            hasFile ? "covered" : "missing",
            "test",
            QualityCutoffMet: false,
            CurrentQuality: null,
            TargetQuality: "WEB 1080p",
            PreventLowerQualityReplacements: false,
            LastQualityDeltaDecision: null,
            LastSearchUtc: null,
            NextEligibleSearchUtc: null,
            UpdatedUtc: Now);
}
