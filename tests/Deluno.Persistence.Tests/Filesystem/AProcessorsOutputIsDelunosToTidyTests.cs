using Deluno.Filesystem;
using Deluno.Libraries.Contracts;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// A processor's output is removed after import even when the download is a
/// torrent.
///
/// <para><b>Why this file exists.</b> Found by the pre-soak hand-off proof. A
/// torrent that goes through a processor is imported from the processor's
/// output, not from the client's download. The import then declined to remove
/// that output "because the download client is still sharing it" - which the
/// client is not: it shares its own copy in the downloads folder and has never
/// heard of this one. The sharing rule removes through the client, so nothing
/// ever removed it, and every processed torrent left a full-size file behind
/// (10 GB on the rig, for one film).</para>
/// </summary>
public sealed class AProcessorsOutputIsDelunosToTidyTests
{
    [Fact]
    public void A_file_under_the_librarys_processor_output_folder_is_the_processors()
    {
        Assert.True(ImportPipelineService.IsProcessorOutput(
            @"C:\Deluno\Weir3\Refined\Movies\Big.Buck.Bunny.2008\Big.Buck.Bunny.2008.mkv",
            Library()));
    }

    /// <summary>This is the file deleting would sabotage: the torrent's own data (#287).</summary>
    [Fact]
    public void The_clients_own_download_is_not()
    {
        Assert.False(ImportPipelineService.IsProcessorOutput(
            @"C:\Deluno\Downloads-Complete\Movies\Big.Buck.Bunny.2008\Big.Buck.Bunny.2008.mkv",
            Library()));
    }

    [Fact]
    public void A_library_that_imports_directly_has_no_processor_output()
    {
        Assert.False(ImportPipelineService.IsProcessorOutput(
            @"C:\Deluno\Weir3\Refined\Movies\Big.Buck.Bunny.2008\Big.Buck.Bunny.2008.mkv",
            Library() with { ImportWorkflow = "standard" }));
    }

    /// <summary>
    /// A library whose output folder sits inside its downloads folder, or is the
    /// same folder, gives no way to tell the two files apart. The client's file
    /// is the one that must not be deleted, so the answer is no.
    /// </summary>
    [Fact]
    public void Overlapping_folders_never_count_as_processor_output()
    {
        var overlapping = Library() with
        {
            DownloadsPath = @"C:\Deluno\Downloads-Complete",
            ProcessorOutputPath = @"C:\Deluno\Downloads-Complete\Refined"
        };

        Assert.False(ImportPipelineService.IsProcessorOutput(@"C:\Deluno\Downloads-Complete\Refined\film\film.mkv", overlapping));
    }

    [Fact]
    public void A_folder_that_only_shares_a_prefix_is_a_different_folder()
    {
        Assert.False(ImportPipelineService.IsProcessorOutput(@"C:\Deluno\Weir3\Refined\Movies-Old\film.mkv", Library()));
    }

    /// <summary>#1151: the movie page said "Imported from your existing library" for a file that came from the processor's output.</summary>
    [Fact]
    public void A_file_from_the_processors_output_says_so_in_the_titles_last_attempt()
    {
        var library = Library() with { ProcessorName = "Weir" };

        Assert.Equal(
            "Imported from Weir's cleaned output.",
            ImportPipelineService.ImportOriginNote(@"C:\Deluno\Weir3\Refined\Movies\Big.Buck.Bunny.2008\Big.Buck.Bunny.2008.mkv", library, fromDownload: true));
    }

    [Fact]
    public void A_file_straight_from_a_download_says_so_and_a_file_already_in_the_library_keeps_the_default()
    {
        Assert.Equal("Imported from a download.", ImportPipelineService.ImportOriginNote(@"C:\Deluno\Downloads-Complete\Movies\Big.Buck.Bunny.2008\a.mkv", Library(), fromDownload: true));
        Assert.Null(ImportPipelineService.ImportOriginNote(@"C:\Movies\Big.Buck.Bunny.2008\a.mkv", Library(), fromDownload: false));
    }

    private static LibraryItem Library()
    {
        var library = (LibraryItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LibraryItem));
        return library with
        {
            Id = "library-1",
            Name = "Movies",
            MediaType = "movies",
            ImportWorkflow = "refine-before-import",
            DownloadsPath = @"C:\Deluno\Downloads-Complete\Movies",
            ProcessorOutputPath = @"C:\Deluno\Weir3\Refined\Movies"
        };
    }
}
