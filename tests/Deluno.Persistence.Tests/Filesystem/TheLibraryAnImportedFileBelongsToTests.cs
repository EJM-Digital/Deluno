using Deluno.Contracts;
using Deluno.Filesystem;
using Deluno.Libraries.Contracts;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>The library an imported file belongs to is the one whose root holds the destination: the longest root when roots nest.</summary>
public sealed class TheLibraryAnImportedFileBelongsToTests
{
    private static LibraryItem Library(string id, string root, string mediaType = "movies")
    {
        var now = DateTimeOffset.UnixEpoch;
        return new LibraryItem(
            id, id, mediaType, "main", root, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private static string Root(string name) => Path.Combine(Path.GetTempPath(), "deluno-media", name);

    [Fact]
    public void A_destination_under_Movies2_belongs_to_Movies2_not_Movies()
    {
        var libraries = new[] { Library("movies", Root("Movies")), Library("movies2", Root("Movies2")) };

        var owner = ImportPipelineService.ResolveLibraryForImport(
            Path.Combine(Root("Movies2"), "Film (2020)", "Film.mkv"), "movies", libraries);

        Assert.Equal("movies2", owner?.Id);
    }

    [Fact]
    public void A_sibling_folder_that_only_shares_the_start_of_the_name_is_not_inside_the_library()
    {
        var libraries = new[] { Library("movies", Root("Movies")) };

        Assert.Null(ImportPipelineService.ResolveLibraryForImport(
            Path.Combine(Root("Movies2"), "Film (2020)", "Film.mkv"), "movies", libraries));
    }

    [Fact]
    public void Nested_roots_pick_the_longest_one_whatever_the_order_they_are_listed_in()
    {
        var outer = Library("outer", Root("Media"));
        var inner = Library("inner", Path.Combine(Root("Media"), "Kids"));
        var destination = Path.Combine(Root("Media"), "Kids", "Film (2020)", "Film.mkv");

        Assert.Equal("inner", ImportPipelineService.ResolveLibraryForImport(destination, "movies", [outer, inner])?.Id);
        Assert.Equal("inner", ImportPipelineService.ResolveLibraryForImport(destination, "movies", [inner, outer])?.Id);
        Assert.Equal("outer", ImportPipelineService.ResolveLibraryForImport(
            Path.Combine(Root("Media"), "Film (2020)", "Film.mkv"), "movies", [inner, outer])?.Id);
    }

    [Fact]
    public void A_library_of_the_other_media_type_or_with_no_root_never_owns_the_file()
    {
        var tv = Library("tv", Root("Movies"), "tv");
        var rootless = Library("rootless", "");

        Assert.Null(ImportPipelineService.ResolveLibraryForImport(
            Path.Combine(Root("Movies"), "Film.mkv"), "movies", [tv, rootless]));
    }

    [Fact]
    public void Matching_ignores_case_only_where_the_file_system_does()
    {
        var libraries = new[] { Library("movies", Root("Movies")) };
        var shouted = Path.Combine(Root("MOVIES"), "Film.mkv");

        var owner = ImportPipelineService.ResolveLibraryForImport(shouted, "movies", libraries);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("movies", owner?.Id);
        }
        else
        {
            Assert.Null(owner);
        }
    }

    [Fact]
    public void A_trailing_separator_on_either_side_changes_nothing()
    {
        var withSlash = Library("movies", Root("Movies") + Path.DirectorySeparatorChar);
        var without = Library("movies", Root("Movies"));
        var file = Path.Combine(Root("Movies"), "Film.mkv");

        Assert.Equal("movies", ImportPipelineService.ResolveLibraryForImport(file, "movies", [withSlash])?.Id);
        Assert.Equal("movies", ImportPipelineService.ResolveLibraryForImport(file + Path.DirectorySeparatorChar, "movies", [without])?.Id);
        Assert.Equal("movies", ImportPipelineService.ResolveLibraryForImport(Root("Movies"), "movies", [without])?.Id);
    }

    [Theory]
    [InlineData(@"\nas\share\Movies", @"\nas\share\Movies\Film (2020)\Film.mkv", true)]
    [InlineData(@"\nas\share\Movies", @"\nas\share\Movies2\Film (2020)\Film.mkv", false)]
    [InlineData(@"\nas\share\Movies", @"\nas\other\Movies\Film.mkv", false)]
    [InlineData(@"D:\", @"D:\Film.mkv", true)]
    [InlineData(@"D:\", @"E:\Film.mkv", false)]
    [InlineData(@"D:\Media", @"D:/Media/Film.mkv", true)]
    [InlineData(@"D:/Media/", @"D:\Media\Kids\Film.mkv", true)]
    [InlineData(@"D:\Media", @"D:\Media2\Film.mkv", false)]
    public void Unc_drive_and_mixed_separator_roots_are_matched_on_a_folder_boundary(string root, string destination, bool belongs)
    {
        var owner = ImportPipelineService.ResolveLibraryForImport(destination, "movies", [Library("movies", root)]);

        Assert.Equal(belongs, owner is not null);
    }

    [Fact]
    public void A_windows_style_path_ignores_case_in_the_shared_helper_and_a_case_sensitive_caller_does_not()
    {
        Assert.True(SafePath.IsInside(@"D:\Media", @"d:\MEDIA\Film.mkv", comparison: StringComparison.OrdinalIgnoreCase));
        Assert.False(SafePath.IsInside(@"D:\Media", @"d:\MEDIA\Film.mkv", comparison: StringComparison.Ordinal));
        Assert.False(SafePath.IsInside("/data/Movies", "/data/Movies2/Film.mkv", comparison: StringComparison.Ordinal));
    }

    [Fact]
    public void A_backup_exclusion_for_logs_does_not_swallow_a_sibling_folder_that_starts_the_same()
    {
        Assert.False(SafePath.IsInside("/data/logs", "/data/logs-archive/a.db", allowEqual: true, comparison: StringComparison.OrdinalIgnoreCase));
        Assert.True(SafePath.IsInside("/data/logs", "/data/logs/a.log", allowEqual: true, comparison: StringComparison.OrdinalIgnoreCase));
    }
}
