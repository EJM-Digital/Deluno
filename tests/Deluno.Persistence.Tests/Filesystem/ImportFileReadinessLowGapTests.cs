using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>A file is ready to import only when it is there and has bytes in it (#1329), on real files in a real temp folder.</summary>
public sealed class ImportFileReadinessLowGapTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "deluno-readiness", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Settled(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        return path;
    }

    [Fact]
    public void A_missing_file_and_a_zero_byte_file_are_not_ready_and_a_settled_file_with_bytes_is()
    {
        var missing = Path.Combine(_folder, "not-there.mkv");
        var empty = Settled("empty.mkv", []);
        var full = Settled("full.mkv", [1, 2, 3, 4]);

        Assert.False(ImportFileReadiness.IsReady(missing));
        Assert.False(ImportFileReadiness.IsReady(empty));
        Assert.True(ImportFileReadiness.IsReady(full));
    }

    [Fact]
    public void A_preview_whose_source_is_missing_is_not_ready()
    {
        var preview = new ImportPreviewResponse(
            Path.Combine(_folder, "gone.mkv"), _folder, Path.Combine(_folder, "out.mkv"), "copy", false, null, null,
            SourceExists: false, DestinationExists: false, 0, 0, true, null, "", [], "", []);

        Assert.False(ImportFileReadiness.IsPreviewReady(preview));
    }

    [Fact]
    public void A_settled_file_is_handed_over_and_a_fresh_or_missing_one_is_not()
    {
        var settled = Settled("settled.mkv", [1, 2, 3, 4]);
        var fresh = Path.Combine(_folder, "fresh.mkv");
        File.WriteAllBytes(fresh, [1, 2, 3, 4]);

        Assert.True(ImportFileReadiness.IsReadyToHandOver(settled));
        Assert.False(ImportFileReadiness.IsReadyToHandOver(fresh));
        Assert.False(ImportFileReadiness.IsReadyToHandOver(Path.Combine(_folder, "missing.mkv")));
    }

    [Fact]
    public void A_finished_file_another_program_holds_exclusively_is_handed_over_only_after_the_hold_settles()
    {
        var path = Settled("held.mkv", [1, 2, 3, 4]);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(ImportFileReadiness.IsReadyToHandOver(path), "a client's hold on a finished file does not keep it back");

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            Assert.False(ImportFileReadiness.IsReadyToHandOver(path), "a file written a moment ago and held may still be being written");
        }
    }

    [Fact]
    public void A_clock_that_fails_with_a_file_error_reads_as_not_ready_and_never_as_an_error()
    {
        var path = Settled("clock.mkv", [1, 2, 3, 4]);

        Assert.False(ImportFileReadiness.IsReady(path, new FailingClock(new IOException("disk"))));
        Assert.False(ImportFileReadiness.IsReady(path, new FailingClock(new UnauthorizedAccessException("denied"))));
        Assert.False(ImportFileReadiness.IsReadyToHandOver(path, new FailingClock(new IOException("disk"))));
        Assert.False(ImportFileReadiness.IsReadyToHandOver(path, new FailingClock(new UnauthorizedAccessException("denied"))));
    }

    private sealed class FailingClock(Exception failure) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw failure;
    }
}
