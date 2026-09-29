using Deluno.Movies.Services;

namespace Deluno.Movies.Tests.Services;

/// <summary>
/// #787 / #1078: bulk library reassignment can move files. These pin the
/// planning decision - which folder or file moves where - kept separate from
/// the disk IO so it can be tested without a filesystem. Only a title's own
/// folder, or a single flat file, ever moves.
/// </summary>
public sealed class MovieFileRelocationPlannerTests
{
    private static readonly string FromRoot = Path.Combine(Path.GetTempPath(), "library", "movies-main");
    private static readonly string ToRoot = Path.Combine(Path.GetTempPath(), "library", "movies-4k");
    private static readonly string[] Roots = [FromRoot, ToRoot];

    private static Task<MovieFileRelocationOutcome> Plan(string filePath, bool shared = false) =>
        MovieFileRelocationPlanner.PlanAsync(
            filePath, FromRoot, ToRoot, Roots,
            (_, _) => Task.FromResult(shared),
            "Dune Part Two (2024)",
            CancellationToken.None);

    [Fact]
    public async Task Moves_the_whole_title_folder_when_it_is_the_titles_own()
    {
        var outcome = await Plan(Path.Combine(FromRoot, "Dune Part Two (2024)", "Dune.Part.Two.2024.mkv"));

        Assert.NotNull(outcome.Plan);
        Assert.True(outcome.Plan!.MovesWholeFolder);
        Assert.Equal(Path.Combine(FromRoot, "Dune Part Two (2024)"), outcome.Plan.SourceDirectory);
        Assert.Equal(Path.Combine(ToRoot, "Dune Part Two (2024)"), outcome.Plan.DestinationDirectory);
    }

    [Fact]
    public async Task Preserves_nested_subfolders_under_the_root()
    {
        var outcome = await Plan(Path.Combine(FromRoot, "Collections", "Dune Part Two (2024)", "Dune.mkv"));

        Assert.True(outcome.Plan!.MovesWholeFolder);
        Assert.Equal(Path.Combine(ToRoot, "Collections", "Dune Part Two (2024)"), outcome.Plan.DestinationDirectory);
    }

    [Fact]
    public async Task A_file_straight_in_the_library_root_moves_alone_into_a_new_title_folder_and_never_the_library_folder()
    {
        // #1078: the relative folder is ".", which used to move the whole
        // library folder with every other title in it.
        var outcome = await Plan(Path.Combine(FromRoot, "Dune.Part.Two.2024.mkv"));

        Assert.NotNull(outcome.Plan);
        Assert.False(outcome.Plan!.MovesWholeFolder);
        Assert.Equal(FromRoot, outcome.Plan.SourceDirectory);
        Assert.Equal(Path.Combine(ToRoot, "Dune Part Two (2024)"), outcome.Plan.DestinationDirectory);
    }

    [Fact]
    public async Task A_folder_that_also_holds_other_titles_files_is_refused_with_a_reason()
    {
        var outcome = await Plan(Path.Combine(FromRoot, "Action", "Dune.mkv"), shared: true);

        Assert.True(outcome.IsRefused);
        Assert.Contains("other titles", outcome.RefusalReason);
    }

    [Fact]
    public async Task A_path_outside_every_library_is_refused()
    {
        var outcome = await Plan(Path.Combine(Path.GetTempPath(), "elsewhere", "Dune Part Two (2024)", "Dune.mkv"));

        Assert.True(outcome.IsRefused);
        Assert.False(string.IsNullOrWhiteSpace(outcome.RefusalReason));
    }

    [Fact]
    public async Task A_file_straight_in_a_different_library_root_than_the_source_is_refused()
    {
        var outcome = await Plan(Path.Combine(ToRoot, "Dune.mkv"));

        Assert.True(outcome.IsRefused);
    }

    [Fact]
    public async Task A_bare_filename_is_refused()
    {
        var outcome = await Plan("movie.mkv");

        Assert.True(outcome.IsRefused);
    }
}
