using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Processing;

public sealed class ProcessorOutputReadinessTests
{
    [Fact]
    public void Rejects_a_recently_written_file()
    {
        using var fixture = new TemporaryOutputFile();

        Assert.False(ProcessorOutputReadiness.IsReady(fixture.FilePath));
    }

    [Fact]
    public void Accepts_a_stable_file_that_can_be_opened_for_reading()
    {
        using var fixture = new TemporaryOutputFile();
        File.SetLastWriteTimeUtc(fixture.FilePath, DateTime.UtcNow.AddSeconds(-5));

        Assert.True(ProcessorOutputReadiness.IsReady(fixture.FilePath));
    }

    [Fact]
    public void Rejects_a_file_locked_by_the_processor()
    {
        using var fixture = new TemporaryOutputFile();
        File.SetLastWriteTimeUtc(fixture.FilePath, DateTime.UtcNow.AddSeconds(-5));
        using var processorHandle = new FileStream(fixture.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.False(ProcessorOutputReadiness.IsReady(fixture.FilePath));
    }

    private sealed class TemporaryOutputFile : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "deluno-processor-output-tests", Guid.NewGuid().ToString("N"));

        public TemporaryOutputFile()
        {
            Directory.CreateDirectory(directory);
            FilePath = System.IO.Path.Combine(directory, "cleaned.mkv");
            File.WriteAllBytes(FilePath, [1, 2, 3]);
        }

        public string FilePath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for a test fixture.
            }
        }
    }
}
