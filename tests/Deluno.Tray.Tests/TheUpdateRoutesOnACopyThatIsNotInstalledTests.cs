using Deluno.Api.Updates;
using Deluno.Contracts;
using Deluno.Tray;
using Microsoft.Extensions.Logging.Abstractions;

namespace Deluno.Tray.Tests;

/// <summary>
/// The Windows update orchestrator behind <c>/api/updates</c>, on a copy that is not a Velopack install (a test run, a
/// developer build, a zip): it says so plainly, offers no check, download or restart, saves only valid preferences, lower-cases
/// the channel, refreshes its feed on a channel change, and never saves over a settings file it cannot read. Real settings
/// files in a temporary folder; the installed-copy half (a real feed, a real download, a real restart) is the
/// <c>update.in-app</c> golden-path scenario.
/// </summary>
public sealed class TheUpdateRoutesOnACopyThatIsNotInstalledTests : IDisposable
{
    private readonly string? _originalLocalAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _originalCommonAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-update-routes-" + Guid.NewGuid().ToString("N"));

    public TheUpdateRoutesOnACopyThatIsNotInstalledTests()
    {
        // As Program.cs does at start; with no hook argument it only sets the locator, which finds no install here.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _originalLocalAppData);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _originalCommonAppData);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private static VelopackUpdateOrchestrator Orchestrator() => new(NullLogger<VelopackUpdateOrchestrator>.Instance);

    private string ConfigPath => Path.Combine(_root, "local", "DelunoConfig", "deluno.json");

    [Fact]
    public async Task The_status_says_the_copy_is_not_installed_and_offers_no_check_download_or_restart()
    {
        var status = await Orchestrator().GetStatusAsync(CancellationToken.None);

        Assert.Equal(UpdateInstallKinds.Manual, status.InstallKind);
        Assert.False(status.IsInstalled);
        Assert.False(status.CanCheck);
        Assert.False(status.CanDownload);
        Assert.False(status.CanApply);
        Assert.Equal("This runtime is not installed through Velopack. Use a packaged installer to enable in-app updates.", status.Message);
        Assert.Contains("Use the latest Windows installer package to move onto the supported updater path.", status.Notes);
        Assert.Contains(status.Notes, note => note.StartsWith("Updates are checked against the ", StringComparison.Ordinal));
        Assert.Contains(status.Notes, note => note.StartsWith("A backup is taken before every install", StringComparison.Ordinal));
        Assert.DoesNotContain(status.Notes, note => note.Contains("Legacy settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_left_at_the_old_path_are_copied_to_the_current_one_and_the_status_says_so()
    {
        var legacy = Path.Combine(_root, "common", "Deluno", "data", "deluno.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "{\"port\":7999}");
        Assert.False(File.Exists(ConfigPath));

        // Reading the status loads the settings, which copies the old file across before the notes are worked out.
        var status = await Orchestrator().GetStatusAsync(CancellationToken.None);

        Assert.Contains("Legacy settings were detected and migrated to the current config path.", status.Notes);
        Assert.True(File.Exists(ConfigPath));
        Assert.Contains("7999", File.ReadAllText(ConfigPath));
        Assert.Equal("{\"port\":7999}", File.ReadAllText(legacy));
    }

    [Fact]
    public async Task Checking_downloading_and_restarting_on_a_copy_that_is_not_installed_say_so_and_change_nothing()
    {
        var orchestrator = Orchestrator();

        var check = await orchestrator.CheckForUpdatesAsync(CancellationToken.None);
        var download = await orchestrator.DownloadUpdatesAsync(CancellationToken.None);
        var apply = await orchestrator.ApplyAndRestartNowAsync(CancellationToken.None);

        Assert.Equal("Updates are available only for a Velopack-installed app.", check.Message);
        Assert.Equal("Download controls require a Velopack-installed app.", download.Message);
        Assert.Equal("Restart apply is only supported for Velopack installs.", apply.Message);
        foreach (var status in new[] { check, download, apply })
        {
            Assert.False(status.UpdateAvailable);
            Assert.False(status.RestartRequired);
            Assert.Null(status.LastError);
            Assert.Null(status.LatestVersion);
        }

        Assert.False(orchestrator.Snapshot().IsInstalled);
    }

    [Fact]
    public async Task Saving_preferences_keeps_only_a_valid_mode_lower_cases_the_channel_and_follows_the_new_channel()
    {
        var orchestrator = Orchestrator();
        var first = await orchestrator.GetPreferencesAsync(CancellationToken.None);

        var saved = await orchestrator.SavePreferencesAsync(new UpdatePreferencesRequest("download-background", "RC", false), CancellationToken.None);

        Assert.Equal("download-background", saved.Mode);
        Assert.Equal("rc", saved.Channel);
        Assert.False(saved.AutoCheck);
        Assert.Equal("rc", (await orchestrator.GetStatusAsync(CancellationToken.None)).Channel);
        var onDisk = File.ReadAllText(ConfigPath);
        Assert.Contains("rc", onDisk);

        // A mode that is not one of the three is ignored, the rest of the request still lands, and an empty channel changes nothing.
        var second = await orchestrator.SavePreferencesAsync(new UpdatePreferencesRequest("whenever", " ", true), CancellationToken.None);
        Assert.Equal("download-background", second.Mode);
        Assert.Equal("rc", second.Channel);
        Assert.True(second.AutoCheck);
        Assert.NotEqual(first.Channel, second.Channel);
        Assert.Equal(second, await orchestrator.GetPreferencesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_read_is_never_saved_over_and_the_refusal_is_in_plain_words()
    {
        const string cut = "{\n  \"updateMode\": \"notify-only\",\n  \"updateChannel\": \"st";
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, cut);

        var failure = await Assert.ThrowsAsync<PlainFailureException>(
            () => Orchestrator().SavePreferencesAsync(new UpdatePreferencesRequest("download-background", "rc", true), CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
        Assert.EndsWith("Nothing was changed.", failure.Message);
        Assert.Equal(cut, File.ReadAllText(ConfigPath));
    }
}

/// <summary>A stable install stays on the stable feed; any other channel opts in to pre-releases; the channel is stamped at pack time.</summary>
public sealed class TheUpdateChannelDecidesWhichReleasesAreSeenTests
{
    [Theory]
    [InlineData("stable", false)]
    [InlineData("Stable", false)]
    [InlineData(" stable ", false)]
    [InlineData("rc", true)]
    [InlineData("rc.30", true)]
    [InlineData("beta", true)]
    public void Only_the_stable_channel_leaves_pre_releases_out(string channel, bool includesPrereleases)
        => Assert.Equal(includesPrereleases, UpdateChannels.IncludesPrereleases(channel));

    [Fact]
    public void The_channel_is_stamped_into_the_build_by_the_packer_and_a_plain_build_is_stable()
    {
        var stamped = typeof(AppSettings).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "DelunoUpdateChannel");
        Assert.Equal("stable", stamped.Value);

        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "scripts", "pack-windows-installer.ps1")))
        {
            folder = folder.Parent;
        }

        var packer = File.ReadAllText(Path.Combine(folder!.FullName, "scripts", "pack-windows-installer.ps1"));
        Assert.Contains("-p:DelunoUpdateChannel=$Channel", packer);
    }
}
