using Deluno.Libraries.Contracts;

namespace Deluno.Platform.Tests;

public sealed class LibraryMediaDeletionTests
{
    [Fact]
    public void Preview_lists_the_title_folder_inside_its_library_root_and_deletes_nothing()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"deluno-removal-{Guid.NewGuid():N}");
        var libraryRoot = Path.Combine(sandbox, "library");
        var titleFolder = Path.Combine(libraryRoot, "Example Movie (2026)");
        var moviePath = Path.Combine(titleFolder, "Example.Movie.2026.mkv");
        var outsidePath = Path.Combine(sandbox, "leave-alone.mkv");

        try
        {
            Directory.CreateDirectory(titleFolder);
            File.WriteAllText(moviePath, "movie");
            File.WriteAllText(outsidePath, "outside");

            var preview = LibraryMediaDeletion.Preview(
                [new TrackedLibraryFile("library-1", moviePath), new TrackedLibraryFile("library-1", outsidePath)],
                [CreateLibrary("library-1", libraryRoot)]);

            Assert.Equal([moviePath], preview.FilePaths);
            Assert.Equal([titleFolder], preview.FolderPaths);
            Assert.Contains(preview.Warnings, warning => warning.Contains("outside the library root", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(moviePath));
            Assert.True(File.Exists(outsidePath));
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
