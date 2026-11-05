using System.Text.RegularExpressions;

namespace Deluno.Tray.Tests;

/// <summary>
/// A balloon click does what the balloon offers, and only that balloon's action (coverage register, platform-install). Read from the
/// tray's own source: each balloon that offers something names the action its click runs, the click handler runs that action once
/// and forgets it, and every balloon call replaces the one before, so a balloon with no offer never runs the last one's. Also the
/// "Deluno is already running" balloon a second launch causes, and the small icon shown while the backup before an update is taken.
/// The golden-path scenarios (tray.running-balloons, tray.clicks-and-second-launch) watch the same on a real desktop.
/// </summary>
public sealed class ATrayBalloonClickDoesWhatTheBalloonOffersTests
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

    private static string Source(string file) => File.ReadAllText(Path.Combine(TrayFolder, file));

    private static string Application => Source("TrayApplication.cs");

    [Fact]
    public void A_click_runs_the_action_of_the_balloon_on_screen_once_and_forgets_it()
    {
        var handler = Regex.Match(Application, @"BalloonTipClicked \+= \(_, _\) =>\s*\{(?<body>.*?)\};", RegexOptions.Singleline);

        Assert.True(handler.Success, "the balloon click handler is in the tray");
        var body = Regex.Replace(handler.Groups["body"].Value, @"\s+", " ").Trim();
        Assert.Equal("var action = _balloonClick; _balloonClick = null; action?.Invoke();", body);
    }

    [Fact]
    public void Every_balloon_replaces_the_one_before_so_a_balloon_that_offers_nothing_clears_the_last_offer()
    {
        var method = Regex.Match(Application, @"private void Balloon\([^)]*\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);

        Assert.True(method.Success);
        var body = method.Groups["body"].Value;
        Assert.Matches(@"_balloonClick = onClick;", body);
        Assert.True(body.IndexOf("_balloonClick = onClick;", StringComparison.Ordinal) < body.IndexOf("ShowBalloonTip", StringComparison.Ordinal),
            "the offer is set before the balloon is shown, whether or not it has one");
    }

    [Theory]
    [InlineData("Deluno is running again", "OpenDeluno")]
    [InlineData("Deluno is already running", "OpenDeluno")]
    [InlineData("Couldn't check for updates", "OpenUpdatesPage")]
    [InlineData("Deluno could not start", "() => _ = RestartHostAsync()")]
    [InlineData("Deluno stopped", "() => _ = RestartHostAsync()")]
    [InlineData("Deluno stayed on its old port", "() => OpenFolder(LogsFolder())")]
    [InlineData("GitHub's limit has lifted", "() => OnInstallApps(null, EventArgs.Empty)")]
    public void Each_balloon_that_offers_something_runs_the_action_it_names(string title, string action)
    {
        var call = Regex.Match(Application, @"Balloon\(\s*\$?""" + Regex.Escape(title).Replace("\"", "\\\"") + @"""(?<args>[^;]*?)\);", RegexOptions.Singleline);

        Assert.True(call.Success, $"the \"{title}\" balloon is in the tray");
        Assert.Contains(action, call.Groups["args"].Value);
    }

    [Fact]
    public void A_second_launch_shows_the_balloon_with_the_address_or_still_starting_and_a_click_opens_Deluno()
    {
        Assert.Matches(@"SecondLaunchSignal\.Listen\(\(\) => InvokeOnUiThread\(ShowAlreadyRunning\)\)", Application);
        var method = Regex.Match(Application, @"private void ShowAlreadyRunning\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(method.Success);
        var body = Regex.Replace(method.Groups["body"].Value, @"\s+", " ").Trim();
        Assert.Equal(
            "var text = LiveAddress is { } address ? address : \"Deluno is still starting.\"; Balloon(\"Deluno is already running\", text, ToolTipIcon.Info, OpenDeluno);",
            body);
    }

    [Fact]
    public void The_backup_before_an_update_shows_the_small_icon_whose_hover_names_the_backup_and_whose_click_says_to_click_again()
    {
        var firstRun = Source("FirstRunTray.cs");

        Assert.Contains("BackupHoverText = \"Deluno - backing up your data before the update\"", firstRun);
        Assert.Contains("BackupOpenNotice = \"Deluno is backing up its data before installing an update. Click the icon again in a moment.\"", firstRun);
        Assert.Contains("new FirstRunTray(FirstRunTrayModel.BackupHoverText, FirstRunTrayModel.BackupOpenNotice)", firstRun);
        // A click on the icon or on Open Deluno shows that notice, never a browser.
        Assert.Matches(@"_notify\?\.ShowBalloonTip\(\s*TrayBalloons\.InformationMilliseconds,\s*FirstRunTrayModel\.OpenNoticeTitle,\s*_openNotice,\s*ToolTipIcon\.Info\);", firstRun);
        Assert.True(Regex.Matches(firstRun, @"ShowNotYetOpen\(\)").Count >= 3, "the menu item, the icon click and the method itself");
    }
}
