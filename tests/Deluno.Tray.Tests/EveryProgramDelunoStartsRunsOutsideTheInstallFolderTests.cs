using System.Text.RegularExpressions;
using Deluno.Infrastructure.Processes;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1532: while NZBGet ran, Deluno's install folder could not be renamed and Velopack's Setup failed "Access is denied".
/// NZBGet was started with no working directory, so it stood in the tray's own folder (the install folder). Read from the
/// source: every ProcessStartInfo in the tray and the app sets a WorkingDirectory, and none of them is the install folder.
/// </summary>
public sealed partial class EveryProgramDelunoStartsRunsOutsideTheInstallFolderTests
{
    /// <summary>Sites that may stand in the install folder, each with its reason.</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["StartSupport.cs"] = "Starts Deluno's own host from its own folder; Setup stops the host before it renames the folder.",
    };

    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "Program.cs")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    private static IEnumerable<string> Files()
        => Directory.GetFiles(Path.Combine(Root, "apps"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static List<(string File, int Line, string Window)> Sites()
    {
        var sites = new List<(string, int, string)>();
        foreach (var path in Files())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//") || !NewStartInfo().IsMatch(lines[i]))
                {
                    continue;
                }

                sites.Add((Path.GetFileName(path), i + 1, string.Join('\n', lines.Skip(i).Take(14))));
            }
        }

        return sites;
    }

    [Fact]
    public void Every_ProcessStartInfo_Sets_A_WorkingDirectory()
    {
        var bare = Sites()
            .Where(site => !Allowed.ContainsKey(site.File) && !site.Window.Contains("WorkingDirectory"))
            .Select(site => $"{site.File}:{site.Line}")
            .ToList();

        Assert.True(bare.Count == 0, "These start a program with no WorkingDirectory, so it stands in the install folder: " + string.Join(", ", bare));
    }

    [Fact]
    public void No_WorkingDirectory_Is_The_Install_Folder()
    {
        var wrong = Sites()
            .Where(site => !Allowed.ContainsKey(site.File))
            .Where(site => WorkingDirectoryValue().Matches(site.Window).Any(m =>
                m.Value.Contains("AppContext.BaseDirectory") || m.Value.Contains("CurrentDirectory") || m.Value.Contains("StartupPath")))
            .Select(site => $"{site.File}:{site.Line}")
            .ToList();

        Assert.True(wrong.Count == 0, "These run a program in the install folder: " + string.Join(", ", wrong));
    }

    [Fact]
    public void Every_Allowed_Site_Still_Exists_And_Has_A_Reason()
    {
        var sites = Sites();
        foreach (var (file, reason) in Allowed)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains(sites, site => site.File == file);
        }
    }

    [Fact]
    public void A_Program_In_The_Install_Folder_Is_Run_Somewhere_Else()
    {
        var install = Path.Combine(Path.GetTempPath(), "deluno-1532-install");
        Directory.CreateDirectory(Path.Combine(install, "current"));
        var elsewhere = Path.Combine(Path.GetTempPath(), "deluno-1532-client");
        Directory.CreateDirectory(elsewhere);
        try
        {
            Assert.Equal(ProcessWorkingDirectory.Neutral, ProcessWorkingDirectory.For(Path.Combine(install, "current", "nzbget.exe"), install));
            Assert.Equal(elsewhere, ProcessWorkingDirectory.For(Path.Combine(elsewhere, "nzbget.exe"), install));
            Assert.Equal(ProcessWorkingDirectory.Neutral, ProcessWorkingDirectory.For(null, install));
            Assert.False(ProcessWorkingDirectory.IsInside(install + "-other", install));
        }
        finally
        {
            Directory.Delete(install, true);
            Directory.Delete(elsewhere, true);
        }
    }

    [Fact]
    public void The_Client_Starters_Use_The_Programs_Own_Folder()
    {
        foreach (var file in new[] { "ServiceProcessStarter.cs", "ComponentInstallService.cs" })
        {
            var text = File.ReadAllText(Path.Combine(Root, "apps", "windows-tray", file));
            Assert.Contains("ProcessWorkingDirectory.For(path", text);
        }
    }

    [GeneratedRegex(@"new\s+(System\.Diagnostics\.)?ProcessStartInfo\b")]
    private static partial Regex NewStartInfo();

    [GeneratedRegex(@"WorkingDirectory\s*=[^,;\n]*")]
    private static partial Regex WorkingDirectoryValue();
}
