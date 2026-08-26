using Deluno.Recovery.Policies;

namespace Deluno.Persistence.Tests.Integrations;

/// <summary>
/// Whether a download that is still being shared costs any disk (#288).
///
/// The dashboard reports gigabytes against these titles, so getting it wrong is
/// not cosmetic: overstating frightens people whose two copies are one file,
/// and understating lets a drive fill while Deluno says everything is fine.
/// </summary>
public sealed class SharingFootprintTests
{
    private const string DownloadsOnC = @"C:\Deluno\e2e\Sintel.mkv";
    private const string LibraryOnC = @"C:\Deluno\Movies";
    private const string LibraryOnD = @"D:\Media\Movies";

    [Fact]
    public void Same_drive_with_single_copy_links_costs_nothing()
    {
        Assert.True(SharingFootprint.SharesOneCopy(DownloadsOnC, LibraryOnC, useHardlinks: true));
        Assert.Null(SharingFootprint.Describe(DownloadsOnC, LibraryOnC, useHardlinks: true));
    }

    [Fact]
    public void Different_drives_cannot_share_one_copy_however_it_is_configured()
    {
        Assert.False(SharingFootprint.SharesOneCopy(DownloadsOnC, LibraryOnD, useHardlinks: true));

        var note = SharingFootprint.Describe(DownloadsOnC, LibraryOnD, useHardlinks: true);

        Assert.NotNull(note);
        Assert.Contains("C:", note);
        Assert.Contains("D:", note);
        // The point of the sentence is the consequence, not the drive letters.
        Assert.Contains("takes its own space", note);
    }

    [Fact]
    public void Same_drive_without_single_copy_links_says_what_to_change()
    {
        Assert.False(SharingFootprint.SharesOneCopy(DownloadsOnC, LibraryOnC, useHardlinks: false));

        var note = SharingFootprint.Describe(DownloadsOnC, LibraryOnC, useHardlinks: false);

        Assert.NotNull(note);
        Assert.Contains("Keep seeding without a second copy", note);
    }

    /// <summary>
    /// A path Deluno cannot read is not the same answer as "different drives".
    /// It reports the space as used — the safe direction — but never invents a
    /// sentence claiming to know where the files are.
    /// </summary>
    [Theory]
    [InlineData(null, LibraryOnC)]
    [InlineData(DownloadsOnC, null)]
    [InlineData("   ", LibraryOnC)]
    public void An_unknown_path_makes_no_claim(string? downloadPath, string? libraryPath)
    {
        Assert.False(SharingFootprint.SharesOneCopy(downloadPath, libraryPath, useHardlinks: true));
        Assert.Null(SharingFootprint.Describe(downloadPath, libraryPath, useHardlinks: true));
    }
}
