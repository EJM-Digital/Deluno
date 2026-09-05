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

    /**
     * DESIGN-007 decision 15: "Enforce retention automatically, and show what a
     * manual empty takes". The empty used to delete first and count afterwards,
     * which is a report rather than a choice — and permanent deletion is the
     * one place a report after the fact is worth nothing.
     */
    [Fact]
    public async Task An_empty_says_what_it_would_take_without_taking_it()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-recycle-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(sandbox, "data");
        var libraryRoot = Path.Combine(sandbox, "library");
        var moviePath = Path.Combine(libraryRoot, "Expired.Movie.2020.mkv");

        try
        {
            Directory.CreateDirectory(libraryRoot);
            File.WriteAllText(moviePath, "movie");

            var service = CreateService(dataRoot, "2026-08-31T00:00:00Z");
            await service.SaveSettingsAsync(new RecycleBinSettings(7, 10_000), CancellationToken.None);
            var moved = await service.MoveAsync(
                [new TrackedLibraryFile("library-1", moviePath)],
                [CreateLibrary("library-1", libraryRoot)],
                CancellationToken.None);
            var stored = Assert.Single(moved.Items);

            // A fortnight later, the retention window has passed.
            var later = CreateService(dataRoot, "2026-09-30T00:00:00Z");
            var preview = await later.PreviewCleanupAsync(CancellationToken.None);

            Assert.Equal(stored.Id, Assert.Single(preview.Items).Id);
            Assert.Equal(1, preview.ExpiredCount);
            Assert.Equal(0, preview.OverCapacityCount);

            // And it took nothing. The whole point.
            Assert.Single(await later.ListAsync(CancellationToken.None));
            Assert.True(Directory.Exists(stored.RecyclePath) || File.Exists(stored.RecyclePath));
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// And it takes exactly what it said it would. The preview and the empty
    /// choose from the same code, because a dialog saying "3 items" that then
    /// removes five is worse than no dialog.
    /// </summary>
    [Fact]
    public async Task What_the_empty_takes_is_what_the_preview_showed()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-recycle-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(sandbox, "data");
        var libraryRoot = Path.Combine(sandbox, "library");

        try
        {
            Directory.CreateDirectory(libraryRoot);
            var tracked = new List<TrackedLibraryFile>();
            foreach (var name in new[] { "One.2020.mkv", "Two.2020.mkv", "Three.2020.mkv" })
            {
                var path = Path.Combine(libraryRoot, name);
                File.WriteAllText(path, name);
                tracked.Add(new TrackedLibraryFile("library-1", path));
            }

            var service = CreateService(dataRoot, "2026-08-31T00:00:00Z");
            await service.SaveSettingsAsync(new RecycleBinSettings(7, 10_000), CancellationToken.None);
            await service.MoveAsync(tracked, [CreateLibrary("library-1", libraryRoot)], CancellationToken.None);

            var later = CreateService(dataRoot, "2026-09-30T00:00:00Z");
            var preview = await later.PreviewCleanupAsync(CancellationToken.None);
            var removed = await later.CleanupAsync(CancellationToken.None);

            Assert.Equal(3, preview.Items.Count);
            Assert.Equal(preview.Items.Count, removed);
            Assert.Empty(await later.ListAsync(CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// Nothing to take reads as "nothing has expired", not as a broken button.
    /// </summary>
    [Fact]
    public async Task A_bin_within_its_limits_would_take_nothing()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-recycle-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(sandbox, "data");
        var libraryRoot = Path.Combine(sandbox, "library");
        var moviePath = Path.Combine(libraryRoot, "Recent.Movie.2026.mkv");

        try
        {
            Directory.CreateDirectory(libraryRoot);
            File.WriteAllText(moviePath, "movie");

            var service = CreateService(dataRoot, "2026-08-31T00:00:00Z");
            await service.SaveSettingsAsync(new RecycleBinSettings(30, 10_000), CancellationToken.None);
            await service.MoveAsync(
                [new TrackedLibraryFile("library-1", moviePath)],
                [CreateLibrary("library-1", libraryRoot)],
                CancellationToken.None);

            var preview = await service.PreviewCleanupAsync(CancellationToken.None);

            Assert.Empty(preview.Items);
            Assert.Equal(0, preview.BytesFreed);
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// The other half of the retention rule, which nothing covered: an item
    /// that has not expired goes anyway once the bin is over its size limit,
    /// and the oldest is the one that goes.
    ///
    /// <para>It happens at the moment of recycling rather than at the next
    /// clean-up — every write enforces retention — which is why a preview a
    /// second later has nothing to report. That is worth knowing and was
    /// written down nowhere.</para>
    /// </summary>
    [Fact]
    public async Task An_over_full_bin_drops_the_oldest_thing_in_it_even_though_it_has_not_expired()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-recycle-{Guid.NewGuid():N}");
        var dataRoot = Path.Combine(sandbox, "data");
        var libraryRoot = Path.Combine(sandbox, "library");
        var library = CreateLibrary("library-1", libraryRoot);
        var sevenHundredKb = new string('x', 700 * 1024);

        try
        {
            Directory.CreateDirectory(libraryRoot);

            // Nothing ever expires, so size is the only thing that can take it.
            var older = CreateService(dataRoot, "2026-08-01T00:00:00Z");
            await older.SaveSettingsAsync(new RecycleBinSettings(3650, 1), CancellationToken.None);

            var firstPath = Path.Combine(libraryRoot, "Older.2026.mkv");
            File.WriteAllText(firstPath, sevenHundredKb);
            var first = Assert.Single(
                (await older.MoveAsync([new TrackedLibraryFile("library-1", firstPath)], [library], CancellationToken.None)).Items);
            Assert.Single(await older.ListAsync(CancellationToken.None));

            var newer = CreateService(dataRoot, "2026-08-02T00:00:00Z");
            var secondPath = Path.Combine(libraryRoot, "Newer.2026.mkv");
            File.WriteAllText(secondPath, sevenHundredKb);
            var second = Assert.Single(
                (await newer.MoveAsync([new TrackedLibraryFile("library-1", secondPath)], [library], CancellationToken.None)).Items);

            // 1.4 MB would not fit in a 1 MB bin, so the older one went as the
            // newer one arrived.
            var remaining = Assert.Single(await newer.ListAsync(CancellationToken.None));
            Assert.Equal(second.Id, remaining.Id);
            Assert.False(File.Exists(first.RecyclePath));

            // And what is left fits, so an empty would take nothing.
            Assert.Empty((await newer.PreviewCleanupAsync(CancellationToken.None)).Items);
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
