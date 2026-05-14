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

        var primaryConfigPath = Path.Combine(_root, "local", "Deluno", "config", "deluno.json");
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
        Assert.False(File.Exists(Path.Combine(_root, "local", "Deluno", "config", "deluno.json")));
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

        var primaryConfigPath = Path.Combine(_root, "local", "Deluno", "config", "deluno.json");
        Assert.True(File.Exists(primaryConfigPath));

        var persisted = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(primaryConfigPath), SerializerOptions);
        Assert.NotNull(persisted);
        Assert.Equal(customDataRoot, persisted!.DataRoot);
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
