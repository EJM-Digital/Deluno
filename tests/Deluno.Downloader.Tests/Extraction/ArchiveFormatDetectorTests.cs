using Deluno.Downloader.Extraction;

namespace Deluno.Downloader.Tests.Extraction;

public class ArchiveFormatDetectorTests
{
    [Theory]
    [InlineData("release.zip", ArchiveFormat.Zip)]
    [InlineData("release.7z", ArchiveFormat.SevenZip)]
    [InlineData("release.tar.gz", ArchiveFormat.TarGz)]
    [InlineData("release.tgz", ArchiveFormat.TarGz)]
    [InlineData("release.tar.bz2", ArchiveFormat.TarBz2)]
    [InlineData("release.tbz2", ArchiveFormat.TarBz2)]
    [InlineData("release.tar", ArchiveFormat.Tar)]
    [InlineData("release.rar", ArchiveFormat.Rar)]
    [InlineData("release.part1.rar", ArchiveFormat.Rar)]
    [InlineData("release.part01.rar", ArchiveFormat.Rar)]
    [InlineData("release.r00", ArchiveFormat.Rar)]
    [InlineData("release.r17", ArchiveFormat.Rar)]
    [InlineData("release.bin", ArchiveFormat.Unknown)]
    [InlineData("just-a-name", ArchiveFormat.Unknown)]
    public void Extension_detection_handles_common_release_layouts(string path, ArchiveFormat expected)
    {
        Assert.Equal(expected, ArchiveFormatDetector.DetectByExtension(path));
    }

    [Fact]
    public async Task Magic_bytes_recognise_zip()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0 });
            Assert.Equal(ArchiveFormat.Zip, await ArchiveFormatDetector.DetectByMagicAsync(tmp, CancellationToken.None));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task Magic_bytes_recognise_7z()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 0 });
            Assert.Equal(ArchiveFormat.SevenZip, await ArchiveFormatDetector.DetectByMagicAsync(tmp, CancellationToken.None));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task Magic_bytes_recognise_rar5()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 });
            Assert.Equal(ArchiveFormat.Rar, await ArchiveFormatDetector.DetectByMagicAsync(tmp, CancellationToken.None));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task Unknown_magic_returns_unknown()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0 });
            Assert.Equal(ArchiveFormat.Unknown, await ArchiveFormatDetector.DetectByMagicAsync(tmp, CancellationToken.None));
        }
        finally { File.Delete(tmp); }
    }
}
