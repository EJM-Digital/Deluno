using System.Text.Json;
using Deluno.Contracts;
using System.Text.Json.Serialization;
using System.Reflection;

namespace Deluno.Tray;

public sealed class AppSettings
{
    public int Port { get; set; } = 7879;
    public string DataRoot { get; set; } = GetDefaultDataRoot();
    public string UpdateMode { get; set; } = Deluno.Api.Updates.UpdateModes.DownloadBackground;
    // The release workflow stamps this into the tray assembly. A fresh RC install
    // must never silently subscribe to the stable feed just because it has not
    // written a config file yet.
    public string UpdateChannel { get; set; } = GetDefaultUpdateChannel();
    /// <summary>
    /// Whether the tray looks for a new version on its own. On for a file that already exists without the line (an install from
    /// before the choice was asked keeps what it did), off for an install with no file yet: the setup guide asks and sets it (#1361).
    /// </summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>
    /// Where updates come from: the built-in <see cref="ProjectRepositories.DelunoWebUrl"/> unless the person chose another.
    /// Only a real override is written to the file, and a saved value that is a former built-in default is read as the
    /// current default, so a repository move reaches copies installed before it.
    /// </summary>
    public string UpdateSource { get; set; } = ProjectRepositories.DelunoWebUrl;

    /// <summary>
    /// The tray's own "Let any address in" recovery action (#939): written by
    /// <see cref="Deluno.Tray.TrayApplication"/> when every address Deluno can
    /// be reached on ends up refused by Allowed Hosts, with no working web UI
    /// left to fix it from. <see cref="DelunoServer"/> and
    /// <see cref="ServiceHost"/> apply it the same way the documented
    /// config-file override works (docs/security-allowed-hosts.md): by
    /// setting <c>Security:AllowedHosts:ForceDisabled</c> in configuration
    /// before the app builds, so Allowed Hosts enforcement is off exactly as
    /// it always was before the feature existed. Off by default (#936); never
    /// true unless this recovery action was used.
    /// </summary>
    public bool AllowedHostsForceDisabled { get; set; }

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

    /// <summary>
    /// Set when the settings file exists but could not be read (busy for the whole wait, or damaged), in everyday words.
    /// While it is set, <see cref="Save"/> refuses: these values stand in for the file and must never be written over it (#1260).
    /// </summary>
    [JsonIgnore]
    public string? LoadProblem { get; private set; }

    /// <summary>
    /// True when <see cref="LoadProblem"/> is set and these values are the defaults, not what the file held. A busy file read
    /// again gives the last values this process read, which are not guessed; a damaged one, or a busy one at the first read, is.
    /// </summary>
    [JsonIgnore]
    public bool ValuesAreGuessed { get; private set; }

    /// <summary>Test seam: the disk. A test makes the settings file busy or cut short through this.</summary>
    internal static IStateFileSystem Io { get; set; } = RealStateFileSystem.Instance;

    private static readonly object LastGoodGate = new();
    private static AppSettings? _lastGood;

