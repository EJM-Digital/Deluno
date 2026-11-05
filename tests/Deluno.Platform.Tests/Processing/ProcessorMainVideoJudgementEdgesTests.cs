using Deluno.Platform.Processing;

namespace Deluno.Platform.Tests.Processing;

/// <summary>
/// The 25% rule's edges, over real temp files: a video exactly at a quarter of the largest is a main video, a processor
/// that renamed the cleaned file is judged by count, and three or more left-out extras are named with "and more".
/// </summary>
public sealed class ProcessorMainVideoJudgementEdgesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-judge-" + Guid.NewGuid().ToString("N"));

    public ProcessorMainVideoJudgementEdgesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "source"));
        Directory.CreateDirectory(Path.Combine(_root, "out"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Source(string name, int bytes)
    {
        var path = Path.Combine(_root, "source", name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string Out(string name, int bytes = 100)
    {
        var path = Path.Combine(_root, "out", name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void A_video_exactly_at_25_percent_of_the_largest_is_a_main_video_the_processor_must_deliver()
    {
        Source("Film.mkv", 4000);
        Source("Part2.mkv", 1000);
        var onlyFilm = Out("Film.mkv");

        var missing = ProcessorMainVideoJudgement.Judge(Path.Combine(_root, "source"), [onlyFilm], "Weir");

        Assert.False(missing.MainVideoDelivered);

        var both = ProcessorMainVideoJudgement.Judge(Path.Combine(_root, "source"), [onlyFilm, Out("Part2.mkv")], "Weir");

        Assert.True(both.MainVideoDelivered);
        Assert.Empty(both.Extras);
        Assert.Null(both.Note);
    }

    [Fact]
    public void A_video_just_under_25_percent_is_an_extra_the_processor_may_leave_out()
    {
        Source("Film.mkv", 4000);
        Source("Trailer.mkv", 999);

        var result = ProcessorMainVideoJudgement.Judge(Path.Combine(_root, "source"), [Out("Film.mkv")], "Weir");

        Assert.True(result.MainVideoDelivered);
        Assert.Equal(["Trailer.mkv"], result.Extras);
        Assert.Contains("left one small extra file out (Trailer.mkv)", result.Note);
    }

    [Fact]
    public void A_processor_that_renamed_the_cleaned_file_is_judged_by_count()
    {
        Source("Film.2020.1080p-GRP.mkv", 4000);

        var renamed = ProcessorMainVideoJudgement.Judge(
            Path.Combine(_root, "source"), [Out("Film (2020).mkv")], "Weir");

        Assert.True(renamed.MainVideoDelivered);

        Source("Second.mkv", 4000);
        var tooFew = ProcessorMainVideoJudgement.Judge(
            Path.Combine(_root, "source"), [Out("Film (2020).mkv")], "Weir");

        Assert.False(tooFew.MainVideoDelivered);
    }

    [Fact]
    public void Three_extras_are_all_named_and_a_fourth_is_told_as_and_more()
    {
        Source("Film.mkv", 4000);
        foreach (var name in new[] { "a.mkv", "b.mkv", "c.mkv" })
        {
            Source(name, 10);
        }

        var three = ProcessorMainVideoJudgement.Judge(Path.Combine(_root, "source"), [Out("Film.mkv")], "Weir");

        Assert.Equal(3, three.Extras.Count);
        Assert.Contains("left 3 small extra files out", three.Note);
        Assert.All(new[] { "a.mkv", "b.mkv", "c.mkv" }, name => Assert.Contains(name, three.Note));
        Assert.DoesNotContain("and more", three.Note);

        Source("d.mkv", 10);
        var four = ProcessorMainVideoJudgement.Judge(Path.Combine(_root, "source"), [Out("Film.mkv")], "Weir");

        Assert.Equal(4, four.Extras.Count);
        Assert.Contains("left 4 small extra files out", four.Note);
        Assert.Contains(", and more)", four.Note);
    }
}
