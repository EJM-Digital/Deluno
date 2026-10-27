using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// "Same content" as the import judges it, on two real files (coverage register, engines): a file the title already holds at the
/// destination is adopted instead of overwritten only when it is the same size and every byte is the same, read in chunks that stop at
/// the first difference. Refs #1296.
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
    public async Task Two_copies_of_one_film_are_the_same_and_a_different_size_never_is()
    {
        var film = Film(5);
        var first = FileOf("a.mkv", film);
        var second = FileOf("b.mkv", film);

        Assert.True(await ImportPipelineService.IsSameContentAsync(first, second, CancellationToken.None));
        Assert.False(await ImportPipelineService.IsSameContentAsync(first, FileOf("shorter.mkv", film[..(film.Length - 1)]), CancellationToken.None));
        Assert.False(await ImportPipelineService.IsSameContentAsync(first, Path.Combine(_folder, "not-there.mkv"), CancellationToken.None));
        Assert.False(await ImportPipelineService.IsSameContentAsync(FileOf("empty-a.mkv", []), FileOf("empty-b.mkv", []), CancellationToken.None));
    }

    [Theory]
    [InlineData("head", 10)]
    [InlineData("middle", 5 * Megabyte / 2)]
    [InlineData("tail", 5 * Megabyte - 10)]
    public async Task A_difference_in_the_head_the_middle_or_the_tail_makes_two_files_different(string where, int at)
    {
        var film = Film(5);
        var first = FileOf($"{where}-a.mkv", film);
        var second = FileOf($"{where}-b.mkv", Changed(film, at));

        Assert.False(await ImportPipelineService.IsSameContentAsync(first, second, CancellationToken.None));
    }

    [Fact]
    public async Task A_difference_anywhere_else_in_the_file_also_makes_two_files_different()
    {
        var film = Film(5);
        var first = FileOf("outside-a.mkv", film);
        var second = FileOf("outside-b.mkv", Changed(film, Megabyte + Megabyte / 2));

        Assert.False(await ImportPipelineService.IsSameContentAsync(first, second, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_check_stops_instead_of_reading_on()
    {
        var film = Film(5);
        var first = FileOf("cancel-a.mkv", film);
        var second = FileOf("cancel-b.mkv", film);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImportPipelineService.IsSameContentAsync(first, second, cancelled.Token));
    }
}
