using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.Realtime;

/// <summary>Every change to the recycle bin's contents or settings pushes RecycleBinChanged so an open Recycle bin screen moves by itself (#1329).</summary>
public sealed class RecycleBinChangedLowGapTests
{
    [Fact]
    public async Task Putting_a_file_in_the_bin_restoring_it_deleting_it_for_good_and_saving_the_settings_each_push_a_change()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-bin-live", Guid.NewGuid().ToString("N"));
        var dataRoot = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var libraryRoot = Directory.CreateDirectory(Path.Combine(root, "Films")).FullName;
        try
        {
            var publisher = new RecordingRealtimeEventPublisher();
            var bin = new RecycleBinService(Options.Create(new StoragePathOptions { DataRoot = dataRoot }), TimeProvider.System, NullLogger<RecycleBinService>.Instance, publisher);
            var now = DateTimeOffset.UnixEpoch;
            var library = new LibraryItem(
                "library-1", "Films", "movies", "main", libraryRoot, null, null, null, null,
                true, true, "direct", null, null, 0, "block",
                false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
            var seen = 0;
            int Pushes() => publisher.Published.Count(item => item.Type == "RecycleBin");
            string NewFile(string name)
            {
                var path = Path.Combine(libraryRoot, name);
                File.WriteAllText(path, "film");
                return path;
            }

            var stored = await bin.StoreRemovedFileAsync(library, NewFile("One.mkv"), null, CancellationToken.None);
            Assert.NotNull(stored);
            Assert.True(Pushes() > seen, "no change was pushed"); seen = Pushes();

            Assert.True((await bin.RestoreAsync(stored!.Id, CancellationToken.None)).Success);
            Assert.True(Pushes() > seen, "no change was pushed"); seen = Pushes();

            var second = await bin.StoreRemovedFileAsync(library, NewFile("Two.mkv"), null, CancellationToken.None);
            Assert.True(Pushes() > seen, "no change was pushed"); seen = Pushes();
            Assert.True((await bin.PermanentlyDeleteAsync(second!.Id, CancellationToken.None)).Success);
            Assert.True(Pushes() > seen, "no change was pushed"); seen = Pushes();

            await bin.SaveSettingsAsync(new RecycleBinSettings(14, 1024), CancellationToken.None);
            Assert.True(Pushes() > seen, "no change was pushed"); seen = Pushes();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
