using Deluno.Platform.Processing;

namespace Deluno.Platform.Tests.Processing;

/// <summary>
/// A completed processor event names the hand-off and the file as two separate
/// claims, and Deluno used to check them independently (#573).
///
/// <para><b>Why this file exists.</b> Being inside the library's output folder
/// says only that the processor wrote where it was told. It does not say the
/// file is the one this hand-off was waiting for. A processor could report
/// hand-off A and hand over another release's file, and Deluno would import it,
/// rename it to A's catalogue title, and mark A completed — the wrong film in
/// the library under the right name.</para>
///
/// <para>A processor says which download a file came from by keeping its name:
/// the folder it writes into, or the file itself. That is what the output-folder
/// watcher already correlates on, so this is the same rule and both ways in
/// agree.</para>
/// </summary>
public sealed class AnOutputBelongsToOneDownloadTests
{
    private const string Root = @"C:\Weir\Refined\Movies";
    private const string Source = @"C:\Downloads-Complete\Movies\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    [Theory]
    // What a processor that writes a folder per download produces.
    [InlineData(@"C:\Weir\Refined\Movies\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO.mkv")]
    // The same, renamed on the way out.
    [InlineData(@"C:\Weir\Refined\Movies\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO\output.mkv")]
    // Nested deeper, still under the download's own folder.
    [InlineData(@"C:\Weir\Refined\Movies\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO\remux\out.mkv")]
    // No folder of its own, but the file keeps the name.
    [InlineData(@"C:\Weir\Refined\Movies\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO.mkv")]
    // Case is not how a processor tells Deluno it means something else.
    [InlineData(@"C:\Weir\Refined\Movies\TEARS.OF.STEEL.2012.1080P.WEB-DL.DDP5.1.H.264-DELUNO\out.mkv")]
    public void An_output_that_keeps_the_downloads_name_is_about_that_download(string output)
        => Assert.True(ProcessorOutputPathPolicy.IsOutputAbout(output, Root, Source));

    [Theory]
    // The bug: another release's file, reported against this hand-off.
    [InlineData(@"C:\Weir\Refined\Movies\Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO\Big.Buck.Bunny.mkv")]
    // Nothing that names any download.
    [InlineData(@"C:\Weir\Refined\Movies\scratch\out.mkv")]
    // Inside the root but naming nothing.
    [InlineData(@"C:\Weir\Refined\Movies\out.mkv")]
    public void An_output_that_does_not_is_not(string output)
        => Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(output, Root, Source));

    /// <summary>The older boundary still holds: outside the folder is outside.</summary>
    [Theory]
    [InlineData(@"C:\Somewhere\Else\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO\out.mkv")]
    [InlineData(@"C:\Weir\Refined\Movies\..\..\Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO\out.mkv")]
    public void An_output_outside_the_libraries_folder_is_refused_however_it_is_named(string output)
    {
        Assert.False(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(output, Root));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(output, Root, Source));
    }

    /// <summary>A source that is a file, not a folder, is named the same way.</summary>
    [Fact]
    public void A_download_that_is_one_file_is_recognised_by_its_name_with_or_without_the_extension()
    {
        const string source = @"C:\Downloads-Complete\Movies\Sintel.2010.1080p.WEB-DL-DELUNO.mkv";
        Assert.True(ProcessorOutputPathPolicy.IsOutputAbout(@"C:\Weir\Refined\Movies\Sintel.2010.1080p.WEB-DL-DELUNO\out.mkv", Root, source));
        Assert.True(ProcessorOutputPathPolicy.IsOutputAbout(@"C:\Weir\Refined\Movies\Sintel.2010.1080p.WEB-DL-DELUNO.mkv", Root, source));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(@"C:\Weir\Refined\Movies\Elephants.Dream\out.mkv", Root, source));
    }

    [Fact]
    public void Nothing_is_about_a_download_Deluno_cannot_name()
    {
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(@"C:\Weir\Refined\Movies\a\out.mkv", Root, null));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(null, Root, Source));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(@"C:\Weir\Refined\Movies\a\out.mkv", null, Source));
    }
}
