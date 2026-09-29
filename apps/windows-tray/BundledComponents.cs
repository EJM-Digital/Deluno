using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Tray;

/// <summary>
/// A separate installer that must run and succeed before its owning
/// component's own installer is attempted -- e.g. Transmission's .msi
/// LaunchConditions refuse with exit code 1603 ("The Visual C++ Runtime is
/// not installed or is outdated") on a machine that has never installed
/// anything built with a recent MSVC toolchain, which a clean Windows Server
/// VM is. Verified and run the same way as any other bundled installer:
/// downloaded, checked against <see cref="MinimumBytes"/> and either a
/// pinned checksum or <see cref="MustBeSigned"/>, then run -- never "just
/// this once" unverified. Skipped when <see cref="SkipRegistryValueName"/>
/// already reads non-zero under HKLM\<see cref="SkipRegistryKeyPath"/>, so a
/// machine that already has the runtime is never asked to reinstall it.
/// </summary>
public sealed record BundledComponentPrerequisite(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("minimumBytes")] long MinimumBytes,
    [property: JsonPropertyName("mustBeSigned")] bool MustBeSigned,
    [property: JsonPropertyName("silentArgs")] string? SilentArgs,
    [property: JsonPropertyName("skipRegistryKeyPath")] string SkipRegistryKeyPath,
    [property: JsonPropertyName("skipRegistryValueName")] string SkipRegistryValueName);

