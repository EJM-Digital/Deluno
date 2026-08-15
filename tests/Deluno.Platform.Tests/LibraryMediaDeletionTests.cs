using Deluno.Platform.Contracts;

namespace Deluno.Platform.Tests;

public sealed class LibraryMediaDeletionTests
{
    [Fact]
    public void Delete_removes_the_title_folder_inside_its_library_root_only()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-removal-{Guid.NewGuid():N}");
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

            var result = LibraryMediaDeletion.Delete(
                [new TrackedLibraryFile("library-1", moviePath)],
                [CreateLibrary("library-1", libraryRoot)],
                CancellationToken.None);

            Assert.False(Directory.Exists(titleFolder));
            Assert.True(File.Exists(outsidePath));
            Assert.Equal(1, result.DeletedFolderCount);
            Assert.True(result.DeletedFileCount >= 2);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void Delete_refuses_a_tracked_path_outside_the_library_root()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-removal-{Guid.NewGuid():N}");
        var libraryRoot = Path.Combine(sandbox, "library");
        var outsidePath = Path.Combine(sandbox, "leave-alone.mkv");

        try
        {
            Directory.CreateDirectory(libraryRoot);
            File.WriteAllText(outsidePath, "outside");

            var result = LibraryMediaDeletion.Delete(
                [new TrackedLibraryFile("library-1", outsidePath)],
                [CreateLibrary("library-1", libraryRoot)],
                CancellationToken.None);

            Assert.True(File.Exists(outsidePath));
            Assert.Equal(0, result.DeletedFileCount);
            Assert.Contains(result.Warnings, warning => warning.Contains("outside the library root", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }

    private static LibraryItem CreateLibrary(string id, string rootPath)
    {
        var now = DateTimeOffset.UtcNow;
        return new LibraryItem(
            id, "Test library", "movies", "main", rootPath, null, null, null, null,
            true, true, "direct", null, null, 0, "block",
            false, false, false, 6, 6, 10, null, null, "active", false, null, null, now, now);
    }
}
