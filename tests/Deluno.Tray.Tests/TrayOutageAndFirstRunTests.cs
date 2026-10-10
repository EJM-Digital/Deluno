using Deluno.Connections.Contracts;
using Deluno.Contracts;
using Deluno.Platform.Contracts;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1213: a hand-off that failed never turns a dependency the latest check found not answering back to answering, and
/// the tray recovers on the first answer. #1214: the tray icon is there, blinking, from the moment the installer starts
/// Deluno for the first time.
/// </summary>
public sealed class TrayOutageAndFirstRunTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 2, 0, 0, TimeSpan.Zero);

    private static ConnectionCheckItem Check(bool ok, DateTimeOffset at)
        => new(ConnectionKinds.Processor, "p1", "Weir", ok, false, ok ? "Answered." : "Did not answer.", at);

    private static ProcessorConnectionItem Processor(string health, DateTimeOffset? writtenUtc)
        => new("p1", "Weir", "generic", "http://processor.invalid/intake", "Authorization", null, true, health, null, writtenUtc, T0, T0);

    private static IReadOnlyList<string> Down(ConnectionCheckItem? check, string health, DateTimeOffset? writtenUtc)
        => TrayPlatformHealth.Down(check is null ? [] : [check], [], [], [Processor(health, writtenUtc)], [], T0.AddHours(1));

    // ------------------------------------------------------------------ #1213

    [Fact]
    public void Unreachable_then_a_failed_hand_off_writing_degraded_stays_down()
        => Assert.Equal(["Weir isn't answering"], Down(Check(false, T0), "degraded", T0.AddSeconds(2)));

    [Fact]
    public void Unreachable_then_any_later_write_that_is_not_an_answer_stays_down()
    {
        foreach (var status in new[] { "degraded", "untested", "unknown", "unreachable" })
        {
            Assert.Equal(["Weir isn't answering"], Down(Check(false, T0), status, T0.AddSeconds(5)));
        }
    }

    [Fact]
    public void Degraded_then_a_successful_check_answers()
        => Assert.Empty(Down(Check(true, T0.AddMinutes(1)), "degraded", T0));

    [Fact]
    public void A_degraded_write_after_a_successful_check_is_amber_like_the_dashboard()
        => Assert.Equal(["Weir isn't answering"], Down(Check(true, T0), "degraded", T0.AddSeconds(2)));

    [Theory]
    [InlineData("healthy")]
    [InlineData("degraded")]
    [InlineData("unreachable")]
    [InlineData("unhealthy")]
    [InlineData("failed")]
    [InlineData("error")]
    [InlineData("unknown")]
    [InlineData("untested")]
    public void The_tray_and_the_dashboard_give_the_same_answer_for_every_stored_status(string status)
    {
        // Both read ConnectionHealthRules.IsDown; with no check yet the tray's answer is that rule alone.
        var tray = Down(null, status, null).Count > 0;
        Assert.Equal(ConnectionHealthRules.IsDown(status), tray);
        Assert.Equal(TrayPlatformHealth.IsDownStatus(status), tray);
        Assert.Equal(status is not ("healthy" or "unknown" or "untested"), tray);
    }

    [Fact]
    public void Recovery_a_healthy_answer_after_the_failed_check_goes_green_at_once()
        => Assert.Empty(Down(Check(false, T0), "healthy", T0.AddMinutes(4)));

    [Fact]
    public void Recovery_a_successful_check_after_a_stored_unreachable_goes_green()
        => Assert.Empty(Down(Check(true, T0.AddMinutes(1)), "unreachable", T0));

    [Fact]
    public void A_stored_unreachable_written_after_a_good_check_is_down_and_a_missing_check_reads_the_stored_health()
    {
        Assert.Equal(["Weir isn't answering"], Down(Check(true, T0), "unreachable", T0.AddSeconds(3)));
        Assert.Equal(["Weir isn't answering"], Down(null, "unreachable", null));
        Assert.Equal(["Weir isn't answering"], Down(null, "degraded", T0));
    }

    [Fact]
    public void A_failed_hand_off_stores_unreachable_when_nothing_answered_and_nothing_when_the_processor_refused()
    {
        Assert.Equal("healthy", ProcessorHealthRules.HealthAfterSubmission(new ProcessorSubmissionResult(true, "submitted", "ok", 202)));
        Assert.Equal("unreachable", ProcessorHealthRules.HealthAfterSubmission(new ProcessorSubmissionResult(false, "failed", "could not submit", null)));
        // The processor answered, so its health is not touched: degraded would show a working processor as down on the
        // Dashboard and, before this fix, hide an outage on the tray.
        Assert.Null(ProcessorHealthRules.HealthAfterSubmission(new ProcessorSubmissionResult(false, "failed", "refused", 400)));
    }

    // ------------------------------------------------------------------ #1214

    [Fact]
    public void The_installers_first_run_marker_makes_a_start_a_first_run()
    {
        Assert.True(FirstRunTrayModel.IsFirstRun([], name => name == FirstRunTrayModel.FirstRunVariable ? "1.0.0" : null));
        Assert.False(FirstRunTrayModel.IsFirstRun([], _ => null));
        Assert.False(FirstRunTrayModel.IsFirstRun([], _ => " "));
    }

    [Theory]
    [InlineData("--veloapp-install")]
    [InlineData("--veloapp-updated")]
    [InlineData("--veloapp-uninstall")]
    [InlineData("--service")]
    [InlineData("--install-service")]
    public void A_hook_call_or_a_service_command_does_not_show_the_first_run_icon(string argument)
        => Assert.False(FirstRunTrayModel.IsFirstRun([argument], _ => "1.0.0"));

    [Fact]
    public void The_first_run_icon_blinks_says_setting_up_and_offers_open_and_quit()
    {
        Assert.True(FirstRunTrayModel.Icon.Blinking);
        Assert.Equal(TrayDot.Starting, FirstRunTrayModel.Icon.Dot);
        Assert.Equal("Deluno - setting up", FirstRunTrayModel.HoverText);
        Assert.True(FirstRunTrayModel.HoverText.Length <= TrayHoverText.Limit);

        var texts = FirstRunTrayModel.Menu.Where(entry => entry.Id != TrayMenuId.Separator).Select(entry => entry.Text).ToArray();
        Assert.Equal(["Open Deluno", "Quit Deluno"], texts);
        Assert.All(FirstRunTrayModel.Menu, entry => Assert.True(entry.Enabled));

        // The wording matches the running tray's own menu.
        var running = TrayMenuState.Build(new TrayStatus());
        Assert.Equal(running.Get(TrayMenuId.Open).Text, FirstRunTrayModel.Menu.First(entry => entry.Id == TrayMenuId.Open).Text);
        Assert.Equal(running.Get(TrayMenuId.Quit).Text, FirstRunTrayModel.Menu.First(entry => entry.Id == TrayMenuId.Quit).Text);
    }

    [Fact]
    public void The_first_run_icon_is_started_before_the_first_run_hook_that_holds_the_picker()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "apps", "windows-tray", "Program.cs"));
        var icon = source.IndexOf("FirstRunTray.StartIfFirstRun", StringComparison.Ordinal);
        var hooks = source.IndexOf("VelopackApp.Build()", StringComparison.Ordinal);
        Assert.True(icon > 0 && hooks > 0, "Program.Main starts the first-run icon and Velopack's hooks.");
        Assert.True(icon < hooks, "The first-run icon must be up before Velopack runs OnFirstRun (the firewall step and the picker).");
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
