using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests;

/// <summary>
/// The recycle bin keeps one list on disk and changes it one operation at a time (coverage register, engines): many moves and deletes
/// asked at once leave a list that reads back whole, with every file moved still there and every file deleted gone. Refs #1296.
/// </summary>
public sealed class RecycleBinRunsOneThingAtATimeTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), $"deluno-recycle-serial-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch (IOException) { }
    }

    private static LibraryItem Library(string root)
    {
        var now = DateTimeOffset.UtcNow;
        return new LibraryItem(
            "library-1", "Test library", "movies", "main", root, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    [Fact]
    public async Task Moves_and_permanent_deletes_asked_together_leave_a_list_that_reads_back_whole()
    {
        var data = Path.Combine(_sandbox, "data");
        var library = Directory.CreateDirectory(Path.Combine(_sandbox, "library")).FullName;
        RecycleBinService NewService() => new(Options.Create(new StoragePathOptions { DataRoot = data }), TimeProvider.System, NullLogger<RecycleBinService>.Instance);
        var service = NewService();
        string[] first = Enumerable.Range(0, 10).Select(index => Path.Combine(library, $"First.{index}.mkv")).ToArray();
        string[] second = Enumerable.Range(0, 10).Select(index => Path.Combine(library, $"Second.{index}.mkv")).ToArray();
        foreach (var file in first.Concat(second))
        {
            await File.WriteAllTextAsync(file, Path.GetFileName(file));
        }

        var binned = await Task.WhenAll(first.Select(file => service.MoveAsync([new TrackedLibraryFile("library-1", file)], [Library(library)], CancellationToken.None)));
        var ids = binned.Select(result => Assert.Single(result.Items).Id).ToArray();

        // Five of the first ten are deleted for good while the other ten are moved in, all at the same moment.
        var deletes = ids.Take(5).Select(id => service.PermanentlyDeleteAsync(id, CancellationToken.None));
        var moves = second.Select(file => service.MoveAsync([new TrackedLibraryFile("library-1", file)], [Library(library)], CancellationToken.None));
        await Task.WhenAll(deletes.Cast<Task>().Concat(moves));

        var listed = await NewService().ListAsync(CancellationToken.None);
        Assert.Equal(15, listed.Count);
        Assert.Equal(15, listed.Select(item => item.Id).Distinct().Count());
        Assert.DoesNotContain(listed, item => ids.Take(5).Contains(item.Id));
        Assert.All(listed, item => Assert.True(File.Exists(item.RecyclePath), $"{item.Id} lost its stored file"));
        Assert.Equal(15, Directory.GetFiles(Path.Combine(_sandbox), "*.mkv", SearchOption.AllDirectories).Length);
    }
}
