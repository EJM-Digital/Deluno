using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Worker.Dashboard;

namespace Deluno.Tray.Tests;

/// <summary>
/// docs/tray-standard.md as tests (#1185): the corner mark and its priority, the hover texts, and the menu's order,
/// wording and enabled rules, all from the state records the tray draws from.
/// </summary>
public sealed class TrayStandardTests
{
    private static TrayStatus Running() => new()
    {
        State = TrayState.Running,
        Address = "http://localhost:7879",
        Port = 7879,
        Version = "1.2.3"
    };

    // ------------------------------------------------------------------ the corner mark

    [Fact]
    public void Nothing_differs_so_there_is_no_mark()
        => Assert.Equal(TrayBadge.None, TrayBadges.Choose(Running()));

    [Theory]
    [InlineData(TrayState.Starting)]
    [InlineData(TrayState.Updating)]
    public void Starting_shows_the_grey_ring(TrayState state)
        => Assert.Equal(TrayBadge.Starting, TrayBadges.Choose(Running() with { State = state }));

    [Fact]
    public void Paused_automation_shows_two_bars()
        => Assert.Equal(TrayBadge.Paused, TrayBadges.Choose(Running() with { AutomationPaused = true }));

    [Fact]
    public void An_update_waiting_shows_the_blue_dot()
    {
        var update = TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0", true, true);
        Assert.Equal(TrayBadge.UpdateReady, TrayBadges.Choose(Running() with { Update = update }));
    }

    [Theory]
    [InlineData(TrayState.Error)]
    [InlineData(TrayState.Stopped)]
    public void A_host_that_stopped_or_gave_up_shows_the_red_dot(TrayState state)
        => Assert.Equal(TrayBadge.NeedsYou, TrayBadges.Choose(Running() with { State = state }));

    [Fact]
    public void A_health_problem_shows_the_red_dot()
        => Assert.Equal(TrayBadge.NeedsYou, TrayBadges.Choose(Running() with { HealthProblem = true }));

    [Fact]
    public void Things_that_need_a_look_show_the_red_dot()
        => Assert.Equal(TrayBadge.NeedsYou, TrayBadges.Choose(Running() with { NeedsLookCount = 2 }));

    [Fact]
    public void Only_one_mark_shows_in_the_standards_order()
    {
        var ready = TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0", true, true);
        var everything = Running() with { NeedsLookCount = 1, AutomationPaused = true, Update = ready };
        Assert.Equal(TrayBadge.NeedsYou, TrayBadges.Choose(everything));

        // Red dot, then grey ring, then two bars, then blue dot.
        var startingPausedUpdate = everything with { NeedsLookCount = 0, State = TrayState.Starting };
        Assert.Equal(TrayBadge.Starting, TrayBadges.Choose(startingPausedUpdate));
        Assert.Equal(TrayBadge.Paused, TrayBadges.Choose(startingPausedUpdate with { State = TrayState.Running }));
        Assert.Equal(TrayBadge.UpdateReady, TrayBadges.Choose(startingPausedUpdate with { State = TrayState.Running, AutomationPaused = false }));
    }

    // ------------------------------------------------------------------ the hover text

    [Fact]
    public void Running_says_where_it_is()
        => Assert.Equal("Deluno - Running at http://localhost:7879", TrayHoverText.Build(Running()));

    [Fact]
    public void Running_with_things_to_look_at_says_what_needs_you()
        => Assert.Equal("Deluno - Running at http://localhost:7879 - 2 things need a look", TrayHoverText.Build(Running() with { NeedsLookCount = 2 }));

    [Fact]
    public void One_thing_reads_as_one_thing()
        => Assert.Equal("Deluno - Running at http://localhost:7879 - 1 thing needs a look", TrayHoverText.Build(Running() with { NeedsLookCount = 1 }));

    [Fact]
    public void A_health_problem_with_no_count_is_still_said()
        => Assert.Equal("Deluno - Running at http://localhost:7879 - Health has a problem", TrayHoverText.Build(Running() with { HealthProblem = true }));

    [Fact]
    public void Paused_says_paused()
        => Assert.Equal("Deluno - Paused", TrayHoverText.Build(Running() with { AutomationPaused = true }));

    [Fact]
    public void An_update_ready_says_so_with_its_version()
    {
        var ready = TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0", true, true);
        Assert.Equal("Deluno - Running at http://localhost:7879 - Update ready (v1.3.0)", TrayHoverText.Build(Running() with { Update = ready }));
    }

