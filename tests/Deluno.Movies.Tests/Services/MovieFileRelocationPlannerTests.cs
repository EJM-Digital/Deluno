using Deluno.Movies.Services;

namespace Deluno.Movies.Tests.Services;

/// <summary>
/// #787: bulk library reassignment can move files, not only re-point the
/// record. These pin the planning decision - which folder moves where - kept
/// separate from the actual disk IO precisely so it can be tested without a
/// filesystem.
/// </summary>
public sealed class MovieFileRelocationPlannerTests
{
    [Fact]
    public void Plan_moves_the_whole_title_folder_when_the_file_sits_under_the_source_root()
    {
        var fromRoot = Path.Combine(Path.GetTempPath(), "library", "movies-main");
        var toRoot = Path.Combine(Path.GetTempPath(), "library", "movies-4k");
        var filePath = Path.Combine(fromRoot, "Dune Part Two (2024)", "Dune.Part.Two.2024.mkv");

        var plan = MovieFileRelocationPlanner.Plan(filePath, fromRoot, toRoot);

        Assert.NotNull(plan);
        Assert.True(plan!.MovesWholeFolder);
        Assert.Equal(Path.Combine(fromRoot, "Dune Part Two (2024)"), plan.SourceDirectory);
        Assert.Equal(Path.Combine(toRoot, "Dune Part Two (2024)"), plan.DestinationDirectory);
    }

    [Fact]
    public void Plan_preserves_nested_subfolders_under_the_root()
    {
        var fromRoot = Path.Combine(Path.GetTempPath(), "library", "movies-main");
        var toRoot = Path.Combine(Path.GetTempPath(), "library", "movies-4k");
        var filePath = Path.Combine(fromRoot, "Collections", "Dune Part Two (2024)", "Dune.Part.Two.2024.mkv");

        var plan = MovieFileRelocationPlanner.Plan(filePath, fromRoot, toRoot);

        Assert.NotNull(plan);
        Assert.True(plan!.MovesWholeFolder);
        Assert.Equal(Path.Combine(toRoot, "Collections", "Dune Part Two (2024)"), plan.DestinationDirectory);
    }

    [Fact]
    public void Plan_falls_back_to_moving_only_the_file_when_it_is_not_under_the_source_root()
    {
        // A manually-relocated file, or a stale library root: nothing here
        // proves the whole containing folder belongs to this title, so only
        // the tracked file relocates rather than risking someone else's files
        // dragged along with it.
        var fromRoot = Path.Combine(Path.GetTempPath(), "library", "movies-main");
        var toRoot = Path.Combine(Path.GetTempPath(), "library", "movies-4k");
        var filePath = Path.Combine(Path.GetTempPath(), "elsewhere", "Dune Part Two (2024)", "Dune.Part.Two.2024.mkv");

        var plan = MovieFileRelocationPlanner.Plan(filePath, fromRoot, toRoot);

        Assert.NotNull(plan);
        Assert.False(plan!.MovesWholeFolder);
        Assert.Equal(toRoot, plan.DestinationDirectory);
    }

    [Fact]
    public void Plan_returns_null_for_a_bare_filename_with_no_containing_directory()
    {
        var plan = MovieFileRelocationPlanner.Plan(
            filePath: "movie.mkv",
            fromLibraryRootPath: Path.Combine(Path.GetTempPath(), "library", "movies-main"),
            toLibraryRootPath: Path.Combine(Path.GetTempPath(), "library", "movies-4k"));

        Assert.Null(plan);
    }
}
