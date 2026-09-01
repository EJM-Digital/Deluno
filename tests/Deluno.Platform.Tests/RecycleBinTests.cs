using Deluno.Libraries.Contracts;
using Deluno.Platform;
using Deluno.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Deluno.Platform.Tests;

public sealed class RecycleBinTests
{
    [Fact]
    public async Task Move_lists_and_restores_a_title_folder_without_touching_other_files()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-recycle-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(sandbox, "data");
        var libraryRoot = Path.Combine(sandbox, "library");
        var titleFolder = Path.Combine(libraryRoot, "Example Movie (2026)");
        var moviePath = Path.Combine(titleFolder, "Example.Movie.2026.mkv");
        var artworkPath = Path.Combine(titleFolder, "poster.jpg");
        var outsidePath = Path.Combine(sandbox, "leave-alone.mkv");

        try
        {
            Directory.CreateDirectory(titleFolder);
            File.WriteAllText(moviePath, "movie");
            File.WriteAllText(artworkPath, "artwork");
            File.WriteAllText(outsidePath, "outside");

            var service = CreateService(dataRoot, "2026-08-31T00:00:00Z");
            var moved = await service.MoveAsync(
                [new TrackedLibraryFile("library-1", moviePath)],
                [CreateLibrary("library-1", libraryRoot)],
                CancellationToken.None);

            var item = Assert.Single(moved.Items);
            Assert.False(Directory.Exists(titleFolder));
            Assert.True(File.Exists(outsidePath));
            Assert.True(Directory.Exists(item.RecyclePath));
            Assert.True(File.Exists(Path.Combine(item.RecyclePath, Path.GetFileName(moviePath))));
            Assert.Equal(1, moved.MovedFolderCount);
            Assert.Equal(0, moved.MovedFileCount);

            var restored = await service.RestoreAsync(item.Id, CancellationToken.None);

            Assert.True(restored.Success);
            Assert.True(File.Exists(moviePath));
            Assert.True(File.Exists(artworkPath));
            Assert.Empty(await service.ListAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public async Task Permanent_delete_removes_the_stored_item_and_state()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-recycle-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(sandbox, "data");
        var libraryRoot = Path.Combine(sandbox, "library");
        var moviePath = Path.Combine(libraryRoot, "Example.Movie.2026.mkv");

        try
        {
            Directory.CreateDirectory(libraryRoot);
            File.WriteAllText(moviePath, "movie");

            var service = CreateService(dataRoot, "2026-08-31T00:00:00Z");
            var moved = await service.MoveAsync(
                [new TrackedLibraryFile("library-1", moviePath)],
                [CreateLibrary("library-1", libraryRoot)],
                CancellationToken.None);
            var item = Assert.Single(moved.Items);

            var deleted = await service.PermanentlyDeleteAsync(item.Id, CancellationToken.None);

            Assert.True(deleted.Success);
            Assert.False(File.Exists(item.RecyclePath));
            Assert.Empty(await service.ListAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    private static RecycleBinService CreateService(string dataRoot, string utcNow)
        => new(
            Options.Create(new StoragePathOptions { DataRoot = dataRoot }),
            new FixedTimeProvider(DateTimeOffset.Parse(utcNow)),
            NullLogger<RecycleBinService>.Instance);

    private static LibraryItem CreateLibrary(string id, string rootPath)
    {
        var now = DateTimeOffset.UtcNow;
        return new LibraryItem(
            id, "Test library", "movies", "main", rootPath, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
