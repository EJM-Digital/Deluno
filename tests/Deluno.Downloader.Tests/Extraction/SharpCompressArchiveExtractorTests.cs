using System.IO.Compression;
using Deluno.Downloader.Extraction;

namespace Deluno.Downloader.Tests.Extraction;

public class SharpCompressArchiveExtractorTests
{
    [Fact]
    public async Task Extracts_a_simple_zip()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"deluno-zip-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(workDir, "test.zip");
        var outDir = Path.Combine(workDir, "out");

        try
        {
            Directory.CreateDirectory(workDir);
            // Use System.IO.Compression to author the test zip — no point
            // hand-rolling Zip64 just for this.
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("inside/hello.txt");
                using var w = new StreamWriter(entry.Open());
                w.Write("hello world");
            }

            var extractor = new SharpCompressArchiveExtractor();
            var result = await extractor.ExtractAsync(zipPath, outDir, password: null, progress: null, CancellationToken.None);

            Assert.True(result.Succeeded, result.FailureReason);
            Assert.Single(result.ExtractedFiles);
            var extracted = result.ExtractedFiles[0];
            Assert.True(File.Exists(extracted));
            Assert.Equal("hello world", await File.ReadAllTextAsync(extracted));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Reports_failure_on_corrupt_archive()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"deluno-corrupt-{Guid.NewGuid():N}");
        var badPath = Path.Combine(workDir, "broken.zip");
        var outDir = Path.Combine(workDir, "out");

        try
        {
            Directory.CreateDirectory(workDir);
            File.WriteAllBytes(badPath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFF, 0xFF, 0xFF });

            var extractor = new SharpCompressArchiveExtractor();
            var result = await extractor.ExtractAsync(badPath, outDir, null, null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.NotNull(result.FailureReason);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Path_traversal_entries_are_skipped()
    {
        // SharpCompress + our ResolveDestinationPath should refuse to
        // extract entries that try to escape the output dir.
        var workDir = Path.Combine(Path.GetTempPath(), $"deluno-trav-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(workDir, "evil.zip");
        var outDir = Path.Combine(workDir, "out");

        try
        {
            Directory.CreateDirectory(workDir);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../../escapes.txt");
                using var w = new StreamWriter(entry.Open());
                w.Write("should not land outside output dir");
            }

            var extractor = new SharpCompressArchiveExtractor();
            var result = await extractor.ExtractAsync(zipPath, outDir, null, null, CancellationToken.None);

            // Extraction "succeeded" in the sense that no exception was
            // thrown; the malicious entry was silently skipped.
            Assert.Empty(result.ExtractedFiles);
            Assert.False(File.Exists(Path.Combine(workDir, "escapes.txt")));
            Assert.False(File.Exists(Path.Combine(workDir, "..", "escapes.txt")));
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}
