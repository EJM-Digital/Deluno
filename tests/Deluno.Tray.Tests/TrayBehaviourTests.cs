using Microsoft.Win32;

namespace Deluno.Tray.Tests;

/// <summary>The pieces behind the tray menu that act: the port change, the sign-in entry, the first-run question, the icon and the second launch (#1185).</summary>
public sealed class TrayBehaviourTests
{
    // ------------------------------------------------------------------ change port

    [Theory]
    [InlineData("7879", 7879)]
    [InlineData(" 8123 ", 8123)]
    [InlineData("65535", 65535)]
    public void A_port_is_a_whole_number_from_1_to_65535(string text, int expected)
        => Assert.Equal(expected, PortChoice.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-5")]
    [InlineData("80a")]
    [InlineData(null)]
    public void Anything_else_is_not_a_port(string? text)
        => Assert.Null(PortChoice.Parse(text));

    [Fact]
    public void A_port_in_use_by_another_program_is_refused_but_Delunos_own_is_not()
    {
        Assert.Contains("already using port 9000", PortChoice.Validate("9000", 7879, port => port == 9000));
        Assert.Null(PortChoice.Validate("7879", 7879, port => port == 7879));
        Assert.Equal("Enter a port number.", PortChoice.Validate(" ", 7879, _ => false));
        Assert.Equal("A port is a whole number from 1 to 65535.", PortChoice.Validate("abc", 7879, _ => false));
    }

    [Fact]
    public async Task A_free_port_restarts_Deluno_on_it_and_says_so()
    {
        var started = new List<int>();
        var changer = new PortChanger(_ => false, port => { started.Add(port); return Task.CompletedTask; });

        var result = await changer.ChangeAsync(7879, 8123);

        Assert.Equal(PortChangeOutcome.Changed, result.Outcome);
        Assert.Equal(8123, result.Port);
        Assert.Equal([8123], started);
        Assert.Contains("8123", result.Message);
    }

    [Fact]
    public async Task A_port_in_use_is_refused_before_anything_stops()
    {
        var started = new List<int>();
        var changer = new PortChanger(port => port == 9000, port => { started.Add(port); return Task.CompletedTask; });

        var result = await changer.ChangeAsync(7879, 9000);

        Assert.Equal(PortChangeOutcome.Refused, result.Outcome);
        Assert.Equal(7879, result.Port);
        Assert.Empty(started);
    }

    [Fact]
    public async Task The_same_port_changes_nothing()
    {
        var started = new List<int>();
        var changer = new PortChanger(_ => false, port => { started.Add(port); return Task.CompletedTask; });

        Assert.Equal(PortChangeOutcome.Unchanged, (await changer.ChangeAsync(7879, 7879)).Outcome);
        Assert.Empty(started);
    }

    [Fact]
    public async Task A_port_that_will_not_start_rolls_back_to_the_old_one()
    {
        var started = new List<int>();
        var changer = new PortChanger(_ => false, port =>
        {
            started.Add(port);
            return port == 8123 ? throw new InvalidOperationException("address already in use") : Task.CompletedTask;
        });

        var result = await changer.ChangeAsync(7879, 8123);

        Assert.Equal(PortChangeOutcome.RolledBack, result.Outcome);
        Assert.Equal(7879, result.Port);
        Assert.Equal([8123, 7879], started);
        Assert.Contains("back on port 7879", result.Message);
    }

    [Fact]
    public async Task When_even_the_old_port_will_not_start_it_says_to_restart()
    {
        var changer = new PortChanger(_ => false, _ => throw new InvalidOperationException("no"));

        var result = await changer.ChangeAsync(7879, 8123);

        Assert.Equal(PortChangeOutcome.Failed, result.Outcome);
        Assert.Contains("Restart Deluno", result.Message);
    }

    // ------------------------------------------------------------------ start with Windows

    private sealed class FakeSignIn(bool on = false) : ISignInEntry
    {
        public bool On { get; private set; } = on;
        public bool IsEnabled() => On;
        public void Enable() => On = true;
        public void Disable() => On = false;
    }

    [Fact]
    public void The_tick_flips_the_entry_both_ways()
    {
        var entry = new FakeSignIn();
        Assert.True(RunKeySignInEntry.Toggle(entry));
        Assert.True(entry.On);
        Assert.False(RunKeySignInEntry.Toggle(entry));
        Assert.False(entry.On);
    }

    [Fact]
    public void Only_a_Yes_turns_it_on()
    {
        var yes = new FakeSignIn();
        StartWithWindowsQuestion.Apply(true, yes);
        Assert.True(yes.On);

        var no = new FakeSignIn();
        StartWithWindowsQuestion.Apply(false, no);
        Assert.False(no.On);

        var unanswered = new FakeSignIn();
        StartWithWindowsQuestion.Apply(null, unanswered);
        Assert.False(unanswered.On);
    }

    [Fact]
    public void The_question_is_worded_as_the_standard_says()
        => Assert.Equal("Start Deluno when you sign in to Windows?", StartWithWindowsQuestion.Text);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void First_run_asks_once_and_stores_the_answer(bool answer, bool expectedOn)
    {
        var entry = new FakeSignIn();
        var asked = 0;
        var oldAnswer = InstallHooks.StartWithWindowsAnswer;
        var oldFactory = InstallHooks.SignInEntryFactory;
        var logs = Path.Combine(Path.GetTempPath(), "deluno-tray-test-" + Guid.NewGuid().ToString("N"));
        HookLog.PrimaryFolderOverride = logs;
        try
        {
            InstallHooks.StartWithWindowsAnswer = () => { asked++; return answer; };
            InstallHooks.SignInEntryFactory = () => entry;

            InstallHooks.AskStartWithWindows();

            Assert.Equal(1, asked);
            Assert.Equal(expectedOn, entry.On);
        }
        finally
        {
            InstallHooks.StartWithWindowsAnswer = oldAnswer;
            InstallHooks.SignInEntryFactory = oldFactory;
            HookLog.PrimaryFolderOverride = null;
            try { Directory.Delete(logs, recursive: true); } catch { }
        }
    }

    [Fact]
    public void The_entry_is_a_per_user_value_holding_the_quoted_path_with_no_flags()
    {
        var subKey = @"Software\DelunoTests\" + Guid.NewGuid().ToString("N");
        try
        {
            var entry = new RunKeySignInEntry(@"C:\Apps\Deluno\current\Deluno.exe", subKey);
            Assert.False(entry.IsEnabled());

            entry.Enable();

            Assert.True(entry.IsEnabled());
            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
            {
                Assert.Equal("\"C:\\Apps\\Deluno\\current\\Deluno.exe\"", key!.GetValue("Deluno"));
            }

            entry.Disable();
            Assert.False(entry.IsEnabled());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\DelunoTests", throwOnMissingSubKey: false);
        }
    }

    // ------------------------------------------------------------------ the icon

    private static readonly TrayIconSpec Plain = new(TrayDot.Green, TrayMark.None);

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void The_icon_is_drawn_at_the_size_asked_for(int size)
    {
        using var bitmap = TrayIconRenderer.RenderBitmap(Plain, size);
        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);
    }