    /// <summary>
    /// The settings. A missing file gives the defaults (a first run). A file that is busy is waited for; one that stays busy gives
    /// the last values this process read, or the defaults if it never read any, with <see cref="LoadProblem"/> set. A damaged file
    /// is copied aside (timestamped), logged, and gives the same. Whenever <see cref="LoadProblem"/> is set nothing saves these
    /// values over the file (#1260).
    /// </summary>
    public static AppSettings Load()
    {
        var pathToRead = ResolveConfigPath(out var loadedFromLegacyPath);
        if (pathToRead is null)
        {
            var settings = new AppSettings
            {
                DataRoot = ResolveFallbackDataRoot(),
                AutoCheckUpdates = false
            };

            if (ShouldPersistPrimaryConfig(settings.DataRoot))
            {
                TryPersistPrimaryConfig(settings);
            }

            return settings;
        }

        var read = StateFile.Read<AppSettings>(pathToRead, JsonOptions, Io);
        switch (read.Kind)
        {
            case StateFileKind.Missing:
                // Removed between the look and the read: the same as no file.
                return new AppSettings { DataRoot = ResolveFallbackDataRoot(), AutoCheckUpdates = false };
            case StateFileKind.Busy:
                return Unread($"Deluno could not read its settings file ({pathToRead}) because something else was using it.");
            case StateFileKind.Unreadable:
                var kept = StateFile.KeepUnreadableCopy(pathToRead, Io);
                var words = $"Deluno could not read its settings file ({pathToRead}) because it is damaged ({read.Detail}).{(kept is null ? string.Empty : $" A copy was kept as {Path.GetFileName(kept)}.")}";
                HookLog.Write(words);
                HookLog.Write(words, TrayStartLog.FileName);
                return Unread(words);
        }

        var loaded = read.Value!;
        loaded.DataRoot = NormalizeDataRoot(loaded.DataRoot);
        if (!Deluno.Api.Updates.UpdateModes.IsValid(loaded.UpdateMode))
        {
            loaded.UpdateMode = Deluno.Api.Updates.UpdateModes.DownloadBackground;
        }

        if (string.IsNullOrWhiteSpace(loaded.UpdateChannel))
        {
            loaded.UpdateChannel = GetDefaultUpdateChannel();
        }

        if (string.IsNullOrWhiteSpace(loaded.UpdateSource) || ProjectRepositories.IsFormerDelunoDefault(loaded.UpdateSource))
        {
            loaded.UpdateSource = ProjectRepositories.DelunoWebUrl;
        }

        // If settings were loaded from the legacy path, transparently persist a normalized
        // copy to the current path so subsequent runs use a single canonical location.
        if (loadedFromLegacyPath)
        {
            TryPersistPrimaryConfig(loaded);
        }

        lock (LastGoodGate)
        {
            _lastGood = loaded.Copy();
        }

        return loaded;
    }

    /// <summary>
    /// The settings for a start. Throws <see cref="StateFileException"/>, in everyday words, when the file could not be read and
    /// the values would be guesses: starting on the default folders could hide a custom data root, and look like an empty install.
    /// The supervisor retries the start, tells the person in a balloon, and the reason goes to the start log.
    /// </summary>
    public static AppSettings LoadForStart()
    {
        var settings = Load();
        if (settings.ValuesAreGuessed)
        {
            throw new StateFileException($"{settings.LoadProblem} Deluno did not start, because starting on the default folders could hide your data. Close anything using that file and choose Restart; if the file is damaged, put the kept copy back after fixing it, or delete it to start with the defaults.");
        }

        return settings;
    }

    /// <summary>The data root, or <see cref="StateFileException"/> when the settings could not be read and the root would be a guess.</summary>
    public static string RequireDataRoot()
    {
        var settings = Load();
        return settings.ValuesAreGuessed
            ? throw new StateFileException($"{settings.LoadProblem} Deluno did not use the default folders instead, because that could hide your data.")
            : settings.DataRoot;
    }

    /// <summary>
    /// Changes the saved settings: reads the file again under its lock, applies <paramref name="change"/> and writes the result
    /// atomically, so two changes never lose each other and nothing is written from a guess. Null when it landed, else why not, in everyday words.
    /// </summary>
    public static string? Update(Action<AppSettings> change)
    {
        var basis = Load();
        if (basis.LoadProblem is not null)
        {
            return $"{basis.LoadProblem} Nothing was changed.";
        }

        var result = StateFile.Update<AppSettings>(
            GetConfigPath(),
            current =>
            {
                var settings = current ?? basis;
                change(settings);
                settings.DataRoot = NormalizeDataRoot(settings.DataRoot);
                return settings;
            },
            "Deluno's settings file",
            JsonOptions,
            Io);
        return result.Problem;
    }