/// <summary>
/// The six components the installer's picker can offer (#640): five download
/// clients and Weir. Read from the embedded copy of
/// <c>installer/bundled-components.json</c>, so what ships is exactly what the
/// manifest says -- there is no separate "the tray's own list" to drift from
/// it.
/// </summary>
public sealed record BundledComponentSpec(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("licence")] string Licence,
    [property: JsonPropertyName("licenceUrl")] string LicenceUrl,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("minimumBytes")] long MinimumBytes,
    [property: JsonPropertyName("mustBeSigned")] bool MustBeSigned,
    [property: JsonPropertyName("silentArgs")] string? SilentArgs,
    [property: JsonPropertyName("defaultPort")] int DefaultPort,
    [property: JsonPropertyName("uninstallDisplayNameHint")] string UninstallDisplayNameHint,
    [property: JsonPropertyName("githubRepo")] string? GithubRepo,
    [property: JsonPropertyName("githubAsset")] string? GithubAsset,
    [property: JsonPropertyName("githubChecksumAsset")] string? GithubChecksumAsset,
    [property: JsonPropertyName("postInstallCommand")] string? PostInstallCommand = null,
    /// <summary>A regex matched against each GitHub release asset's own file name to find the *checksum* file, for a vendor (like NZBGet) whose checksum file name also embeds the version, e.g. "nzbget-21.1.sig.txt". Takes precedence over <see cref="GithubChecksumAsset"/> when set.</summary>
    [property: JsonPropertyName("githubChecksumAssetPattern")] string? GithubChecksumAssetPattern = null,
    /// <summary>Prerequisite software (e.g. a runtime redistributable) that must be installed before this component's own installer will run. Skipped when already present, verified the same way as any other bundled installer, and run before the component's own installer.</summary>
    [property: JsonPropertyName("prerequisite")] BundledComponentPrerequisite? Prerequisite = null,
    [property: JsonPropertyName("postInstallArgs")] string? PostInstallArgs = null,
    [property: JsonPropertyName("readinessUrl")] string? ReadinessUrl = null,
    [property: JsonPropertyName("readinessField")] string? ReadinessField = null,
    [property: JsonPropertyName("readinessValue")] string? ReadinessValue = null,
    [property: JsonPropertyName("readinessTimeoutSeconds")] int? ReadinessTimeoutSeconds = null,
    /// <summary>A regex matched against each GitHub release asset's own file name, for a vendor (like NZBGet) whose asset name embeds the version so no single fixed name survives an upgrade. Takes precedence over <see cref="GithubAsset"/> when set.</summary>
    [property: JsonPropertyName("githubAssetPattern")] string? GithubAssetPattern = null,
    /// <summary>A directory-listing page to resolve "latest" from, for a vendor (like Deluge) that does not publish GitHub Releases at all.</summary>
    [property: JsonPropertyName("directoryListingUrl")] string? DirectoryListingUrl = null,
    /// <summary>A regex matched against each link's file name in <see cref="DirectoryListingUrl"/>; the highest version number among matches wins.</summary>
    [property: JsonPropertyName("assetPattern")] string? AssetPattern = null,
    /// <summary>Appended to the resolved asset's file name to fetch a per-file checksum (e.g. ".sha256"), read the same "hash  filename" way as a GitHub SHA256SUMS.txt line.</summary>
    [property: JsonPropertyName("checksumSuffix")] string? ChecksumSuffix = null,
    /// <summary>A path to write <see cref="ConfigFileContent"/> to before the first start, e.g. to enable a client's Web UI/API and pick its port ahead of time so Deluno's own detection never has to guess. Environment variables in the path are expanded.</summary>
    [property: JsonPropertyName("configFilePath")] string? ConfigFilePath = null,
    [property: JsonPropertyName("configFileContent")] string? ConfigFileContent = null,
    /// <summary>A second, untracked process started (fire-and-forget) before <see cref="PostInstallCommand"/> -- for a component (like Deluge) whose Web UI/API is a separate process from the daemon it talks to.</summary>
    [property: JsonPropertyName("preStartCommand")] string? PreStartCommand = null,
    [property: JsonPropertyName("preStartArgs")] string? PreStartArgs = null,
    /// <summary>The Web UI/API user name Deluno gives a component that refuses to start without credentials (qBittorrent 5.2.1, #1050) or whose shipped default is a known public password (NZBGet). A password is generated at install, filled into the config through the <c>{{username}}</c>, <c>{{password}}</c> and <c>{{qbittorrentPbkdf2}}</c> placeholders, and kept in Deluno's own secret store for the connection to use.</summary>
    [property: JsonPropertyName("credentialsUsername")] string? CredentialsUsername = null,
    /// <summary>A file the vendor's own installer ships (e.g. NZBGet's nzbget.conf.template) that <see cref="ConfigFilePath"/> is built from, with <see cref="ConfigReplacements"/> applied, instead of writing <see cref="ConfigFileContent"/> from scratch. Environment variables are expanded.</summary>
    [property: JsonPropertyName("configTemplateSourcePath")] string? ConfigTemplateSourcePath = null,
    /// <summary>Exact text in the template mapped to its replacement. Values may use the credential placeholders and environment variables. A replacement whose text is not in the template stops the config being seeded, rather than writing a config that silently lacks it.</summary>
    [property: JsonPropertyName("configReplacements")] Dictionary<string, string>? ConfigReplacements = null,
    /// <summary>Start the post-install command with no console window (a console-mode server such as NZBGet's <c>-s</c> or the Transmission daemon).</summary>
    [property: JsonPropertyName("startHidden")] bool StartHidden = false,
    /// <summary>A Windows service the component's own package installs and starts that Deluno does not want (Transmission's MSI registers an auto-start service bound to every address). It is stopped and disabled in the same elevated step as the install, and Deluno runs the daemon itself, bound to this computer only.</summary>
    [property: JsonPropertyName("disableServiceAfterInstall")] string? DisableServiceAfterInstall = null)
{
    /// <summary>The manifest's own key, e.g. "qbittorrent" -- filled in by <see cref="BundledComponents.Load"/>.</summary>
    [JsonIgnore]
    public string Kind { get; init; } = string.Empty;

    public const string DownloadClientCategory = "download-client";
    public const string ProcessorCategory = "processor";

    /// <summary>
    /// A download client Deluno started for the person is started again the next
    /// time they sign in to Windows (see <see cref="ComponentAutoStarter"/>), so a
    /// restart does not leave Deluno pointing at a client that is not running.
    /// Weir looks after its own start.
    /// </summary>
    [JsonIgnore]
    public bool RestartsAtSignIn => string.Equals(Category, DownloadClientCategory, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(PostInstallCommand);
}

public static class BundledComponents
{
    private const string ResourceName = "Deluno.Tray.Assets.bundled-components.json";

    public static IReadOnlyList<BundledComponentSpec> Load()
    {
        using var stream = typeof(BundledComponents).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found in {Assembly.GetExecutingAssembly().FullName}.");
        using var document = JsonDocument.Parse(stream);

        var specs = new List<BundledComponentSpec>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith('$'))
            {
                continue; // "$comment" and similar are documentation, not a component.
            }

            var spec = property.Value.Deserialize<BundledComponentSpec>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException($"Could not read the '{property.Name}' entry in bundled-components.json.");
            specs.Add(spec with { Kind = property.Name });
        }

        return specs;
    }
}
