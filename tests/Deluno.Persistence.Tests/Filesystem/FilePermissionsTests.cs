using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The one place that applies the Import Policy "Set Permissions" setting
/// (#766) to a file — shared by the import path and by
/// <see cref="SubtitleFileWriter"/> (#766, #838).
/// </summary>
public sealed class FilePermissionsTests
{
    /// <summary>
    /// #766's audit gap: earlier tests skipped themselves on Windows rather
    /// than proving the guard runs there. This is the opposite test — it
    /// only runs <i>on</i> Windows, and asserts that "set permissions" turned
    /// on still does nothing and never throws. <c>File.SetUnixFileMode</c>
    /// throws <c>PlatformNotSupportedException</c> on Windows, so a guard
    /// that failed to skip would fail this test rather than silently doing
    /// the wrong thing.
    /// </summary>
    [Fact]
    public async Task Windows_skips_the_configured_chmod_even_when_the_setting_is_on()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            var settings = FakePlatformSettingsRepository.Default(setPermissionsOnImport: true, folderChmod: "700");

            await FilePermissions.ApplyAsync(path, settings, NullLogger.Instance, CancellationToken.None);

            // Nothing thrown is the assertion: PlatformNotSupportedException
            // from a guard that failed to skip would have failed this test
            // before reaching here.
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Applies_the_configured_chmod_on_non_Windows_hosts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            var settings = FakePlatformSettingsRepository.Default(setPermissionsOnImport: true, folderChmod: "700");

            await FilePermissions.ApplyAsync(path, settings, NullLogger.Instance, CancellationToken.None);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Leaves_permissions_untouched_when_the_setting_is_off()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var settings = FakePlatformSettingsRepository.Default(setPermissionsOnImport: false, folderChmod: "700");

            await FilePermissions.ApplyAsync(path, settings, NullLogger.Instance, CancellationToken.None);

            Assert.NotEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
