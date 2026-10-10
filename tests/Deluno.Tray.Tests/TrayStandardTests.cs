using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Connections.Contracts;
using Deluno.Platform.Contracts;

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
        Version = "1.2.3",
        HealthKnown = true
    };

    private static TrayUpdate UpdateReady() => TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0", true, true);

    // ------------------------------------------------------------------ the dot and the extra mark

    [Fact]
    public void Running_with_everything_answering_is_a_green_dot_and_no_mark()
        => Assert.Equal(new TrayIconSpec(TrayDot.Green, TrayMark.None), TrayBadges.Choose(Running()));

    [Fact]
    public void One_dependency_down_is_an_amber_dot()
        => Assert.Equal(new TrayIconSpec(TrayDot.Amber, TrayMark.None), TrayBadges.Choose(Running() with { Down = ["SABnzbd isn't answering"] }));

    [Fact]
    public void Several_dependencies_down_is_still_one_amber_dot()
        => Assert.Equal(TrayDot.Amber, TrayBadges.Choose(Running() with { Down = ["SABnzbd isn't answering", "NZBgeek isn't answering", "Weir isn't answering"] }).Dot);

    [Theory]
    [InlineData(TrayState.Error)]
    [InlineData(TrayState.Stopped)]
    public void A_host_that_stopped_or_gave_up_is_a_red_dot(TrayState state)
        => Assert.Equal(TrayDot.Red, TrayBadges.Choose(Running() with { State = state }).Dot);

    [Fact]
    public void A_host_that_is_down_is_red_even_when_a_connection_is_down_too()
        => Assert.Equal(TrayDot.Red, TrayBadges.Choose(Running() with { State = TrayState.Error, Down = ["SABnzbd isn't answering"] }).Dot);

    [Theory]
    [InlineData(TrayState.Starting)]
    [InlineData(TrayState.Updating)]
    public void Starting_is_a_blinking_dot(TrayState state)
    {
        var spec = TrayBadges.Choose(Running() with { State = state });
        Assert.Equal(TrayDot.Starting, spec.Dot);
        Assert.True(spec.Blinking);
    }

    [Fact]
    public void A_running_host_blinks_until_the_first_health_reading()
    {
        Assert.True(TrayBadges.Choose(Running() with { HealthKnown = false }).Blinking);
        Assert.False(TrayBadges.Choose(Running()).Blinking);
    }

    [Fact]
    public void Only_starting_blinks()
    {
        foreach (var status in new[]
                 {
                     Running(),
                     Running() with { Down = ["SABnzbd isn't answering"] },
                     Running() with { State = TrayState.Error },
                     Running() with { AutomationPaused = true },
                     Running() with { Update = UpdateReady() }
                 })
        {
            Assert.False(TrayBadges.Choose(status).Blinking);
        }
    }

    [Fact]
    public void A_health_reading_that_failed_is_amber_and_says_so_never_green()
    {
        var status = Running() with { HealthUnreadable = true };
        Assert.Equal(new TrayIconSpec(TrayDot.Amber, TrayMark.None), TrayBadges.Choose(status));
        Assert.False(TrayBadges.Choose(status).Blinking);
        Assert.Equal("Deluno - can't read its own health yet", TrayHoverText.Build(status));
        Assert.Equal("Deluno - can't read its own health yet", TrayMenuState.Build(status).Get(TrayMenuId.StatusLine).Text);

        // The next reading that works clears it.
        Assert.Equal(TrayDot.Green, TrayBadges.Choose(status with { HealthUnreadable = false }).Dot);
    }

    [Fact]
    public void Paused_automation_shows_the_pause_bars_on_the_green_dot()
        => Assert.Equal(new TrayIconSpec(TrayDot.Green, TrayMark.Paused), TrayBadges.Choose(Running() with { AutomationPaused = true }));

    [Fact]
    public void An_update_waiting_shows_the_arrow_on_the_green_dot()
        => Assert.Equal(new TrayIconSpec(TrayDot.Green, TrayMark.UpdateReady), TrayBadges.Choose(Running() with { Update = UpdateReady() }));

    [Fact]
    public void Paused_and_an_update_together_show_the_pause_bars()
        => Assert.Equal(TrayMark.Paused, TrayBadges.Choose(Running() with { AutomationPaused = true, Update = UpdateReady() }).Mark);

    [Fact]
    public void The_marks_sit_on_top_of_the_colour()
    {
        Assert.Equal(
            new TrayIconSpec(TrayDot.Amber, TrayMark.Paused),
            TrayBadges.Choose(Running() with { Down = ["SABnzbd isn't answering"], AutomationPaused = true }));
        Assert.Equal(
            new TrayIconSpec(TrayDot.Red, TrayMark.UpdateReady),
            TrayBadges.Choose(Running() with { State = TrayState.Stopped, Update = UpdateReady() }));
    }

    [Fact]
    public void A_failed_download_or_a_bell_item_does_not_change_the_colour()
    {
        // The tray carries no count of things that need a look, and the status has nowhere to put one: the colour is
        // decided from the host and from what does not answer, so a failed download or a bell row cannot move it.
        Assert.DoesNotContain(typeof(TrayStatus).GetProperties(), property => property.Name.Contains("NeedsLook", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(TrayServerSignals).GetProperties(), property => property.Name.Contains("NeedsLook", StringComparison.Ordinal));
        Assert.Equal(TrayDot.Green, TrayBadges.Choose(Running()).Dot);

        // Health rows for a failed download, a held hand-off or a failed grab are not platform health.
        var rows = new[]
        {
            Problem("download:failed:1", "download", "A download failed"),
            Problem("handoff:1", "processor", "A hand-off is held"),
            Problem("grab:1", "grab", "A grab failed")
        };
        Assert.Empty(TrayPlatformHealth.Down([], [], [], [], rows, Now));
    }

    // ------------------------------------------ reading what does not answer, from the server's health data

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static HealthCheckResultItem Problem(string id, string category, string name, string status = HealthCheckStatuses.Problem, DateTimeOffset? snoozedUntil = null)
        => new(id, category, name, status, "message", null, null, null, Now, Now, snoozedUntil);

    private static ConnectionCheckItem Check(string kind, string id, string name, bool ok)
        => new(kind, id, name, ok, false, ok ? "Answered." : "Did not answer.", Now);

    private static DownloadClientItem Client(string id, string name, string health, bool enabled = true)
        => new(id, name, "sabnzbd", null, null, null, null, null, false, null, null, null, null, 100, enabled, health, null, null, null, null, Now, Now);

    private static IndexerItem Indexer(string id, string name, string health, bool enabled = true)
        => new(id, name, "newznab", "private", "http://indexer.invalid", null, 100, "", "", "both", enabled, health, null, null, null, null, 0, null, null, Now, Now);

    private static ProcessorConnectionItem Processor(string id, string name, string health, bool enabled = true)
        => new(id, name, "generic", "http://processor.invalid/intake", "Authorization", null, enabled, health, null, null, Now, Now);

    [Fact]
    public void Everything_answering_leaves_nothing_down()
        => Assert.Empty(TrayPlatformHealth.Down(
            [Check("downloadClient", "c1", "SABnzbd", true)],
            [Client("c1", "SABnzbd", "healthy")],
            [Indexer("i1", "NZBgeek", "healthy")],
            [Processor("p1", "Weir", "healthy")],
            [Problem("library:l1:root", "library", "Movies (library root)", HealthCheckStatuses.Ok)],
            Now));

    [Fact]
    public void A_download_client_an_indexer_and_the_processor_that_do_not_answer_are_named()
    {
        var down = TrayPlatformHealth.Down(
            [],
            [Client("c1", "SABnzbd", "unreachable")],
            [Indexer("i1", "NZBgeek", "unreachable")],
            [Processor("p1", "Weir", "unreachable")],
            [],
            Now);

        Assert.Equal(["SABnzbd isn't answering", "NZBgeek isn't answering", "Weir isn't answering"], down);
    }

    [Fact]
    public void The_latest_real_check_wins_over_the_stored_health()
    {
        var down = TrayPlatformHealth.Down(
            [Check("downloadClient", "c1", "SABnzbd", false), Check("indexer", "i1", "NZBgeek", true)],
            [Client("c1", "SABnzbd", "healthy")],
            [Indexer("i1", "NZBgeek", "unreachable")],
            [],
            [],
            Now);

        Assert.Equal(["SABnzbd isn't answering"], down);
    }

    [Fact]
    public void A_usenet_client_with_no_provider_reads_as_a_setup_problem_and_keeps_the_dot_amber()
    {
        var noProvider = Deluno.Connections.Usenet.UsenetProviderText.NoProvider;
        var down = TrayPlatformHealth.Down(
            [new ConnectionCheckItem("downloadClient", "c1", "SABnzbd", false, false, noProvider, Now)],
            [Client("c1", "SABnzbd", "degraded")],
            [], [], [], Now);

        Assert.Equal(["SABnzbd has no Usenet provider set up"], down);
        var status = Running() with { Down = down };
        Assert.Equal("Deluno - SABnzbd has no Usenet provider set up", TrayHoverText.Build(status));
        Assert.Equal(TrayDot.Amber, TrayBadges.Choose(status).Dot);
    }

    [Fact]
    public void A_usenet_client_whose_stored_health_carries_the_no_provider_reason_reads_the_same_without_a_check()
    {
        var item = Client("c1", "SABnzbd", "unreachable") with { LastHealthMessage = Deluno.Connections.Usenet.UsenetProviderText.NoProvider };

        Assert.Equal(["SABnzbd has no Usenet provider set up"], TrayPlatformHealth.Down([], [item], [], [], [], Now));
    }

    [Fact]
    public void A_processor_whose_stored_health_is_newer_than_its_last_check_reads_as_down_at_once()
    {
        // Weir quit: a hand-off failed and the pass that follows Weir wrote "unreachable" after the last real check said ok.
        var processor = Processor("p1", "Weir", "unreachable") with { LastHealthTestUtc = Now };
        var staleCheck = new ConnectionCheckItem("processor", "p1", "Weir", true, false, "Answered.", Now.AddMinutes(-9));

        Assert.Equal(["Weir isn't answering"], TrayPlatformHealth.Down([staleCheck], [], [], [processor], [], Now));
    }

    [Fact]
    public void A_real_check_newer_than_the_stored_health_still_wins()
    {
        var processor = Processor("p1", "Weir", "unreachable") with { LastHealthTestUtc = Now.AddMinutes(-9) };
        var check = new ConnectionCheckItem("processor", "p1", "Weir", true, false, "Answered.", Now);

        Assert.Empty(TrayPlatformHealth.Down([check], [], [], [processor], [], Now));
    }

    [Fact]
    public void Deluno_itself_is_not_called_down_while_it_starts()
    {
        // The metadata service is Deluno's own broker, named "deluno". The failed check taken while it started stays on
        // record; for ten minutes after the host starts it is not a reading of this run.
        var failed = new ConnectionCheckItem("metadata", "metadata", "deluno", false, false, "Did not answer.", Now.AddMinutes(-1));

        Assert.Empty(TrayPlatformHealth.Down([failed], [], [], [], [], Now, hostStartedUtc: Now.AddMinutes(-3)));
        Assert.Empty(TrayPlatformHealth.Down([failed], [], [], [], [], Now, hostStartedUtc: Now.AddMinutes(30)));
    }

    [Fact]
    public void The_metadata_service_is_named_plainly_once_it_has_really_failed_after_the_start_up()
    {
        var failed = new ConnectionCheckItem("metadata", "metadata", "deluno", false, false, "Did not answer.", Now);

        Assert.Equal(["The metadata service isn't answering"],
            TrayPlatformHealth.Down([failed], [], [], [], [], Now, hostStartedUtc: Now.AddHours(-1)));
    }

    [Fact]
    public void A_connection_that_is_switched_off_is_not_a_problem()
        => Assert.Empty(TrayPlatformHealth.Down([], [Client("c1", "SABnzbd", "unreachable", enabled: false)], [], [], [], Now));

    [Fact]
    public void The_metadata_service_a_library_folder_and_the_disk_are_named()
    {
        var down = TrayPlatformHealth.Down(
            [Check("metadata", "metadata", "TMDb", false)],
            [], [], [],
            [
                Problem("library:l1:root", "library", "Movies (library root)"),
                Problem("system:disk-space", "system", "Deluno's own storage")
            ],
            Now);

        Assert.Equal(["TMDb isn't answering", "The Movies library folder has a problem", "Deluno's own disk is running low"], down);
    }

    [Fact]
    public void Other_health_problems_and_snoozed_ones_do_not_colour_the_dot()
    {
        var down = TrayPlatformHealth.Down(
            [], [], [], [],
            [
                Problem("library:l1:folder-chain", "library", "The Movies library's category and save path"),
                Problem("library:l2:root", "library", "TV (library root)", snoozedUntil: Now.AddHours(1))
            ],
            Now);

        Assert.Empty(down);
    }

    // ------------------------------------------------------------------ the hover text

    [Fact]
    public void Running_says_where_it_is()
        => Assert.Equal("Deluno - Running at http://localhost:7879", TrayHoverText.Build(Running()));

    [Fact]
    public void One_dependency_down_says_what_is_wrong()
        => Assert.Equal("Deluno - SABnzbd isn't answering", TrayHoverText.Build(Running() with { Down = ["SABnzbd isn't answering"] }));

    [Fact]
    public void Several_down_name_the_first_and_count_the_rest()
    {
        var down = new[] { "SABnzbd isn't answering", "NZBgeek isn't answering", "Weir isn't answering" };
        Assert.Equal("Deluno - SABnzbd isn't answering and 2 more", TrayHoverText.Build(Running() with { Down = down }));
        Assert.Equal("Deluno - SABnzbd isn't answering and 1 more", TrayHoverText.Build(Running() with { Down = down[..2] }));
    }

    [Fact]
    public void The_status_line_says_the_same_words_as_the_hover_text()
    {
        var status = Running() with { Down = ["SABnzbd isn't answering"], AutomationPaused = true };
        Assert.Equal(TrayHoverText.Build(status), TrayMenuState.Build(status).Get(TrayMenuId.StatusLine).Text);
        Assert.Equal("Deluno - SABnzbd isn't answering - Paused", TrayHoverText.Build(status));
    }

    [Fact]
    public void The_hover_text_never_carries_a_count_of_things_to_look_at()
    {
        var text = TrayHoverText.Build(Running() with { Down = ["SABnzbd isn't answering"], Update = UpdateReady() });
        Assert.DoesNotContain("need a look", text);
        Assert.DoesNotContain("needs a look", text);
    }

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
        => Assert.Equal("Deluno - Stopped - choose Restart Deluno", TrayHoverText.Build(Running() with { State = TrayState.Error, Down = ["SABnzbd isn't answering"] }));

    [Fact]
    public void A_long_text_is_shortened_by_whole_parts_never_mid_word()
    {
        var status = Running() with
        {
            Address = "http://an-extremely-long-machine-name-on-the-local-network.example.internal:7879",
            Down = ["SABnzbd isn't answering", "NZBgeek isn't answering", "Weir isn't answering"],
            Update = TrayUpdate.Describe(true, UpdateStates.ReadyToRestart, "1.3.0-rc.10", true, true)
        };

        var text = TrayHoverText.Build(status);

        Assert.True(text.Length <= TrayHoverText.Limit, text);
        Assert.StartsWith("Deluno - SABnzbd isn't answering and 2 more", text);
        Assert.DoesNotContain("...", text);
    }

    [Fact]
    public void The_last_resort_cut_lands_on_a_word_boundary()
    {
        var status = Running();
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
    [InlineData("ConnectionChecked", true)]
    [InlineData("HealthChanged", true)]
    [InlineData("AttentionChanged", false)]
    [InlineData("SettingsChanged", true)]
    [InlineData("DownloadProgress", false)]
    [InlineData("ActivityEventAdded", false)]
    public void Only_the_pushes_that_can_move_the_marks_wake_the_health_watcher(string name, bool moves)
        => Assert.Equal(moves, TrayHealthWatcher.Moves(name));

    // ------------------------------------------------------------------ a check that failed says so (#1185)

    [Fact]
    public void A_failed_check_is_the_updates_pages_sentence_and_a_good_one_is_nothing()
    {
        const string sentence = "Couldn't check for updates (GitHub limit, retry after 10:35 AM).";
        Assert.Equal(sentence, TrayUpdate.CheckProblem(Deluno.Api.Updates.UpdateStates.Error, sentence));
        Assert.Null(TrayUpdate.CheckProblem(Deluno.Api.Updates.UpdateStates.UpToDate, "You are on the latest version."));
        Assert.Null(TrayUpdate.CheckProblem(Deluno.Api.Updates.UpdateStates.Error, " "));
    }

    [Fact]
    public void The_status_line_and_hover_text_carry_the_failed_check_after_what_is_wrong()
    {
        var status = new TrayStatus
        {
            State = TrayState.Running,
            HealthKnown = true,
            Down = ["SABnzbd isn't answering"],
            UpdateCheckProblem = "Couldn't check for updates (GitHub limit, retry after 10:35 AM)."
        };

        var words = TrayHoverText.StatusWords(status, withAddress: false);

        Assert.Equal("SABnzbd isn't answering - Couldn't check for updates (GitHub limit, retry after 10:35 AM)", words);
        Assert.True(("Deluno - " + words).Length <= TrayHoverText.Limit);
        Assert.DoesNotContain("Couldn't", TrayHoverText.StatusWords(status with { UpdateCheckProblem = null }));
    }

    [Fact]
    public void A_failed_check_never_pushes_out_what_is_wrong_when_the_text_is_too_long()
    {
        var status = new TrayStatus
        {
            State = TrayState.Running,
            HealthKnown = true,
            Address = "http://localhost:7879",
            Down = ["SABnzbd isn't answering", "NZBgeek isn't answering"],
            UpdateCheckProblem = "Couldn't check for updates (GitHub limit, retry after 10:35 AM)."
        };

        var text = TrayHoverText.Build(status);

        Assert.True(text.Length <= TrayHoverText.Limit);
        Assert.Contains("SABnzbd isn't answering and 1 more", text);
    }
}
