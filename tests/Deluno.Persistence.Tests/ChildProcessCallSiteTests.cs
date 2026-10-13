using System.Text.RegularExpressions;

namespace Deluno.Persistence.Tests;

/// <summary>
/// #1258: every program Deluno starts has its output read as it arrives and a time limit, which is what
/// <c>ChildProcess</c> does. This keeps a new call site from quietly redirecting a pipe and not reading it.
/// </summary>
public sealed class ChildProcessCallSiteTests
{
    /// <summary>The only files that may redirect a child's output themselves, and why.</summary>
    private static readonly string[] Allowed =
    [
        // The helper itself.
        "src/Deluno.Infrastructure/Processes/ChildProcess.cs",
        // Streams ffmpeg's progress line by line while it runs and reads standard output on its own task; bounded by a limit and the caller's token.
        "src/Deluno.Filesystem/Subtitles/FfmpegSpeechDetector.cs",
    ];

    [Fact]
    public void Only_the_helper_and_the_listener_redirect_a_childs_output()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        foreach (var folder in new[] { "src", "apps" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/") || Allowed.Contains(relative))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                if (Regex.IsMatch(text, @"Redirect(StandardOutput|StandardError)\s*=\s*true") || Regex.IsMatch(text, @"StandardOutput\.ReadToEnd\(\)|StandardError\.ReadToEnd\(\)"))
                {
                    offenders.Add(relative);
                }
            }
        }

        Assert.True(offenders.Count == 0, "These start a program and read its output themselves; use ChildProcess so no pipe is left unread and the wait has a limit: " + string.Join(", ", offenders));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deluno.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
