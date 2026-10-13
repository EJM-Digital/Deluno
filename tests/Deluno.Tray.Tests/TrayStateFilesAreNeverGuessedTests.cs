using System.Text.Json;
using Deluno.Contracts;
using Deluno.Tests.Support;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// #1260, the tray's own files: <c>deluno.json</c> (and everything that follows from where it says the data is), the started
/// clients, the pending-install recorder. A busy file (another handle open, or a delete pending) is waited for and then
/// reported; a damaged one is copied aside and logged; neither is ever replaced by defaults.
/// </summary>
public sealed class TrayStateFilesAreNeverGuessedTests : IDisposable
{
    private const string Good = "{\n  \"port\": 7999,\n  \"dataRoot\": \"%ROOT%\",\n  \"updateMode\": \"notify-only\"\n}";

    private readonly string? _originalLocalAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _originalCommonAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-tray-guess-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedStateFileSystem _io = new();

    private string ConfigPath => Path.Combine(_root, "local", "DelunoConfig", "deluno.json");
    private string CustomRoot => Path.Combine(_root, "custom-data");

    public TrayStateFilesAreNeverGuessedTests()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
        HookLog.PrimaryFolderOverride = Path.Combine(_root, "logs");
        HookLog.FallbackFolderOverride = Path.Combine(_root, "logs-fallback");
        AppSettings.ForgetLastRead();
    }

    public void Dispose()
    {
        AppSettings.Io = RealStateFileSystem.Instance;
        AppSettings.ForgetLastRead();
        HookLog.PrimaryFolderOverride = null;
        HookLog.FallbackFolderOverride = null;
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _originalLocalAppData);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _originalCommonAppData);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void SeedSettings(string? text = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, (text ?? Good).Replace("%ROOT%", CustomRoot.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
    }

    private string LogText()
    {
        var folder = Path.Combine(_root, "logs");
        return Directory.Exists(folder) ? string.Join('\n', Directory.GetFiles(folder).Select(File.ReadAllText)) : string.Empty;
    }

    // ---------------------------------------------------------------- AppSettings: busy

    [Fact]
    public void A_settings_file_held_by_another_handle_is_waited_for_then_reported_never_replaced_by_defaults()
    {
        SeedSettings();
        var before = File.ReadAllText(ConfigPath);
        using var other = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AppSettings.Io = _io;

        var settings = AppSettings.Load();

        Assert.NotNull(settings.LoadProblem);
        Assert.Contains("something else was using it", settings.LoadProblem);
        Assert.True(settings.ValuesAreGuessed);
        Assert.Equal(StateFile.ReadAttempts - 1, _io.Sleeps.Count);
        Assert.Contains("Nothing was saved", settings.Save());
        other.Dispose();
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void A_settings_file_with_a_delete_pending_is_busy_and_save_update_and_start_all_refuse()
    {
        SeedSettings();
        var before = File.ReadAllText(ConfigPath);
        _io.ReadFault = (_, _) => ScriptedStateFileSystem.AccessDenied();
        AppSettings.Io = _io;

        var settings = AppSettings.Load();

        Assert.True(settings.ValuesAreGuessed);
        Assert.Contains("Nothing was saved", settings.Save());
        Assert.Contains("Nothing was changed", AppSettings.Update(s => s.Port = 1234));
        Assert.Throws<StateFileException>(AppSettings.LoadForStart);
        Assert.Throws<StateFileException>(AppSettings.RequireDataRoot);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void A_settings_file_that_is_busy_for_a_moment_is_read_when_it_clears_and_no_problem_is_reported()
    {
        SeedSettings();
        _io.ReadFault = (_, attempt) => attempt <= 6 ? ScriptedStateFileSystem.SharingViolation() : null;
        AppSettings.Io = _io;

        var settings = AppSettings.Load();

        Assert.Null(settings.LoadProblem);
        Assert.False(settings.ValuesAreGuessed);
        Assert.Equal(7999, settings.Port);
        Assert.Equal(CustomRoot, settings.DataRoot);
        Assert.Null(settings.Save());
    }

    [Fact]
    public void A_busy_settings_file_after_a_good_read_gives_the_values_last_read_not_defaults_and_still_refuses_to_save()
    {
        SeedSettings();
        AppSettings.Io = _io;
        Assert.Equal(7999, AppSettings.Load().Port);
        _io.ReadFault = (_, _) => ScriptedStateFileSystem.SharingViolation();

        var settings = AppSettings.Load();

        Assert.False(settings.ValuesAreGuessed);
        Assert.NotNull(settings.LoadProblem);
        Assert.Equal(7999, settings.Port);
        Assert.Equal(CustomRoot, settings.DataRoot);
        Assert.Equal(CustomRoot, AppSettings.RequireDataRoot());
        Assert.Contains("Nothing was saved", settings.Save());
        Assert.NotNull(AppSettings.Update(s => s.Port = 1));
        Assert.Equal(CustomRoot, AppSettings.LoadForStart().DataRoot);
    }

    // ---------------------------------------------------------------- AppSettings: truncated, missing

    [Fact]
    public void A_cut_short_settings_file_is_copied_aside_logged_and_never_saved_over()
    {
        const string cut = "{\n  \"port\": 7999,\n  \"dataRoot\": \"D:\\\\Media\\\\Del";
        SeedSettings(cut);
        _io.Now = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
        AppSettings.Io = _io;

        var settings = AppSettings.Load();
        var again = AppSettings.Load();

        Assert.True(settings.ValuesAreGuessed);
        Assert.Contains("damaged", settings.LoadProblem);
        Assert.Contains("deluno.json.unreadable-20261014-100000", settings.LoadProblem);
        Assert.Contains("Nothing was saved", settings.Save());
        Assert.NotNull(AppSettings.Update(s => s.Port = 1));
        Assert.Equal(cut, File.ReadAllText(ConfigPath));
        var copy = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, "deluno.json.unreadable-*"));
        Assert.Equal(cut, File.ReadAllText(copy));
        Assert.Contains("damaged", LogText());
        Assert.Contains("deluno.json.unreadable-20261014-100000", LogText());
        Assert.True(again.ValuesAreGuessed);
    }

    [Fact]
    public void A_start_with_an_unreadable_settings_file_does_not_start_on_the_default_folders_and_says_why_in_plain_words()
    {
        SeedSettings("{\"port\":79");
        AppSettings.Io = _io;

        var failure = Assert.Throws<StateFileException>(AppSettings.LoadForStart);

        Assert.Contains("did not start", failure.Message);
        Assert.Contains("could hide your data", failure.Message);
        Assert.Contains("copy was kept", failure.Message);
        // The supervisor's balloon carries this reason after "Deluno could not be started after N tries ...:".
        var balloon = HostRestartReporter.GaveUpMessage(3, TimeSpan.FromMinutes(2), failure.Message);
        Assert.Contains("did not start, because starting on the default folders", balloon);
    }

    [Fact]
    public void A_missing_settings_file_is_a_first_run_with_the_defaults_and_no_problem()
    {
        var settings = AppSettings.Load();

        Assert.Null(settings.LoadProblem);
        Assert.False(settings.ValuesAreGuessed);
        Assert.Equal(Path.Combine(_root, "local", "DelunoData"), settings.DataRoot);
        Assert.Equal(Path.Combine(_root, "local", "DelunoData"), AppSettings.LoadForStart().DataRoot);
    }

    [Fact]
    public void A_good_settings_file_is_saved_atomically_and_keeps_every_field()
    {
        SeedSettings();
        var settings = AppSettings.Load();
        settings.Port = 8000;

        Assert.Null(settings.Save());

        var reloaded = AppSettings.Load();
        Assert.Equal(8000, reloaded.Port);
        Assert.Equal(CustomRoot, reloaded.DataRoot);
        Assert.Equal("notify-only", reloaded.UpdateMode);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!, "*.tmp"));
    }

    [Fact]
    public void A_settings_save_that_cannot_land_says_so()
    {
        SeedSettings();
        var settings = AppSettings.Load();
        _io.MoveFault = (_, _) => ScriptedStateFileSystem.SharingViolation();
        AppSettings.Io = _io;

        Assert.Contains("could not save its settings", settings.Save());
    }

    [Fact]
    public async Task Two_settings_changes_at_once_lose_neither()
    {
        SeedSettings();
        var port = Task.Run(() => { for (var i = 0; i < 15; i++) { Assert.Null(AppSettings.Update(s => s.Port++)); } });
        var hosts = Task.Run(() => { for (var i = 0; i < 15; i++) { Assert.Null(AppSettings.Update(s => s.AllowedHostsForceDisabled = i % 2 == 0)); } });
        await Task.WhenAll(port, hosts);

        var settings = AppSettings.Load();
        Assert.Equal(7999 + 15, settings.Port);
        Assert.Equal(CustomRoot, settings.DataRoot);
    }

    // ---------------------------------------------------------------- knock-on users of the settings

    [Fact]
    public async Task The_pre_update_backup_never_copies_the_default_folder_when_the_settings_cannot_be_read()
    {
        SeedSettings("{\"dataRoot\":\"D:\\\\Me");
        Directory.CreateDirectory(Path.Combine(_root, "local", "DelunoData"));
        File.WriteAllText(Path.Combine(_root, "local", "DelunoData", "platform.json"), "{}");

        var failure = await Assert.ThrowsAsync<StateFileException>(() => PreUpdateBackupGate.TakePreUpdateBackupAsync(null, CancellationToken.None));

        Assert.Contains("did not use the default folders instead", failure.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "local", "DelunoData", "backups")));
    }

    [Fact]
    public void Component_paths_follow_the_data_root_the_settings_name_and_refuse_a_guess()
    {
        SeedSettings();
        Assert.Equal(CustomRoot, AppSettings.RequireDataRoot());

        File.WriteAllText(ConfigPath, "{\"dataRoot\":\"");
        // Still the root this process last read, but never a guess; a process that never read it has nothing to go on.
        Assert.Equal(CustomRoot, AppSettings.RequireDataRoot());
        AppSettings.ForgetLastRead();

        Assert.Throws<StateFileException>(AppSettings.RequireDataRoot);
    }

    [Fact]
    public void The_relaunch_heartbeat_lookup_with_unreadable_settings_looks_in_the_default_folders_and_says_so()
    {
        SeedSettings("{\"dataRoot\":\"");
        var defaultRoot = Path.Combine(_root, "local", "DelunoData");
        Directory.CreateDirectory(defaultRoot);
        File.WriteAllText(Path.Combine(defaultRoot, "jobs.db"), "x");

        var age = HeartbeatEvidence.AgeOnThisMachine();

        Assert.NotNull(age);
        Assert.Contains("default folders only", LogText());
    }

    [Fact]
    public void The_relaunch_heartbeat_lookup_follows_a_custom_data_root_when_the_settings_can_be_read()
    {
        SeedSettings();
        Directory.CreateDirectory(CustomRoot);
        File.WriteAllText(Path.Combine(CustomRoot, "jobs.db"), "x");

        var age = HeartbeatEvidence.AgeOnThisMachine();

        Assert.NotNull(age);
        Assert.DoesNotContain("default folders only", LogText());
    }

    // ---------------------------------------------------------------- config/started-components.json

    private string StartedPath => Path.Combine(_root, "started-components.json");

    [Fact]
    public void Started_clients_a_busy_file_keeps_the_other_clients_and_the_add_is_reported()
    {
        new FileStartedComponentsRegistry(StartedPath).Add("nzbget");
        var before = File.ReadAllText(StartedPath);
        _io.ReadFault = (_, _) => ScriptedStateFileSystem.SharingViolation();
        var busy = new FileStartedComponentsRegistry(StartedPath, _io);

        var failure = Assert.Throws<StateFileException>(() => busy.Add("deluge"));

        Assert.Contains("in use", failure.Message);
        Assert.Empty(busy.Read());
        Assert.Equal(before, File.ReadAllText(StartedPath));
    }

    [Fact]
    public void Started_clients_a_delete_pending_file_is_busy_too()
    {
        new FileStartedComponentsRegistry(StartedPath).Add("nzbget");
        _io.ReadFault = (_, _) => ScriptedStateFileSystem.AccessDenied();

        Assert.Throws<StateFileException>(() => new FileStartedComponentsRegistry(StartedPath, _io).Add("deluge"));

        Assert.Equal(["nzbget"], new FileStartedComponentsRegistry(StartedPath).Read());
    }

    [Fact]
    public void Started_clients_a_file_held_by_another_handle_with_no_sharing_is_busy()
    {
        new FileStartedComponentsRegistry(StartedPath).Add("nzbget");
        using var other = new FileStream(StartedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<StateFileException>(() => new FileStartedComponentsRegistry(StartedPath, _io).Add("deluge"));
    }

    [Fact]
    public void Started_clients_a_missing_file_is_a_first_add_and_adding_twice_changes_nothing()
    {
        var registry = new FileStartedComponentsRegistry(StartedPath, _io);
        Assert.Empty(registry.Read());

        registry.Add("nzbget");
        registry.Add("NZBGet");

        Assert.Equal(["nzbget"], registry.Read());
    }

    [Fact]
    public async Task Started_clients_two_adds_at_once_keep_both()
    {
        var a = Task.Run(() => { for (var i = 0; i < 12; i++) { new FileStartedComponentsRegistry(StartedPath).Add($"a{i}"); } });
        var b = Task.Run(() => { for (var i = 0; i < 12; i++) { new FileStartedComponentsRegistry(StartedPath).Add($"b{i}"); } });
        await Task.WhenAll(a, b);

        Assert.Equal(24, new FileStartedComponentsRegistry(StartedPath).Read().Count);
    }

    // ---------------------------------------------------------------- the picker's record of what is still to install

    [Fact]
    public void The_pending_install_recorder_reports_a_list_it_could_not_change_and_leaves_it_as_it_was()
    {
        PendingComponentInstalls.Add(CustomRoot, ["nzbget", "weir"], null, false, "limit");
        var path = PendingComponentInstalls.PathFor(CustomRoot);
        var before = File.ReadAllText(path);
        _io.ReadFault = (_, _) => ScriptedStateFileSystem.SharingViolation();

        var problems = PendingInstallRecorder.Record(
            [new ComponentInstallResult("nzbget", ComponentInstallOutcome.Installed, "ok")], CustomRoot, _io);

        Assert.NotEmpty(problems);
        Assert.All(problems, problem => Assert.Contains("in use", problem));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Contains("was not changed", LogText());
    }

    [Fact]
    public void The_pending_install_recorder_reports_nothing_when_the_list_can_be_changed()
    {
        PendingComponentInstalls.Add(CustomRoot, ["nzbget", "weir"], null, false, "limit");

        var problems = PendingInstallRecorder.Record([new ComponentInstallResult("nzbget", ComponentInstallOutcome.Installed, "ok")], CustomRoot);

        Assert.Empty(problems);
        Assert.Equal(["weir"], PendingComponentInstalls.Read(CustomRoot)!.Kinds);
    }

    // ---------------------------------------------------------------- the notice after Transmission's folder was written again

    [Fact]
    public void The_transmission_notice_busy_or_damaged_is_do_not_know_not_nothing_to_add_back()
    {
        var path = ComponentStateMigration.ReaddNoticePath(CustomRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new TransmissionReaddNotice(DateTimeOffset.UtcNow, false, [])));
        _io.ReadFault = (_, _) => ScriptedStateFileSystem.SharingViolation();

        Assert.Equal(StateFileKind.Busy, TransmissionReadd.PendingChecked(CustomRoot, _io).Kind);
        Assert.Null(TransmissionReadd.Pending(CustomRoot, _io));

        _io.ReadFault = null;
        File.WriteAllText(path, "{\"CreatedUtc\":\"2026");
        Assert.Equal(StateFileKind.Unreadable, TransmissionReadd.PendingChecked(CustomRoot, _io).Kind);

        File.Delete(path);
        Assert.Equal(StateFileKind.Missing, TransmissionReadd.PendingChecked(CustomRoot, _io).Kind);
    }

    // ---------------------------------------------------------------- config/download-layout.json

    [Fact]
    public void The_download_layout_record_busy_damaged_and_missing_are_told_apart()
    {
        Assert.Equal(StateFileKind.Missing, DownloadLayoutRecord.LoadChecked(CustomRoot, _io).Kind);
        Assert.Null(DownloadLayoutRecord.Save(CustomRoot, new DownloadLayout("E:\\Done", "E:\\Partial")));
        Assert.Equal("E:\\Done", DownloadLayoutRecord.LoadChecked(CustomRoot, _io).Value!.CompletedRoot);

        _io.ReadFault = (_, _) => ScriptedStateFileSystem.AccessDenied();
        Assert.Equal(StateFileKind.Busy, DownloadLayoutRecord.LoadChecked(CustomRoot, _io).Kind);
        _io.ReadFault = null;

        File.WriteAllText(DownloadLayoutRecord.PathFor(CustomRoot), "{\"CompletedRoot\":\"E:\\\\Do");
        Assert.Equal(StateFileKind.Unreadable, DownloadLayoutRecord.LoadChecked(CustomRoot, _io).Kind);
        File.WriteAllText(DownloadLayoutRecord.PathFor(CustomRoot), "{\"CompletedRoot\":\"\",\"IncompleteRoot\":\"\"}");
        Assert.Equal(StateFileKind.Unreadable, DownloadLayoutRecord.LoadChecked(CustomRoot, _io).Kind);
    }

    [Fact]
    public void A_download_layout_that_cannot_be_remembered_says_why()
    {
        _io.MoveFault = (_, _) => ScriptedStateFileSystem.SharingViolation();

        Assert.NotNull(DownloadLayoutRecord.Save(CustomRoot, new DownloadLayout("E:\\Done", "E:\\Partial"), _io));
    }
}
