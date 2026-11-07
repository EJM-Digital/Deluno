using System.Text.RegularExpressions;
using Deluno.Infrastructure.Processes;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1532: while NZBGet ran, Deluno's install folder could not be renamed and Velopack's Setup failed "Access is denied".
/// NZBGet was started with no working directory, so it stood in the tray's own folder (the install folder). Read from the
/// source: every place that starts a program sets a real WorkingDirectory from <see cref="ProcessWorkingDirectory"/>, never
/// empty and never the install folder. Setup renames the install ROOT (the parent of <c>current</c>), so that is what is protected.
/// </summary>
public sealed partial class EveryProgramDelunoStartsRunsOutsideTheInstallFolderTests
{
    /// <summary>Specific call sites (file, and text on the starting line) that may stand in the install folder, each with its reason.</summary>
    private static readonly (string File, string Snippet, string Reason)[] Allowed =
    [
        ("StartSupport.cs", "new ProcessStartInfo(exePath, arguments)",
            "Relaunches Deluno.exe itself (--after-restart) from its own folder. Its exe is inside the install root, so Velopack stops it before it renames the folder."),
    ];

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

    private sealed record Site(string File, int Line, string Text, string Window);

    private static List<Site> Sites(Regex pattern)
    {
        var sites = new List<Site>();
        foreach (var path in Files())
        {
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//") || !pattern.IsMatch(lines[i]))
                {
                    continue;
                }

                var window = string.Join('\n', lines.Skip(i).Take(14).Where(line => !line.TrimStart().StartsWith("//")));
                sites.Add(new Site(Path.GetFileName(path), i + 1, lines[i], window));
            }
        }

        return sites;
    }

    private static bool IsAllowed(Site site)
        => Allowed.Any(a => a.File == site.File && site.Text.Contains(a.Snippet));

    private static List<Site> StartInfoSites() => Sites(NewStartInfo());

    [Fact]
    public void Every_ProcessStartInfo_Assigns_A_Real_WorkingDirectory_From_ProcessWorkingDirectory()
    {
        var bad = StartInfoSites()
            .Where(site => !IsAllowed(site))
            .Where(site => !WorkingDirectoryFromHelper().IsMatch(site.Window))
            .Select(site => $"{site.File}:{site.Line}")
            .ToList();

        Assert.True(bad.Count == 0, "These start a program without a WorkingDirectory taken from ProcessWorkingDirectory, so it can stand in the install folder: " + string.Join(", ", bad));
    }

    [Fact]
    public void No_WorkingDirectory_Falls_Back_To_Empty_Or_Null()
    {
        var bad = StartInfoSites()
            .Where(site => EmptyWorkingDirectory().IsMatch(site.Window))
            .Select(site => $"{site.File}:{site.Line}")
            .ToList();

        Assert.True(bad.Count == 0, "These can run a program with an empty WorkingDirectory, which inherits the install folder: " + string.Join(", ", bad));
    }

    [Fact]
    public void Nothing_Starts_A_Program_Any_Other_Way()
    {
        var other = Sites(OtherStartApis())
            .Select(site => $"{site.File}:{site.Line}")
            .ToList();

        Assert.True(other.Count == 0, "These start a program without a ProcessStartInfo that carries a WorkingDirectory: " + string.Join(", ", other));
    }

    [Fact]
    public void Every_Allowed_Site_Still_Exists_And_Has_A_Reason()
    {
        var sites = StartInfoSites();
        foreach (var (file, snippet, reason) in Allowed)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains(sites, site => site.File == file && site.Text.Contains(snippet));
        }
    }

    [Fact]
    public void The_Guard_Sees_The_Shapes_It_Is_Meant_To_Catch()
    {
        Assert.Matches(NewStartInfo(), "var a = new ProcessStartInfo(path, args);");
        Assert.Matches(NewStartInfo(), "ProcessStartInfo b = new();");
        Assert.Matches(OtherStartApis(), "Process.Start(\"notepad.exe\");");
        Assert.Matches(OtherStartApis(), "Process.Start(file, args);");
        Assert.Matches(OtherStartApis(), "var p = new Process { StartInfo = info };");
        Assert.DoesNotMatch(OtherStartApis(), "Process.Start(info);");
        Assert.DoesNotMatch(OtherStartApis(), "Process.Start(new ProcessStartInfo(path) { WorkingDirectory = x });");
        Assert.Matches(EmptyWorkingDirectory(), "WorkingDirectory = workingDirectory ?? \"\"");
        Assert.Matches(EmptyWorkingDirectory(), "WorkingDirectory = null");
    }

    [Fact]
    public void The_Install_Root_Is_The_Parent_Of_Current_And_Everything_Under_It_Is_Protected()
    {
        var root = Path.Combine(Path.GetTempPath(), "deluno-1532", "Deluno");
        var current = Path.Combine(root, "current");
        var packages = Path.Combine(root, "packages");
        var legacy = Path.Combine(root, "components", "nzbget");
        var elsewhere = Path.Combine(Path.GetTempPath(), "deluno-1532", "NZBGet");
        foreach (var folder in new[] { current, packages, legacy, elsewhere })
        {
            Directory.CreateDirectory(folder);
        }

        try
        {
            var installRoot = ProcessWorkingDirectory.InstallRoot(current + Path.DirectorySeparatorChar);
            Assert.Equal(root, installRoot);

            Assert.Equal(ProcessWorkingDirectory.Neutral, ProcessWorkingDirectory.For(Path.Combine(current, "Deluno.exe"), installRoot));
            Assert.Equal(ProcessWorkingDirectory.Neutral, ProcessWorkingDirectory.For(Path.Combine(packages, "x.exe"), installRoot));
            Assert.Equal(ProcessWorkingDirectory.Neutral, ProcessWorkingDirectory.For(Path.Combine(legacy, "nzbget.exe"), installRoot));
            Assert.Equal(ProcessWorkingDirectory.Neutral, ProcessWorkingDirectory.For(Path.Combine(root, "nzbget.exe"), installRoot));
            Assert.Equal(elsewhere, ProcessWorkingDirectory.For(Path.Combine(elsewhere, "nzbget.exe"), installRoot));
            Assert.Equal(ProcessWorkingDirectory.Neutral, ProcessWorkingDirectory.For(null, installRoot));
            Assert.False(ProcessWorkingDirectory.IsInside(root + "-other", root));
        }
        finally
        {
            Directory.Delete(Path.Combine(Path.GetTempPath(), "deluno-1532"), true);
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

    [GeneratedRegex(@"new\s+(System\.Diagnostics\.)?ProcessStartInfo\b|ProcessStartInfo\s+\w+\s*=\s*new\s*\(")]
    private static partial Regex NewStartInfo();

    /// <summary>Process.Start with a file name (not a ProcessStartInfo), and a Process built around a StartInfo.</summary>
    [GeneratedRegex(@"Process\.Start\(\s*(""|[A-Za-z_][\w.]*\s*,)|new\s+(System\.Diagnostics\.)?Process\s*\{[^}]*StartInfo")]
    private static partial Regex OtherStartApis();

    [GeneratedRegex(@"WorkingDirectory\s*=\s*[^;\n]*ProcessWorkingDirectory\.(Neutral|For\()")]
    private static partial Regex WorkingDirectoryFromHelper();

    [GeneratedRegex(@"WorkingDirectory\s*=\s*([^;\n]*\?\?\s*(""""|null|string\.Empty)|""""|null\b|string\.Empty)")]
    private static partial Regex EmptyWorkingDirectory();
}