    [Fact]
    public void Starting_and_a_restart_in_progress_are_said_plainly()
    {
        Assert.Equal("Deluno - Starting...", TrayHoverText.Build(new TrayStatus()));
        Assert.Equal(
            "Deluno - Stopped by itself - starting again (try 2 of 5)",
            TrayHoverText.Build(new TrayStatus { RestartNote = "starting again (try 2 of 5)" }));
    }

    [Fact]
    public void A_host_that_gave_up_says_how_to_get_it_back()
        => Assert.Equal("Deluno - Stopped - choose Restart Deluno", TrayHoverText.Build(Running() with { State = TrayState.Error, NeedsLookCount = 3 }));

    [Fact]
    public void A_long_text_is_shortened_by_whole_parts_never_mid_word()
    {
        var status = Running() with
        {
            Address = "http://an-extremely-long-machine-name-on-the-local-network.example.internal:7879",
            NeedsLookCount = 12,
            Update = TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0-rc.10", true, true)
        };

        var text = TrayHoverText.Build(status);

        Assert.True(text.Length <= TrayHoverText.Limit, text);
        Assert.StartsWith("Deluno - Running", text);
        Assert.Contains("12 things need a look", text);
        Assert.DoesNotContain("...", text);
    }

    [Fact]
    public void The_last_resort_cut_lands_on_a_word_boundary()
    {
        var status = Running() with { NeedsLookCount = 1, AutomationPaused = false };
        var words = string.Join(" ", Enumerable.Repeat("wordy", 40));
        var text = TrayHoverText.Build(status with { RestartNote = words, State = TrayState.Starting });

        Assert.True(text.Length <= TrayHoverText.Limit);
        Assert.EndsWith("...", text);
        Assert.EndsWith("wordy...", text);
    }

    // ------------------------------------------------------------------ the menu

    [Fact]
    public void The_menu_is_in_the_standards_order_and_wording()
    {
        var state = TrayMenuState.Build(Running() with { Lan = TrayLanState.Blocked });

        Assert.Equal(
            [
                "Open Deluno",
                "Deluno - Running at http://localhost:7879",
                "-",
                "Pause automation",
                "Restart Deluno",
                "-",
                "Copy address",
                "Allow other devices on your network...",
                "Only allow this PC",
                "Change port (7879)...",
                "-",
                "Open data folder",
                "Open logs folder",
                "Start with Windows",
                "-",
                "Check for updates",
                "Deluno v1.2.3",
                "Report a problem...",
                "Advanced",
                "-",
                "Quit Deluno"
            ],
            state.VisibleTexts());
    }

    [Fact]
    public void Advanced_holds_the_service_and_any_address_items()
    {
        var state = TrayMenuState.Build(Running());
        Assert.Equal(["Run as a Windows service...", "Let any address in..."], state.AdvancedEntries.Select(e => e.Text));
    }

    [Fact]
    public void There_is_no_stop_start_or_open_updates_item()
    {
        var texts = TrayMenuState.Build(Running()).Entries.Concat(TrayMenuState.Build(Running()).AdvancedEntries).Select(e => e.Text).ToList();
        Assert.DoesNotContain("Stop", texts);
        Assert.DoesNotContain("Start", texts);
        Assert.DoesNotContain("Open Updates", texts);
        Assert.DoesNotContain("Quit", texts);
    }

    [Fact]
    public void Open_Deluno_is_the_bold_default_and_the_status_line_is_greyed_out()
    {
        var state = TrayMenuState.Build(Running());
        Assert.True(state.Get(TrayMenuId.Open).Bold);
        Assert.False(state.Get(TrayMenuId.StatusLine).Enabled);
        Assert.Equal(TrayHoverText.Build(Running()), state.Get(TrayMenuId.StatusLine).Text);
        Assert.False(state.Get(TrayMenuId.Version).Enabled);
    }

    [Fact]
    public void Pause_flips_to_Resume_when_automation_is_paused()
    {
        Assert.Equal("Pause automation", TrayMenuState.Build(Running()).Get(TrayMenuId.PauseResume).Text);
        Assert.Equal("Resume automation", TrayMenuState.Build(Running() with { AutomationPaused = true }).Get(TrayMenuId.PauseResume).Text);
    }

    [Fact]
    public void Pause_needs_a_running_host_and_Restart_works_when_stopped()
    {
        var starting = TrayMenuState.Build(new TrayStatus());
        Assert.False(starting.Get(TrayMenuId.PauseResume).Enabled);
        Assert.False(starting.Get(TrayMenuId.Restart).Enabled);

        var gaveUp = TrayMenuState.Build(Running() with { State = TrayState.Error });
        Assert.False(gaveUp.Get(TrayMenuId.PauseResume).Enabled);
        Assert.True(gaveUp.Get(TrayMenuId.Restart).Enabled);
        Assert.True(gaveUp.Get(TrayMenuId.Open).Enabled, "a click is answered with a balloon saying why, so the item stays");
    }

