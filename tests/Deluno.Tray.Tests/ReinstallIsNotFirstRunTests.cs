using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1256: Setup over an existing Deluno runs the install hook, and a non-silent Setup starts the app with the first-run
/// marker. A reinstall over existing data must never show the component picker, the Windows Firewall question or the
/// Start-with-Windows question again; it is a first run only when no data or settings exist.
/// </summary>
public sealed class ReinstallIsNotFirstRunTests
{
    private static AppSettingsPathState State() => new(
        @"C:\Users\Example\AppData\Local\DelunoConfig\deluno.json",
        @"C:\ProgramData\Deluno\data\deluno.json",
        PrimaryConfigExists: false,
        LegacyConfigExists: false,
        DefaultDataRoot: @"C:\Users\Example\AppData\Local\DelunoData",
        LegacyDefaultDataRoot: @"C:\ProgramData\Deluno\data");

    [Fact]
    public void No_settings_and_no_data_is_a_true_first_install()
        => Assert.False(ExistingInstall.Detect(State(), _ => false, _ => false));

    [Fact]
    public void A_settings_file_means_a_reinstall()
        => Assert.True(ExistingInstall.Detect(State() with { PrimaryConfigExists = true }, _ => false, _ => false));

    [Fact]
    public void An_old_settings_file_means_a_reinstall()
        => Assert.True(ExistingInstall.Detect(State() with { LegacyConfigExists = true }, _ => false, _ => false));

    [Fact]
    public void The_settings_file_kept_inside_the_install_folder_by_older_builds_means_a_reinstall()
        => Assert.True(ExistingInstall.Detect(State(), path => path.EndsWith(@"Deluno\config\deluno.json", StringComparison.OrdinalIgnoreCase), _ => false));

    [Theory]
    [InlineData(@"C:\Users\Example\AppData\Local\DelunoData")]
    [InlineData(@"C:\ProgramData\Deluno\data")]
    public void Data_in_either_default_data_folder_means_a_reinstall(string folder)
        => Assert.True(ExistingInstall.Detect(State(), _ => false, f => f == folder));

    [Fact]
    public void A_reinstall_runs_none_of_the_first_run_steps_and_says_so()
    {
        var ran = false;
        var log = new List<string>();

        var asked = ExistingInstall.RunFirstRunSteps(existingInstall: true, () => ran = true, log.Add);

        Assert.False(asked);
        Assert.False(ran);
        Assert.Contains(log, l => l.Contains("reinstall") && l.Contains("picker") && l.Contains("firewall") && l.Contains("Start with Windows"));
    }

    [Fact]
    public void A_true_first_install_runs_the_first_run_steps()
    {
        var ran = false;

        Assert.True(ExistingInstall.RunFirstRunSteps(existingInstall: false, () => ran = true, _ => { }));
        Assert.True(ran);
    }

    [Fact]
    public void The_first_run_icon_is_not_shown_for_a_reinstall()
    {
        Assert.False(FirstRunTrayModel.IsFirstRun([], _ => "1.0.0", existingInstall: true));
        Assert.True(FirstRunTrayModel.IsFirstRun([], _ => "1.0.0", existingInstall: false));
        Assert.False(FirstRunTrayModel.IsFirstRun([], _ => null, existingInstall: false));
    }
}
