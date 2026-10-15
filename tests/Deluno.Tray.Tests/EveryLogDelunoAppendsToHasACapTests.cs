using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1335: the tray's start-up log was appended on every start with no size cap. Every log the tray writes goes through
/// <see cref="CappedLog"/>, and a scan of the program's source finds any file that is appended to some other way.
/// </summary>
[Collection("HookLogSeams")]
public sealed class EveryLogDelunoAppendsToHasACapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-cap-" + Guid.NewGuid().ToString("N"));

    public EveryLogDelunoAppendsToHasACapTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        CappedLog.MaxBytes = CappedLog.DefaultMaxBytes;
        HookLog.PrimaryFolderOverride = null;
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void A_log_past_its_cap_is_rolled_to_a_copy_and_a_new_file_is_started()
    {
        CappedLog.MaxBytes = 200;
        var path = Path.Combine(_root, "tray-startup.log");
        for (var line = 0; line < 100; line++)
        {
            CappedLog.Append(path, new string('x', 50) + Environment.NewLine);
        }

        Assert.True(new FileInfo(path).Length <= 200 + 60, "the live file stays near the cap");
        Assert.True(File.Exists(path + ".1"));
        Assert.True(new FileInfo(path + ".1").Length <= 200 + 60, "the copy is not larger than the cap either");
    }

    [Fact]
    public void The_start_up_log_the_tray_writes_on_every_start_is_capped()
    {
        CappedLog.MaxBytes = 300;
        HookLog.PrimaryFolderOverride = _root;
        for (var start = 0; start < 200; start++)
        {
            HookLog.Write("Deluno started on port 8080", "tray-startup.log");
        }

        var total = Directory.GetFiles(_root).Sum(file => new FileInfo(file).Length);
        Assert.True(total <= 2 * (300 + 120), $"tray-startup.log and its copy stay near twice the cap, not {total} bytes");
    }

    [Fact]
    public void A_line_is_never_lost_to_the_cap_when_the_copy_cannot_be_made()
    {
        CappedLog.MaxBytes = 10;
        var path = Path.Combine(_root, "held.log");
        File.WriteAllText(path, new string('y', 50));
        Directory.CreateDirectory(path + ".1"); // a folder where the copy should go: the move fails
        CappedLog.Append(path, "newest line" + Environment.NewLine);

        Assert.Contains("newest line", File.ReadAllText(path));
    }

    [Fact]
    public void No_log_file_in_the_program_is_appended_to_except_through_a_cap()
    {
        var offenders = new List<string>();
        foreach (var file in new[] { "apps", "src" }
                     .SelectMany(top => Directory.EnumerateFiles(Path.Combine(RepoRoot(), top), "*.cs", SearchOption.AllDirectories))
                     // The server's own file logger rolls by size and by days and prunes the folder (DelunoFileLogOptions).
                     .Where(path => Path.GetFileName(path) != "DelunoFileLogger.cs")
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                var appends = line.Contains("File.AppendAllText", StringComparison.Ordinal)
                              || line.Contains("File.AppendAllLines", StringComparison.Ordinal)
                              || line.Contains("File.AppendText", StringComparison.Ordinal)
                              || line.Contains("FileMode.Append", StringComparison.Ordinal);
                // The one append that is the cap itself.
                if (appends && !line.Contains("File.AppendAllText(path, text)", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{index + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "These append to a file without the cap: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_server_logger_the_host_writes_is_capped_by_size_and_by_days()
    {
        var options = new Deluno.Infrastructure.Observability.DelunoFileLogOptions();
        Assert.True(options.MaxFileBytes > 0 && options.MaxTotalBytes > 0 && options.RetainDays > 0);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "Program.cs")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
