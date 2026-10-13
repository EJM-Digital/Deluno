using System.Text.Json;
using Deluno.Integrations.Processors;
using Deluno.Tests.Recorded;
using Deluno.Platform.Contracts;

namespace Deluno.Integrations.Tests.Processors;

/// <summary>
/// #1144: a processor's library-folders list, read in the shape Weir publishes it (an object holding a "libraries" array; Weir
/// 1.0.0-rc.13's recording, #1237), and the entry chosen for a library when a workflow was copied by hand.
/// </summary>
public sealed class ProcessorLibraryFoldersTests
{
    private const string MoviesKey = "01a127b7c0c9718d90fc38b21d1b9daf";
    private const string TvKey = "01a127b7c0eb7b108e7ec900f36acb11";

    /// <summary>
    /// Weir's recorded answer (a Movies and a TV library, both with folders from the manager), with the TV library's watched folder empty
    /// and a third library that Weir keeps for itself added by copying the first and changing it.
    /// </summary>
    private static string Answer()
        => RecordedReplies.Weir("library-folders").Body(
            "library-folders",
            ("libraries.1.watched_folder", string.Empty),
            ("+libraries", 3),
            ("libraries.2.name", "Weir only"), ("libraries.2.watched_folder", @"E:\Mine"), ("libraries.2.output_folder", @"E:\Out"),
            ("libraries.2.manager_library_key", null), ("libraries.2.folders_from_manager", false));

    [Fact]
    public void The_rc5_answer_is_read_and_an_empty_watched_folder_counts_as_not_reported()
    {
        using var document = JsonDocument.Parse(Answer());

        var folders = ProcessorConnectionService.ParseLibraryFolders(document.RootElement);

        Assert.Equal(@"C:\Downloads\Completed\Movies", ProcessorLibraryFolder.ForLibrary(folders, MoviesKey)?.WatchedFolder);
        var tv = ProcessorLibraryFolder.ForLibrary(folders, TvKey);
        Assert.Null(tv?.WatchedFolder);
        Assert.Equal(@"C:\Weir\Ready\TV", tv?.OutputFolder);
        Assert.Equal(@"C:\Weir\Ready\Movies", ProcessorLibraryFolder.ForLibrary(folders, MoviesKey)?.OutputFolder);
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