    private static AppSettings Unread(string problem)
    {
        AppSettings settings;
        bool guessed;
        lock (LastGoodGate)
        {
            guessed = _lastGood is null;
            settings = _lastGood?.Copy() ?? new AppSettings();
        }

        settings.LoadProblem = problem;
        settings.ValuesAreGuessed = guessed;
        return settings;
    }

    private AppSettings Copy() => new()
    {
        Port = Port,
        DataRoot = DataRoot,
        UpdateMode = UpdateMode,
        UpdateChannel = UpdateChannel,
        AutoCheckUpdates = AutoCheckUpdates,
        UpdateSource = UpdateSource,
        AllowedHostsForceDisabled = AllowedHostsForceDisabled
    };

    /// <summary>Test seam: forgets the values this process last read.</summary>
    internal static void ForgetLastRead()
    {
        lock (LastGoodGate)
        {
            _lastGood = null;
        }
    }

    /// <summary>Writes these settings. Returns null when they landed, else why not, in everyday words. Refuses while <see cref="LoadProblem"/> is set.</summary>
    public string? Save()
    {
        if (LoadProblem is not null)
        {
            return $"{LoadProblem} Nothing was saved.";
        }

        DataRoot = NormalizeDataRoot(DataRoot);
        var problem = StateFile.Write(GetConfigPath(), Serialize(this), Io);
        return problem is null ? null : $"Deluno could not save its settings: {problem}.";
    }

    private static string? ResolveConfigPath(out bool loadedFromLegacyPath)
    {
        loadedFromLegacyPath = false;
        var configPath = GetConfigPath();
        if (Io.Exists(configPath))
        {
            return configPath;
        }

        // Where it was kept before #1177: inside the install folder Velopack's Setup replaces on a reinstall.
        // Read it from there once and write it to the current path (the same copy-forward the ProgramData fallback gets).
        var installFolderConfigPath = GetInstallFolderConfigPath();
        if (Io.Exists(installFolderConfigPath))
        {
            loadedFromLegacyPath = true;
            return installFolderConfigPath;
        }

        var legacyConfigPath = GetLegacyConfigPath();
        if (Io.Exists(legacyConfigPath))
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
        // Only ever called when the primary file does not exist yet, so it never replaces a file that holds the person's settings.
        var normalized = settings.Copy();
        normalized.DataRoot = NormalizeDataRoot(settings.DataRoot);
        // A failure to keep the copy is not worth stopping for: the next load reads the legacy path again and tries once more.
        _ = StateFile.Write(GetConfigPath(), Serialize(normalized), Io);
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

    /// <summary>
    /// Beside the default data root, never inside <c>%LocalAppData%\Deluno</c>: that is Velopack's install root and a
    /// reinstall replaces it, which would lose the pointer to a custom data root (#1177).
    /// </summary>
    private static string GetConfigPath()
    {
        return Path.Combine(GetLocalApplicationDataPath(), "DelunoConfig", "deluno.json");
    }

    private static string GetInstallFolderConfigPath()
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

    internal static string GetDefaultUpdateChannel()
    {
        const string metadataKey = "DelunoUpdateChannel";
        var assembly = Assembly.GetEntryAssembly();
        var configuredChannel = assembly?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, metadataKey, StringComparison.OrdinalIgnoreCase))
            ?.Value;

        return string.IsNullOrWhiteSpace(configuredChannel)
            ? "stable"
            : configuredChannel.Trim().ToLowerInvariant();
    }

    /// <summary>The file text: the update address only when it is a real override of the built-in default.</summary>
    internal static string Serialize(AppSettings settings)
    {
        var copy = settings.Copy();
        if (string.IsNullOrWhiteSpace(copy.UpdateSource)
            || string.Equals(copy.UpdateSource.Trim().TrimEnd('/'), ProjectRepositories.DelunoWebUrl, StringComparison.OrdinalIgnoreCase)
            || ProjectRepositories.IsFormerDelunoDefault(copy.UpdateSource))
        {
            copy.UpdateSource = null!;
        }

        return JsonSerializer.Serialize(copy, JsonOptions);
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
