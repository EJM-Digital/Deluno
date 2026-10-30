using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// What Checkpoint A (#917) opens for a download: a file as itself, a film folder as its one video, a
/// season pack as every video largest first, and nothing when only a sample or nothing has landed.
/// </summary>
public sealed class DownloadIntegrityFileResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "deluno-resolver-" + Guid.NewGuid().ToString("N"));

    public DownloadIntegrityFileResolverTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Make(string relative, int bytes = 1)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void A_file_path_resolves_to_itself_and_a_missing_path_to_nothing()
    {
        var file = Make("movie.mkv");
        Assert.Equal([file], DownloadIntegrityFileResolver.Resolve(file));
        Assert.Empty(DownloadIntegrityFileResolver.Resolve(Path.Combine(root, "nothing-here")));
    }

    [Fact]
    public void A_film_folder_resolves_to_its_one_video_and_ignores_samples_and_extras()
    {
        var movie = Make("Film (2020)/Film.mkv", 100);
        Make("Film (2020)/Film.nfo");
        Make("Film (2020)/sample.mkv", 500);
        Make("Film (2020)/Sample-Film.mkv", 500);
        Make("Film (2020)/Film-Sample.mkv", 500);
        Make("Film (2020)/Film.sample.mkv", 500);
        Make("Film (2020)/sample.Film.mkv", 500);
        Assert.Equal([movie], DownloadIntegrityFileResolver.Resolve(Path.Combine(root, "Film (2020)")));
    }

    [Fact]
    public void A_season_pack_resolves_to_every_video_largest_first()
    {
        var small = Make("Pack/Show.S01E01.mkv", 10);
        var large = Make("Pack/Nested/Show.S01E02.mkv", 300);
        var middle = Make("Pack/Show.S01E03.mp4", 100);
        Assert.Equal([large, middle, small], DownloadIntegrityFileResolver.Resolve(Path.Combine(root, "Pack")));
    }

    [Fact]
    public void A_folder_with_only_a_sample_or_nothing_resolves_to_nothing()
    {
        Make("Early/sample.mkv", 10);
        Directory.CreateDirectory(Path.Combine(root, "Empty"));
        Assert.Empty(DownloadIntegrityFileResolver.Resolve(Path.Combine(root, "Early")));
        Assert.Empty(DownloadIntegrityFileResolver.Resolve(Path.Combine(root, "Empty")));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_share_that_drops_mid_listing_resolves_to_nothing_this_pass(Type exceptionType)
    {
        Directory.CreateDirectory(Path.Combine(root, "Share"));
        IEnumerable<string> Listing(string _)
        {
            yield return "a.mkv";
            throw (Exception)Activator.CreateInstance(exceptionType)!;
        }

        Assert.Empty(DownloadIntegrityFileResolver.Resolve(Path.Combine(root, "Share"), Listing, _ => 1));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void A_file_whose_size_cannot_be_read_still_counts_but_sorts_last(Type exceptionType)
    {
        Directory.CreateDirectory(Path.Combine(root, "Pack2"));
        var result = DownloadIntegrityFileResolver.Resolve(
            Path.Combine(root, "Pack2"),
            _ => ["gone.mkv", "kept.mkv"],
            path => path == "gone.mkv" ? throw (Exception)Activator.CreateInstance(exceptionType)! : 50);
        Assert.Equal(["kept.mkv", "gone.mkv"], result);
    }
}
