using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// Whether a file is ready to import, decided on a real file in a real temp folder while a real background writer is
/// appending to it: not ready while it is being written or was written moments ago, ready once the writer has stopped
/// and the file has sat still.
/// </summary>
public sealed class FileReadinessOnDiskTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "deluno-tests", Guid.NewGuid().ToString("N"));

    public FileReadinessOnDiskTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task A_file_an_active_writer_keeps_appending_to_is_not_ready_until_the_writer_stops_and_the_file_has_sat_still()
    {
        var path = Path.Combine(_folder, "Sintel.2010.mkv");
        File.WriteAllBytes(path, new byte[1024]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        Assert.True(ImportFileReadiness.IsReady(path), "a settled file is ready");

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Write(new byte[512]);
                }

                await Task.Delay(50, CancellationToken.None); // reason: the writer's own rhythm, an append every 50 ms for as long as the test lets it
            }
        });
        await Deluno.Persistence.Tests.Support.Wait.UntilAsync(() => new FileInfo(path).Length > 1024, "the writer to make its first append");
        var readyWhileWriting = false;
        for (var look = 0; look < 10; look++)
        {
            readyWhileWriting |= ImportFileReadiness.IsReady(path);
            await Task.Delay(40, CancellationToken.None); // reason: sample the file several times while the writer is active
        }

        Assert.False(readyWhileWriting, "a file still being appended to is never ready");

        await stop.CancelAsync();
        await writer;
        Assert.False(ImportFileReadiness.IsReady(path), "a file written a moment ago is not ready yet");

        // After the writer stops, the file is ready once it has been still for the minimum age; the wait ends as soon as it is.
        var ready = await ImportFileReadiness.WaitUntilReadyAsync(path, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.True(ready, "the file became ready once nothing wrote to it");
        Assert.True(ImportFileReadiness.IsReady(path));
    }

    [Fact]
    public async Task A_file_that_keeps_changing_is_given_back_as_not_ready_when_the_window_runs_out()
    {
        var path = Path.Combine(_folder, "Sintel.2010.mkv");
        File.WriteAllBytes(path, new byte[1024]);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Write(new byte[512]);
                }

                await Task.Delay(50, CancellationToken.None); // reason: the writer's own rhythm
            }
        });

        var ready = await ImportFileReadiness.WaitUntilReadyAsync(path, TimeSpan.FromSeconds(3), CancellationToken.None);
        await stop.CancelAsync();
        await writer;

        Assert.False(ready, "a file that never settles is not ready, and the caller is told to try again later");
    }
}
