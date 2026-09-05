using Deluno.Worker.Services;

namespace Deluno.Worker.Tests.Processing;

public sealed class ProcessorOutputReadinessTests
{
    /// <summary>
    /// A file still being written is not ready — and this is the third attempt
    /// at testing that without a race.
    ///
    /// <para>The rule is "last written more than two seconds ago", measured
    /// against the wall clock. Relying on the assertion arriving within two
    /// seconds of creating the file worked until the machine was busy. Pinning
    /// the write time to <c>UtcNow</c> left no margin at all and failed the same
    /// way under a full parallel run. Pinning it to the *future* was worse
    /// still: it never flakes, and it never fails either, because a negative
    /// age is under any positive threshold — the assertion survives the rule
    /// being deleted entirely.</para>
    ///
    /// <para>So the clock is held still instead of the file. The age is then
    /// exactly what the test says it is, and shortening the window breaks
    /// it.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1.9)]
    public void Rejects_a_file_written_within_the_stable_window(double ageSeconds)
    {
        using var fixture = new TemporaryOutputFile();
        var writtenAt = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.FilePath, writtenAt);

        var now = new FixedClock(writtenAt.AddSeconds(ageSeconds));

        Assert.False(ProcessorOutputReadiness.IsReady(fixture.FilePath, now));
    }

    /// <summary>The other side of the same line, so the boundary is stated.</summary>
    [Fact]
    public void Accepts_a_file_once_it_is_past_the_stable_window()
    {
        using var fixture = new TemporaryOutputFile();
        var writtenAt = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(fixture.FilePath, writtenAt);

        Assert.True(ProcessorOutputReadiness.IsReady(fixture.FilePath, new FixedClock(writtenAt.AddSeconds(2.1))));
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
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
