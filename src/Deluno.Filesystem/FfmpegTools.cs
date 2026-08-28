namespace Deluno.Filesystem;

/// <summary>
/// Where FFmpeg's two executables are, answered once.
///
/// <para><b>Why this is a type and not a private method.</b> The resolution
/// order lived inside <see cref="FfprobeMediaProbeService"/> while ffprobe was
/// the only binary Deluno ran. Timing sync needs <c>ffmpeg</c> as well, and a
/// second private copy of "check the environment variable, then beside the
/// executable, then the PATH" is the exact shape every defect in this codebase
/// has had: one rule written twice in two places that cannot check each other.
/// The two would have drifted the first time an install put them somewhere
/// new.</para>
///
/// <para><b>They travel together.</b> Deluno ships an LGPL <i>shared</i> build,
/// so the executables are useless without the DLLs beside them — which is why
/// the bundled location is a folder of its own rather than the application
/// directory, and why the override that matters is a directory rather than two
/// unrelated file paths. <c>scripts/fetch-ffmpeg.ps1</c> fills that folder and
/// the publish copies it.</para>
/// </summary>
public static class FfmpegTools
{
    /// <summary>
    /// The folder the publish puts FFmpeg in, relative to the application.
    /// Named here because the publish script and the resolver have to agree and
    /// only one of them can be tested.
    /// </summary>
    public const string BundledFolder = "tools/ffmpeg";

    /// <summary>Point Deluno at a folder holding both executables.</summary>
    public const string DirectoryVariable = "DELUNO_FFMPEG_DIR";

    /// <summary>
    /// The full path to <c>ffprobe</c>, or <c>null</c> when nothing anywhere
    /// answers to that name.
    ///
    /// <para>Null rather than the bare name is deliberate: "we did not look"
    /// and "we looked and it is not there" are different facts, and
    /// <see cref="SubtitleInventory.ProbeStatus"/> already carries that
    /// distinction to the screen.</para>
    /// </summary>
    public static string? Ffprobe() => Resolve("ffprobe", "DELUNO_FFPROBE_PATH");

    /// <summary>The full path to <c>ffmpeg</c>, or <c>null</c>.</summary>
    public static string? Ffmpeg() => Resolve("ffmpeg", "DELUNO_FFMPEG_PATH");

    private static string? Resolve(string name, string fileVariable)
    {
        var executable = OperatingSystem.IsWindows() ? name + ".exe" : name;

        // 1. This exact binary, named outright. The oldest override, kept
        //    because an existing install may be relying on it.
        var configuredFile = Environment.GetEnvironmentVariable(fileVariable);
        if (!string.IsNullOrWhiteSpace(configuredFile) && File.Exists(configuredFile))
        {
            return configuredFile;
        }

        // 2. A folder holding both, which is what a shared build actually is.
        var configuredDirectory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            var candidate = Path.Combine(configuredDirectory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 3. Bundled by the publish.
        var bundled = Path.Combine(AppContext.BaseDirectory, BundledFolder.Replace('/', Path.DirectorySeparatorChar), executable);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        // 4. Loose beside the application. Where the Windows installer used to
        //    put ffprobe, before there were DLLs to keep with it.
        var alongside = Path.Combine(AppContext.BaseDirectory, executable);
        if (File.Exists(alongside))
        {
            return alongside;
        }

        // 5. The PATH. Resolved here rather than handed to the process start
        //    as a bare name, so a caller can tell an absent binary from a
        //    failing one without starting a process to find out.
        return SearchPath(executable);
    }

    private static string? SearchPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, executable);
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid characters in it. Somebody else's
                // problem; skip it rather than fail every probe on the machine.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
