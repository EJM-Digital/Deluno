namespace Deluno.Platform.Tests;

public sealed class TitleOwnFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"deluno-own-{Guid.NewGuid():N}");

    public TitleOwnFilesTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private string Make(params string[] parts)
    {
        var path = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_folder_holding_only_the_title_and_its_artwork_goes_whole()
    {
        var film = Make("Dune (2021)", "Dune.mkv");
        Make("Dune (2021)", "poster.jpg");
        Make("Dune (2021)", "Dune.en.srt");

        var plan = TitleOwnFiles.Plan(root, [film]);

        var unit = Assert.Single(plan);
        Assert.True(unit.IsDirectory);
        Assert.Equal(Path.Combine(root, "Dune (2021)"), unit.Path);
    }

    [Fact]
    public void A_group_folder_holding_only_that_title_goes_whole()
    {
        var dune = Make("A", "Dune (2021)", "Dune.mkv");

        var unit = Assert.Single(TitleOwnFiles.Plan(root, [dune]));

        Assert.Equal(Path.Combine(root, "A"), unit.Path);
    }

    [Fact]
    public void A_sidecar_belongs_to_the_video_with_the_longest_matching_name()
    {
        var one = Make("Shared", "Dune.mkv");
        var oneSubtitle = Make("Shared", "Dune.en.srt");
        Make("Shared", "Dune.Part.Two.mkv");
        Make("Shared", "Dune.Part.Two.en.srt");

        var plan = TitleOwnFiles.Plan(root, [one]);

        Assert.Equal(new[] { oneSubtitle, one }.Order(), plan.Select(unit => unit.Path).Order());
        Assert.All(plan, unit => Assert.False(unit.IsDirectory));
    }

    [Fact]
    public void A_group_folder_with_another_title_in_it_is_never_taken_and_a_file_of_unknown_kind_keeps_its_folder()
    {
        var dune = Make("A", "Dune (2021)", "Dune.mkv");
        Make("A", "Arrival (2016)", "Arrival.mkv");
        var solo = Make("B", "Solo (2018)", "Solo.mkv");
        Make("B", "Solo (2018)", "something.xyz");

        var plan = TitleOwnFiles.Plan(root, [dune, solo]);

        Assert.Contains(plan, unit => unit.IsDirectory && unit.Path == Path.Combine(root, "A", "Dune (2021)"));
        Assert.DoesNotContain(plan, unit => unit.Path == Path.Combine(root, "A"));
        Assert.Contains(plan, unit => !unit.IsDirectory && unit.Path == solo);
        Assert.DoesNotContain(plan, unit => unit.Path == Path.Combine(root, "B", "Solo (2018)"));
    }

    [Fact]
    public void Folders_above_the_taken_units_are_listed_deepest_first_and_never_the_library_root()
    {
        var dune = Make("A", "Dune (2021)", "Dune.mkv");
        Make("A", "Arrival (2016)", "Arrival.mkv");

        var tidy = TitleOwnFiles.AncestorsToTidy(root, TitleOwnFiles.Plan(root, [dune]));

        Assert.Equal([Path.Combine(root, "A")], tidy);
    }
}
