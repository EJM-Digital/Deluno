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

    public int Port { get; set; } = 7879;
    public string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DelunoData");
    public string UpdateMode { get; set; } = Deluno.Api.Updates.UpdateModes.DownloadBackground;
    public string UpdateChannel { get; set; } = "stable";
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateSource { get; set; } = "https://github.com/jampat000/Deluno";

    public static AppSettings Load()
    {
        var pathToRead = ResolveConfigPath();
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

    private static string? ResolveConfigPath()
    {
        if (File.Exists(_configPath))
        {
            return _configPath;
        }

        if (File.Exists(_legacyConfigPath))
        {
            return _legacyConfigPath;
        }

        return null;
    }

    private static string NormalizeDataRoot(string? current)
    {
        if (!string.IsNullOrWhiteSpace(current))
        {
            return current;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DelunoData");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
