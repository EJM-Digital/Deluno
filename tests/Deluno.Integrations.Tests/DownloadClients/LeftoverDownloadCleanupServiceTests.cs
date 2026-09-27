using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Deluno.Integrations.DownloadClients;
using Deluno.Platform.Contracts;
using Deluno.Platform.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Deluno.Integrations.Tests.DownloadClients;

/// <summary>
/// #932: the scheduled half of the leftovers list. Built on
/// <see cref="IOrphanedDownloadService"/> (proving "nothing claims this" is
/// its job already), this adds only what an unattended pass needs beyond the
/// manual click: the configurable age threshold, and never touching an item
/// still linked from anywhere else.
/// </summary>
public sealed class LeftoverDownloadCleanupServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T12:00:00Z");

    [Fact]
    public async Task Switched_off_removes_nothing_and_never_even_lists()
    {
        var orphans = new Mock<IOrphanedDownloadService>();
        var service = CreateService(orphans, enabled: false, ageHours: 24);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Held);
        orphans.Verify(instance => instance.ListAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task An_item_younger_than_the_age_threshold_is_held_back()
    {
        var young = Item("young.mkv", Now.AddHours(-10));
        var orphans = new Mock<IOrphanedDownloadService>();
        orphans.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([young]);
        var service = CreateService(orphans, enabled: true, ageHours: 24);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Held);
        orphans.Verify(instance => instance.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task An_item_older_than_the_age_threshold_is_removed_with_the_reason_said()
    {
        var old = Item("old.mkv", Now.AddHours(-30));
        var orphans = new Mock<IOrphanedDownloadService>();
        orphans.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([old]);
        orphans
            .Setup(instance => instance.RemoveAsync(old.Path, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new OrphanedDownloadAction(true, "Removed old.mkv."));
        var service = CreateService(orphans, enabled: true, ageHours: 24);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Equal(0, result.Held);
        orphans.Verify(
            instance => instance.RemoveAsync(
                old.Path,
                It.IsAny<CancellationToken>(),
                It.Is<string?>(reason => reason != null && reason.Contains("nothing is using it"))),
            Times.Once);
    }

    /// <summary>A configured age threshold of, say, one week is honoured rather than the fixed default.</summary>
    [Fact]
    public async Task A_configured_age_threshold_is_used_instead_of_the_default()
    {
        var item = Item("week-old.mkv", Now.AddDays(-6));
        var orphans = new Mock<IOrphanedDownloadService>();
        orphans.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([item]);
        var service = CreateService(orphans, enabled: true, ageHours: 24 * 7);

        var result = await service.RunAsync(CancellationToken.None);

        // Six days old, threshold is seven - still held.
        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Held);
    }

    /// <summary>Whatever RemoveAsync itself refuses (not Deluno's own grab, inside a library root, and so on) counts as held, not removed - the same rule stays the only rule.</summary>
    [Fact]
    public async Task An_item_the_shared_remove_rule_refuses_is_held_not_removed()
    {
        var item = Item("not-deluno.mkv", Now.AddDays(-3));
        var orphans = new Mock<IOrphanedDownloadService>();
        orphans.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([item]);
        orphans
            .Setup(instance => instance.RemoveAsync(item.Path, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(new OrphanedDownloadAction(false, "Deluno cannot show it downloaded not-deluno.mkv, so it will not remove it."));
        var service = CreateService(orphans, enabled: true, ageHours: 24);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Held);
    }

    /// <summary>
    /// A leftover still hardlinked to a library file is held back even though
    /// it is well past the age threshold - deleting this name would not free
    /// the disk space, since the same bytes are still reachable through the
    /// library's own copy.
    /// </summary>
    [Fact]
    public async Task A_leftover_still_hardlinked_elsewhere_is_held_regardless_of_age()
    {
        var directory = Directory.CreateTempSubdirectory("leftover-cleanup-hardlink-test-");
        try
        {
            var downloadCopy = Path.Combine(directory.FullName, "Still.Shared.mkv");
            var libraryCopy = Path.Combine(directory.FullName, "Still Shared.mkv");
            await File.WriteAllTextAsync(downloadCopy, "same bytes");
            CreateHardLink(downloadCopy, libraryCopy);

            var item = new OrphanedDownloadItem(downloadCopy, "Still.Shared.mkv", "library-1", "Library", 10, Now.AddDays(-10), DelunoGrabbedIt: true);
            var orphans = new Mock<IOrphanedDownloadService>();
            orphans.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([item]);
            var service = CreateService(orphans, enabled: true, ageHours: 24);

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(0, result.Removed);
            Assert.Equal(1, result.Held);
            orphans.Verify(instance => instance.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()), Times.Never);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>The same leftover once its only other name is gone: nothing else holds the bytes, so it is removed on the same pass.</summary>
    [Fact]
    public async Task A_leftover_with_no_other_name_left_is_removed()
    {
        var directory = Directory.CreateTempSubdirectory("leftover-cleanup-unlinked-test-");
        try
        {
            var downloadCopy = Path.Combine(directory.FullName, "Only.Copy.mkv");
            await File.WriteAllTextAsync(downloadCopy, "only copy");

            var item = new OrphanedDownloadItem(downloadCopy, "Only.Copy.mkv", "library-1", "Library", 9, Now.AddDays(-10), DelunoGrabbedIt: true);
            var orphans = new Mock<IOrphanedDownloadService>();
            orphans.Setup(instance => instance.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([item]);
            orphans
                .Setup(instance => instance.RemoveAsync(downloadCopy, It.IsAny<CancellationToken>(), It.IsAny<string?>()))
                .ReturnsAsync(new OrphanedDownloadAction(true, "Removed Only.Copy.mkv."));
            var service = CreateService(orphans, enabled: true, ageHours: 24);

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(1, result.Removed);
            Assert.Equal(0, result.Held);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static OrphanedDownloadItem Item(string name, DateTimeOffset lastWrittenUtc)
    {
        var path = Path.Combine(Path.GetTempPath(), $"leftover-cleanup-tests-{Guid.NewGuid():n}", name);
        return new OrphanedDownloadItem(path, name, "library-1", "Library", 4096, lastWrittenUtc, DelunoGrabbedIt: true);
    }

    private static LeftoverDownloadCleanupService CreateService(Mock<IOrphanedDownloadService> orphans, bool enabled, int ageHours)
    {
        var platform = new Mock<IPlatformSettingsRepository>();
        var settings = (PlatformSettingsSnapshot)RuntimeHelpers.GetUninitializedObject(typeof(PlatformSettingsSnapshot)) with
        {
            LeftoverDownloadCleanupEnabled = enabled,
            LeftoverDownloadCleanupAgeHours = ageHours
        };
        platform.Setup(repo => repo.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        var timeProvider = new FakeTimeProvider(Now);

        return new LeftoverDownloadCleanupService(
            orphans.Object,
            platform.Object,
            timeProvider,
            NullLogger<LeftoverDownloadCleanupService>.Instance);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Test-only hardlink creation - .NET has no public API for this, the same reason ImportPipelineService P/Invokes its own.</summary>
    private static void CreateHardLink(string existingPath, string newPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!WindowsCreateHardLink(newPath, existingPath, IntPtr.Zero))
            {
                throw new IOException($"Could not create a test hardlink (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            return;
        }

        using var process = Process.Start(new ProcessStartInfo("ln", [existingPath, newPath])
        {
            UseShellExecute = false,
            RedirectStandardError = true
        }) ?? throw new IOException("Could not start 'ln'.");
        process.WaitForExit(5000);
        if (process.ExitCode != 0)
        {
            throw new IOException($"'ln' failed: {process.StandardError.ReadToEnd()}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool WindowsCreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}
