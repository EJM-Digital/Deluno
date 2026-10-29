using System.Text.Json;
using Deluno.Infrastructure.Storage;
using Deluno.Libraries.Contracts;
using Deluno.Persistence.Tests.Support;
using Deluno.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Persistence.Tests.RealDisk;

/// <summary>
/// A removed title's saved record is kept as long as any of its files is still in the recycle bin, and dropped once
/// retention has taken the last one (coverage register, area engines). Real files, the real bin and its real state file.
/// </summary>
public sealed class ARemovedTitlesRecordIsKeptWhileItsFilesAreInTheBinTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("deluno-title-record-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private int RecordsInTheBin()
    {
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "data", "recycle-bin", "state.json")));
        foreach (var property in state.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("titles", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetArrayLength();
            }
        }

        return 0;
    }

    [Fact]
    public async Task The_record_stays_while_any_of_the_titles_files_is_in_the_bin_and_goes_after_retention_once_none_remain()
    {
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var bin = new RecycleBinService(
            Options.Create(new StoragePathOptions { DataRoot = Path.Combine(_root, "data") }),
            clock,
            NullLogger<RecycleBinService>.Instance);
        var libraryRoot = Directory.CreateDirectory(Path.Combine(_root, "films")).FullName;
        var now = clock.GetUtcNow();
        var library = new LibraryItem(
            "library-1", "Films", "movies", "main", libraryRoot, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
        string[] files = [Path.Combine(libraryRoot, "Dune (2021)", "Dune (2021).mkv"), Path.Combine(libraryRoot, "Dune Extras", "Behind the scenes.mkv")];
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "content");
        }

        // The first file goes into the bin now, the second three weeks later; the record belongs to both.
        var first = Assert.Single((await bin.MoveAsync([new TrackedLibraryFile("library-1", files[0])], [library], CancellationToken.None)).Items);
        clock.Advance(TimeSpan.FromDays(21));
        var second = Assert.Single((await bin.MoveAsync([new TrackedLibraryFile("library-1", files[1])], [library], CancellationToken.None)).Items);
        await bin.AttachTitleAsync(
            [first.Id, second.Id],
            new RecycleBinTitleSnapshot("title-1", "movies", "movie", "movie-1", "Dune", 2021, "{}", now),
            CancellationToken.None);
        Assert.Equal(1, RecordsInTheBin());

        // Retention (30 days) takes the first file, but the second is still here, so the record stays.
        clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(1, await bin.CleanupAsync(CancellationToken.None));
        Assert.Equal(1, RecordsInTheBin());
        Assert.Equal(second.Id, Assert.Single(await bin.ListAsync(CancellationToken.None)).Id);

        // Retention takes the last one, and the record goes with it.
        clock.Advance(TimeSpan.FromDays(25));
        Assert.Equal(1, await bin.CleanupAsync(CancellationToken.None));
        Assert.Empty(await bin.ListAsync(CancellationToken.None));
        Assert.Equal(0, RecordsInTheBin());
    }
}
