using System.Diagnostics;
using Deluno.Filesystem;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>A fact that is skipped, with its reason, where there are no two real mounts to test on (CI makes them on its Linux runner).</summary>
public sealed class RequiresTwoMountsFactAttribute : FactAttribute
{
    public RequiresTwoMountsFactAttribute()
    {
        if (!OperatingSystem.IsLinux()
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DELUNO_TWO_MOUNTS_A"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DELUNO_TWO_MOUNTS_B")))
        {
            Skip = "Needs two tmpfs mounts on Linux (CI makes them and sets DELUNO_TWO_MOUNTS_A and DELUNO_TWO_MOUNTS_B).";
        }
    }
}

/// <summary>
/// A hard link cannot cross file systems. On Linux (the Docker image) every path is rooted at "/", so the import preview used
/// to promise a hard link between a downloads mount and a media mount that link() then refused. The preview now compares the
/// mounted roots (the same rule as the library health row, #1464).
/// </summary>
public sealed class CanLikelyHardlinkAcrossMountsTests
{
    private static readonly string[] ContainerMounts = ["/", "/data", "/downloads", "/media"];

    [Fact]
    public void Two_folders_on_different_mounts_of_one_root_are_not_promised_a_hard_link()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // these are Linux paths; the Windows twin is below
        }

        Assert.False(ImportPipelineService.CanLikelyHardlink("/downloads/Film.2026/Film.mkv", "/media/movies/Film (2026)/Film.mkv", ContainerMounts));
    }

    [Fact]
    public void Two_folders_on_one_mount_are_promised_a_hard_link()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // these are Linux paths; the Windows twin is below
        }

        Assert.True(ImportPipelineService.CanLikelyHardlink("/media/downloads/Film.mkv", "/media/movies/Film (2026)/Film.mkv", ContainerMounts));
        Assert.True(ImportPipelineService.CanLikelyHardlink("/downloads/a/Film.mkv", "/downloads/b/Film.mkv", ContainerMounts));
    }

    [Fact]
    public void A_mount_is_matched_on_a_folder_boundary_so_downloads2_is_not_on_the_downloads_mount()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // these are Linux paths; the Windows twin is below
        }

        Assert.False(ImportPipelineService.CanLikelyHardlink("/downloads2/Film.mkv", "/downloads/Film.mkv", ContainerMounts));
    }

    [Fact]
    public void A_folder_on_no_listed_mount_falls_back_to_the_path_root_as_before()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // these are Linux paths; the Windows twin is below
        }

        Assert.True(ImportPipelineService.CanLikelyHardlink("/srv/a/Film.mkv", "/srv/b/Film.mkv", ["/data"]));
    }

    [Fact]
    public void On_a_listed_drive_both_folders_are_one_volume_and_another_drive_is_not()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(ImportPipelineService.CanLikelyHardlink(@"C:\Downloads\Film.mkv", @"C:\Media\Film.mkv", [@"C:\", @"D:\"]));
        Assert.False(ImportPipelineService.CanLikelyHardlink(@"C:\Downloads\Film.mkv", @"D:\Media\Film.mkv", [@"C:\", @"D:\"]));
    }

    // CI makes two real tmpfs mounts under one root and names them (the "Backend" job, ci.yml); elsewhere the test is skipped.
    private static readonly string? MountA = OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("DELUNO_TWO_MOUNTS_A") : null;
    private static readonly string? MountB = OperatingSystem.IsLinux() ? Environment.GetEnvironmentVariable("DELUNO_TWO_MOUNTS_B") : null;

    [RequiresTwoMountsFact]
    public void On_two_real_tmpfs_mounts_the_preview_agrees_with_what_the_operating_system_does()
    {
        if (string.IsNullOrEmpty(MountA) || string.IsNullOrEmpty(MountB))
        {
            return;
        }

        var inA = Path.Combine(MountA, "Film.mkv");
        var alsoInA = Path.Combine(MountA, "Film-link.mkv");
        var inB = Path.Combine(MountB, "Film.mkv");
        File.WriteAllText(inA, "a film");

        // What the operating system does: a link on the same mount works, one across the mounts is refused.
        Assert.Equal(0, Link(inA, alsoInA));
        Assert.NotEqual(0, Link(inA, inB));

        // What the preview promises agrees with it.
        Assert.True(ImportPipelineService.CanLikelyHardlink(inA, alsoInA));
        Assert.False(ImportPipelineService.CanLikelyHardlink(inA, inB));
    }

    private static int Link(string existing, string link)
    {
        using var process = Process.Start(new ProcessStartInfo("ln", [existing, link]) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        process.WaitForExit(30_000);
        return process.ExitCode;
    }
}
