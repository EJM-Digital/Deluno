using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Processing;

public sealed class ProcessorOutputReadinessTests
{
    /// <summary>
    /// A file still being written is not ready.
    ///
    /// <para>The write time is pinned rather than assumed. Readiness is "older
    /// than two seconds", and this used to rely on the assertion arriving
    /// within that window of creating the file — which is true until the
    /// machine is busy, and then the file is genuinely old enough and the test
    /// fails having found nothing wrong. It did exactly that under a full
    /// parallel run. Stating the age makes the test about the rule instead of
    /// about how fast the machine is.</para>
    /// </summary>
    [Fact]
    public void Rejects_a_recently_written_file()
    {
        using var fixture = new TemporaryOutputFile();
        File.SetLastWriteTimeUtc(fixture.FilePath, DateTime.UtcNow);

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
