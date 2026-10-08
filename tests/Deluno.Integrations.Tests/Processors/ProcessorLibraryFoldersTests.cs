using System.Text.Json;
using Deluno.Integrations.Processors;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>
/// #1144: a processor's library-folders list, read in the shape Weir 1.0.0-rc.5 publishes it (an object holding a
/// "libraries" array), and the entry chosen for a library when a workflow was copied by hand.
/// </summary>
public sealed class ProcessorLibraryFoldersTests
{
    private const string Rc5Answer = """
        {
          "libraries": [
            { "id": 1, "name": "Movies", "media_type": "movie", "watched_folder": "D:\\Downloads\\Completed\\Movies",
              "work_folder": "C:\\ProgramData\\Weir\\work\\Movies", "output_folder": "D:\\Processed\\Movies",
              "manager_library_key": "movies-lib", "folders_from_manager": true },
            { "id": 2, "name": "TV", "media_type": "tv", "watched_folder": "",
              "work_folder": "C:\\ProgramData\\Weir\\work\\TV", "output_folder": "D:\\Processed\\TV",
              "manager_library_key": "tv-lib", "folders_from_manager": true },
            { "id": 3, "name": "Weir only", "media_type": "movie", "watched_folder": "E:\\Mine",
              "work_folder": "C:\\w", "output_folder": "E:\\Out", "manager_library_key": null, "folders_from_manager": false }
          ]
        }
        """;

    [Fact]
    public void The_rc5_answer_is_read_and_an_empty_watched_folder_counts_as_not_reported()
    {
        using var document = JsonDocument.Parse(Rc5Answer);

        var folders = ProcessorConnectionService.ParseLibraryFolders(document.RootElement);

        Assert.Equal(@"D:\Downloads\Completed\Movies", ProcessorLibraryFolder.ForLibrary(folders, "movies-lib")?.WatchedFolder);
        var tv = ProcessorLibraryFolder.ForLibrary(folders, "tv-lib");
        Assert.Null(tv?.WatchedFolder);
        Assert.Equal(@"D:\Processed\TV", tv?.OutputFolder);
        Assert.Equal(@"D:\Processed\Movies", ProcessorLibraryFolder.ForLibrary(folders, "movies-lib")?.OutputFolder);
        Assert.Contains(folders, folder => folder.ManagerLibraryKey is null && folder.WatchedFolder == @"E:\Mine");
    }

    [Fact]
    public void A_hand_copied_duplicate_loses_to_the_entry_whose_folders_come_from_Deluno()
    {
        var folders = new[]
        {
            new ProcessorLibraryFolder("movies-lib", @"E:\Copy", FoldersFromManager: false),
            new ProcessorLibraryFolder("movies-lib", @"D:\Downloads\Completed\Movies", FoldersFromManager: true)
        };

        Assert.Equal(@"D:\Downloads\Completed\Movies", ProcessorLibraryFolder.ForLibrary(folders, "movies-lib")?.WatchedFolder);
        Assert.Equal(@"E:\Copy", ProcessorLibraryFolder.ForLibrary(folders.Take(1), "movies-lib")?.WatchedFolder);
    }
}
