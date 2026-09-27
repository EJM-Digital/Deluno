using System.Text;
using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Media;

/// <summary>
/// #838: a written subtitle is always UTF-8, whatever a provider actually
/// sent, and the same "Set Permissions" setting an imported media file gets
/// (#766) is applied to it too — on non-Windows hosts only.
/// </summary>
public sealed class SubtitleFileWriterEncodingAndPermissionsTests
{
    [Fact]
    public async Task A_windows_1252_subtitle_is_re_encoded_to_utf8_on_write()
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not really a film");

        // "café" in Windows-1252: the é is 0xE9, one byte — invalid as UTF-8
        // on its own, which is exactly what makes this provider payload not
        // already UTF-8.
        var windows1252 = new byte[] { 0x31, 0x0D, 0x0A, 0x63, 0x61, 0x66, 0xE9 };

        var writer = new SubtitleFileWriter(NullLogger<SubtitleFileWriter>.Instance, FakePlatformSettingsRepository.With());
        var written = await writer.WriteAsync(video, "en", false, windows1252, CancellationToken.None);

        var bytesOnDisk = await File.ReadAllBytesAsync(written);
        // Valid UTF-8 with no BOM: decoding strictly must not throw, and the
        // café spelling must survive the round trip rather than becoming a
        // replacement character.
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytesOnDisk);
        Assert.Equal("1\r\ncafé", text);
    }

    [Fact]
    public async Task A_subtitle_already_in_utf8_is_written_unchanged_in_substance()
    {
        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not really a film");

        var utf8 = "1\r\n00:00:01,000 --> 00:00:03,000\r\nCafé, naïve.\r\n"u8.ToArray();

        var writer = new SubtitleFileWriter(NullLogger<SubtitleFileWriter>.Instance, FakePlatformSettingsRepository.With());
        var written = await writer.WriteAsync(video, "en", false, utf8, CancellationToken.None);

        Assert.Equal("Café, naïve.", (await File.ReadAllLinesAsync(written))[2]);
    }

    [Fact]
    public async Task Applies_the_configured_chmod_to_a_written_subtitle_on_non_Windows_hosts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not really a film");

        var writer = new SubtitleFileWriter(
            NullLogger<SubtitleFileWriter>.Instance,
            FakePlatformSettingsRepository.With(setPermissionsOnImport: true, folderChmod: "700"));

        var written = await writer.WriteAsync(video, "en", false, "1\r\nHello.\r\n"u8.ToArray(), CancellationToken.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(written));
    }

    [Fact]
    public async Task Leaves_a_written_subtitles_permissions_untouched_when_the_setting_is_off()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not really a film");

        var writer = new SubtitleFileWriter(
            NullLogger<SubtitleFileWriter>.Instance,
            FakePlatformSettingsRepository.With(setPermissionsOnImport: false, folderChmod: "700"));

        var written = await writer.WriteAsync(video, "en", false, "1\r\nHello.\r\n"u8.ToArray(), CancellationToken.None);

        Assert.NotEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(written));
    }

    /// <summary>
    /// #766's audit gap, for the subtitle writer specifically: proves the
    /// Windows guard runs rather than merely skipping the test on Windows.
    /// </summary>
    [Fact]
    public async Task Windows_skips_chmod_on_a_written_subtitle_even_when_the_setting_is_on()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var folder = new TemporaryFolder();
        var video = Path.Combine(folder.Path, "Dune.mkv");
        await File.WriteAllTextAsync(video, "not really a film");

        var writer = new SubtitleFileWriter(
            NullLogger<SubtitleFileWriter>.Instance,
            FakePlatformSettingsRepository.With(setPermissionsOnImport: true, folderChmod: "700"));

        // Nothing thrown is the assertion: File.SetUnixFileMode throws
        // PlatformNotSupportedException on Windows, so a guard that failed
        // to skip would fail this test before reaching here.
        await writer.WriteAsync(video, "en", false, "1\r\nHello.\r\n"u8.ToArray(), CancellationToken.None);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("deluno-subtitles").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { /* a test folder */ }
        }
    }
}