    [Fact]
    public void Copy_address_needs_a_live_address()
    {
        Assert.False(TrayMenuState.Build(new TrayStatus()).Get(TrayMenuId.CopyAddress).Enabled);
        Assert.True(TrayMenuState.Build(Running()).Get(TrayMenuId.CopyAddress).Enabled);
    }

    [Fact]
    public void The_firewall_items_follow_what_Windows_Firewall_says()
    {
        var none = TrayMenuState.Build(Running() with { Lan = TrayLanState.NotAvailable });
        Assert.False(none.Get(TrayMenuId.AllowLan).Visible);
        Assert.False(none.Get(TrayMenuId.OnlyThisPc).Visible);

        var blocked = TrayMenuState.Build(Running() with { Lan = TrayLanState.Blocked });
        Assert.True(blocked.Get(TrayMenuId.AllowLan).Enabled);
        Assert.False(blocked.Get(TrayMenuId.OnlyThisPc).Enabled);

        var open = TrayMenuState.Build(Running() with { Lan = TrayLanState.Open });
        Assert.True(open.Get(TrayMenuId.AllowLan).Visible, "stays so a removed rule can be put back");
        Assert.True(open.Get(TrayMenuId.AllowLan).Enabled);
        Assert.True(open.Get(TrayMenuId.OnlyThisPc).Enabled);

        var busy = TrayMenuState.Build(Running() with { Lan = TrayLanState.Open, LanBusy = true });
        Assert.False(busy.Get(TrayMenuId.AllowLan).Enabled);
        Assert.False(busy.Get(TrayMenuId.OnlyThisPc).Enabled);
    }

    [Fact]
    public void Change_port_names_the_live_port()
        => Assert.Equal("Change port (8123)...", TrayMenuState.Build(Running() with { Port = 8123 }).Get(TrayMenuId.ChangePort).Text);

    [Fact]
    public void Start_with_Windows_is_a_tick_that_follows_the_choice()
    {
        Assert.False(TrayMenuState.Build(Running()).Get(TrayMenuId.StartWithWindows).Checked);
        Assert.True(TrayMenuState.Build(Running() with { StartsWithWindows = true }).Get(TrayMenuId.StartWithWindows).Checked);
    }

    [Fact]
    public void Quit_says_what_it_stops()
    {
        var quit = TrayMenuState.Build(Running()).Get(TrayMenuId.Quit);
        Assert.Equal("Quit Deluno", quit.Text);
        Assert.Equal("Stops Deluno and closes this icon. Download clients Deluno started keep running.", quit.ToolTip);
    }

    [Fact]
    public void The_pending_lines_show_at_the_top_only_while_something_is_pending()
    {
        var quiet = TrayMenuState.Build(Running());
        Assert.False(quiet.Get(TrayMenuId.ReaddTransmission).Visible);
        Assert.False(quiet.Get(TrayMenuId.InstallApps).Visible);
        Assert.Equal("Open Deluno", quiet.VisibleTexts()[0]);

        var pending = TrayMenuState.Build(Running() with { TransmissionReaddPending = true, InstallAppsText = "Try again: install qBittorrent..." });
        Assert.Equal(["Add Transmission's torrents back...", "Try again: install qBittorrent...", "-", "Open Deluno"], pending.VisibleTexts().Take(4));
    }

    // ------------------------------------------------------------------ the update item

