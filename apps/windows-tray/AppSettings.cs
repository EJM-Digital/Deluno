using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Tray;

public sealed class AppSettings
{
    public int Port { get; set; } = 7879;
    public string DataRoot { get; set; } = GetDefaultDataRoot();
    public string UpdateMode { get; set; } = Deluno.Api.Updates.UpdateModes.DownloadBackground;
    public string UpdateChannel { get; set; } = "stable";
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateSource { get; set; } = "https://github.com/jampat000/Deluno";

    public static AppSettingsPathState InspectPathState()
    {
        return new AppSettingsPathState(
            PrimaryConfigPath: GetConfigPath(),
            LegacyConfigPath: GetLegacyConfigPath(),
            PrimaryConfigExists: File.Exists(GetConfigPath()),
            LegacyConfigExists: File.Exists(GetLegacyConfigPath()),
            DefaultDataRoot: GetDefaultDataRoot(),
            LegacyDefaultDataRoot: GetLegacyDefaultDataRoot());
    }

    public static AppSettings Load()
    {
        var pathToRead = ResolveConfigPath(out var loadedFromLegacyPath);
        if (pathToRead is null)
        {
            var settings = new AppSettings
            {
                DataRoot = ResolveFallbackDataRoot()
            };

            if (ShouldPersistPrimaryConfig(settings.DataRoot))
            {
                TryPersistPrimaryConfig(settings);
            }

            return settings;
        }

        try
        {
            var json = File.ReadAllText(pathToRead);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.DataRoot = NormalizeDataRoot(settings.DataRoot);
            if (!Deluno.Api.Updates.UpdateModes.IsValid(settings.UpdateMode))
            {
                settings.UpdateMode = Deluno.Api.Updates.UpdateModes.DownloadBackground;
            }

            if (string.IsNullOrWhiteSpace(settings.UpdateChannel))
            {
                settings.UpdateChannel = "stable";
            }

            if (string.IsNullOrWhiteSpace(settings.UpdateSource))
            {
                settings.UpdateSource = "https://github.com/jampat000/Deluno";
            }

            // If settings were loaded from the legacy path, transparently persist a normalized
            // copy to the current path so subsequent runs use a single canonical location.
            if (loadedFromLegacyPath)
            {
                TryPersistPrimaryConfig(settings);
            }

            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var configPath = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        DataRoot = NormalizeDataRoot(DataRoot);
        File.WriteAllText(configPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static string? ResolveConfigPath(out bool loadedFromLegacyPath)
    {
        loadedFromLegacyPath = false;
        var configPath = GetConfigPath();
        if (File.Exists(configPath))
        {
            return configPath;
        }

        var legacyConfigPath = GetLegacyConfigPath();
        if (File.Exists(legacyConfigPath))
        {
            loadedFromLegacyPath = true;
            return legacyConfigPath;
        }

        return null;
    }

    private static string NormalizeDataRoot(string? current)
    {
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                var trimmed = current.Trim();
                if (Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(Path.GetFullPath(GetLegacyDefaultDataRoot()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    return GetLegacyDefaultDataRoot();
                }

                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return GetDefaultDataRoot();
            }
        }

        return GetDefaultDataRoot();
    }

    private static void TryPersistPrimaryConfig(AppSettings settings)
    {
        try
        {
            var configPath = GetConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var normalized = new AppSettings
            {
                Port = settings.Port,
                DataRoot = NormalizeDataRoot(settings.DataRoot),
                UpdateMode = settings.UpdateMode,
                UpdateChannel = settings.UpdateChannel,
                AutoCheckUpdates = settings.AutoCheckUpdates,
                UpdateSource = settings.UpdateSource
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(normalized, JsonOptions));
        }
        catch
        {
            // Keep legacy-path fallback behavior if primary-path persistence fails.
        }
    }

    private static string ResolveFallbackDataRoot()
    {
        var legacyDataRoot = GetLegacyDefaultDataRoot();
        if (LegacyDataRootShouldBeAdopted(legacyDataRoot))
        {
            return legacyDataRoot;
        }

        return GetDefaultDataRoot();
    }

    private static bool ShouldPersistPrimaryConfig(string dataRoot)
    {
        return Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(GetLegacyDefaultDataRoot()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool LegacyDataRootShouldBeAdopted(string legacyDataRoot)
    {
        try
        {
            if (!Directory.Exists(legacyDataRoot))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetConfigPath()
    {
        return Path.Combine(GetLocalApplicationDataPath(), "Deluno", "config", "deluno.json");
    }

    private static string GetLegacyConfigPath()
    {
        return Path.Combine(GetCommonApplicationDataPath(), "Deluno", "data", "deluno.json");
    }

    private static string GetDefaultDataRoot()
    {
        return Path.Combine(GetLocalApplicationDataPath(), "DelunoData");
    }

    private static string GetLegacyDefaultDataRoot()
    {
        return Path.Combine(GetCommonApplicationDataPath(), "Deluno", "data");
    }

    private static string GetLocalApplicationDataPath()
    {
        return GetPathOverride("DELUNO_TEST_LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private static string GetCommonApplicationDataPath()
    {
        return GetPathOverride("DELUNO_TEST_COMMONAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    }

    private static string? GetPathOverride(string environmentVariableName)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariableName);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record AppSettingsPathState(
    string PrimaryConfigPath,
    string LegacyConfigPath,
    bool PrimaryConfigExists,
    bool LegacyConfigExists,
    string DefaultDataRoot,
    string LegacyDefaultDataRoot);