    [Theory]
    [InlineData(TrayDot.Green)]
    [InlineData(TrayDot.Amber)]
    [InlineData(TrayDot.Red)]
    [InlineData(TrayDot.Starting)]
    public void The_dot_sits_in_the_bottom_right_corner_in_its_colour(TrayDot dot)
    {
        using var bitmap = TrayIconRenderer.RenderBitmap(new TrayIconSpec(dot, TrayMark.None), 32);
        var bounds = TrayIconRenderer.DotBounds(32);
        Assert.True(bounds.X > 8, "the dot stays in the corner");

        if (dot != TrayDot.Starting)
        {
            // Three pixels in from the ring, on the dot's own colour.
            var pixel = bitmap.GetPixel(bounds.X + 3, bounds.Y + bounds.Height / 2);
            Assert.Equal(TrayIconRenderer.DotColor(dot).ToArgb(), pixel.ToArgb());
        }
    }

    [Fact]
    public void Green_amber_and_red_are_three_different_colours()
        => Assert.Equal(3, new[] { TrayDot.Green, TrayDot.Amber, TrayDot.Red }.Select(dot => TrayIconRenderer.DotColor(dot).ToArgb()).Distinct().Count());

    [Fact]
    public void The_off_frame_of_a_blink_leaves_the_dot_out()
    {
        var starting = new TrayIconSpec(TrayDot.Starting, TrayMark.None);
        using var on = TrayIconRenderer.RenderBitmap(starting, 32, dotShown: true);
        using var off = TrayIconRenderer.RenderBitmap(starting, 32, dotShown: false);
        var b = TrayIconRenderer.DotBounds(32);

        Assert.NotEqual(on.GetPixel(b.X + 3, b.Y + b.Height / 2), off.GetPixel(b.X + 3, b.Y + b.Height / 2));
    }

