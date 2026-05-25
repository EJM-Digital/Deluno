using Deluno.Downloader.Postprocessing;

namespace Deluno.Downloader.Tests.Postprocessing;

public class SampleAndProofFilterTests
{
    [Fact]
    public async Task Filters_sample_directories_and_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"deluno-filter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sample"));
            Directory.CreateDirectory(Path.Combine(dir, "proof"));
            Directory.CreateDirectory(Path.Combine(dir, "screens"));

            var files = new[]
            {
                Path.Combine(dir, "movie.mkv"),                  // keep
                Path.Combine(dir, "movie-sample.mkv"),           // sample suffix → drop
                Path.Combine(dir, "sample", "tinyclip.mkv"),     // in sample dir → drop
                Path.Combine(dir, "proof", "screenshot.jpg"),    // proof dir → drop
                Path.Combine(dir, "screens", "shot01.png"),      // screens dir → drop
                Path.Combine(dir, "release.nfo"),                // nfo → drop
                Path.Combine(dir, "release.sfv"),                // sfv → drop
                Path.Combine(dir, "shortcut.url"),               // url → drop
                Path.Combine(dir, "subtitles.srt"),              // keep
            };
            foreach (var f in files) File.WriteAllBytes(f, new byte[] { 0 });

            var filter = new SampleAndProofFilter();
            var kept = await filter.ProcessAsync(dir, files, CancellationToken.None);

            Assert.Equal(2, kept.Count);
            Assert.Contains(kept, p => p.EndsWith("movie.mkv"));
            Assert.Contains(kept, p => p.EndsWith("subtitles.srt"));

            // Dropped files are also deleted from disk.
            Assert.False(File.Exists(Path.Combine(dir, "movie-sample.mkv")));
            Assert.False(File.Exists(Path.Combine(dir, "release.nfo")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
