using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Tray;

public sealed class AppSettings
{
    private static readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Deluno", "config", "deluno.json");

    private static readonly string _legacyConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Deluno", "data", "deluno.json");

    private static readonly string _defaultDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DelunoData");

    private static readonly string _legacyDefaultDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Deluno", "data");

    public int Port { get; set; } = 7879;
    public string DataRoot { get; set; } = _defaultDataRoot;
    public string UpdateMode { get; set; } = Deluno.Api.Updates.UpdateModes.DownloadBackground;
    public string UpdateChannel { get; set; } = "stable";
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateSource { get; set; } = "https://github.com/jampat000/Deluno";

    public static AppSettingsPathState InspectPathState()
    {
        return new AppSettingsPathState(
            PrimaryConfigPath: _configPath,
            LegacyConfigPath: _legacyConfigPath,
            PrimaryConfigExists: File.Exists(_configPath),
            LegacyConfigExists: File.Exists(_legacyConfigPath),
            DefaultDataRoot: _defaultDataRoot,
            LegacyDefaultDataRoot: _legacyDefaultDataRoot);
    }

    public static AppSettings Load()
    {
        var pathToRead = ResolveConfigPath(out var loadedFromLegacyPath);
        if (pathToRead is null)
        {
            return new AppSettings();
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
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        DataRoot = NormalizeDataRoot(DataRoot);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static string? ResolveConfigPath(out bool loadedFromLegacyPath)
    {
        loadedFromLegacyPath = false;
        if (File.Exists(_configPath))
        {
            return _configPath;
        }

        if (File.Exists(_legacyConfigPath))
        {
            loadedFromLegacyPath = true;
            return _legacyConfigPath;
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
                    .Equals(Path.GetFullPath(_legacyDefaultDataRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    return _legacyDefaultDataRoot;
                }

                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return _defaultDataRoot;
            }
        }

        return _defaultDataRoot;
    }

    private static void TryPersistPrimaryConfig(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            var normalized = new AppSettings
            {
                Port = settings.Port,
                DataRoot = NormalizeDataRoot(settings.DataRoot),
                UpdateMode = settings.UpdateMode,
                UpdateChannel = settings.UpdateChannel,
                AutoCheckUpdates = settings.AutoCheckUpdates,
                UpdateSource = settings.UpdateSource
            };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(normalized, JsonOptions));
        }
        catch
        {
            // Keep legacy-path fallback behavior if primary-path persistence fails.
        }
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
