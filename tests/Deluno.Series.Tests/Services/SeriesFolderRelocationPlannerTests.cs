using Deluno.Series.Services;

namespace Deluno.Series.Tests.Services;

/// <summary>
/// #788: the series twin of bulk rename-apply has no single stored "series
/// folder" to rename - only episode file paths, spread across however many
/// seasons. These pin the planner that finds the one folder every episode
/// file actually shares, kept separate from disk IO so it is testable
/// without a filesystem.
/// </summary>
public sealed class SeriesFolderRelocationPlannerTests
{
    [Fact]
    public void Plan_finds_the_series_folder_above_per_season_subfolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "library", "tv-main");
        var seriesDir = Path.Combine(root, "The Expanse (2015)");
        var episodes = new[]
        {
            Path.Combine(seriesDir, "Season 01", "S01E01.mkv"),
            Path.Combine(seriesDir, "Season 01", "S01E02.mkv"),
            Path.Combine(seriesDir, "Season 02", "S02E01.mkv")
        };

        var plan = SeriesFolderRelocationPlanner.Plan(episodes, "The Expanse (2015) [renamed]");

        Assert.NotNull(plan);
        Assert.Equal(seriesDir, plan!.SourceDirectory);
        Assert.Equal(Path.Combine(root, "The Expanse (2015) [renamed]"), plan.DestinationDirectory);
    }

    [Fact]
    public void Plan_works_when_episodes_sit_flat_with_no_season_subfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "library", "tv-main");
        var seriesDir = Path.Combine(root, "The Expanse (2015)");
        var episodes = new[]
        {
            Path.Combine(seriesDir, "S01E01.mkv"),
            Path.Combine(seriesDir, "S01E02.mkv")
        };

        var plan = SeriesFolderRelocationPlanner.Plan(episodes, "The Expanse (2015) [renamed]");

        Assert.NotNull(plan);
        Assert.Equal(seriesDir, plan!.SourceDirectory);
    }

    [Fact]
    public void Plan_returns_null_when_a_single_episode_has_no_parent_directory()
    {
        var plan = SeriesFolderRelocationPlanner.Plan(["episode.mkv"], "The Expanse (2015) [renamed]");

        Assert.Null(plan);
    }

    [Fact]
    public void Plan_returns_null_for_an_empty_file_list()
    {
        var plan = SeriesFolderRelocationPlanner.Plan([], "The Expanse (2015) [renamed]");

        Assert.Null(plan);
    }
}