    [Theory]
    [InlineData(TrayMark.Paused)]
    [InlineData(TrayMark.UpdateReady)]
    public void An_extra_mark_is_in_the_opposite_corner_and_leaves_the_dot_alone(TrayMark mark)
    {
        using var plain = TrayIconRenderer.RenderBitmap(Plain, 32);
        using var marked = TrayIconRenderer.RenderBitmap(new TrayIconSpec(TrayDot.Green, mark), 32);
        var dot = TrayIconRenderer.DotBounds(32);
        var corner = TrayIconRenderer.MarkBounds(32);

        Assert.NotEqual(plain.GetPixel(corner.Width / 2, corner.Height / 2), marked.GetPixel(corner.Width / 2, corner.Height / 2));
        Assert.Equal(plain.GetPixel(dot.X + 3, dot.Y + dot.Height / 2), marked.GetPixel(dot.X + 3, dot.Y + dot.Height / 2));
        Assert.True(corner.Right <= dot.X + 4, "the two marks do not sit on each other");
    }

    [Fact]
    public void The_pause_bars_and_the_update_arrow_look_different()
    {
        using var paused = TrayIconRenderer.RenderBitmap(new TrayIconSpec(TrayDot.Green, TrayMark.Paused), 32);
        using var update = TrayIconRenderer.RenderBitmap(new TrayIconSpec(TrayDot.Green, TrayMark.UpdateReady), 32);
        var corner = TrayIconRenderer.MarkBounds(32);

        var differs = false;
        for (var x = 0; x < corner.Width; x++)
        {
            for (var y = 0; y < corner.Height; y++)
            {
                differs |= paused.GetPixel(x, y) != update.GetPixel(x, y);
            }
        }

        Assert.True(differs);
    }

    [Fact]
    public void A_rendered_icon_is_owned_and_can_be_disposed_and_replaced_many_times()
    {
        for (var i = 0; i < 50; i++)
        {
            using var icon = TrayIconRenderer.Render(new TrayIconSpec(i % 2 == 0 ? TrayDot.Red : TrayDot.Green, TrayMark.None), 16);
            Assert.Equal(16, icon.Icon.Width);
        }
    }

    // ------------------------------------------------------------------ a second launch

    [Fact]
    public void A_second_launch_reaches_the_running_copy()
    {
        var name = @"Local\DelunoTrayTest" + Guid.NewGuid().ToString("N");
        using var heard = new ManualResetEventSlim();
        using var listener = SecondLaunchSignal.Listen(heard.Set, name);

        Assert.True(SecondLaunchSignal.Notify(name));

        Assert.True(heard.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_second_launch_with_nothing_running_finds_no_one()
        => Assert.False(SecondLaunchSignal.Notify(@"Local\DelunoTrayNobodyHome" + Guid.NewGuid().ToString("N")));
}