    [Fact]
    public void The_update_item_has_five_states()
    {
        Assert.Equal(("Check for updates", true, UpdateMenuAction.Check), Shape(TrayUpdate.Describe(true, UpdateStates.UpToDate, null, false, false)));
        Assert.Equal(("Checking for updates...", false, UpdateMenuAction.Wait), Shape(TrayUpdate.Describe(true, UpdateStates.Checking, null, false, false)));
        Assert.Equal(("Downloading update...", false, UpdateMenuAction.Wait), Shape(TrayUpdate.Describe(true, UpdateStates.Downloading, "1.3.0", true, false)));
        Assert.Equal(("Download update (v1.3.0)", true, UpdateMenuAction.Download), Shape(TrayUpdate.Describe(true, UpdateStates.UpdateAvailable, "1.3.0", true, false)));
        Assert.Equal(("Restart to update (v1.3.0)", true, UpdateMenuAction.Restart), Shape(TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0", true, true)));
    }

    [Fact]
    public void A_copy_that_is_not_installed_sends_the_update_item_to_System_Updates()
        => Assert.Equal(("Check for updates", true, UpdateMenuAction.OpenUpdatePage), Shape(TrayUpdate.Describe(false, UpdateStates.NotSupported, null, false, false)));

    [Fact]
    public void The_menu_shows_the_update_items_wording()
    {
        var ready = TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0", true, true);
        Assert.Equal("Restart to update (v1.3.0)", TrayMenuState.Build(Running() with { Update = ready }).Get(TrayMenuId.Update).Text);
        var checking = TrayUpdate.Describe(true, UpdateStates.Checking, null, false, false);
        Assert.False(TrayMenuState.Build(Running() with { Update = checking }).Get(TrayMenuId.Update).Enabled);
    }

    private static (string, bool, UpdateMenuAction) Shape(TrayUpdate update) => (update.Text, update.Enabled, update.Action);

    // ------------------------------------------------------------------ clicks and balloons

    [Fact]
    public void A_click_opens_once_and_the_double_click_after_it_does_not_open_again()
    {
        var gate = new OpenClickGate(TimeSpan.FromMilliseconds(500));
        var start = DateTimeOffset.UtcNow;
        Assert.True(gate.ShouldOpen(start));
        Assert.False(gate.ShouldOpen(start.AddMilliseconds(200)));
        Assert.True(gate.ShouldOpen(start.AddSeconds(5)));
    }

    [Fact]
    public void Balloons_stay_ten_seconds_for_information_and_thirty_for_problems()
    {
        Assert.Equal(10_000, TrayBalloons.DurationFor(problem: false));
        Assert.Equal(30_000, TrayBalloons.DurationFor(problem: true));
    }

    [Theory]
    [InlineData("AttentionChanged", true)]
    [InlineData("HealthChanged", true)]
    [InlineData("SettingsChanged", true)]
    [InlineData("DownloadProgress", false)]
    [InlineData("ActivityEventAdded", false)]
    public void Only_the_pushes_that_can_move_the_marks_wake_the_watcher(string name, bool moves)
        => Assert.Equal(moves, TrayAttentionWatcher.Moves(name));

    // ------------------------------------------------------------------ the red dot's count

    [Fact]
    public void The_count_adds_up_each_kind_of_thing_that_needs_a_look()
    {
        var inputs = new TrayAttentionInputs(RecoveryOpen: 2, FailedProcessorHandoffs: 1, Stalled: 3, IndexersFailing: 1, InternetOutage: true);
        Assert.Equal(8, TrayAttention.Total(inputs));
        Assert.Equal(0, TrayAttention.Total(new TrayAttentionInputs()));
    }

    [Fact]
    public void A_health_problem_about_a_connection_is_not_counted_twice()
    {
        var now = DateTimeOffset.UtcNow;
        var health = new[]
        {
            Check("indexer:abc", HealthCheckStatuses.Problem),
            Check("download-client:xyz", HealthCheckStatuses.Problem),
            Check("library-root:1", HealthCheckStatuses.Problem),
            Check("disk:c", HealthCheckStatuses.Ok)
        };

        var attention = TrayAttention.From(summary: null, health, now);

        Assert.Equal(1, attention.Count);
        Assert.True(attention.HealthProblem);
    }

    [Fact]
    public void A_snoozed_problem_is_neither_counted_nor_a_red_dot()
    {
        var now = DateTimeOffset.UtcNow;
        var snoozed = Check("library-root:1", HealthCheckStatuses.Problem) with { SnoozedUntilUtc = now.AddHours(1) };

        var attention = TrayAttention.From(summary: null, [snoozed], now);

        Assert.Equal(0, attention.Count);
        Assert.False(attention.HealthProblem);
    }

    [Fact]
    public void The_count_reads_the_summarys_own_numbers()
    {
        var summary = new DashboardSummary(
            DateTimeOffset.UtcNow, 0, [], null, null, null,
            new DashboardImportsSection(DateTimeOffset.UtcNow, 4, new Dictionary<string, int>(), 2, 1),
            null, null, null, null, null, null, null, null, null, null, null);

        var inputs = TrayAttention.InputsFrom(summary, []);

        Assert.Equal(4, inputs.RecoveryOpen);
        Assert.Equal(2, inputs.FailedImportJobs);
        Assert.Equal(1, inputs.FailedProcessorHandoffs);
        Assert.Equal(7, TrayAttention.Total(inputs));
    }

    private static HealthCheckResultItem Check(string id, string status)
        => new(id, "category", "name", status, "message", null, null, null, DateTimeOffset.UtcNow, null, null);
}
