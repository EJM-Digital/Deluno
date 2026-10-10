using Deluno.Api.Updates;
using Deluno.Platform.Contracts;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1246: the tray icon's accessible name must be its hover text in every state. <see cref="Shell"/> is the notification
/// area as measured on Windows 11 (UI Automation on a WinForms NotifyIcon): the name reads the tip the icon was added
/// with, then a space, then the current tip, and taking the icon off and back on starts it afresh.
/// </summary>
public sealed class TrayAccessibleNameTests
{
    private sealed class Shell : ITrayShellIcon
    {
        private bool _visible;
        private string _text = "";
        private string _tipWhenAdded = "";

        public bool Visible
        {
            get => _visible;
            set
            {
                if (value && !_visible)
                {
                    _tipWhenAdded = _text;
                }

                _visible = value;
            }
        }

        public string Text
        {
            get => _text;
            set => _text = value;
        }

        /// <summary>What UI Automation reads for the icon's button.</summary>
        public string AccessibleName => !_visible ? "" : _tipWhenAdded == _text ? _text : $"{_tipWhenAdded} {_text}";
    }

    private static TrayStatus Running() => new()
    {
        State = TrayState.Running,
        Address = "http://localhost:7879",
        Port = 7879,
        Version = "1.2.3",
        HealthKnown = true
    };

    /// <summary>Every state the standard lists, in the order a real session meets them.</summary>
    public static TheoryData<string, TrayStatus> States => new()
    {
        { "starting", new TrayStatus() },
        { "running", Running() },
        { "amber with its reason", Running() with { Down = ["SABnzbd isn't answering", "Weir isn't answering"] } },
        { "amber, health unreadable", Running() with { HealthUnreadable = true } },
        { "paused", Running() with { AutomationPaused = true } },
        { "update ready", Running() with { Update = TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0", true, true) } },
        { "updating", Running() with { State = TrayState.Updating } },
        { "restarting by itself", new TrayStatus { RestartNote = "starting again (try 2 of 5)" } },
        { "red, stopped", Running() with { State = TrayState.Stopped } },
        { "red, gave up", Running() with { State = TrayState.Error } },
        { "running again", Running() }
    };

    [Fact]
    public void The_accessible_name_is_the_hover_text_after_every_state_change()
    {
        var shell = new Shell();
        var tip = new TrayIconTip(shell);
        var statuses = States.Select(row => (Name: (string)row[0], Status: (TrayStatus)row[1])).ToList();

        // The icon is shown at once, in the Starting state, and every change after it goes through the same tip.
        tip.Publish(TrayHoverText.Build(statuses[0].Status));
        shell.Visible = true;
        Assert.Equal(TrayHoverText.Build(statuses[0].Status), shell.AccessibleName);

        foreach (var (name, status) in statuses.Skip(1))
        {
            tip.Publish(TrayHoverText.Build(status));
            Assert.True(TrayHoverText.Build(status) == shell.AccessibleName, $"{name}: hover \"{TrayHoverText.Build(status)}\" but the accessible name is \"{shell.AccessibleName}\"");
        }
    }

    [Fact]
    public void The_accessible_name_never_keeps_Starting_once_Deluno_is_running()
    {
        var shell = new Shell();
        var tip = new TrayIconTip(shell);
        tip.Publish(TrayHoverText.Build(new TrayStatus()));
        shell.Visible = true;
        Assert.Contains("Starting", shell.AccessibleName);

        tip.Publish(TrayHoverText.Build(Running()));

        Assert.DoesNotContain("Starting", shell.AccessibleName);
    }

    [Fact]
    public void An_unchanged_hover_text_leaves_the_icon_alone()
    {
        var toggles = 0;
        var shell = new CountingShell(new Shell(), () => toggles++);
        var tip = new TrayIconTip(shell);
        tip.Publish("Deluno - Running at http://localhost:7879");
        shell.Visible = true;
        var before = toggles;

        tip.Publish("Deluno - Running at http://localhost:7879");

        Assert.Equal(before, toggles);
    }

    private sealed class CountingShell(Shell inner, Action onVisibleChange) : ITrayShellIcon
    {
        public bool Visible
        {
            get => inner.Visible;
            set
            {
                onVisibleChange();
                inner.Visible = value;
            }
        }

        public string Text
        {
            get => inner.Text;
            set => inner.Text = value;
        }
    }
}
