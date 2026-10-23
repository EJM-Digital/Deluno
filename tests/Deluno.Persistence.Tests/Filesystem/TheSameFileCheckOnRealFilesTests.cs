using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// "Same content" as the import judges it, on two real files (coverage register, engines): a file the title already holds at the
/// destination is adopted instead of overwritten only when it is the same size and the same bytes in the first, middle and last
/// megabyte. A film is not hashed in full because that is slow over a share. Refs #1296.
/// </summary>
public sealed class TheSameFileCheckOnRealFilesTests : IDisposable
{
    private const int Megabyte = 1024 * 1024;
    private readonly string _folder = Directory.CreateTempSubdirectory("deluno-same-content-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string FileOf(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Film(int megabytes)
    {
        var bytes = new byte[megabytes * Megabyte];
        new Random(2026).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Changed(byte[] original, int at)
    {
        var copy = (byte[])original.Clone();
        copy[at] ^= 0xFF;
        return copy;
    }

    [Fact]
    public void Two_copies_of_one_film_are_the_same_and_a_different_size_never_is()
    {
        var film = Film(5);
        var first = FileOf("a.mkv", film);
        var second = FileOf("b.mkv", film);

        Assert.True(ImportPipelineService.IsSameContent(first, second));
        Assert.False(ImportPipelineService.IsSameContent(first, FileOf("shorter.mkv", film[..(film.Length - 1)])));
        Assert.False(ImportPipelineService.IsSameContent(first, Path.Combine(_folder, "not-there.mkv")));
        Assert.False(ImportPipelineService.IsSameContent(FileOf("empty-a.mkv", []), FileOf("empty-b.mkv", [])));
    }

    [Theory]
    [InlineData("head", 10)]
    [InlineData("middle", 5 * Megabyte / 2)]
    [InlineData("tail", 5 * Megabyte - 10)]
    public void A_difference_in_the_head_the_middle_or_the_tail_makes_two_files_different(string where, int at)
    {
        var film = Film(5);
        var first = FileOf($"{where}-a.mkv", film);
        var second = FileOf($"{where}-b.mkv", Changed(film, at));

        Assert.False(ImportPipelineService.IsSameContent(first, second));
    }

    [Fact(Skip = "Known gap, to be filed: only the first, middle and last megabyte are compared, so two files of the same size that differ anywhere else are adopted as identical and the title keeps the older one. Needs a decision on the cost of a full compare over a share.")]
    public void A_difference_anywhere_else_in_the_file_also_makes_two_files_different()
    {
        var film = Film(5);
        var first = FileOf("outside-a.mkv", film);
        var second = FileOf("outside-b.mkv", Changed(film, Megabyte + Megabyte / 2));

        Assert.False(ImportPipelineService.IsSameContent(first, second));
    }
}
