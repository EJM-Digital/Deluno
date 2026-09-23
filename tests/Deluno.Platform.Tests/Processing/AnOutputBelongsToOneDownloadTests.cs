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
///
/// <para>Paths are built rather than written out, because the backend CI runs
/// on Linux and a literal <c>C:\…</c> is one long segment there rather than a
/// path.</para>
/// </summary>
public sealed class AnOutputBelongsToOneDownloadTests
{
    private const string Ours = "Tears.of.Steel.2012.1080p.WEB-DL.DDP5.1.H.264-DELUNO";
    private const string Theirs = "Big.Buck.Bunny.2008.1080p.WEB-DL.DDP5.1.H.264-DELUNO";

    private static readonly string Root = OperatingSystem.IsWindows()
        ? @"C:\Weir\Refined\Movies"
        : "/weir/refined/movies";

    private static readonly string Source = Path.Combine(Downloads, Ours);

    private static string Downloads => OperatingSystem.IsWindows()
        ? @"C:\Downloads-Complete\Movies"
        : "/downloads-complete/movies";

    private static string InRoot(params string[] parts) => Path.Combine([Root, .. parts]);

    public static TheoryData<string> Keeps => new(
    [
        // What a processor that writes a folder per download produces.
        InRoot(Ours, $"{Ours}.mkv"),
        // The same, renamed on the way out.
        InRoot(Ours, "output.mkv"),
        // Nested deeper, still under the download's own folder.
        InRoot(Ours, "remux", "out.mkv"),
        // No folder of its own, but the file keeps the name.
        InRoot($"{Ours}.mkv"),
        // Case is not how a processor tells Deluno it means something else.
        InRoot(Ours.ToUpperInvariant(), "out.mkv")
    ]);

    public static TheoryData<string> DoesNot => new(
    [
        // The bug: another release's file, reported against this hand-off.
        InRoot(Theirs, $"{Theirs}.mkv"),
        // Nothing that names any download.
        InRoot("scratch", "out.mkv"),
        // Inside the root but naming nothing.
        InRoot("out.mkv")
    ]);

    [Theory]
    [MemberData(nameof(Keeps))]
    public void An_output_that_keeps_the_downloads_name_is_about_that_download(string output)
        => Assert.True(ProcessorOutputPathPolicy.IsOutputAbout(output, Root, Source));

    [Theory]
    [MemberData(nameof(DoesNot))]
    public void An_output_that_does_not_is_not(string output)
        => Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(output, Root, Source));

    /// <summary>The older boundary still holds: outside the folder is outside.</summary>
    [Fact]
    public void An_output_outside_the_libraries_folder_is_refused_however_it_is_named()
    {
        var elsewhere = Path.Combine(Downloads, Ours, "out.mkv");
        Assert.False(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(elsewhere, Root));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(elsewhere, Root, Source));

        var climbedOut = Path.Combine(Root, "..", "..", Ours, "out.mkv");
        Assert.False(ProcessorOutputPathPolicy.IsOutputOwnedByLibrary(climbedOut, Root));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(climbedOut, Root, Source));
    }

    /// <summary>A source that is a file, not a folder, is named the same way.</summary>
    [Fact]
    public void A_download_that_is_one_file_is_recognised_by_its_name_with_or_without_the_extension()
    {
        var source = Path.Combine(Downloads, "Sintel.2010.1080p.WEB-DL-DELUNO.mkv");
        Assert.True(ProcessorOutputPathPolicy.IsOutputAbout(InRoot("Sintel.2010.1080p.WEB-DL-DELUNO", "out.mkv"), Root, source));
        Assert.True(ProcessorOutputPathPolicy.IsOutputAbout(InRoot("Sintel.2010.1080p.WEB-DL-DELUNO.mkv"), Root, source));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(InRoot("Elephants.Dream", "out.mkv"), Root, source));
    }

    [Fact]
    public void Nothing_is_about_a_download_Deluno_cannot_name()
    {
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(InRoot("a", "out.mkv"), Root, null));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(null, Root, Source));
        Assert.False(ProcessorOutputPathPolicy.IsOutputAbout(InRoot("a", "out.mkv"), null, Source));
    }
}
