using Deluno.Filesystem;
using Deluno.Persistence.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The chgrp step of "Set permissions": a group that does not exist, a non-zero exit and an invalid octal chmod string are each a logged
/// warning with the file intact, never a crash. (Unix only: Windows skips the whole step, which FilePermissionsTests proves.)
/// </summary>
public sealed class ChgrpAndChmodWarnAndCarryOnTests
{
    private sealed class Capture : ILogger
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task A_group_that_does_not_exist_is_a_warning_and_the_file_is_intact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "the film");
            var log = new Capture();
            var settings = FakePlatformSettingsRepository.Default(setPermissionsOnImport: true, chownGroup: "deluno-no-such-group");

            await FilePermissions.ApplyAsync(path, settings, log, CancellationToken.None);

            var warning = Assert.Single(log.Lines, line => line.Level == LogLevel.Warning);
            Assert.Contains("deluno-no-such-group", warning.Message);
            Assert.True(
                warning.Message.Contains("exited with code", StringComparison.Ordinal) || warning.Message.Contains("binary was not found", StringComparison.Ordinal),
                warning.Message);
            Assert.Equal("the film", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("rwx")]
    [InlineData("999")]
    [InlineData("77777777777777")]
    public async Task An_invalid_octal_chmod_string_is_a_warning_not_a_crash_and_the_file_is_intact(string chmod)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "the film");
            var before = File.GetUnixFileMode(path);
            var log = new Capture();
            var settings = FakePlatformSettingsRepository.Default(setPermissionsOnImport: true, folderChmod: chmod);

            await FilePermissions.ApplyAsync(path, settings, log, CancellationToken.None);

            var warning = Assert.Single(log.Lines, line => line.Level == LogLevel.Warning);
            Assert.Contains("Could not set permissions", warning.Message);
            Assert.Equal(before, File.GetUnixFileMode(path));
            Assert.Equal("the film", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_chmod_that_fails_still_lets_the_chgrp_step_run()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            var log = new Capture();
            var settings = FakePlatformSettingsRepository.Default(setPermissionsOnImport: true, folderChmod: "rwx", chownGroup: "deluno-no-such-group");

            await FilePermissions.ApplyAsync(path, settings, log, CancellationToken.None);

            Assert.Equal(2, log.Lines.Count(line => line.Level == LogLevel.Warning));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
