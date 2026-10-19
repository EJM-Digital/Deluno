using System.Text.Json;
using Deluno.Tray;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Deluno.Tray.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string? _originalLocalAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA");
    private readonly string? _originalCommonAppData = Environment.GetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deluno-tray-tests", Guid.NewGuid().ToString("N"));

    public AppSettingsTests()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", Path.Combine(_root, "local"));
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", Path.Combine(_root, "common"));
    }

    [Fact]
    public void Load_UsesLegacyDataRoot_WhenLegacyDataExistsWithoutConfig()
    {
        var legacyDataRoot = Path.Combine(_root, "common", "Deluno", "data");
        Directory.CreateDirectory(legacyDataRoot);
        File.WriteAllText(Path.Combine(legacyDataRoot, "platform.db"), "seed");

        var pathState = AppSettings.InspectPathState();
        Assert.Equal(Path.Combine(_root, "common", "Deluno", "data"), pathState.LegacyDefaultDataRoot);

        var settings = AppSettings.Load();

        Assert.Equal(legacyDataRoot, settings.DataRoot);

        var primaryConfigPath = Path.Combine(_root, "local", "DelunoConfig", "deluno.json");
        Assert.True(File.Exists(primaryConfigPath));

        var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(primaryConfigPath), SerializerOptions);
        Assert.NotNull(persisted);
        Assert.Equal(legacyDataRoot, persisted!.DataRoot);
    }

    [Fact]
    public void Load_UsesPrimaryDefaultDataRoot_WhenLegacyDataIsMissing()
    {
        var settings = AppSettings.Load();

        Assert.Equal(Path.Combine(_root, "local", "DelunoData"), settings.DataRoot);
        Assert.False(File.Exists(Path.Combine(_root, "local", "DelunoConfig", "deluno.json")));
    }

    // #1361: a fresh install does not look for updates on its own until the setup guide asks and the person says yes.
    [Fact]
    public void A_fresh_install_has_the_background_update_check_off()
    {
        Assert.False(AppSettings.Load().AutoCheckUpdates);
    }

    [Fact]
    public void A_fresh_install_that_adopts_old_data_also_starts_with_the_check_off_and_keeps_it_off_on_the_next_load()
    {
        var legacy = Path.Combine(_root, "common", "Deluno", "data");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "platform.db"), "seed");
        Assert.False(AppSettings.Load().AutoCheckUpdates);
        Assert.False(AppSettings.Load().AutoCheckUpdates);
    }

    [Fact]
    public void An_existing_install_whose_file_never_mentioned_the_check_keeps_checking()
    {
        var path = Path.Combine(_root, "local", "DelunoConfig", "deluno.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"port\": 7879, \"dataRoot\": \"" + Path.Combine(_root, "d").Replace("\\", "\\\\") + "\" }");
        Assert.True(AppSettings.Load().AutoCheckUpdates);
    }

    [Fact]
    public void Saying_yes_in_the_setup_guide_turns_the_check_on_and_it_stays_on()
    {
        Assert.Null(AppSettings.Update(saved => saved.AutoCheckUpdates = true));
        Assert.True(AppSettings.Load().AutoCheckUpdates);
    }

    [Fact]
    public void Load_PreservesLegacyConfigCustomDataRoot_AndMigratesConfig()
    {
        var customDataRoot = Path.Combine(_root, "shared-data");
        Directory.CreateDirectory(customDataRoot);

        var pathState = AppSettings.InspectPathState();
        Assert.Equal(Path.Combine(_root, "common", "Deluno", "data"), pathState.LegacyDefaultDataRoot);

        var legacyConfigPath = Path.Combine(_root, "common", "Deluno", "data", "deluno.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyConfigPath)!);
        File.WriteAllText(
            legacyConfigPath,
            JsonSerializer.Serialize(
                new AppSettings
                {
                    Port = 8989,
                    DataRoot = customDataRoot,
                    UpdateMode = "notify-only",
                    UpdateChannel = "beta",
                    AutoCheckUpdates = false,
                    UpdateSource = "https://example.invalid/Deluno"
                },
                SerializerOptions));

        var settings = AppSettings.Load();

        Assert.Equal(8989, settings.Port);
        Assert.Equal(customDataRoot, settings.DataRoot);
        Assert.Equal("notify-only", settings.UpdateMode);
        Assert.Equal("beta", settings.UpdateChannel);
        Assert.False(settings.AutoCheckUpdates);
        Assert.Equal("https://example.invalid/Deluno", settings.UpdateSource);

        var primaryConfigPath = Path.Combine(_root, "local", "DelunoConfig", "deluno.json");
        Assert.True(File.Exists(primaryConfigPath));

        var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(primaryConfigPath), SerializerOptions);
        Assert.NotNull(persisted);
        Assert.Equal(customDataRoot, persisted!.DataRoot);
    }

    /// <summary>
    /// #939: the tray's "Let any address in" recovery action round-trips
    /// through Save/Load the same as every other setting, so
    /// DelunoServer/ServiceHost see it on the next start.
    /// </summary>
    [Fact]
    public void Save_and_load_round_trips_AllowedHostsForceDisabled()
    {
        var settings = AppSettings.Load();
        Assert.False(settings.AllowedHostsForceDisabled);

        settings.AllowedHostsForceDisabled = true;
        settings.Save();

        var reloaded = AppSettings.Load();
        Assert.True(reloaded.AllowedHostsForceDisabled);
    }

    /// <summary>#1177: the pointer to a custom data root lives outside the install folder Velopack's Setup replaces.</summary>
    [Fact]
    public void A_custom_data_root_is_still_found_after_the_install_folder_is_removed()
    {
        var custom = Path.Combine(_root, "elsewhere", "MyData");
        var settings = AppSettings.Load();
        settings.DataRoot = custom;
        settings.Save();
        var install = Path.Combine(_root, "local", "Deluno");
        Directory.CreateDirectory(Path.Combine(install, "current"));

        Directory.Delete(install, recursive: true);

        Assert.Equal(custom, AppSettings.Load().DataRoot);
        Assert.False(Directory.Exists(Path.Combine(install, "config")));
    }

    [Fact]
    public void A_pointer_kept_in_the_install_folder_is_read_and_copied_to_the_new_place()
    {
        var custom = Path.Combine(_root, "elsewhere", "OldData");
        var old = Path.Combine(_root, "local", "Deluno", "config", "deluno.json");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, JsonSerializer.Serialize(new { dataRoot = custom, port = 7881 }, SerializerOptions));

        var loaded = AppSettings.Load();

        Assert.Equal(custom, loaded.DataRoot);
        Assert.Equal(7881, loaded.Port);
        var current = Path.Combine(_root, "local", "DelunoConfig", "deluno.json");
        Assert.True(File.Exists(current));

        Directory.Delete(Path.Combine(_root, "local", "Deluno"), recursive: true);
        Assert.Equal(custom, AppSettings.Load().DataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DELUNO_TEST_LOCALAPPDATA", _originalLocalAppData);
        Environment.SetEnvironmentVariable("DELUNO_TEST_COMMONAPPDATA", _originalCommonAppData);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for test temp directories.
        }
    }
}
