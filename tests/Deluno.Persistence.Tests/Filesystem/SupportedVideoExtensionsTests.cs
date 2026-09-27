using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The one video-extension list ImportPipelineService, ExistingLibraryImportService,
/// FilesystemReconciliationService, SubtitleInventoryService and
/// ManualImportFolderScanner all read from now, instead of five separately
/// maintained copies that had already drifted (#917's repetition fix).
/// </summary>
public sealed class SupportedVideoExtensionsTests
{
    [Theory]
    [InlineData("Movie.mkv")]
    [InlineData("Movie.mp4")]
    [InlineData("Movie.m4v")]
    [InlineData("Movie.avi")]
    [InlineData("Movie.mov")]
    [InlineData("Movie.wmv")]
    [InlineData("Movie.ts")]
    [InlineData("Movie.m2ts")]
    [InlineData("Movie.MKV")]
    public void Recognises_every_extension_every_prior_copy_recognised(string fileName)
    {
        Assert.True(SupportedVideoExtensions.Contains(fileName));
        Assert.Contains(Path.GetExtension(fileName), SupportedVideoExtensions.Extensions, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Movie.srt")]
    [InlineData("Movie.nfo")]
    [InlineData("Movie.txt")]
    [InlineData("")]
    [InlineData(null)]
    public void Does_not_recognise_a_non_video_file(string? fileName)
    {
        Assert.False(SupportedVideoExtensions.Contains(fileName));
    }
}
