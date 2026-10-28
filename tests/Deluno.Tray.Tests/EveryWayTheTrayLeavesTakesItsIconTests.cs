using System.Text.RegularExpressions;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// The rc.30.36 scenario suite (28 Oct): after "Run as a Windows service..." the tray process ended, but its icon was still
/// in the notification area. The tray is never disposed after Application.Run, so an icon not hidden before
/// Application.Exit stays there, dead, until the pointer passes over it. Quit and the restart already hid it; the switch
/// to a service did not. Every way the tray leaves hides its icon first.
/// </summary>
public sealed class EveryWayTheTrayLeavesTakesItsIconTests
{
    private static string Source(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "Program.cs")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(Path.Combine(dir!.FullName, "apps", "windows-tray", file));
    }

    [Fact]
    public void Each_Application_Exit_in_the_tray_is_preceded_by_hiding_its_icon()
    {
        var source = Source("TrayApplication.cs");
        var exits = Regex.Matches(source, @"Application\.Exit\(\);").Select(match => match.Index).ToList();

        Assert.True(exits.Count >= 3, $"expected Quit, the restart and the switch to a service; found {exits.Count}");
        foreach (var at in exits)
        {
            // The statements just before the exit, back to the method it is in.
            var before = source[Math.Max(0, at - 600)..at];
            var start = before.LastIndexOf("private ", StringComparison.Ordinal);
            var body = start >= 0 ? before[start..] : before;
            var line = source[..at].Count(c => c == '\n') + 1;
            Assert.True(body.Contains("_notify.Visible = false", StringComparison.Ordinal), $"TrayApplication.cs:{line} exits without hiding the icon");
        }
    }
}
