using Deluno.Tray;

namespace Deluno.Tray.Tests;

/// <summary>The tray's Pause / Resume automation item: the same switch as the Dashboard's Resume, a Settings push, the pause bars.</summary>
public sealed class TheTrayPauseItemFlipsTheAutomationSwitchTests
{
    [Fact]
    public async Task Pause_switches_automation_off_tells_the_web_screens_and_reports_paused()
    {
        var calls = new List<string>();

        var paused = await AutomationSwitchChange.ApplyAsync(
            resume: false,
            enabled => { calls.Add($"set:{enabled}"); return Task.FromResult(enabled); },
            () => { calls.Add("publish"); return Task.CompletedTask; });

        Assert.True(paused);
        Assert.Equal(["set:False", "publish"], calls);
    }

    [Fact]
    public async Task Resume_switches_automation_on_tells_the_web_screens_and_reports_not_paused()
    {
        var calls = new List<string>();

        var paused = await AutomationSwitchChange.ApplyAsync(
            resume: true,
            enabled => { calls.Add($"set:{enabled}"); return Task.FromResult(enabled); },
            () => { calls.Add("publish"); return Task.CompletedTask; });

        Assert.False(paused);
        Assert.Equal(["set:True", "publish"], calls);
    }

    [Fact]
    public async Task The_pause_bars_follow_what_was_saved_not_what_was_asked()
    {
        // The repository answers with the switch as it now is; if it could not turn automation on, the item still reads paused.
        var paused = await AutomationSwitchChange.ApplyAsync(resume: true, _ => Task.FromResult(false), () => Task.CompletedTask);

        Assert.True(paused);
    }

    [Fact]
    public async Task A_failed_save_publishes_nothing_and_reaches_the_caller_for_its_balloon()
    {
        var published = false;

        var failure = await Assert.ThrowsAsync<IOException>(() => AutomationSwitchChange.ApplyAsync(
            resume: false,
            _ => throw new IOException("the settings database is locked"),
            () => { published = true; return Task.CompletedTask; }));

        Assert.Equal("the settings database is locked", failure.Message);
        Assert.False(published);
    }
}

/// <summary>The tray's Open Deluno item when there is nothing live to open.</summary>
public sealed class OpenDelunoSaysWhyItCannotOpenTests
{
    [Theory]
    [InlineData(TrayState.Starting, "Deluno is still starting. Click the icon again in a moment.")]
    [InlineData(TrayState.Updating, "Deluno is updating. Click the icon again in a moment.")]
    [InlineData(TrayState.Error, "Deluno is not running. Choose Restart Deluno from the icon menu.")]
    [InlineData(TrayState.Stopped, "Deluno is not running. Choose Restart Deluno from the icon menu.")]
    public void The_balloon_says_which_of_starting_updating_or_not_running_it_is(TrayState state, string words)
        => Assert.Equal(words, OpenDelunoAnswer.Why(state));

    [Fact]
    public void The_balloon_has_its_plain_title()
        => Assert.Equal("Deluno can't open yet", OpenDelunoAnswer.Title);
}
