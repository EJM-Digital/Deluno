using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Tray;

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
    [property: JsonPropertyName("preStartArgs")] string? PreStartArgs = null)
{
    /// <summary>The manifest's own key, e.g. "qbittorrent" -- filled in by <see cref="BundledComponents.Load"/>.</summary>
    [JsonIgnore]
    public string Kind { get; init; } = string.Empty;

    public const string DownloadClientCategory = "download-client";
    public const string ProcessorCategory = "processor";
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
