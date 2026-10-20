using System.Text.RegularExpressions;

namespace Deluno.Tray.Tests;

/// <summary>
/// A browser opens only when the person clicks something (the icon, a menu item, a balloon, the picker's button). Start-up,
/// sign-in start, an update restart, a port change and a second launch never open one. Read from the tray's own source: every
/// place that can open a page is found, and every way into it is a click handler. The golden-path scenarios check the same
/// thing on a real machine by looking for a browser process after each of those events.
/// </summary>
public sealed partial class ABrowserOpensOnlyWhenThePersonClicksTests
{
    private static readonly string TrayFolder = FindTrayFolder();

    private static string FindTrayFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "apps", "windows-tray", "Program.cs")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "apps", "windows-tray");
    }

    private static IEnumerable<(string File, int Line, string Method, string Text)> Lines()
    {
        foreach (var path in Directory.GetFiles(TrayFolder, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var method = "";
            var number = 0;
            foreach (var line in File.ReadLines(path))
            {
                number++;
                var declaration = MethodDeclaration().Match(line);
                if (declaration.Success)
                {
                    method = declaration.Groups[1].Value;
                }

                if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal) && !line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    yield return (Path.GetFileName(path), number, method, line);
                }
            }
        }
    }

    [GeneratedRegex(@"^    (?:private|public|internal|protected)[^=;(]*?\s(\w+)\s*(?:<[^>]*>)?\(")]
    private static partial Regex MethodDeclaration();

    [Fact]
    public void Only_two_places_in_the_tray_open_a_page_in_the_browser_and_each_is_a_click_handler_helper()
    {
        // A page is opened by handing Windows an address (a ProcessStartInfo built from the live address or a localhost URL).
        var sites = Lines()
            .Where(line => line.Text.Contains("ProcessStartInfo(", StringComparison.Ordinal)
                && (line.Text.Contains("address + path", StringComparison.Ordinal) || line.Text.Contains("http://", StringComparison.Ordinal)))
            .Select(line => $"{line.File}:{line.Method}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ComponentPickerDialog.cs:OpenDeluno", "TrayApplication.cs:OpenAddress"], sites);
    }

    [Fact]
    public void Every_way_into_the_tray_s_open_helper_is_a_click_a_menu_item_or_a_balloon_the_person_clicked()
    {
        var callers = Lines()
            .Where(line => line.File == "TrayApplication.cs")
            .Where(line => OpenCall().IsMatch(line.Text) && (!MethodDeclaration().IsMatch(line.Text) || line.Text.Contains("=>", StringComparison.Ordinal)))
            .Select(line => line.Method)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        // TrayApplication is the constructor's icon click handlers, Handlers is the menu's "Open Deluno" item, OnUpdateItem is the
        // update menu item, OpenFromIcon / OpenFromMenu / OpenDeluno / OpenUpdatesPage are the helpers between them. The next
        // test pins the click wiring itself.
        Assert.Equal(
            ["Handlers", "OnUpdateItem", "OpenDeluno", "OpenFromIcon", "OpenFromMenu", "OpenUpdatesPage", "TrayApplication"],
            callers);
    }

    [Fact]
    public void The_helpers_are_run_by_clicks_only()
    {
        var source = File.ReadAllText(Path.Combine(TrayFolder, "TrayApplication.cs"));

        // The icon: a left click and a double click.
        Assert.Contains("_notify.MouseClick +=", source);
        Assert.Contains("_notify.DoubleClick += (_, _) => OpenFromIcon();", source);
        // The menu: the Open item is wired to the same helper.
        Assert.Contains("[TrayMenuId.Open] = (_, _) => OpenFromMenu(),", source);
        // A balloon runs its action only when the person clicks the balloon.
        Assert.Contains("_notify.BalloonTipClicked += (_, _) =>", source);
        Assert.Contains("var action = _balloonClick;", source);

        // Nothing at start, at an update restart, at a port change or at a second launch reaches the helpers directly: they are
        // only ever handed over as a balloon's click action (a method group), never called.
        foreach (var (_, line, method, text) in Lines().Where(line => line.File == "TrayApplication.cs"))
        {
            if (method is "OnPortChanged" or "RestartOnPortAsync" or "OnSecondLaunch" or "ShowSecondLaunchBalloon"
                or "OnUpdateRestart" or "Start" or "StartHost" or "OnStarted")
            {
                Assert.False(OpenCall().IsMatch(text), $"{method} (line {line}) opens a page without a click");
            }
        }
    }

    [GeneratedRegex(@"\b(OpenAddress|OpenDeluno|OpenUpdatesPage|OpenFromIcon|OpenFromMenu)\(")]
    private static partial Regex OpenCall();
}
