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
}
